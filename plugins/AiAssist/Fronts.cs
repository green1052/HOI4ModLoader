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

    private IHook<GeneralRetFn>? _unitRequests, _areaDefense;
    private IHook<BalanceFn>? _armyBalancing;
    private int _raised, _unguarded;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> RaisedAt = new(); // front line section -> hour last logged
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> UnguardedAt = new(); // tag -> hour last logged

    /// <summary>
    /// The division is in no army, or its army has no order at all (its front gone, an area defence order deleted): kept out of
    /// the regrouping and the army balancing, it would stand there with no order for good, so the game may place it.
    /// </summary>
    private static bool OrderlessArmy(nint unit)
    {
        var group = Division.Group(unit);
        return group == 0 || Native.PdxArray(group + OrdersGroup.RootOrdersOffset).Length == 0
            && (*(nint*)(group + OrdersGroup.ParentOffset) is var parent && (parent == 0 || Native.PdxArray(parent + OrdersGroup.RootOrdersOffset).Length == 0));
    }

    /// <summary>The theatre general's country (from its fronts) can't be invaded.</summary>
    private static bool GeneralCantBeInvaded(nint general)
    {
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            var country = front == 0 ? 0 : *(nint*)(front + CountryOfFront);
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag > 0) return tag != Game.ControlledTag && CantBeInvaded(country, tag);
        }
        return false;
    }

    /// <summary>At war on a land line, and no enemy at war holds a port (from the last controller scan).</summary>
    private static bool CantBeInvaded(nint country, int tag) =>
        PortHolders is { } ports && _line.Values.Any(l => l.Tag == tag) && !ports.Any(holder => Game.AtWar(country, holder));
    private static int _stationed;
    private static bool _holdLine, _axis, _beachDefense;
    private const int AxisHours = 24, ThinSectorWeight = 8, SectorHours = 72, SalientHead = -1; // a thin sector is held 3 days unless taken
    private const int AxisFreshHours = 2 * AxisHours; // the general updates its fronts about once a day (25 hours seen): an axis lives until the next one
    // CAIFront+0x218: its CAIForceConcentration (the game's armoured push), embedded. +0 divisions it was given (0: none), +0x14 its units,
    // +0x20 its army, +0x48 target (optional, set flag +0x78) with its CPdxArray<CProvince*> path at +0x60 (path[0] ours .. path[n-1] the target), +0x84 attack on.
    private const int ConcentrationOfFront = 0x218, UnitsOfConcentration = 0x14, ArmyOfConcentration = 0x20, PathOfConcentration = 0x60, HasTarget = 0x78, AttackOn = 0x84;
    private const int ArmiesOfAiFront = 0x60; // CAIFront: CPdxArray<COrdersGroup*>
    private static int _spearheads;

    // Keyed by raw CAIGeneral pointers, which a freed and reallocated general can reuse: entries carry the tag they were made for.
    // Hour: last refreshed (hourly while the theatre has a line); Since: when its anchor was picked; Anchor: the province it is on.
    // Head: the spearhead's province, SalientHead for a salient's neck, 0 for a thin sector.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, int Head, HashSet<int> Core, HashSet<int> Line, int Since, int Anchor)> Axes = new(); // general -> its breakthrough axis
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
        // Area defence against enemies that hold no port: no orders made, and DropAreaDefence deletes the ones left, so their
        // armies' divisions are collected for the land fronts (on an order they never are). Requests at 0 alone didn't free an
        // army already on an area defence front: Italy kept shipping its East African divisions to the Dodecanese.
        _areaDefense = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateAreaDefense), general =>
        {
            var skip = false;
            Safely(() => skip = GeneralCantBeInvaded(general));
            return skip ? 0 : _areaDefense!.OriginalFunction(general);
        });
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
                    if (unit != 0 && (Pinned(unit) || FreshlyArrived(unit)) && !OrderlessArmy(unit)) continue;
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
                        if ((uint)indices[k] >= (uint)unit.Length || !Pinned(unit[indices[k]]) || OrderlessArmy(unit[indices[k]])) indices[kept++] = indices[k];
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
        var hour = Game.Hour;
        (int Tag, nint Province, int Weight, HashSet<int> Line)? spot = null; // the thinnest enemy sector on the theatre's line, for the axis
        List<long>? armies = null; // CRef ids of the theatre's armies at war, for paradrops
        nint theatre = 0; // the theatre's country (at war)
        HashSet<nint>? enemyLine = null; // enemy provinces on the theatre's line: the salients' necks are among them
        List<(nint Front, HashSet<nint> Threatened)>? atWar = null; // for railway guns
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            var country = front == 0 ? 0 : *(nint*)(front + CountryOfFront);
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag == 0 || tag == Game.ControlledTag) continue;
            // Area defence (the AI's port and home garrisons, battle plan type 5) against enemies that hold no port, so can't
            // land: a land war takes them all. Checked first: an area defence front has no front line. Left alone, Italy walked
            // its Somali line divisions to Kismayo, and its Eritrean ones to ports in Libya and Italy, against Ethiopia.
            if (*(int*)(front + AreaDefense) != 0)
            {
                if (_holdLine && *(int*)(front + MaxWanted) > 0 && CantBeInvaded(country, tag))
                {
                    *(int*)(front + MinWanted) = *(int*)(front + DesiredWanted) = *(int*)(front + MaxWanted) = 0;
                    Interlocked.Increment(ref _unguarded);
                    if (!UnguardedAt.TryGetValue(tag, out var at) || hour - at is < 0 or >= 24)
                    {
                        UnguardedAt[tag] = hour;
                        _log?.Info($"#{tag}: its enemies hold no port, so its port and home garrisons (area defence) go to the land fronts");
                    }
                }
                continue;
            }
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
                if (!enemyHeld.TryGetValue(theirs, out var held)) held = enemyHeld[theirs] = Province.Enemy(country, tag, theirs);
                if (!held || ours == 0) continue;
                contact.Add(ours);
                if (Province.Divisions(theirs) > 0) threatened.Add(ours);
            }
            var hostile = enemyHeld.ContainsValue(true);
            if (hostile)
            {
                theatre = country;
                if (_encircle || _pincers || _feints) foreach (var (p, held) in enemyHeld) if (held) (enemyLine ??= []).Add(p);
                if (_railGuns && threatened.Count > 0) (atWar ??= []).Add((front, threatened));
            }
            if (_paradrops && hostile)
                foreach (var army in Native.PdxArray(front + ArmiesOfAiFront))
                    if (army != 0) (armies ??= []).Add(new OrdersGroup(army).Ref);
            // The attack gate and the backfill read this; a front with no threat left drops out at once.
            if (threatened.Count > 0) _line[front] = (hour, tag, threatened);
            else _line.TryRemove(front, out _);
            if (_axis && hostile) // the day's breakthrough axis: the enemy province on this theatre's line that is easiest to take
            {
                foreach (var our in threatened)
                foreach (var id in Game.Neighbours(our))
                {
                    var p = Game.Province(id);
                    if (p == 0 || !Province.Enemy(country, tag, p)) continue;
                    var weight = Province.AttackWeight(p, our);
                    if (spot == null || spot.Value.Tag == tag && weight < spot.Value.Weight) spot = (tag, p, weight, spot?.Line ?? []);
                }
                if (spot?.Tag == tag) foreach (var our in threatened) spot.Value.Line.Add(Game.Id(our));
            }
            // Hold the line: at war, every province of ours on the line keeps a defender, and the front as many divisions as the
            // enemy divisions across it, whatever else (a supply crisis, another front) the game weighs. The theatre distribution
            // takes a front's divisions for other fronts only beyond its minimum (CollectReassignableUnits and the donation pass
            // 0x141A5D070 keep AI_THEATRE_DISTRIBUTION_PERCENTAGE_OF_MINIMUM_UNITS_TO_KEEP x +0x90): left alone, Italy shipped its
            // Somali divisions to Eritrea and Ethiopia walked in.
            if (!_holdLine || !hostile || Grads.ContainsKey(tag) // on a grad, it gets the divisions, not the old border
                || *(int*)(front + AreaDefense) != 0 || *(nint*)(front + InvasionTarget) != 0
                || *(byte*)(front + FixedRequest) != 0 || *(byte*)(front + OtherRequest) != 0) continue; // garrisons, invasions and fixed shares: as the game asks
            var facing = 0;
            foreach (var (p, held) in enemyHeld) if (held) facing += Province.Divisions(p);
            var need = Math.Max(contact.Count, facing);
            if (*(int*)(front + MinWanted) >= need) continue;
            *(int*)(front + MinWanted) = need;
            *(int*)(front + DesiredWanted) = Math.Max(*(int*)(front + DesiredWanted), need);
            *(int*)(front + MaxWanted) = Math.Max(*(int*)(front + MaxWanted), *(int*)(front + DesiredWanted));
            Interlocked.Increment(ref _raised);
            if (!RaisedAt.TryGetValue(section, out var last) || hour - last is < 0 or >= 24) // once a day per front in the log
            {
                RaisedAt[section] = hour;
                _log?.Info($"#{tag}: a front of {contact.Count} line province(s) facing {facing} enemy division(s) keeps at least {need}");
            }
        }
        if (_axis && spot is { } line && theatre != 0) Anchor(general, theatre, line.Tag, line.Province, line.Weight, line.Line, enemyLine, hour);
        if (_railGuns) PublishGunArmies(general, AxisFrontArmies(general, atWar, hour), hour);
        foreach (var (g, kept) in Axes) if (hour - kept.Hour is < 0 or >= 2 * AxisHours) Axes.TryRemove(g, out _); // generals come and go all game
        if (spot is { } found && armies != null) AxisArmies[general] = (hour, found.Tag, armies.ToArray());
        foreach (var (g, kept) in AxisArmies) if (hour - kept.Hour is < 0 or >= 2 * AxisHours) AxisArmies.TryRemove(g, out _);
        // Fronts come and go all game: drop the stale lines (matched by value, so one refreshed meanwhile stays).
        foreach (var entry in _line) if (hour - entry.Value.Hour is < 0 or >= LineFreshHours) _line.TryRemove(entry);
    }

    /// <summary>
    /// The theatre's axis for this hour, re-published with the current line: an enemy salient's neck (cut off from both flanks,
    /// kept until the pocket closes or a week passes), else the game's own spearhead while it attacks across the line (moving on
    /// with its head; a path past a neutral or allied province may miss the line), else the thinnest enemy sector, held three
    /// days and moved only when taken (on to its thinnest enemy neighbour): offensive divisions don't swap far-apart sectors.
    /// AI thread of the general; published records are never changed.
    /// </summary>
    private static void Anchor(nint general, nint country, int tag, nint thinnest, int weight, HashSet<int> line, HashSet<nint>? enemyLine, int hour)
    {
        var mine = Axes.TryGetValue(general, out var current) && current.Tag == tag && hour - current.Hour is >= 0 and < AxisFreshHours; // this theatre's, from its last update
        if (_encircle && enemyLine != null && Salient(general, country, tag, enemyLine, hour) is { } salient)
        {
            var same = mine && current.Head == SalientHead && current.Core == salient.Core;
            Axes[general] = (hour, tag, SalientHead, salient.Core, line, same ? current.Since : hour, salient.Neck[0]);
            SecondArms.TryRemove(general, out _);
            return;
        }
        if (Spearhead(general) is var head and not 0)
        {
            var same = mine && current.Head == head;
            var core = same ? current.Core : Rings(head);
            if (core.Overlaps(line))
            {
                if (!same) Interlocked.Increment(ref _spearheads);
                Axes[general] = (hour, tag, head, core, line, same ? current.Since : hour, head);
                SecondArms.TryRemove(general, out _);
                return;
            }
        }
        // A massed enemy: two arms close behind it, kept three days while both arm targets are still the enemy's.
        if (_pincers && enemyLine != null)
        {
            if (mine && current.Head == PincerHead && hour - current.Since is >= 0 and < SectorHours && SecondArms.TryGetValue(general, out var arm) && arm.Tag == tag
                && Game.Province(current.Anchor) is var a && a != 0 && Province.Enemy(country, tag, a) && Game.Province(arm.Anchor) is var b && b != 0 && Province.Enemy(country, tag, b))
            {
                Axes[general] = (hour, tag, PincerHead, current.Core, line, current.Since, current.Anchor);
                SecondArms[general] = (hour, tag, arm.Core, arm.Anchor);
                return;
            }
            if (Pincer(enemyLine) is { } pin)
            {
                Axes[general] = (hour, tag, PincerHead, Rings(pin.A), line, hour, pin.A);
                SecondArms[general] = (hour, tag, Rings(pin.B), pin.B);
                Interlocked.Increment(ref _pincered);
                _log?.Info($"#{tag}: pincer around {pin.Enemies} enemy division(s) at {pin.Mass}: arms at {pin.A} and {pin.B}");
                StartFeint(general, tag, pin.A, enemyLine, hour);
                return;
            }
        }
        SecondArms.TryRemove(general, out _);
        var sticky = mine && current.Head == 0 && hour - current.Since is >= 0 and < SectorHours;
        var (anchor, since) = sticky ? (current.Anchor, current.Since) : (0, hour);
        if (sticky && Game.Province(anchor) is var at && at != 0 && !Province.Enemy(country, tag, at))
            (anchor, since) = (Forward(country, tag, at), hour); // taken: the push goes on from there
        if (anchor == 0)
        {
            if (weight > ThinSectorWeight) return; // no thin sector anywhere: no new axis (the last one runs out)
            (anchor, since) = (Game.Id(thinnest), hour);
        }
        var kept = mine && current.Head == 0 && current.Anchor == anchor;
        Axes[general] = (hour, tag, 0, kept ? current.Core : Rings(anchor), line, since, anchor);
        if (!kept && !sticky && enemyLine != null) StartFeint(general, tag, anchor, enemyLine, hour); // a new push (not the next step of one): a feint first
    }

    /// <summary>The easiest enemy province to take next to <paramref name="taken"/> (a sector we just took), 0 if none.</summary>
    private static int Forward(nint country, int tag, nint taken)
    {
        var (best, weight) = (0, int.MaxValue);
        foreach (var id in Game.Neighbours(taken))
            if (Game.Province(id) is var p && p != 0 && Province.Enemy(country, tag, p) && Province.AttackWeight(p, taken) is var w && w < weight)
                (best, weight) = (id, w);
        return best;
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
            var tag = Game.Tag(country);
            foreach (var p in Native.PdxArray(fc + PathOfConcentration)) // path[0] ours (the source), path[n - 1] the target
                if (p != 0 && Province.Enemy(country, tag, p)) return Game.Id(p);
        }
        return 0;
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
        var hour = Game.Hour;
        foreach (var (general, axis) in Axes)
        {
            if (axis.Tag != tag || hour - axis.Hour is < 0 or >= AxisFreshHours || !axis.Line.Contains(Game.Id(from))) continue;
            var target = Game.Id(to);
            // While a feint runs only it attacks (and pins next to it); then the axis, and a pincer's second arm.
            if (FeintCore(general, tag, hour) is { } feint)
                return feint.Contains(target) || Province.Divisions(to) > 0 && Strong(from, to) && Game.Neighbours(to).Any(feint.Contains);
            var second = SecondCore(general, tag, hour);
            bool OnArm(int id) => axis.Core.Contains(id) || second != null && second.Contains(id);
            return OnArm(target) || Province.Divisions(to) > 0 && Strong(from, to) && Game.Neighbours(to).Any(OnArm);
        }
        return true;
    }

    /// <summary>Our stack in <paramref name="from"/> passes vanilla's strength test for a local attack on <paramref name="to"/> (divisions, forts).</summary>
    private static bool Strong(nint from, nint to) => Province.Divisions(from) >= Province.Divisions(to) * (Province.FortLevel(to) / 2 + 1);

    /// <summary>Whether the province is on one of the tag's breakthrough axes of the last two days (CAS backs it).</summary>
    private static bool OnAnyAxis(int tag, int province)
    {
        var hour = Game.Hour;
        foreach (var axis in Axes.Values)
            if (axis.Tag == tag && hour - axis.Hour is >= 0 and < 2 * AxisHours && axis.Core.Contains(province)) return true;
        foreach (var arm in SecondArms.Values)
            if (arm.Tag == tag && hour - arm.Hour is >= 0 and < 2 * AxisHours && arm.Core.Contains(province)) return true;
        return false;
    }
}
