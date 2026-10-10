using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// Attach slots on the marshal card, like leader_groups_portrait_window has; appended at the window's end so it draws on top.
[assembly: GuiPatch("interface/leadergroups.gui", "gui/army_group_attached_units.gui", After = @"(?s)name = ""army_group_portrait_window"".*?(?=\n\t\})")]

namespace AirWingFieldMarshal;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>COrdersGroup::GetAttachWingStatus: `cmp byte [rcx+IsArmyGroup],0; ...; jz ok; mov eax,3` (3 = "only armies").</summary>
    [Signature] public const string AttachStatus = "80 79 ?? 00 48 8B FA 48 8B D9 74 10 B8 03 00 00 00";

    /// <summary>CAS target selection, two loops over a combat side's divisions: `mov rax,[rdx|rcx]; cmp [rax+Army],rsi; jz hit`.</summary>
    [Signature] public const string CasLoop1 = "48 8B 02 48 39 B0 ?? ?? ?? ?? 74 ?? 49 FF C0 48 83 C2 08 4C 3B C1 7C E8";
    [Signature] public const string CasLoop2 = "48 8B 01 48 39 B0 ?? ?? ?? ?? 74 ?? 48 FF C2 48 83 C1 08 49 3B D0 7C E8";

    /// <summary>COrdersGroup daily update of attached wings' missions (reads ATTACHED_WINGS_ORDER_UPDATE_DAYS).</summary>
    [Signature] public const string UpdateAttachedWings = "48 89 4C 24 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? B8 28 12 00 00 E8 ? ? ? ? 48 2B E0 0F 29 B4 24 ? ? ? ? 4C 8B F9";

    /// <summary>Army card: creates the attached-wing slots ("attached_units" gridbox).</summary>
    [Signature] public const string CreateWingSlots = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 40 48 8B D9 33 ED";

    /// <summary>bool: are air wings currently selected.</summary>
    [Signature] public const string AirWingsSelected = "48 83 EC 28 48 8B 0D ? ? ? ? 48 85 C9 74 ? 48 81 C1 38 05 00 00";

    /// <summary>
    /// UI: attach air wings to a group (CPdxArray&lt;CAirWing*&gt;* wings, COrdersGroup* group): posts one vanilla
    /// CAttachAirWingToArmyCommand for them. The army card's right-click and the army list both end here.
    /// </summary>
    [Signature] public const string PostAttachWings = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 8B FA 48 8B F1 48 8B 0D ? ? ? ? 48 8B 01 FF 90 ? ? ? ? 48 8B D8 B9 48 00 00 00";

    /// <summary>
    /// CAS target selection (collects CAirMission::STacticalTarget combats in the mission regions), per combat:
    /// `mov rbp,[rsi+28h]; mov rcx,rbp; call CAirWing::GetAttachedOrdersGroup; test rax,rax; jz take; ...`. Unattached wings
    /// take the combat; attached ones fall through to the two CAS loops above, which keep it only if a division on either
    /// side is in their army.
    /// </summary>
    [Signature] public const string AttachedCheck = "48 8B 6E 28 48 8B CD E8 ? ? ? ? 48 85 C0 0F 84 ? ? ? ? 48 8B CD E8";
}

/// <summary>
/// Attached air wings, two settings that change the game (single player):
/// - FieldMarshal: air wings can be attached to army groups (field marshals), not only armies (generals); the GuiPatch
///   above gives the marshal card attach slots. In multiplayer (SpreadOverArmies) right-clicking a field marshal's card
///   with air wings selected attaches them to his armies instead, spread over them, with the vanilla attach command: the
///   game then runs their missions and bases like any army's wings, and other players need nothing.
/// - WholeMissionArea: attached wings give close air support to every combat in their mission regions, like unattached
///   wings, instead of only to combats their own army's divisions fight in.
/// </summary>
[Plugin("com.green1052.airwingfieldmarshal", "Attached Air Wings", "1.0.0", GameVersion = "1.19.3")]
public unsafe class AirWingFieldMarshal : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate void GroupFn(nint group);
    [Function(CallingConventions.Microsoft)] public delegate void CardFn(nint card);
    [Function(CallingConventions.Microsoft)] public delegate void CardGroupFn(nint card, nint group);
    [Function(CallingConventions.Microsoft)] public delegate byte PostAttachFn(nint wings, nint group);
    [Function(CallingConventions.Microsoft)] public delegate byte ByteFn(); // native bool comes back in al only

    private IHook<GroupFn>? _updateAttachedWings;
    private IHook<CardFn>? _createWingSlots, _cardRightClick;
    private IHook<CardGroupFn>? _refreshWingSlots;
    private IHook<PostAttachFn>? _postAttach;
    private ByteFn? _airWingsSelected;
    private int _cardWingGrid;

    public override void Load()
    {
        var fieldMarshal = Config.Bind("FieldMarshal", "FieldMarshal", true,
            "Air wings can be attached to field marshals (army groups), not only to generals. Single player: changes the game.");
        var spread = Config.Bind("Multiplayer", "SpreadOverArmies", true,
            "In multiplayer, where a field marshal can't hold air wings: right-clicking his card with air wings selected attaches them to his armies, spread over them.");
        var wholeArea = Config.Bind("Support", "WholeMissionArea", true,
            "Air wings attached to a general give close air support to every battle in their mission regions, not only their own divisions' battles. Single player: changes the game.");
        if (Multiplayer)
        {
            if (fieldMarshal && spread) LoadMultiplayer();
            else Log.Info("multiplayer: nothing to do (spreading over armies is off)");
            return;
        }
        if (wholeArea)
        {
            // `jz take` (0F 84 rel32) becomes `jmp take; nop` (E9 rel32+1, 90): every wing takes the combat.
            var jz = Sig.Find(Signatures.AttachedCheck) + 15;
            var rel = *(int*)(jz + 2);
            Patches.Bytes(jz, "0F 84" + Hex(rel), "E9" + Hex(rel + 1) + "90");
        }
        if (fieldMarshal) LoadFieldMarshal();
    }

    private static string Hex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));

    private void LoadFieldMarshal()
    {
        // Resolve everything first: a missing pattern throws before anything is patched.
        var attachStatus = Sig.Find(Signatures.AttachStatus);
        var casLoops = new[] { Sig.Find(Signatures.CasLoop1), Sig.Find(Signatures.CasLoop2) };
        var update = Sig.Find(Signatures.UpdateAttachedWings);
        var createSlots = Sig.Find(Signatures.CreateWingSlots);
        var refreshSlots = Sig.Find(ArmyCard.RefreshWingSlotsSig);
        var rightClick = Sig.Find(ArmyCard.RightClickSig);
        var wingsSelected = Sig.Find(Signatures.AirWingsSelected);

        // Field offsets come from the instructions themselves where the patterns contain them.
        if (*(byte*)(attachStatus + 2) != OrdersGroup.IsArmyGroupOffset)
            throw new InvalidOperationException($"COrdersGroup layout changed (army group flag at 0x{*(byte*)(attachStatus + 2):X})");
        _cardWingGrid = *(int*)(refreshSlots + 32);
        _airWingsSelected = Patches.Function<ByteFn>(wingsSelected);

        // GetAttachWingStatus: the army-group `jz` becomes an unconditional jmp.
        Patches.Bytes(attachStatus + 10, "74", "EB");

        // CAS only picks combats where a division's army is the attached group. Over `mov rax,[arr]; cmp [rax+Army],rsi`,
        // also accept that army's army group. Leaves ZF set on a match so the original `jz` right after decides.
        foreach (var loop in casLoops)
        {
            Patches.Asm(loop, 10,
                "use64",
                $"mov rax, [{(*(byte*)(loop + 2) == 0x02 ? "rdx" : "rcx")}]",
                $"mov rax, [rax+{*(int*)(loop + 6)}]",
                "cmp rax, rsi",
                "je done",
                "test rax, rax",
                "jz miss",
                $"cmp [rax+{OrdersGroup.ParentOffset}], rsi",
                "jmp done",
                "miss:",
                "cmp rax, rsi", // rax = 0, rsi != 0 -> ZF = 0
                "done:");
        }

        _updateAttachedWings = Patches.Hook<GroupFn>(update, UpdateAttachedWings);
        _createWingSlots = Patches.Hook<CardFn>(createSlots, card =>
            ArmyCard.AsArmyCard(card, true, () => _createWingSlots!.OriginalFunction(card)));
        _refreshWingSlots = Patches.Hook<CardGroupFn>(refreshSlots, (card, group) =>
            ArmyCard.AsArmyCard(card, *(nint*)(card + _cardWingGrid) != 0, () => _refreshWingSlots!.OriginalFunction(card, group)));
        _cardRightClick = Patches.Hook<CardFn>(rightClick, card =>
            ArmyCard.AsArmyCard(card, _airWingsSelected!() != 0, () => _cardRightClick!.OriginalFunction(card)));
    }

    // Daily attached-wing order update. It picks mission regions from where the group's divisions are,
    // and an army group's divisions belong to its armies.
    private void UpdateAttachedWings(nint ptr)
    {
        var g = new OrdersGroup(ptr);
        if (g is { IsArmyGroup: true, AttachedWingCount: > 0 }) g.WithArmiesMembers(() => _updateAttachedWings!.OriginalFunction(ptr));
        else _updateAttachedWings!.OriginalFunction(ptr);
    }

    private void LoadMultiplayer()
    {
        var rightClick = Sig.Find(ArmyCard.RightClickSig);
        var postAttach = Sig.Find(Signatures.PostAttachWings);
        _airWingsSelected = Patches.Function<ByteFn>(Sig.Find(Signatures.AirWingsSelected));

        // A marshal's card ignores a right-click with wings selected; as an army card's it gets to the attach below...
        _cardRightClick = Patches.Hook<CardFn>(rightClick, card =>
            ArmyCard.AsArmyCard(card, _airWingsSelected!() != 0, () => _cardRightClick!.OriginalFunction(card)));
        // ...which an army group can't take: attach to its armies instead.
        _postAttach = Patches.Hook<PostAttachFn>(postAttach, (wings, group) =>
            group != 0 && new OrdersGroup(group).IsArmyGroup ? SpreadOverArmies(wings, group) : _postAttach!.OriginalFunction(wings, group));
    }

    private byte SpreadOverArmies(nint wings, nint group)
    {
        var all = Native.PdxArray(wings).ToArray();
        var armies = new OrdersGroup(group).Armies.ToArray();
        var shares = ArmyCard.Spread(all, armies.Length);
        byte posted = 0;
        for (var a = 0; a < shares.Length; a++)
        {
            var army = armies[a];
            posted |= Native.WithPdxArray(shares[a], array => _postAttach!.OriginalFunction(array, army));
        }
        Log.Info($"{all.Length} air wings attached across {shares.Length} of the marshal's armies");
        return posted;
    }
}
