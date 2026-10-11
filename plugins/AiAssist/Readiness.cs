using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Offensive readiness: an "attack on" an AI army isn't ready for is dropped; the AI asks again every hour ----

    private const int GroupOfCommand = 0x28, OrderOfCommand = 0x30, ExecuteOn = 0x34;                     // COrderExecuteCommand
    private const int TypeOfOrder = 0x30, Executing = 0x248, Invasion = 3, Paradrop = 4;                   // COrder
    private const int UnitOfMemberSlot = 4, StrengthRatioSlot = 34, OrgRatioSlot = 39;                     // member -> CArmy; CArmy ratios (1e5)
    private const int SupplyOfUnit = 0x40;                                                                 // CArmy: supply ratio (1e5)
    private const long WeakOrg = 35 * Native.FixedPoint / 100;
    private const int MaxWeakPercent = 30;

    [Function(CallingConventions.Microsoft)] public delegate byte PostFn(nint cmd, nint tag);

    private IHook<PostFn>? _post;
    private delegate* unmanaged<nint, int, nint> _findOrder;
    private nint _executeVTable;
    private long _minOrg, _minStrength, _minSupply;
    private static bool _frontShortening, _pocketClosing;
    private int _held;

    private void LoadReadiness()
    {
        _findOrder = (delegate* unmanaged<nint, int, nint>)Sig.Find(Signatures.FindOrder);
        _executeVTable = Sig.VTable(Signatures.ExecuteCommand);
        _post = Patches.Hook<PostFn>(Sig.Find(Signatures.PostAICommand), (cmd, tag) =>
        {
            if (*(nint*)cmd != _executeVTable || *(byte*)(cmd + ExecuteOn) == 0 || *(int*)tag == Game.ControlledTag || !SheepMod.Active || Ready(cmd, tag))
                return _post!.OriginalFunction(cmd, tag);
            ((delegate* unmanaged<nint, int, void>)(*(nint**)cmd)[0])(cmd, 1); // deleted, as the game does with an invalid command
            Interlocked.Increment(ref _held);
            return 0;
        });
    }

    /// <summary>Whether the army (and an army group's armies) of an "attack on" command is fit to attack. Read only: AI threads.</summary>
    private bool Ready(nint cmd, nint tag)
    {
        var reference = (int*)(cmd + GroupOfCommand);
        if (reference[0] == 0 && reference[1] == 0) return true;
        var group = _resolve((nint)reference);
        if (group == 0) return true;
        var id = *(int*)(cmd + OrderOfCommand);
        if (id != 0)
        {
            var order = _findOrder(group, id);
            if (order == 0 || *(byte*)(order + Executing) != 0 || *(int*)(order + TypeOfOrder) is Invasion or Paradrop) return true; // running, or at sea
        }
        List<nint> groups = [group];
        if (new OrdersGroup(group).IsArmyGroup) groups.AddRange(new OrdersGroup(group).Armies.ToArray());

        long count = 0, org = 0, strength = 0, supply = 0, weak = 0;
        foreach (var g in groups)
        foreach (var member in Native.PdxArray(g + OrdersGroup.MembersOffset))
        {
            var unit = ((delegate* unmanaged<nint, nint>)(*(nint**)member)[UnitOfMemberSlot])(member);
            if (unit == 0) continue;
            var o = Ratio(unit, OrgRatioSlot);
            count++;
            org += o;
            strength += Ratio(unit, StrengthRatioSlot);
            supply += *(long*)(unit + SupplyOfUnit);
            if (o < WeakOrg) weak++;
        }
        return count == 0 || org / count >= _minOrg && strength / count >= _minStrength && supply / count >= _minSupply && weak * 100 <= count * MaxWeakPercent
            && (!_holdLine || Manned(*(int*)tag, LineOf(*(int*)tag, Hour))); // a thin line doesn't advance: attacking widens it, the holes get the spearhead cut off
    }
}
