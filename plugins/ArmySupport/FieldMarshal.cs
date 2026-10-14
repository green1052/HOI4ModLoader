using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// Attach slots on the marshal card, like leader_groups_portrait_window has; appended at the window's end so it draws on top.
[assembly: GuiPatch("interface/leadergroups.gui", "gui/army_group_attached_units.gui", After = @"(?s)name = ""army_group_portrait_window"".*?(?=\n\t\})")]

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>COrdersGroup::GetAttachWingStatus: `cmp byte [rcx+IsArmyGroup],0; ...; jz ok; mov eax,3` (3 = "only armies").</summary>
    [Signature] public const string AttachStatus = "80 79 ?? 00 48 8B FA 48 8B D9 74 10 B8 03 00 00 00";

    /// <summary>
    /// CAS target selection of an attached wing, two loops over a combat side's divisions: `mov rax,[rdx|rcx]; cmp
    /// [rax+Army],rsi; jz hit`. The wing takes the combat only if a division on either side is in its army.
    /// </summary>
    [Signature] public const string CasLoop1 = "48 8B 02 48 39 B0 ?? ?? ?? ?? 74 ?? 49 FF C0 48 83 C2 08 4C 3B C1 7C E8";
    [Signature] public const string CasLoop2 = "48 8B 01 48 39 B0 ?? ?? ?? ?? 74 ?? 48 FF C2 48 83 C1 08 49 3B D0 7C E8";

    /// <summary>COrdersGroup daily update of attached wings' missions (reads ATTACHED_WINGS_ORDER_UPDATE_DAYS).</summary>
    [Signature] public const string UpdateAttachedWings = "48 89 4C 24 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? B8 28 12 00 00 E8 ? ? ? ? 48 2B E0 0F 29 B4 24 ? ? ? ? 4C 8B F9";

    /// <summary>
    /// Army card wing slot refresh: `cmp esi,3; jnb` adds the "+" (attach) slot only while fewer than three wing types
    /// (fighter, CAS, tactical bomber) are shown.
    /// </summary>
    [Signature] public const string WingAddSlotCap = "83 FE 03 0F 83 ? ? ? ? 48 8B 9F";

    /// <summary>Army card: creates the attached-wing slots ("attached_units" gridbox).</summary>
    [Signature] public const string CreateWingSlots = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 40 48 8B D9 33 ED";

    /// <summary>
    /// UI: attach air wings to a group (CPdxArray&lt;CAirWing*&gt;* wings, COrdersGroup* group): posts one vanilla
    /// CAttachAirWingToArmyCommand for them. The army card's right-click and the army list both end here.
    /// </summary>
    [Signature] public const string PostAttachWings = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 8B FA 48 8B F1 48 8B 0D ? ? ? ? 48 8B 01 FF 90 ? ? ? ? 48 8B D8 B9 48 00 00 00";

    /// <summary>
    /// Army card right-click, railway gun branch: `mov rcx,[r13+10h]; call IsRailwayGunSelected; test al,al; jz; cmp byte
    /// [r13+CardIsArmyGroup],0; jnz skip; ...; call GetSelectedRailwayGuns; ...; call PostAssignRailwayGuns`.
    /// </summary>
    [Signature] public const string CardAssign = "49 8B 4D 10 E8 ? ? ? ? 84 C0 74 ? 41 80 BD ? ? ? ? 00 0F 85 ? ? ? ? 49 8B 55 10 48 8D 4D ? E8 ? ? ? ? 90 4D 8B C6 48 8D 55 ? 49 8B 4D 10 E8";

    /// <summary>The army's railway gun box, assign button: posts an unassign for guns assigned elsewhere, then the assign.</summary>
    [Signature] public const string BoxUnassign = "4C 8B C6 48 8D 54 24 20 48 8B 0D ? ? ? ? E8 ? ? ? ? 90 48 8B 54 24 20 48 85 D2 74 ? 44 89 7C 24 2C 48 8B 4C 24 30 48 8B 01 FF 50 10 48 83 C3 08 48 3B DF 0F 85 ? ? ? ? 4C 8B C5 48 8D 54 24 38 48 8B 0D ? ? ? ? E8";

    /// <summary>UnitController::DistributeRailwayGuns(controller, -1, 0): *controller is the orders group whose guns it places.</summary>
    [Signature] public const string Distribute = "48 89 5C 24 10 48 89 4C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 00 DD FF FF B8 00 24 00 00 E8";
}

/// <summary>
/// Field marshals: right-click his card with air wings or railway guns selected.
/// - Single player (changes the game): they go to the army group itself. Air wings can be attached to it (the GuiPatch above
///   gives the marshal card attach slots); their missions follow his armies' divisions and they support those divisions'
///   battles. His railway guns take positions along all of his armies' divisions.
/// - Multiplayer: they are spread over his armies with the vanilla attach and assign commands; the game then runs them like
///   any army's, and other players need nothing.
/// Railway guns assigned elsewhere are unassigned first, as the army's own railway gun box does; guns already on the target
/// are left out (one of them would make the game reject the whole batch).
/// </summary>
public unsafe partial class ArmySupport
{
    private const int ViewOfCard = 0x10;
    private const int AssignedTo = 0x440, GunsOfGroup = 0x18; // CRailwayGun -> its orders group's railway gun container (group + 0x18), or 0

    [Function(CallingConventions.Microsoft)] public delegate void GroupFn(nint group);
    [Function(CallingConventions.Microsoft)] public delegate void CardFn(nint card);
    [Function(CallingConventions.Microsoft)] public delegate void CardGroupFn(nint card, nint group);
    [Function(CallingConventions.Microsoft)] public delegate byte PostAttachFn(nint wings, nint group);
    [Function(CallingConventions.Microsoft)] public delegate byte PostAssignFn(nint view, nint guns, nint group);
    [Function(CallingConventions.Microsoft)] public delegate void DistributeFn(nint controller, int a2, int a3);
    [Function(CallingConventions.Microsoft)] public delegate byte ByteFn(); // native bool comes back in al only

    private IHook<GroupFn>? _updateAttachedWings;
    private IHook<CardFn>? _createWingSlots, _cardRightClick;
    private IHook<CardGroupFn>? _refreshWingSlots;
    private IHook<PostAttachFn>? _postAttach;
    private IHook<PostAssignFn>? _postAssign;
    private IHook<DistributeFn>? _distribute;
    private ByteFn? _airWingsSelected;
    private delegate* unmanaged<nint, nint, nint, byte> _postUnassign;
    private int _cardWingGrid;

    private void LoadFieldMarshal()
    {
        // Resolve everything first: a missing pattern throws before anything is patched.
        var rightClick = Sig.Find(ArmyCard.RightClickSig);
        _airWingsSelected = Patches.Function<ByteFn>(Sig.Find(Gui.AirWingsSelectedSig));
        var at = Sig.Find(Signatures.CardAssign);
        var gunsSelected = (delegate* unmanaged<nint, byte>)(at + 9 + *(int*)(at + 5));
        var postAssign = at + 57 + *(int*)(at + 53);
        var addSlotCap = Sig.Find(Signatures.WingAddSlotCap);
        var unassign = Sig.Find(Signatures.BoxUnassign);
        _postUnassign = (delegate* unmanaged<nint, nint, nint, byte>)(unassign + 20 + *(int*)(unassign + 16));

        if (Multiplayer)
        {
            // An army group can't take the attach below: attach to its armies instead.
            _postAttach = Patches.Hook<PostAttachFn>(Sig.Find(Signatures.PostAttachWings), (wings, group) =>
                group != 0 && new OrdersGroup(group).IsArmyGroup ? SpreadWings(wings, group) : _postAttach!.OriginalFunction(wings, group));
        }
        else LetArmyGroupsHold();

        // The "+" slot stays once all three wing types are attached: more wings of a type are attached through it too
        // (cmp esi,4: at most three type slots, so the add slot always follows).
        Patches.Bytes(addSlotCap + 2, "03", "04");

        // A marshal's card ignores a right-click with air wings or railway guns selected; as an army card's it gets to the attach/assign.
        _cardRightClick = Patches.Hook<CardFn>(rightClick, card =>
            ArmyCard.AsArmyCard(card, _airWingsSelected!() != 0 || gunsSelected(*(nint*)(card + ViewOfCard)) != 0,
                () => _cardRightClick!.OriginalFunction(card)));
        // Only a marshal's assign is ours; an army's goes through as the game made it.
        _postAssign = Patches.Hook<PostAssignFn>(postAssign, (view, guns, group) =>
            group != 0 && new OrdersGroup(group).IsArmyGroup ? AssignGuns(view, guns, group) : _postAssign!.OriginalFunction(view, guns, group));
    }

    /// <summary>Single player: army groups hold air wings and railway guns themselves.</summary>
    private void LetArmyGroupsHold()
    {
        var attachStatus = Sig.Find(Signatures.AttachStatus);
        var casLoops = new[] { Sig.Find(Signatures.CasLoop1), Sig.Find(Signatures.CasLoop2) };
        var update = Sig.Find(Signatures.UpdateAttachedWings);
        var createSlots = Sig.Find(Signatures.CreateWingSlots);
        var refreshSlots = Sig.Find(ArmyCard.RefreshWingSlotsSig);
        var distribute = Sig.Find(Signatures.Distribute);

        // Field offsets come from the instructions themselves where the patterns contain them.
        if (*(byte*)(attachStatus + 2) != OrdersGroup.IsArmyGroupOffset)
            throw new InvalidOperationException($"COrdersGroup layout changed (army group flag at 0x{*(byte*)(attachStatus + 2):X})");
        _cardWingGrid = *(int*)(refreshSlots + 32);

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

        // Daily attached-wing order update and railway gun distribution: both work from the group's divisions, and an army
        // group's divisions belong to its armies.
        _updateAttachedWings = Patches.Hook<GroupFn>(update, ptr =>
        {
            var g = new OrdersGroup(ptr);
            if (g is { IsArmyGroup: true, AttachedWingCount: > 0 }) g.WithArmiesMembers(() => _updateAttachedWings!.OriginalFunction(ptr));
            else _updateAttachedWings!.OriginalFunction(ptr);
        });
        _distribute = Patches.Hook<DistributeFn>(distribute, (controller, a2, a3) =>
        {
            var group = new OrdersGroup(*(nint*)controller);
            if (group.Ptr != 0 && group.IsArmyGroup) group.WithArmiesMembers(() => _distribute!.OriginalFunction(controller, a2, a3));
            else _distribute!.OriginalFunction(controller, a2, a3);
        });
        _createWingSlots = Patches.Hook<CardFn>(createSlots, card =>
            ArmyCard.AsArmyCard(card, true, () => _createWingSlots!.OriginalFunction(card)));
        _refreshWingSlots = Patches.Hook<CardGroupFn>(refreshSlots, (card, group) =>
            ArmyCard.AsArmyCard(card, *(nint*)(card + _cardWingGrid) != 0, () => _refreshWingSlots!.OriginalFunction(card, group)));
    }

    /// <summary>Multiplayer: the marshal's air wings attached to his armies, a share each.</summary>
    private byte SpreadWings(nint wings, nint group)
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

    private byte AssignGuns(nint view, nint guns, nint group)
    {
        var all = Native.PdxArray(guns).ToArray();
        // Each target gets a share, in selection order.
        nint[] targets = Multiplayer ? [.. new OrdersGroup(group).Armies] : [group];
        var shares = ArmyCard.Spread(all, targets.Length);
        byte posted = 0;
        for (var t = 0; t < shares.Length; t++)
        {
            var target = targets[t];
            var share = shares[t].Where(gun => Owner(gun) != target).ToArray();
            if (share.Length == 0) continue;
            foreach (var gun in share)
            {
                var from = Owner(gun);
                if (from != 0) Native.WithPdxArray([gun], array => _postUnassign(view, array, from));
            }
            posted |= Native.WithPdxArray(share, array => _postAssign!.OriginalFunction(view, array, target));
        }
        Log.Info($"{all.Length} railway guns selected for a field marshal, assigned to {shares.Length} group(s)");
        return posted;
    }

    /// <summary>The orders group the gun is assigned to, or 0.</summary>
    private static nint Owner(nint gun)
    {
        var container = *(nint*)(gun + AssignedTo);
        return container == 0 ? 0 : container - GunsOfGroup;
    }
}
