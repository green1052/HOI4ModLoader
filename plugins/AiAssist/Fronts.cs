using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Fronts: each front's threatened line and the theatre's breakthrough axis, tracked as the front requests are set ----

    private const int MinWanted = 0x90, DesiredWanted = 0x94, MaxWanted = 0x98, FixedRequest = 0x9C, CountryOfFront = 0xE0, FrontOfAiFront = 0xE8;
    private const int SectionOfAiFront = 0xF0, AreaDefense = 0x104, InvasionTarget = 0x120, OtherRequest = 0x1DC, FrontsOfGeneral = 0x48;
    private const int SectionsOfFront = 0x58, IdOfSection = 8, PairsOfSection = 0x48;

    [Function(CallingConventions.Microsoft)] public delegate nint GeneralRetFn(nint general);
    [Function(CallingConventions.Microsoft)] public delegate void BalanceFn(nint front, nint units, nint partitions, nint specs, nint log);

    private IHook<GeneralRetFn>? _unitRequests;
    private IHook<BalanceFn>? _armyBalancing;
    private int _raised;
    private static int _stationed;
    private static bool _holdLine, _axis, _beachDefense;
    private const int AxisHours = 24, ThinSectorWeight = 8;
    // CAIFront+0x218: its CAIForceConcentration (the game's armoured push), embedded. +0 divisions it was given (0: none), +0x14 its units,
    // +0x20 its army, +0x48 target (optional, set flag +0x78) with its CPdxArray<CProvince*> path at +0x60 (path[0] ours .. path[n-1] the target), +0x84 attack on.
    private const int ConcentrationOfFront = 0x218, UnitsOfConcentration = 0x14, ArmyOfConcentration = 0x20, PathOfConcentration = 0x60, HasTarget = 0x78, AttackOn = 0x84;
    private const int ArmiesOfAiFront = 0x60; // CAIFront: CPdxArray<COrdersGroup*>
    private static int _spearheads;

    // Keyed by raw CAIGeneral pointers, which a freed and reallocated general can reuse: entries carry the tag they were made for.
    // Head: the spearhead's province the axis is anchored on, 0 for a thin-sector axis.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, int Head, HashSet<int> Core, HashSet<int> Line)> Axes = new(); // general -> its breakthrough axis, for a day
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, long[] Armies)> AxisArmies = new(); // general -> its armies at war (CRef ids), for paradrops

    private void LoadFronts()
    {
        if (_axis) _ = Sig.Find(Signatures.ConcentrationActive) + Sig.Find(Signatures.ConcentrationPath) + Sig.Find(Signatures.ConcentrationAttacking); // the force concentration offsets are still where they were
        _unitRequests = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateFrontUnitRequests), general =>
        {
            var result = _unitRequests!.OriginalFunction(general);
            Safely(() => TrackFronts(general));
            return result;
        });
        if (!_holdLine) return;
        // The regrouping drains fronts through here (unassign, then one mass move a turn), which no front request can block:
        // the last defender of a province, and the wall around a pocket, are kept out of its lists instead. Vanilla would
        // reassign them and staff the new front with divisions from far away, while the replacements walk the pocket breaks out.
        _collect = Patches.Hook<CollectFn>(Sig.Find(Signatures.CollectReassignableUnits), (context, list) =>
        {
            var result = _collect!.OriginalFunction(context, list);
            Safely(() =>
            {
                var data = *(nint*)list;
                int n = *(int*)(list + 0xC), kept = 0;
                for (var i = 0; i < n; i++)
                {
                    var entry = data + 16 * i; // {CArmy*, front slot*}
                    var unit = *(nint*)entry;
                    if (unit != 0 && (Pinned(unit) || FreshlyArrived(unit))) continue;
                    if (kept != i) Buffer.MemoryCopy((void*)entry, (void*)(data + 16 * kept), 16, 16);
                    kept++;
                }
                *(int*)(list + 0xC) = kept;
            });
            return result;
        });
        // Army balancing regroups the same divisions into new armies.
        _armyBalancing = Patches.Hook<BalanceFn>(Sig.Find(Signatures.ApplyArmiesBalancing), (front, units, partitions, specs, log) =>
        {
            Safely(() =>
            {
                var unit = Native.PdxArray(units);
                for (int p = 0, parts = *(int*)(partitions + 0xC); p < parts; p++)
                {
                    var part = *(nint*)partitions + 0x18 * p; // CPdxArray<int> indices into units, for army p
                    var indices = (int*)*(nint*)part;
                    int n = *(int*)(part + 0xC), kept = 0;
                    for (var k = 0; k < n; k++)
                        if ((uint)indices[k] >= (uint)unit.Length || !Pinned(unit[indices[k]])) indices[kept++] = indices[k];
                    *(int*)(part + 0xC) = kept; // the game skips empty partitions
                }
            });
            _armyBalancing!.OriginalFunction(front, units, partitions, specs, log);
        });
    }

    /// <summary>
    /// Per land front, after the game set its requests and before the theatre's divisions are shared out: records its
    /// threatened provinces, keeps a defender per province of a front at war (hold the line), and picks the theatre's axis.
    /// </summary>
    private void TrackFronts(nint general)
    {
        var hour = Hour;
        (int Tag, nint Province, int Weight, HashSet<int> Line)? spot = null; // the thinnest enemy sector on the theatre's line, for the axis
        List<long>? armies = null; // CRef ids of the theatre's armies at war, for paradrops
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            var country = front == 0 ? 0 : *(nint*)(front + CountryOfFront);
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag == 0 || tag == Game.ControlledTag) continue;
            var reference = (int*)(front + FrontOfAiFront);
            if (reference[0] == 0 && reference[1] == 0) continue;
            var frontLine = _resolve((nint)reference);
            if (frontLine == 0) continue;
            nint section = 0;
            foreach (var s in Native.PdxArray(frontLine + SectionsOfFront))
                if (s != 0 && *(int*)(s + IdOfSection) == *(int*)(front + SectionOfAiFront)) { section = s; break; }
            if (section == 0) continue;

            // Both sides of the section: enemy provinces (held by a country at war with us), and our provinces facing them
            // (each needs a defender) or facing enemy divisions (a hole there lets them through).
            var enemyHeld = new Dictionary<nint, bool>();
            var contact = new HashSet<nint>();
            var threatened = new HashSet<nint>();
            var pairs = *(nint*)(section + PairsOfSection);
            for (int i = 0, n = *(int*)(section + PairsOfSection + 0xC); i < n; i++)
            {
                nint ours = *(nint*)(pairs + 16 * i), theirs = *(nint*)(pairs + 16 * i + 8);
                if (theirs == 0) continue;
                if (!enemyHeld.TryGetValue(theirs, out var held)) held = enemyHeld[theirs] = Enemy(country, tag, theirs);
                if (!held || ours == 0) continue;
                contact.Add(ours);
                if (Divisions(theirs) > 0) threatened.Add(ours);
            }
            var hostile = enemyHeld.ContainsValue(true);
            if (_paradrops && hostile)
                foreach (var army in Native.PdxArray(front + ArmiesOfAiFront))
                    if (army != 0) (armies ??= []).Add(*(long*)(army + RefOfGroup));
            // The attack gate and the backfill read this; a front with no threat left drops out at once.
            if (threatened.Count > 0) _line[front] = (hour, tag, threatened);
            else _line.TryRemove(front, out _);
            if (_axis && hostile) // the day's breakthrough axis: the enemy province on this theatre's line that is easiest to take
            {
                foreach (var our in threatened)
                foreach (var id in Game.Neighbours(our))
                {
                    var p = Game.Province(id);
                    if (p == 0 || !Enemy(country, tag, p)) continue;
                    var weight = SpotWeight(p, our);
                    if (spot == null || spot.Value.Tag == tag && weight < spot.Value.Weight) spot = (tag, p, weight, spot?.Line ?? []);
                }
                if (spot?.Tag == tag) foreach (var our in threatened) spot.Value.Line.Add(Game.Id(our));
            }
            // Hold the line: at war, every province of ours on the line keeps a defender, whatever else (a supply crisis,
            // another front) the game weighs; it would ship them off and leave the line empty while fighting.
            if (!_holdLine || !hostile || Grads.ContainsKey(tag) // on a grad, it gets the divisions, not the old border
                || *(int*)(front + AreaDefense) != 0 || *(nint*)(front + InvasionTarget) != 0
                || *(byte*)(front + FixedRequest) != 0 || *(byte*)(front + OtherRequest) != 0) continue; // garrisons, invasions and fixed shares: as the game asks
            if (*(int*)(front + MinWanted) >= contact.Count) continue;
            *(int*)(front + MinWanted) = contact.Count;
            *(int*)(front + DesiredWanted) = Math.Max(*(int*)(front + DesiredWanted), contact.Count);
            *(int*)(front + MaxWanted) = Math.Max(*(int*)(front + MaxWanted), *(int*)(front + DesiredWanted));
            Interlocked.Increment(ref _raised);
        }
        // The game's own spearhead attacks across this line: the axis is where its path crosses it, and moves on with its head (the path
        // is fixed: no zig-zag). A path past a neutral or allied province may miss the line: then the thin sector, or the whole line would stand still.
        if (spot is { } line && Spearhead(general) is var head and not 0 && Rings(head) is var core && core.Overlaps(line.Line))
        {
            if (!(Axes.TryGetValue(general, out var current) && current.Tag == line.Tag && current.Head == head && hour - current.Hour is >= 0 and < AxisHours))
            {
                Axes[general] = (hour, line.Tag, head, core, line.Line);
                Interlocked.Increment(ref _spearheads);
            }
        }
        else if (spot is { } best && best.Weight <= ThinSectorWeight // no thin sector anywhere: no axis today
            && !(Axes.TryGetValue(general, out var axis) && axis.Tag == best.Tag && hour - axis.Hour is >= 0 and < AxisHours)) // held a day: divisions don't zig-zag
            Axes[general] = (hour, best.Tag, 0, Rings(Game.Id(best.Province)), best.Line);
        foreach (var (g, kept) in Axes) if (hour - kept.Hour is < 0 or >= 2 * AxisHours) Axes.TryRemove(g, out _); // generals come and go all game
        if (spot is { } found && armies != null) AxisArmies[general] = (hour, found.Tag, armies.ToArray());
        foreach (var (g, kept) in AxisArmies) if (hour - kept.Hour is < 0 or >= 2 * AxisHours) AxisArmies.TryRemove(g, out _);
        // Fronts come and go all game: drop the stale lines (matched by value, so one refreshed meanwhile stays).
        foreach (var entry in _line) if (hour - entry.Value.Hour is < 0 or >= LineFreshHours) _line.TryRemove(entry);
    }

    /// <summary>
    /// The theatre's armoured spearhead while it attacks: the first enemy province on its force concentration's path, 0 if none.
    /// The game's own liveness test first: a front that lost its concentration keeps a stale path and attack flag. Read only, on
    /// the general's own AI thread, the only one that rebuilds and frees the path. Invasion fronts: the landing has its own target.
    /// The flag is the concentration wanting the attack on; the attack gate may still hold it (as with a thin-sector axis).
    /// </summary>
    // ponytail: first attacking concentration of the theatre only; pick by path progress if two attack at once and it matters.
    private static int Spearhead(nint general)
    {
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            if (front == 0) continue;
            var fc = front + ConcentrationOfFront;
            var country = *(nint*)(front + CountryOfFront);
            if (country == 0 || *(int*)fc <= 0 || *(int*)(fc + UnitsOfConcentration) == 0 || *(nint*)(fc + ArmyOfConcentration) == 0
                || *(byte*)(fc + HasTarget) == 0 || *(byte*)(fc + AttackOn) == 0 || *(nint*)(fc + PathOfConcentration) == 0 || *(nint*)(front + InvasionTarget) != 0) continue;
            var tag = *(int*)(country + 8);
            foreach (var p in Native.PdxArray(fc + PathOfConcentration)) // path[0] ours (the source), path[n - 1] the target
                if (p != 0 && Enemy(country, tag, p)) return Game.Id(p);
        }
        return 0;
    }

    /// <summary>How hard the enemy province is to take from ours: its divisions and forts, more for harsh terrain or a river in front of it.</summary>
    private static int SpotWeight(nint target, nint from)
    {
        var weight = 2 * Divisions(target) + _fortLevel(target);
        var terrain = Terrain(target);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban | Marsh | Jungle)) != 0) weight += 3;
        if (Game.AdjacencyType(from, Game.Id(target)) >= River) weight += 2;
        return weight;
    }

    /// <summary>The province and the two rings of provinces around it (an axis' core, a beach's surroundings).</summary>
    private static HashSet<int> Rings(int target)
    {
        var core = new HashSet<int> { target };
        var ring = new List<int> { target };
        for (var depth = 0; depth < 2; depth++)
        {
            var next = new List<int>();
            foreach (var id in ring)
            foreach (var n in Game.Province(id) is var p && p != 0 ? Game.Neighbours(p) : [])
                if (core.Add(n)) next.Add(n);
            ring = next;
        }
        return core;
    }

    /// <summary>
    /// Whether a local attack from <paramref name="from"/> may go to <paramref name="to"/>: off a theatre's line that has
    /// an axis today, anything; on it, only targets on the axis (the theatre's other sectors hold while it pushes), and
    /// pinning attacks on enemy stacks next to the axis, which could shift into the breakthrough (a division under attack
    /// only moves to retreat), where the attack passes vanilla's strength test.
    /// </summary>
    private static bool OnAxis(int tag, nint from, nint to)
    {
        if (Grads.ContainsKey(tag)) return true; // on its grad: no breakthrough; the grad's own filter picks the targets
        var hour = Hour;
        foreach (var axis in Axes.Values)
            if (axis.Tag == tag && hour - axis.Hour is >= 0 and < AxisHours && axis.Line.Contains(Game.Id(from)))
                return axis.Core.Contains(Game.Id(to)) || Divisions(to) > 0 && Strong(from, to) && Game.Neighbours(to).Exists(axis.Core.Contains);
        return true;
    }

    /// <summary>Our stack in <paramref name="from"/> passes vanilla's strength test for a local attack on <paramref name="to"/> (divisions, forts).</summary>
    private static bool Strong(nint from, nint to) => Divisions(from) >= Divisions(to) * (_fortLevel(to) / 2 + 1);

    /// <summary>Whether the province is on one of the tag's breakthrough axes of the last two days (CAS backs it).</summary>
    private static bool OnAnyAxis(int tag, int province)
    {
        var hour = Hour;
        foreach (var axis in Axes.Values)
            if (axis.Tag == tag && hour - axis.Hour is >= 0 and < 2 * AxisHours && axis.Core.Contains(province)) return true;
        return false;
    }
}
