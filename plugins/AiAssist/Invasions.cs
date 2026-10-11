using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Strategic invasions: beaches scored by what is there; a launch waits while its beach is crowded ----

    // CalcNavalInvasionTarget's parallel_for closure: +0 bool* found (the current target is still a candidate: kept, no pick),
    // +8 bool* takeFirst (the feasibility check: the first candidate wins), +0x10 CPdxArray<CProvince*>* candidates, +0x48 CCountry*,
    // +0x60 SRWLOCK*, +0x68 CPdxArray<long>* scores (same index as the candidates).
    private const int FoundOfClosure = 0, TakeFirstOfClosure = 8, CandidatesOfClosure = 0x10, CountryOfClosure = 0x48, LockOfClosure = 0x60, ScoresOfClosure = 0x68;
    private const int TargetsOfInvasion = 0x70, BeachHours = 24; // COrder: CPdxArray<int> beach ids
    private const int BeachPercent = 50, BeachHoldHours = 7 * 24; // a launch waits while enemy divisions around a beach outnumber half the landing, a week at most

    [Function(CallingConventions.Microsoft)] public delegate void CandidateFn(nint closure, nint province);

    private IHook<CandidateFn>? _candidate;
    private static delegate* unmanaged<nint, nint> _bunkerType;
    private static bool _strategicInvasions;
    private static int _rescored, _landingsHeld;
    // Ids and numbers only: bounded by tags x coast, overwritten daily.
    private static readonly ConcurrentDictionary<(int Tag, int Province), (int Hour, double Factor)> Beaches = new();
    // Invasion group -> the hour its launch was first held. Never dereferenced; a reused pointer carries another tag or an old hour.
    private static readonly ConcurrentDictionary<nint, (int Hour, int Tag)> Holds = new();

    [DllImport("kernel32")] private static extern void AcquireSRWLockExclusive(nint srw);
    [DllImport("kernel32")] private static extern void ReleaseSRWLockExclusive(nint srw);

    private void LoadInvasions()
    {
        _bunkerType = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.CoastalBunkerType);
        _candidate = Patches.Hook<CandidateFn>(Sig.Find(Signatures.InvasionTargetCandidate), (closure, province) =>
        {
            _candidate!.OriginalFunction(closure, province);
            if (!SheepMod.Active) return;
            try { Rescore(closure, province); } // no closure: once per candidate beach
            catch (Exception e) { Failed(e); }
        });
        _supportValid = Patches.Hook<ObjectiveFn>(Sig.Find(Signatures.InvasionSupportValid), objective =>
        {
            var valid = _supportValid!.OriginalFunction(objective);
            if (valid == 0 || !SheepMod.Active) return valid;
            try { TrackRoute(objective); }
            catch (Exception e) { Failed(e); }
            return valid;
        });
    }

    // ---- Invasion escort: fighters (and naval bombers) over the sea regions an AI landing has to sail through ----
    //
    // Vanilla already does the naval side: a landing only sails once its country (with allies) dominates every strategic sea region
    // on its route (COrder::CanExecute 0x14103CA80 -> 0x140EA1250 -> 0x141003F50, DOMINANCE_CONTROLLED_THRESHOLD_RATIO; the AI's
    // launch reads it cached at COrder+0x11A, written by 0x14102BB10), and the naval AI's CInvasionSupportObjective collects those
    // regions (0x1420141F0) for its fleets, at the priority common/ai_navy/goals gives naval_invasion_support (mod-able).
    // NAI.PATROL_FLEETS_PER_INVASION_REGION_ON_PATH is read by nothing but its loader. The air AI never looks at the route: a sea
    // region's fighters follow the enemy planes seen there (NAVAL_FIGHTERS_PER_PLANE). So the route is taken from the support objective.

    // CInvasionSupportObjective: +8 CCountry*, +0x10 CPdxArray<CStrategicRegion*> the route's regions (from the path's sea provinces).
    private const int CountryOfObjective = 0x08, RegionsOfObjective = 0x10;
    private const int RouteHours = 72, NavalStrike = 4, EscortPercent = 200, EscortFighters = 100; // planes

    [Function(CallingConventions.Microsoft)] public delegate byte ObjectiveFn(nint objective);

    private IHook<ObjectiveFn>? _supportValid;
    private static int _escorted;
    // (tag, objective) -> when last seen alive, its route's region ids. Never dereferenced; stale ones expire.
    private static readonly ConcurrentDictionary<(int Tag, nint Objective), (int Hour, int[] Regions)> Routes = new();

    /// <summary>Records a live invasion support objective's route (region ids), once an hour. Naval AI worker threads: read only.</summary>
    private static void TrackRoute(nint objective)
    {
        var country = *(nint*)(objective + CountryOfObjective);
        var tag = country == 0 ? 0 : *(int*)(country + 8);
        if (tag <= 0 || tag == Game.ControlledTag) return;
        var hour = Hour;
        if (Routes.TryGetValue((tag, objective), out var kept))
        {
            if (kept.Hour == hour) return;
        }
        else
            foreach (var (key, route) in Routes)
                if (hour - route.Hour is < 0 or >= RouteHours) Routes.TryRemove(key, out _); // a new objective: drop the dead ones
        var regions = Native.PdxArray(objective + RegionsOfObjective);
        var ids = new int[regions.Length];
        for (var i = 0; i < ids.Length; i++) ids[i] = regions[i] == 0 ? 0 : *(int*)(regions[i] + SuperiorityIdOfRegion);
        Routes[(tag, objective)] = (hour, ids);
    }

    /// <summary>
    /// The sea regions on the routes of the country's planned landings, where the air AI scored them at all: twice the fighters vanilla
    /// asks for (a hundred at least) and twice the naval bombers (those follow enemy ships). Scores are never raised, as with bombing
    /// targets: only how many planes a region the allocator already serves wants. Writes only this country's own entries: AI worker threads.
    /// </summary>
    private static void EscortInvasions(nint country, nint entries, nint totals)
    {
        var tag = *(int*)(country + 8);
        if (tag <= 0 || tag == Game.ControlledTag) return; // no IsEmpty: it takes every lock
        var data = *(nint*)entries;
        var n = *(int*)(entries + 0xC);
        if (data == 0 || n <= 0) return;
        Span<byte> done = n <= 4096 ? stackalloc byte[n] : new byte[n]; // a region on two routes is raised once
        done.Clear();
        var hour = Hour;
        long fighters = 0, bombers = 0;
        foreach (var (key, route) in Routes)
        {
            if (key.Tag != tag || hour - route.Hour is < 0 or >= RouteHours) continue;
            foreach (var id in route.Regions)
            {
                var i = id - 1; // entry 0 is region 1
                if ((uint)i >= (uint)n || done[i] != 0) continue;
                done[i] = 1;
                var entry = data + (nint)i * RegionEntrySize;
                var region = *(nint*)(entry + RegionOfEntry);
                if (region == 0 || LandRegion(region) || *(long*)(entry + ScoreOfEntry) <= 0) continue;
                var fighter = (long*)(entry + WishesOfEntry);
                var raised = Math.Max(*fighter * EscortPercent / 100, EscortFighters * Native.FixedPoint);
                fighters += raised - *fighter;
                *fighter = raised;
                var strike = (long*)(entry + WishesOfEntry + 8 * NavalStrike);
                bombers += *strike * (EscortPercent - 100) / 100;
                *strike = *strike * EscortPercent / 100;
            }
        }
        if (fighters == 0 && bombers == 0) return;
        if (totals != 0)
        {
            *(long*)totals += fighters; // the per-mission sums URP already made
            *(long*)(totals + 8 * NavalStrike) += bombers;
        }
        Interlocked.Increment(ref _escorted);
    }

    /// <summary>Scales a kept candidate's vanilla score by its beach factor. TBB workers: game state read only, the score under the scorer's lock.</summary>
    private static void Rescore(nint closure, nint province)
    {
        var found = *(byte**)(closure + FoundOfClosure);
        var takeFirst = *(byte**)(closure + TakeFirstOfClosure);
        if (found == null || takeFirst == null || *found != 0 || *takeFirst != 0) return; // no pick follows: the current target is kept, or the first one wins
        var country = *(nint*)(closure + CountryOfClosure);
        var tag = country == 0 ? 0 : *(int*)(country + 8);
        if (tag <= 0 || tag == Game.ControlledTag || Scale(closure, province, 1) < 0) return; // not kept: no path, not at war, excluded
        var factor = BeachFactor(country, tag, province);
        if (factor != 1 && Scale(closure, province, factor) >= 0) Interlocked.Increment(ref _rescored);
    }

    /// <summary>Multiplies the candidate's score by <paramref name="factor"/> under the closure's SRWLOCK (vanilla's append holds it too); its index, or -1.</summary>
    private static int Scale(nint closure, nint province, double factor)
    {
        var srw = *(nint*)(closure + LockOfClosure);
        nint candidates = *(nint*)(closure + CandidatesOfClosure), scores = *(nint*)(closure + ScoresOfClosure);
        if (srw == 0 || candidates == 0 || scores == 0) return -1;
        AcquireSRWLockExclusive(srw);
        try
        {
            var i = Native.PdxArray(candidates).IndexOf(province);
            var score = Native.PdxArray<long>(scores);
            if (i < 0 || i >= score.Length) return -1;
            if (factor != 1) score[i] = Math.Max(1, (long)(score[i] * factor)); // > 0: the pick wants a positive weight
            return i;
        }
        finally { ReleaseSRWLockExclusive(srw); }
    }

    /// <summary>
    /// How much better than vanilla's distance/port score the beach is: worth (victory points, capital, supply hub, airfields, resources
    /// on it or next to it) up; enemy divisions within two rings and the beach's land forts and coastal bunkers down. A day per beach.
    /// </summary>
    // ponytail: no "behind the front" term; the ring garrison already sinks beaches next to the enemy's front. Add one if logs show front-hugging landings.
    // The beach's own garrison counts twice (TargetValue and the ring); drop the x2 for an empty beach if those dominate.
    private static double BeachFactor(nint country, int tag, nint beach)
    {
        var hour = Hour;
        var id = Game.Id(beach);
        if (Beaches.TryGetValue((tag, id), out var kept) && hour - kept.Hour is >= 0 and < BeachHours) return kept.Factor;
        var held = Defenders(country, tag, id, out var value);
        var factor = (1 + Math.Max(value, 0) / 50) * (held == 0 ? 2 : 1 / (1 + held / 2.0)) / (1 + _fortLevel(beach) + CoastalBunker(beach));
        Beaches[(tag, id)] = (hour, factor);
        return factor;
    }

    /// <summary>Enemy divisions on the beach and its two rings; <paramref name="value"/> the best target value there (half off the beach).</summary>
    private static int Defenders(nint country, int tag, int beach, out double value)
    {
        var held = 0;
        value = 0;
        foreach (var id in Rings(beach))
        {
            var p = Game.Province(id);
            if (p == 0 || !Enemy(country, tag, p)) continue;
            held += Divisions(p);
            value = Math.Max(value, TargetValue(p) * (id == beach ? 1 : 0.5));
        }
        return held;
    }

    /// <summary>The province's coastal bunker, working level (bombarded levels don't count).</summary>
    private static int CoastalBunker(nint province)
    {
        var db = *(nint*)_buildingDb;
        var type = db == 0 ? 0 : _bunkerType(db); // the null type when undefined: Find gives 0 for it
        var bunker = type == 0 ? 0 : _findBuilding(province + BuildingsOfProvince, type);
        return bunker == 0 ? 0 : *(short*)(bunker + WorkingLevel);
    }

    /// <summary>The group's invasion order that hasn't sailed yet, or 0.</summary>
    private static nint Unlaunched(nint group)
    {
        foreach (var order in Native.PdxArray(group + OrdersOfGroup))
            if (order != 0 && *(int*)(order + TypeOfOrder) == Invasion && *(byte*)(order + Executing) == 0) return order; // paradrops: never AI-launched with id 0
        return 0;
    }

    /// <summary>
    /// Whether the invasion may sail: the enemy divisions around its most crowded beach are at most half of ours. The AI re-posts a
    /// held launch every update; after a week it goes anyway (vanilla). A delay, not a new beach: the AI keeps its target. AI thread, read only.
    /// </summary>
    // ponytail: no re-targeting; post CRemoveNavalInvasionTargetCommand (ctor 0x14183B9A0) for a held beach if logs show invasions sitting out the hold.
    private static bool BeachClear(nint group, nint invasion, int tag)
    {
        var country = Game.Country(tag);
        if (country == 0) return true;
        var g = new OrdersGroup(group);
        var ours = Native.PdxArray(g.Members).Length;
        if (g.IsArmyGroup)
            foreach (var army in g.Armies)
                if (army != 0) ours += Native.PdxArray(new OrdersGroup(army).Members).Length;
        var held = 0;
        foreach (var id in Native.PdxArray<int>(invasion + TargetsOfInvasion)) held = Math.Max(held, Defenders(country, tag, id, out _));
        if (held * 100 <= ours * BeachPercent)
        {
            Holds.TryRemove(group, out _);
            return true;
        }
        var hour = Hour;
        var first = Holds.GetOrAdd(group, (hour, tag));
        if (first.Tag != tag || hour < first.Hour || hour - first.Hour >= 2 * BeachHoldHours + BeachHours)
            Holds[group] = first = (hour, tag); // a reused group pointer, a loaded save, or a new plan long after the last hold
        if (hour - first.Hour >= BeachHoldHours) return true; // waited long enough: vanilla goes
        Interlocked.Increment(ref _landingsHeld);
        return false;
    }
}
