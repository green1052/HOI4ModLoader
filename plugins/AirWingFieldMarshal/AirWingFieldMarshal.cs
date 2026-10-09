using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

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

    /// <summary>Army card: refreshes the slots. `cmp byte [rcx+CardIsArmyGroup],0; ...; jnz skip; mov rcx,[rcx+CardWingGrid]`.</summary>
    [Signature] public const string RefreshWingSlots = "48 89 5C 24 ? 57 48 83 EC 30 80 B9 ? ? ? ? 00 48 8B DA 48 8B F9 0F 85 ? ? ? ? 48 8B 89 ? ? ? ?";

    /// <summary>Army card: right-click (attach selected wings, among others).</summary>
    [Signature] public const string CardRightClick = "48 8B C4 48 89 58 ? 48 89 70 ? 48 89 78 ? 55 41 54 41 55 41 56 41 57 48 8D 68 ? 48 81 EC B0 00 00 00 0F 29 70 ? 4C 8B E9 48 83 C1 30";

    /// <summary>bool: are air wings currently selected.</summary>
    [Signature] public const string AirWingsSelected = "48 83 EC 28 48 8B 0D ? ? ? ? 48 85 C9 74 ? 48 81 C1 38 05 00 00";
}

/// <summary>
/// Lets air wings be attached to army groups (field marshals), not only armies (generals).
/// Ships the "AirWingFieldMarshal" HOI4 mod (attach slots on the marshal card), generated into Documents on build.
/// </summary>
[Plugin("com.green1052.airwingfieldmarshal", "Air Wing Field Marshal", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe class AirWingFieldMarshal : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate void GroupFn(nint group);
    [Function(CallingConventions.Microsoft)] public delegate void CardFn(nint card);
    [Function(CallingConventions.Microsoft)] public delegate void CardGroupFn(nint card, nint group);
    [Function(CallingConventions.Microsoft)] public delegate byte ByteFn(); // native bool comes back in al only

    IHook<GroupFn>? _updateAttachedWings;
    IHook<CardFn>? _createWingSlots, _cardRightClick;
    IHook<CardGroupFn>? _refreshWingSlots;
    ByteFn? _airWingsSelected;
    int _cardIsArmyGroup, _cardWingGrid;

    public override void Load()
    {
        // Resolve everything first: a missing pattern throws before anything is patched.
        var attachStatus = Sig.Find(Signatures.AttachStatus);
        var casLoops = new[] { Sig.Find(Signatures.CasLoop1), Sig.Find(Signatures.CasLoop2) };
        var update = Sig.Find(Signatures.UpdateAttachedWings);
        var createSlots = Sig.Find(Signatures.CreateWingSlots);
        var refreshSlots = Sig.Find(Signatures.RefreshWingSlots);
        var rightClick = Sig.Find(Signatures.CardRightClick);
        var wingsSelected = Sig.Find(Signatures.AirWingsSelected);

        // Field offsets come from the instructions themselves where the patterns contain them.
        if (*(byte*)(attachStatus + 2) != OrdersGroup.IsArmyGroupOffset)
            throw new InvalidOperationException($"COrdersGroup layout changed (army group flag at 0x{*(byte*)(attachStatus + 2):X})");
        _cardIsArmyGroup = *(int*)(refreshSlots + 12);
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
        _createWingSlots = Patches.Hook<CardFn>(createSlots, card => AsArmyCard(card, true, () => _createWingSlots!.OriginalFunction(card)));
        _refreshWingSlots = Patches.Hook<CardGroupFn>(refreshSlots, (card, group) =>
            AsArmyCard(card, *(nint*)(card + _cardWingGrid) != 0, () => _refreshWingSlots!.OriginalFunction(card, group)));
        _cardRightClick = Patches.Hook<CardFn>(rightClick, card =>
            AsArmyCard(card, _airWingsSelected!() != 0, () => _cardRightClick!.OriginalFunction(card)));
    }

    // Daily attached-wing order update. It picks mission regions from the group's own front orders,
    // but an army group's fronts usually live on its armies, so it sees those too for the duration of the call.
    void UpdateAttachedWings(nint ptr)
    {
        var g = new OrdersGroup(ptr);
        if (!g.IsArmyGroup || g.AttachedWingCount == 0)
        {
            _updateAttachedWings!.OriginalFunction(ptr);
            return;
        }

        // Keep the game's order (multiplayer is lockstep: every client must see the same sequence).
        var merged = new List<nint>(Native.PdxArray(g.Orders).ToArray());
        foreach (var army in g.Armies) merged.AddRange(Native.PdxArray(new OrdersGroup(army).Orders).ToArray());
        merged = merged.Distinct().ToList();

        var orders = g.Orders;
        var saved = (*(nint*)orders, *(long*)(orders + 8)); // data, capacity|size
        fixed (nint* data = merged.ToArray())
        {
            *(nint*)orders = (nint)data;
            *(int*)(orders + 8) = *(int*)(orders + 0xC) = merged.Count;
            try { _updateAttachedWings!.OriginalFunction(ptr); }
            finally { (*(nint*)orders, *(long*)(orders + 8)) = saved; }
        }
    }

    // The card UI skips attached-wing slots and right-click attach for army-group cards.
    // Treat the card as an army card while those run (UI thread only, so nothing else sees the flag flip).
    void AsArmyCard(nint card, bool when, Action original)
    {
        var flag = (byte*)(card + _cardIsArmyGroup);
        var was = *flag;
        if (when) *flag = 0;
        try { original(); }
        finally { *flag = was; }
    }
}
