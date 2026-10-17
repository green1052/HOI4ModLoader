using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Navy: idle task forces train at peace and bombard coastal battles at war ----

    private const int FleetsOfCountry = 0x278, TaskForcesOfFleet = 0xB8, HomeOfFleet = 0xD8;  // CCountry: CPdxArray<CFleet*>; CFleet: CPdxArray<CTaskForce*>, home base
    private const int RefOfTaskForce = 0x18, CombatOfTaskForce = 0x1B4, LocationOfTaskForce = 0x1F0;
    private const int FleetOfTaskForce = 0x340, MissionOfTaskForce = 0x374, ParentOfTaskForce = 0x4B0, ActivityOfTaskForce = 0x4B8;
    private const int HoldMission = 0, NavalTrainingMission = 7, ReserveFleetMission = 8;   // NNavalMission::EMissionType
    private const int NavalCommandSize = 0x48, StopAtMaxOfNavalCommand = 0x44;              // CNavalMissionSetTypeCommand, CNavalMissionMoveCommand
    private const int MaxBombardSteps = 6;                                                   // reach, in sea provinces
    private const int BombardStayHours = 7 * 24;  // stays at a battle that long: battles flicker hourly
    private const int BombardWaitHours = 14 * 24; // with no battle in reach, waits on hold at sea that long instead of going to port

    [Function(CallingConventions.Microsoft)] public delegate void UnassignedTaskForcesFn(nint slots, nint tag);
    [Function(CallingConventions.Microsoft)] public delegate int FilterTaskForcesFn(nint list, nint unused);

    private const int ObjectiveOfSlot = 0x18, AirBaseOfShip = 0x730, OwnerOfTaskForce = 0x1D8, ControllerOfTaskForce = 0x1DC, ResolvedTaskForce = 0x10;
    private const int PatrolMission = 1, StrikeForceMission = 2;
    private IHook<FilterTaskForcesFn>? _filterTaskForces;

    private IHook<UnassignedTaskForcesFn>? _unassignedTaskForces;
    private static delegate* unmanaged<nint, nint, int, nint> _setNavalMission;
    private static delegate* unmanaged<nint, nint, int, byte, nint> _navalMove;
    private static delegate* unmanaged<nint, byte> _canTrainNavy;
    private static delegate* unmanaged<nint, long*, long*> _shoreBombardment, _shipBombardment;
    private static delegate* unmanaged<nint, nint, nint, byte, int, byte, nint> _moveShips;
    private const int ShipsOfTaskForce = 0x348, MoveShipsSize = 0x58;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Tag, int Sea), int> Claimed = new(); // sea a split-off half is meant for -> hour
    private static int _fleetsSplit;
    // task force -> sea it was sent to, hour, tag (each country covers its own battles)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Province, int Hour, int Tag)> Bombarding = new();
    private const int MinSplitShips = 4; // each half keeps two ships at least
    private static int _fleetsTraining, _fleetsBombarding;
    private static bool _navalTraining, _shoreSupport;

    private void LoadNavy(bool training, bool bombard)
    {
        (_navalTraining, _shoreSupport) = (training, bombard);
        _setNavalMission = (delegate* unmanaged<nint, nint, int, nint>)Sig.Find(Signatures.SetNavalMission);
        _navalMove = (delegate* unmanaged<nint, nint, int, byte, nint>)Sig.Find(Signatures.NavalMove);
        _canTrainNavy = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.TaskForceCanTrain);
        _shoreBombardment = (delegate* unmanaged<nint, long*, long*>)Sig.Find(Signatures.TaskForceShoreBombardment);
        _shipBombardment = (delegate* unmanaged<nint, long*, long*>)Sig.Find(Signatures.ShipShoreBombardment);
        _moveShips = (delegate* unmanaged<nint, nint, nint, byte, int, byte, nint>)Sig.Find(Signatures.MoveShips);
        _atWarAny = (delegate* unmanaged<nint, int, byte>)Sig.Find(Signatures.AtWarAny);
        _postAi = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.PostAiCommand);
        Allocator.Free(Allocator.AllocZeroed(NavalCommandSize)); // resolve the heap functions here, not on AI threads
        if (bombard)
            _filterTaskForces = Patches.Hook<FilterTaskForcesFn>(Sig.Find(Signatures.FilterTaskForces), (list, unused) =>
            {
                var removed = _filterTaskForces!.OriginalFunction(list, unused);
                Safely(() => TakeForShore(list));
                return removed;
            });
        _unassignedTaskForces = Patches.Hook<UnassignedTaskForcesFn>(Sig.Find(Signatures.UnassignedTaskForces), (slots, tag) =>
        {
            Safely(() => Navy(slots, *(int*)tag));
            _unassignedTaskForces!.OriginalFunction(slots, tag);
        });
    }

    /// <summary>
    /// Task forces no naval objective took, before vanilla sends them to port. At peace, those that can still gain
    /// experience train; at war, those with guns or carriers go bombard a coastal battle. Taken ones leave the list.
    /// AI worker thread.
    /// </summary>
    private static void Navy(nint slots, int tag)
    {
        if (tag <= 0 || tag == Game.ControlledTag || *(int*)(slots + 0xC) == 0) return;
        var country = Game.Country(tag);
        var diplomacy = country == 0 ? 0 : *(nint*)(country + DiplomacyOfCountry);
        if (diplomacy == 0) return;
        var atWar = _atWarAny(diplomacy, 0) != 0;
        if (atWar ? !_shoreSupport : !_navalTraining) return;
        if (!atWar && WarComing(diplomacy)) return; // training ships have low organisation
        var byRef = TaskForcesByRef(country);
        var targets = atWar ? BombardTargets(country, tag) : [];
        var data = *(nint**)slots;
        var count = *(int*)(slots + 0xC);
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var slot = data[i];
            var tf = slot == 0 ? 0 : byRef.GetValueOrDefault(*(long*)slot);
            if (tf == 0 || !Idle(tf) || !(atWar ? Bombard(tf, tag, targets) : TrainNavy(tf, tag))) data[kept++] = slot;
        }
        *(int*)(slots + 0xC) = kept;
    }

    /// <summary>
    /// At war, before the naval objectives take task forces: one with guns or carriers per uncovered coastal battle of ours is
    /// taken off patrol, strike force, hold or training (never invasion support, convoys, raiding or mines) and sent to bombard.
    /// Ones already bombarding stay ours. Taken ones leave the list. AI worker thread.
    /// </summary>
    // ponytail: no look at enemy fleets; add the strategic navy's region danger if battle fleets get caught off the coast.
    private static void TakeForShore(nint list)
    {
        var count = *(int*)(list + 0xC);
        if (count == 0) return;
        var data = *(nint**)list;
        var tag = 0;
        for (var i = 0; i < count && tag == 0; i++)
            if (data[i] != 0 && Game.Resolve(*(long*)data[i]) is var r && r != 0 && *(long*)(r - ResolvedTaskForce + RefOfTaskForce) == *(long*)data[i])
                tag = *(int*)(r - ResolvedTaskForce + ControllerOfTaskForce) is > 0 and var c ? c : *(int*)(r - ResolvedTaskForce + OwnerOfTaskForce);
        if (tag <= 0 || tag == Game.ControlledTag) return;
        var country = Game.Country(tag);
        var diplomacy = country == 0 ? 0 : *(nint*)(country + DiplomacyOfCountry);
        if (diplomacy == 0 || _atWarAny(diplomacy, 0) == 0) return;
        var byRef = TaskForcesByRef(country);
        var targets = BombardTargets(country, tag);
        var hour = Game.Hour;
        foreach (var (tf, sent) in Bombarding) if (hour - sent.Hour is < 0 or >= BombardWaitHours) Bombarding.TryRemove(tf, out _); // sunk, merged, done
        foreach (var (key, at) in Claimed) if (hour - at is < 0 or >= BombardStayHours) Claimed.TryRemove(key, out _);
        var open = targets.Count(t => !Covered(tag, t.Sea, hour));
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var slot = data[i];
            var tf = slot == 0 ? 0 : byRef.GetValueOrDefault(*(long*)slot);
            var mission = tf == 0 ? -1 : *(int*)(tf + MissionOfTaskForce);
            var free = tf != 0 && Idle(tf) && mission is HoldMission or PatrolMission or StrikeForceMission or NavalTrainingMission;
            var ours = tf != 0 && Bombarding.TryGetValue(tf, out var sent) && hour - sent.Hour is >= 0 and < BombardWaitHours;
            var take = false;
            if (free && ours) take = Bombard(tf, tag, targets);
            else if (free && open > 0 && Bombard(tf, tag, targets))
            {
                take = true;
                open--;
            }
            if (!take && ours) Bombarding.TryRemove(tf, out _); // back to the naval AI
            if (take) *(nint*)(slot + ObjectiveOfSlot) = 0;
            else data[kept++] = slot;
        }
        *(int*)(list + 0xC) = kept;
    }

    /// <summary>The country's task forces by CRef id (the naval AI's slots hold CRefs).</summary>
    private static Dictionary<long, nint> TaskForcesByRef(nint country)
    {
        var byRef = new Dictionary<long, nint>();
        foreach (var fleet in Native.PdxArray(country + FleetsOfCountry))
            if (fleet != 0)
                foreach (var tf in Native.PdxArray(fleet + TaskForcesOfFleet))
                    if (tf != 0) byRef[*(long*)(tf + RefOfTaskForce)] = tf;
        return byRef;
    }

    /// <summary>Has a carrier (a ship with an air base).</summary>
    private static bool HasCarrier(nint tf)
    {
        foreach (var ship in Native.PdxArray(tf + ShipsOfTaskForce))
            if (ship != 0 && *(nint*)(ship + AirBaseOfShip) != 0) return true;
        return false;
    }

    /// <summary>Not in a reserve fleet, not detached, out of combat and not repairing or refitting.</summary>
    private static bool Idle(nint tf) =>
        *(int*)(tf + MissionOfTaskForce) != ReserveFleetMission && *(long*)(tf + ParentOfTaskForce) == 0
        && *(int*)(tf + CombatOfTaskForce) <= 0 && *(int*)(tf + ActivityOfTaskForce) == 0;

    /// <summary>Trains a task force that can still gain experience, until its ships are regulars. Whether taken.</summary>
    private static bool TrainNavy(nint tf, int tag)
    {
        var fleet = *(nint*)(tf + FleetOfTaskForce);
        if (fleet == 0 || *(nint*)(fleet + HomeOfFleet) == 0 || _canTrainNavy(tf) == 0) return false;
        if (*(int*)(tf + MissionOfTaskForce) == NavalTrainingMission) return true;
        var cmd = Allocator.AllocZeroed(NavalCommandSize);
        _setNavalMission(cmd, tf, NavalTrainingMission);
        *(byte*)(cmd + StopAtMaxOfNavalCommand) = 1; // as vanilla's training objective sends it
        if (!Post(cmd, tag)) return false;
        Interlocked.Increment(ref _fleetsTraining);
        return true;
    }

    /// <summary>One of the tag's task forces was sent to the sea within BombardStayHours.</summary>
    private static bool Covered(int tag, int sea, int hour) =>
        Bombarding.Any(b => b.Value.Tag == tag && b.Value.Province == sea && hour - b.Value.Hour is >= 0 and < BombardStayHours);

    private const int CombatsOfGameState = 0x268, ProvinceOfCombat = 0x38; // CPdxArray<CCombat*>; defender's province
    [VTable] public const string LandCombat = "CLandCombat";

    /// <summary>
    /// Sea provinces next to our land battles (the defender's province; only it gets bombarded), weighted by our fighting
    /// divisions in or next to the battle, heaviest first.
    /// </summary>
    private static List<(int Sea, int Weight)> BombardTargets(nint country, int tag)
    {
        var mine = new Dictionary<nint, int>(); // our fighting divisions by province
        foreach (var unit in Game.Units(country))
            if (unit != 0 && Division.IsLand(unit) && Division.Fighting(unit) && Division.Location(unit) is var at && at != 0)
                mine[at] = mine.GetValueOrDefault(at) + 1;
        var weight = new Dictionary<int, int>();
        if (mine.Count == 0) return [];
        var landCombat = Sig.VTable(LandCombat);
        foreach (var combat in Native.PdxArray(Game.State + CombatsOfGameState).ToArray())
        {
            if (combat == 0 || *(nint*)combat != landCombat || *(nint*)(combat + ProvinceOfCombat) is not (var battle and not 0)) continue;
            var ours = mine.GetValueOrDefault(battle) // defending
                + (Province.Enemy(country, tag, battle) ? Game.Neighbours(battle).Sum(id => mine.GetValueOrDefault(Game.Province(id))) : 0); // attacking
            if (ours == 0) continue;
            foreach (var id in Game.Neighbours(battle))
                if (Game.Province(id) is var sea && sea != 0 && Province.Controller(sea) == 0 && !LandRegion(*(nint*)(sea + RegionOfProvince)))
                    weight[id] = weight.GetValueOrDefault(id) + ours;
        }
        return weight.Select(kv => (kv.Key, kv.Value)).OrderByDescending(t => t.Value).ToList();
    }

    // ponytail: no look at enemy fleets on the way or at the target; add the strategic navy's region danger if fleets sail into them.
    /// <summary>
    /// Sends a task force with guns or carriers on hold (only ships on hold bombard) to the nearest uncovered battle sea in
    /// reach; split in two when a second battle has none. One already there or on its way stays. Whether taken.
    /// </summary>
    private static bool Bombard(nint tf, int tag, List<(int Sea, int Weight)> targets)
    {
        var hour = Game.Hour;
        var on = *(int*)(tf + MissionOfTaskForce) == HoldMission;
        var ours = Bombarding.TryGetValue(tf, out var sent) && hour - sent.Hour >= 0;
        // Of several at one battle, only one stays while another battle has none.
        var doubled = ours && Bombarding.Any(b => b.Key < tf && b.Value.Tag == tag && b.Value.Province == sent.Province && hour - b.Value.Hour is >= 0 and < BombardStayHours);
        if (on && ours && hour - sent.Hour < BombardStayHours && !(doubled && targets.Any(t => t.Sea != sent.Province
            && !Covered(tag, t.Sea, hour)))) return true;
        if (targets.Count == 0) return on && ours && hour - sent.Hour < BombardWaitHours; // battles pause: it waits at sea, not in port
        long guns;
        _shoreBombardment(tf, &guns);
        if (guns <= 0 && !HasCarrier(tf)) return false; // a carrier's wings reach the battle from there
        var here = *(nint*)(tf + LocationOfTaskForce);
        if (here == 0) return false;
        if (on && targets.Any(t => t.Sea == Game.Id(here)))
        {
            Bombarding[tf] = (Game.Id(here), hour, tag); // the wait for the next battle counts from now
            return true;
        }
        var steps = SeaSteps(here, MaxBombardSteps);
        var covered = Bombarding.Where(b => b.Key != tf && b.Value.Tag == tag && hour - b.Value.Hour is >= 0 and < BombardStayHours).Select(b => b.Value.Province).ToHashSet();
        var reach = targets.Where(t => steps.ContainsKey(t.Sea)).OrderBy(t => covered.Contains(t.Sea)).ThenBy(t => steps[t.Sea]).ToList(); // uncovered first, then nearest
        var sea = reach.FirstOrDefault().Sea;
        if (sea == 0) return on && ours && hour - sent.Hour < BombardWaitHours;
        // Check the move first: commands run later on the main thread, and a refused move must not leave a hold or split behind.
        var move = Allocator.AllocZeroed(NavalCommandSize);
        _navalMove(move, tf, sea, 0);
        if (!Commands.CanExecute(move))
        {
            Commands.Free(move);
            return false;
        }
        if (!on)
        {
            var hold = Allocator.AllocZeroed(NavalCommandSize);
            _setNavalMission(hold, tf, HoldMission);
            if (!Post(hold, tag))
            {
                Commands.Free(move);
                return false;
            }
        }
        var other = reach.FirstOrDefault(t => t.Sea != sea && !covered.Contains(t.Sea) && !(Claimed.TryGetValue((tag, t.Sea), out var c) && hour - c is >= 0 and < BombardStayHours)).Sea;
        if (other != 0 && Split(tf, tag)) Claimed[(tag, other)] = hour; // the half left behind takes that battle next pass
        if (!Post(move, tag)) return false;
        Bombarding[tf] = (sea, hour, tag);
        Interlocked.Increment(ref _fleetsBombarding);
        return true;
    }

    /// <summary>
    /// Splits off half the ships (dealt alternately, strongest bombardment first) as a new task force where it is.
    /// Whether the command went out.
    /// </summary>
    private static bool Split(nint tf, int tag)
    {
        var ships = Native.PdxArray(tf + ShipsOfTaskForce).ToArray().Where(s => s != 0).ToList();
        if (ships.Count < MinSplitShips) return false;
        var half = ships.OrderByDescending(s => { long v; _shipBombardment(s, &v); return v; }).Where((_, i) => i % 2 == 1).ToArray();
        var cmd = Allocator.AllocZeroed(MoveShipsSize);
        Native.WithPdxArray(half, list => _moveShips(cmd, list, 0, 1, -1, 0));
        if (!Post(cmd, tag)) return false;
        Interlocked.Increment(ref _fleetsSplit);
        return true;
    }

    /// <summary>Sea provinces within <paramref name="max"/> steps of <paramref name="from"/> by sea, with their step count.</summary>
    private static Dictionary<int, int> SeaSteps(nint from, int max)
    {
        var steps = new Dictionary<int, int> { [Game.Id(from)] = 0 };
        var ring = new List<nint> { from };
        for (var step = 1; step <= max && ring.Count > 0; step++)
        {
            var next = new List<nint>();
            foreach (var p in ring)
            foreach (var id in Game.Neighbours(p))
                if (!steps.ContainsKey(id) && Game.Province(id) is var q && q != 0 && Province.Controller(q) == 0 && !LandRegion(*(nint*)(q + RegionOfProvince)))
                {
                    steps[id] = step;
                    next.Add(q);
                }
            ring = next;
        }
        return steps;
    }

    /// <summary>Posts an AI command if it can run, else frees it. Whether it went out.</summary>
    private static bool Post(nint cmd, int tag)
    {
        if (!Commands.CanExecute(cmd))
        {
            Commands.Free(cmd);
            return false;
        }
        return _postAi(cmd, &tag) != 0; // deleted by PostAICommand
    }
}
