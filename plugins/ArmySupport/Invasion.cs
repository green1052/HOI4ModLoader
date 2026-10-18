using HOI4ModLoader;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>CNavalMissionSetTypeCommand ctor (mem[0x48], CTaskForce*, int mission).</summary>
    [Signature] public const string SetNavalMission = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 41 8B D8 48 8B F2 48 8B F9 C6 41 ? ? C7 41 ? ? ? ? ? 33 C9 89 4F ? C7 47 ? ? ? ? ? 66 89 4F ? 48 89 4F ? 48 8D 05 ? ? ? ? 48 89 07 48 8D 4F ? E8 ? ? ? ? 90 89 5F ? 66 C7 47";

    /// <summary>Adds a strategic region (+0x30: its id) to a task force's mission (+0x28: its CRef, the task force's +0x08).</summary>
    [VTable] public const string AddNavalRegion = "CNavalMissionAddRegionCommand";
}

/// <summary>
/// Auto invasion support: a naval invasion of the player's with no task force on naval invasion support gets the idle
/// ones (holding, not in combat) on that mission over the sea regions next to its port and its beaches. Vanilla commands,
/// so multiplayer works. Several beaches per invasion are vanilla: click more of them.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int InvasionOrder = 3, RegionsOfGameState = 0x2E0;
    private const int FleetsOfCountry = 0x278, TaskForcesOfFleet = 0xB8, CombatOfTaskForce = 0x1B4, MissionOfTaskForce = 0x374;
    private const int ParentOfTaskForce = 0x4B0, ActivityOfTaskForce = 0x4B8;
    private const int HoldMission = 0, InvasionSupportMission = 9, NavalCommandSize = 0x48, AddRegionSize = 0x38, CRefOfTaskForce = 0x08;

    private delegate* unmanaged<nint, nint, int, nint> _setNavalMission;
    private readonly HashSet<int> _supported = []; // invasion order ids given task forces
    private PerGame _invasionGame = null!;

    private void LoadInvasionSupport()
    {
        _setNavalMission = (delegate* unmanaged<nint, nint, int, nint>)Sig.Find(Signatures.SetNavalMission);
        _invasionGame = new PerGame(_supported.Clear);
        MainThread.Every(TimeSpan.FromSeconds(10), () => Log.Guard("invasion support", SupportInvasions));
    }

    private void SupportInvasions()
    {
        var tag = Game.ControlledTag;
        var country = Game.HasStarted ? Game.Country(tag) : 0;
        if (country == 0) return;
        _invasionGame.Check();
        var taskForces = new List<nint>();
        foreach (var fleet in Native.PdxArray(country + FleetsOfCountry))
            if (fleet != 0)
                foreach (var tf in Native.PdxArray(fleet + TaskForcesOfFleet))
                    if (tf != 0) taskForces.Add(tf);
        if (taskForces.Any(tf => *(int*)(tf + MissionOfTaskForce) == InvasionSupportMission)) return; // already covered by the player

        var groups = new HashSet<nint>();
        foreach (var unit in Game.Units(country))
            if (Division.Group(unit) is var group && group != 0) groups.Add(group);
        foreach (var group in groups)
        foreach (var order in Native.PdxArray(group + InvasionOrders))
        {
            if (order == 0 || Order.Type(order) != InvasionOrder || *(int*)(order + SourceOfOrder) is var source && source <= 0) continue;
            var beaches = Native.PdxArray<int>(order + TargetsOfOrder);
            if (beaches.Length == 0 || _supported.Contains(Order.Id(order))) continue;
            // ponytail: the seas next to the port and the beaches, not the whole route; the route is sub_14103CA80's path.
            var regions = new HashSet<int>();
            foreach (var id in beaches.ToArray().Append(source)) SeaRegionsNext(id, regions);
            var idle = taskForces.Where(tf => *(int*)(tf + MissionOfTaskForce) == HoldMission && *(long*)(tf + ParentOfTaskForce) == 0
                                               && *(int*)(tf + CombatOfTaskForce) <= 0 && *(int*)(tf + ActivityOfTaskForce) == 0).ToList();
            if (regions.Count == 0 || idle.Count == 0) continue;
            foreach (var tf in idle)
            {
                var cmd = Allocator.AllocZeroed(NavalCommandSize);
                _setNavalMission(cmd, tf, InvasionSupportMission);
                if (!Commands.TryPost(cmd)) continue;
                foreach (var region in regions)
                {
                    var add = Commands.New(Signatures.AddNavalRegion, AddRegionSize, 0, 0); // as its ctor fills it
                    *(long*)(add + 0x28) = *(long*)(tf + CRefOfTaskForce);
                    *(int*)(add + 0x30) = region;
                    Commands.TryPost(add);
                }
            }
            _supported.Add(Order.Id(order));
            Log.Info($"invasion support: {idle.Count} task force(s) over {regions.Count} sea region(s) for the landing from province {source}");
            return; // the next invasion once these are busy
        }
    }

    /// <summary>Adds the strategic region ids of the sea provinces next to the province.</summary>
    private static void SeaRegionsNext(int province, HashSet<int> regions)
    {
        var p = Game.Province(province);
        var all = Native.PdxArray(Game.State + RegionsOfGameState);
        if (p == 0) return;
        foreach (var n in Game.Neighbours(p))
            if (Game.Province(n) is var q && q != 0 && (Flags(q) & LandFlag) == 0 && *(nint*)(q + RegionOfProvince) is var region && region != 0)
                if (all.IndexOf(region) is var id and >= 0) regions.Add(id);
    }
}
