using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Army training: AI armies with nothing to do train their divisions up to regulars ----

    private const int TrainingOfGroup = 0x19D;                           // COrdersGroup: training on
    private const int TrainingOfCommand = 0x30, StopAtMaxOfCommand = 0x31; // COrderSetTrainingCommand
    private const long MinTrainingStrength = Native.FixedPoint * 7 / 10;
    private const int WarSignalsOfDiplomacy = 0x158; // CPdxArray<int>: why the AI expects a war
    private const int WarPrepStrategy = 4, FocusTargetingThem = 9, FocusTargetingUs = 10;

    /// <summary>The AI expects a war: a war preparation strategy, or a focus aimed at or by another country.</summary>
    private static bool WarComing(nint diplomacy)
    {
        foreach (var reason in Native.PdxArray<int>(diplomacy + WarSignalsOfDiplomacy))
            if (reason is WarPrepStrategy or FocusTargetingThem or FocusTargetingUs) return true;
        return false;
    }

    [Function(CallingConventions.Microsoft)] public delegate void UpdateArmyTrainingFn(nint country, byte haltAll, nint fronts);

    private IHook<UpdateArmyTrainingFn>? _updateArmyTraining;
    private static delegate* unmanaged<nint, byte> _belowMaxLevel;
    private static int _armiesTraining, _armiesStopped;

    private void LoadArmyTraining()
    {
        _belowMaxLevel = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.BelowMaxLevel);
        _atWarAny = (delegate* unmanaged<nint, int, byte>)Sig.Find(Signatures.AtWarAny);
        _postAi = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.PostAiCommand);
        _ = Sig.VTable(Signatures.SetTrainingCommand); // the class and the heap functions are found here, not on AI threads
        Allocator.Resolve();
        _updateArmyTraining = Patches.Hook<UpdateArmyTrainingFn>(Sig.Find(Signatures.UpdateArmyTraining), (country, haltAll, fronts) =>
        {
            var done = false;
            Safely(() => done = TrainArmies(country));
            if (!done) _updateArmyTraining!.OriginalFunction(country, haltAll, fronts);
        });
    }

    /// <summary>
    /// Replaces the AI's daily army training pass. An army trains while a division is below regular and none fights, stands
    /// next to enemy ground or is under 70 % strength; at war only if it holds no front. None while <see cref="WarComing"/>.
    /// Training stops at regular by itself. AI worker thread.
    /// </summary>
    private static bool TrainArmies(nint country)
    {
        var tag = Game.Tag(country);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        var diplomacy = *(nint*)(country + DiplomacyOfCountry);
        if (diplomacy == 0) return false;
        var atWar = _atWarAny(diplomacy, 0) != 0;
        var coming = WarComing(diplomacy); // training keeps organisation low
        var divisions = new Dictionary<nint, List<nint>>();
        foreach (var unit in Game.Units(country))
            if (unit != 0 && Division.IsLand(unit) && Division.Group(unit) is var g && g != 0)
            {
                if (!divisions.TryGetValue(g, out var list)) divisions[g] = list = [];
                list.Add(unit);
            }
        foreach (var (group, units) in divisions)
        {
            if (new OrdersGroup(group).IsArmyGroup) continue;
            var training = *(byte*)(group + TrainingOfGroup) != 0;
            var want = !coming && _belowMaxLevel(group) != 0
                && !(atWar && (Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray() is var orders
                    && (orders.All(o => o == 0) || orders.Any(o => o != 0 && Order.Type(o) == Order.Front)))) // at war: no orders or a front order
                && units.All(u => !Division.Fighting(u) && Division.Strength(u) >= MinTrainingStrength
                    && Division.Location(u) is var at && at != 0 && !Province.NextToEnemy(country, tag, at));
            if (want == training) continue;
            var cmd = Commands.NewOrderCommand(Signatures.SetTrainingCommand, new OrdersGroup(group).Ref, 0, 0);
            *(byte*)(cmd + TrainingOfCommand) = want ? (byte)1 : (byte)0;
            *(byte*)(cmd + StopAtMaxOfCommand) = want ? (byte)1 : (byte)0;
            if (!Post(cmd, *(int*)(group + TagOfGroup))) continue;
            Interlocked.Increment(ref want ? ref _armiesTraining : ref _armiesStopped);
        }
        return true;
    }
}
