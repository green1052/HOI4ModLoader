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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Need)> RaisedAt = new(); // front line section -> last logged
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, int Need)> Needs = new(); // section -> the week's highest minimum; a reallocated section can reuse an address: entries carry their tag
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> UnguardedAt = new(); // tag -> hour last logged

    /// <summary>
    /// The division has no army, or its army has no order (front gone, area defence order deleted). The regrouping and army
    /// balancing may take it even when pinned: kept out of them, it would stand idle for good.
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
            if (tag > 0) return tag != Game.ControlledTag && NoLandings(country, tag);
        }
        return false;
    }

    private static int _bystanders;

    private const int ExpectedTagsOfDiplomacy = 0xF8; // CPdxArray<int>, index-aligned with WarSignalsOfDiplomacy: rebuilt daily
    private const int RelationsOfDiplomacy = 0x08, GuaranteesOfRelation = 0x290; // CRelation*[tag index]; non-zero: it guarantees that one

    /// <summary>
    /// The border across these provinces is a bystanders' one: at least BystanderShare % of it is held by countries that are
    /// no threat, and none of the rest is one we're at war with or expect a war with (<see cref="Verdict"/>).
    /// </summary>
    private static bool Bystanders(nint country, int tag, IEnumerable<nint> across)
    {
        var diplomacy = *(nint*)(country + DiplomacyOfCountry);
        if (diplomacy == 0) return false;
        var expected = Native.PdxArray<int>(diplomacy + ExpectedTagsOfDiplomacy).ToArray();
        var ours = LandDivisions(country);
        int safe = 0, other = 0;
        var verdicts = new Dictionary<int, bool?>(); // per country: true no threat, false keeps the front, null neither
        foreach (var p in across)
        {
            var c = Province.Controller(p);
            if (c <= 0 || c == tag) continue;
            if (!verdicts.TryGetValue(c, out var verdict)) verdicts[c] = verdict = Verdict(country, tag, c, expected, ours);
            if (verdict == false) return false;
            if (verdict == true) safe++;
            else other++;
        }
        return safe > 0 && safe * 100 >= (safe + other) * BystanderShare;
    }

    private const int AllyLosingHours = 7 * 24;

    /// <summary>The enemy took ground on or next to this line in the last week.</summary>
    private static bool AllyLosing(nint country, int tag, IEnumerable<nint> line, int hour)
    {
        foreach (var p in line)
            foreach (var id in Game.Neighbours(p).Append(Game.Id(p)))
                if (Taken(id, hour, AllyLosingHours) && Game.Province(id) is var q && q != 0 && Province.Enemy(country, tag, q)) return true;
        return false;
    }

    private const int MaxLandLink = 4000; // provinces searched for a land route home
    private static int _overseas;

    /// <summary>A land route through our and allied ground leads from <paramref name="from"/> to ground of ours (true too when the search gives up).</summary>
    private static bool LandLinked(nint country, int tag, nint from)
    {
        var seen = new HashSet<int> { Game.Id(from) };
        var queue = new Queue<nint>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            if (seen.Count > MaxLandLink) return true;
            foreach (var id in Game.Neighbours(queue.Dequeue()))
            {
                if (!seen.Add(id) || Game.Province(id) is not (var p and not 0)) continue;
                var c = Province.Controller(p);
                if (c == tag) return true;
                if (c > 0 && Province.Friendly(country, tag, p)) queue.Enqueue(p);
            }
        }
        return false;
    }

    private const int BystanderShare = 80, SmallArmyPercent = 50, FactionOfDiplomacy = 0x290;

    /// <summary>
    /// True: no threat (an ally, marked no target by our AI strategies, or a factionless neutral under half our land divisions).
    /// False: it keeps the front (at war, an expected war or its guarantor, a positive conquer or contain). Null: neither.
    /// </summary>
    private static bool? Verdict(nint country, int tag, int c, int[] expected, int ours)
    {
        if (Array.IndexOf(expected, c) >= 0 || Game.AtWar(country, c) || Guarantees(c, expected)) return false;
        var index = Game.TagIndex(c);
        var conquer = Strategy(country, ConquerStrategy, index);
        if (conquer > 0) return false;
        // Antagonize is ideological, not military intent, so it is ignored; a negative conquer means it won't attack.
        if (conquer < 0 || Game.Allied(country, c) || Strategy(country, IgnoreStrategy, index) > 0 || Strategy(country, BefriendStrategy, index) > 0
            || Strategy(country, AllianceStrategy, index) > 0 || Strategy(country, ProtectStrategy, index) > 0) return true;
        if (Strategy(country, ContainStrategy, index) > 0) return false;
        var them = Game.Country(c);
        var theirs = them == 0 ? 0 : *(nint*)(them + DiplomacyOfCountry);
        if (theirs == 0 || *(nint*)(theirs + FactionOfDiplomacy) != 0) return null; // in a faction: its friends are a threat
        return LandDivisions(them) * 100 < ours * SmallArmyPercent ? true : null;
    }

    /// <summary>The country's land divisions.</summary>
    private static int LandDivisions(nint country)
    {
        var n = 0;
        if (country != 0)
            foreach (var u in Game.Units(country)) if (u != 0 && Division.IsLand(u)) n++;
        return n;
    }

    private const int AiOfCountry = 0x228, StrategiesOfAi = 0x68, StrategyValueSlot = 4; // CCountry -> CCountryAI; its strategy table, GetValue(type, tag index, 0)
    // ai_strategy types, in the game's token table order (0x1429CAD60): conquer 0, antagonize 1, befriend 2, protect 3,
    // alliance 6, contain 13, ignore 37, prepare_for_war 43.
    private const int ConquerStrategy = 0, BefriendStrategy = 2, ProtectStrategy = 3, AllianceStrategy = 6, ContainStrategy = 13, IgnoreStrategy = 37;

    /// <summary>The country's AI strategy value of <paramref name="type"/> toward the country at tag index <paramref name="index"/>; 0 for a player's or none.</summary>
    private static int Strategy(nint country, int type, int index)
    {
        var ai = *(nint*)(country + AiOfCountry);
        if (ai == 0 || index < 0) return 0;
        var strategies = ai + StrategiesOfAi;
        return ((delegate* unmanaged<nint, int, int, int, int>)(*(nint**)strategies)[StrategyValueSlot])(strategies, type, index, 0);
    }

    /// <summary>The country of <paramref name="tag"/> guarantees one of <paramref name="targets"/>.</summary>
    private static bool Guarantees(int tag, int[] targets)
    {
        var other = Game.Country(tag);
        var diplomacy = other == 0 ? 0 : *(nint*)(other + DiplomacyOfCountry);
        if (diplomacy == 0) return false;
        var relations = *(nint**)(diplomacy + RelationsOfDiplomacy);
        var count = *(int*)(diplomacy + RelationsOfDiplomacy + 0xC);
        foreach (var target in targets)
            if (Game.TagIndex(target) is var i && i >= 0 && i < count && relations[i] != 0 && *(nint*)(relations[i] + GuaranteesOfRelation) != 0) return true;
        return false;
    }

    /// <summary>
    /// At war on a land line, and no enemy at war holds a port (last controller scan): nobody can land, so all port and home
    /// garrisons go to the line.
    /// </summary>
    private static bool NoLandings(nint country, int tag) =>
        _portHolders is { } ports && HasLine(tag) && !ports.Any(holder => Game.AtWar(country, holder));

    /// <summary>
    /// Its line was short in the last GarrisonHours, or a war on its home land border is on or coming: garrisons keep only a
    /// floor and the rest go to the line. A month, so they don't go back to the coast as soon as the line is manned.
    /// </summary>
    private static bool GarrisonsToLine(nint country, int tag) =>
        _portHolders != null && (HasLine(tag) && ShortOfMen.TryGetValue(tag, out var short_) && Game.Hour - short_ is >= 0 and < GarrisonHours
                                 || LandWarAtHome(country, tag));

    private const int GarrisonHours = 30 * 24, CoresOfState = 0xB0;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Day, bool On)> LandWars = new();

    /// <summary>
    /// A war on its home land border is on or coming: a country it is at war with, or expects a war with (justification, war
    /// goal, focus, war preparation, either way), holds ground next to its core ground. Cached a day.
    /// </summary>
    private static bool LandWarAtHome(nint country, int tag)
    {
        var day = Game.Hour / 24;
        if (LandWars.TryGetValue(tag, out var cached) && cached.Day == day) return cached.On;
        var on = false;
        var diplomacy = *(nint*)(country + DiplomacyOfCountry);
        var controllers = _controllers;
        if (diplomacy != 0 && controllers.Length > 0)
        {
            var hostile = Native.PdxArray<int>(diplomacy + ExpectedTagsOfDiplomacy).ToArray().ToHashSet();
            var checkedTags = new Dictionary<int, bool>();
            bool Hostile(int c) => c > 0 && c != tag && (hostile.Contains(c) || (checkedTags.TryGetValue(c, out var w) ? w : checkedTags[c] = Game.AtWar(country, c)));
            for (var id = 1; id < controllers.Length && !on; id++)
            {
                if (controllers[id] != tag) continue;
                var near = false;
                foreach (var n in Game.Neighbours(Game.Province(id)))
                    if ((uint)n < (uint)controllers.Length && Hostile(controllers[n])) { near = true; break; }
                if (!near) continue;
                var state = Game.Province(id) is var p && p != 0 ? Province.State(p) : 0;
                on = state != 0 && Native.PdxArray<int>(state + CoresOfState).Contains(tag);
            }
        }
        LandWars[tag] = (day, on);
        return on;
    }

    /// <summary>A tracked line of the tag's. Enumerates the dictionary lock-free (Values locks every bucket and copies).</summary>
    private static bool HasLine(int tag)
    {
        foreach (var e in Line) if (e.Value.Tag == tag) return true;
        return false;
    }
    private static int _stationed;
    private static bool _holdLine, _axis, _beachDefense;
    private const int AxisHours = 24, ThinSectorWeight = 8, SectorHours = 72, SalientHead = -1; // a thin sector is held 3 days unless taken
    private const int AxisFreshHours = 2 * AxisHours; // the general updates its fronts about daily: an axis lives until the next update
    // CAIFront+0x218: its embedded CAIForceConcentration (the game's armoured push). +0 divisions given (0: none), +0x14 units, +0x20 army,
    // +0x48 target (if flag +0x78) with its CPdxArray<CProvince*> path at +0x60 (path[0] ours .. path[n-1] the target), +0x84 attack on.
    private const int ConcentrationOfFront = 0x218, UnitsOfConcentration = 0x14, ArmyOfConcentration = 0x20, PathOfConcentration = 0x60, HasTarget = 0x78, AttackOn = 0x84;
    private const int ArmiesOfAiFront = 0x60; // CAIFront: CPdxArray<COrdersGroup*>
    private static int _spearheads;

    // Keyed by raw CAIGeneral pointers, which a reallocated general can reuse: entries carry their tag.
    // Hour: last refreshed; Since: when its anchor was picked; Anchor: its province.
    // Head: the spearhead's province, SalientHead for a salient's neck, PincerHead for a pincer's first arm, 0 for a thin sector.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, int Head, HashSet<int> Core, HashSet<int> Line, int Since, int Anchor)> Axes = new(); // general -> its breakthrough axis

    private void LoadFronts()
    {
        if (_axis) _ = Sig.Find(Signatures.ConcentrationActive) + Sig.Find(Signatures.ConcentrationPath) + Sig.Find(Signatures.ConcentrationAttacking); // pins the force concentration offsets
        _unitRequests = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateFrontUnitRequests), general =>
        {
            var result = _unitRequests!.OriginalFunction(general);
            Safely(() => TrackFronts(general));
            return result;
        });
        if (!_holdLine) return;
        // Area defence against enemies that hold no port: no new orders (DropAreaDefence deletes the old ones), so the
        // divisions are free for the land fronts. Zero requests alone don't free an army already on such a front.
        _areaDefense = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateAreaDefense), general =>
        {
            var skip = false;
            Safely(() => skip = GeneralCantBeInvaded(general));
            return skip ? 0 : _areaDefense!.OriginalFunction(general);
        });
        // The regrouping drains fronts through here, which no front request can block: pinned divisions (a province's last
        // defender, a pocket's wall) and fresh arrivals are left out of its list.
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
                    var pinned = Pinned(unit);
                    var fresh = !pinned && FreshlyArrived(unit);
                    if ((pinned || fresh) && !OrderlessArmy(unit))
                    {
                        if (fresh) Interlocked.Increment(ref _stationed); // counted where it is left out, not on every check
                        continue;
                    }
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
        nint theatre = 0; // the theatre's country (at war)
        HashSet<nint>? enemyLine = null; // enemy provinces on the theatre's line: the salients' necks are among them
        List<(nint Front, HashSet<nint> Threatened)>? atWar = null; // for railway guns
        var areaFronts = 0;
        nint mainArea = 0; // the area defence front asking most: the capital's (vanilla's capital defence, max 100)
        foreach (var f in Native.PdxArray(general + FrontsOfGeneral))
            if (f != 0 && *(int*)(f + AreaDefense) != 0)
            {
                areaFronts++;
                if (mainArea == 0 || *(int*)(f + MaxWanted) > *(int*)(mainArea + MaxWanted)) mainArea = f;
            }
        var othersDesired = 0; // what vanilla wants on the other area fronts: their share of the floor
        foreach (var f in Native.PdxArray(general + FrontsOfGeneral))
            if (f != 0 && f != mainArea && *(int*)(f + AreaDefense) != 0 && *(int*)(f + MaxWanted) > 0) othersDesired += *(int*)(f + DesiredWanted);
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            var country = front == 0 ? 0 : *(nint*)(front + CountryOfFront);
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag == 0 || tag == Game.ControlledTag) continue;
            // Area defence (port and home garrisons, battle plan type 5): checked first, as it has no front line.
            if (*(int*)(front + AreaDefense) != 0)
            {
                if (_holdLine) UnguardAreaDefence(front, country, tag, hour, areaFronts, front == mainArea, othersDesired);
                continue;
            }
            var frontLine = Game.Resolve(*(long*)(front + FrontOfAiFront));
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
            // The attack gate and the backfill read this; a front with no threat left drops out at once.
            if (threatened.Count > 0) Line[front] = (hour, tag, threatened);
            else Line.TryRemove(front, out _);
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
            // Hold the line: at war, every line province keeps a defender and the front as many divisions as the enemy across it.
            // The theatre distribution only takes divisions beyond a front's minimum (CollectReassignableUnits and the donation
            // pass 0x141A5D070 keep AI_THEATRE_DISTRIBUTION_PERCENTAGE_OF_MINIMUM_UNITS_TO_KEEP x +0x90).
            // A front on a bystanders' border wants no divisions, so they go where a war is or may come.
            if (_holdLine && !hostile && enemyHeld.Count > 0 && *(int*)(front + MaxWanted) > 0 && Bystanders(country, tag, enemyHeld.Keys))
            {
                DropFront(front);
                Interlocked.Increment(ref _bystanders);
                continue;
            }
            // An ally's front overseas (all in allied ground no land route links to ours) wants none, unless the ally is losing
            // ground there and our own line is manned.
            bool? overseasLine = null; // its land-route search, run once for both checks
            if (_holdLine && contact.Count > 0 && *(int*)(front + MaxWanted) > 0 && Overseas(country, tag, contact, hour, overseasLine = OverseasLine(country, tag, contact)))
            {
                DropFront(front);
                Interlocked.Increment(ref _overseas);
                continue;
            }
            if (!_holdLine || !hostile || Grads.ContainsKey(tag) // on a grad, it gets the divisions, not the old border
                || *(nint*)(front + InvasionTarget) != 0
                || *(byte*)(front + FixedRequest) != 0 || *(byte*)(front + OtherRequest) != 0) continue; // garrisons, invasions and fixed shares: as the game asks
            KeepMinimum(front, section, tag, enemyHeld, contact, hour, overseasLine);
            CapToSupply(front, tag, contact, hour);
        }
        if (_axis && spot is { } line && theatre != 0) Anchor(general, theatre, line.Tag, line.Province, line.Weight, line.Line, enemyLine, hour);
        if (_railGuns) PublishGunArmies(general, AxisFrontArmies(general, atWar, hour), hour);
        PruneFronts(hour);
    }

    private const int AreaSharePercent = 33, AreaFloorPercent = 10, AreaLateSharePercent = 50, AreaLateFloorPercent = 35, LateFrom = 1939, LateBy = 1942;

    /// <summary>
    /// Area defence fronts (port and home garrisons, battle plan type 5). Against enemies that hold no port, they want nothing.
    /// Otherwise a floor of divisions guards home and, at war on a land line, together they want at most AreaSharePercent of
    /// the land divisions; floor and share rise from LateFrom to LateBy.
    /// </summary>
    private void UnguardAreaDefence(nint front, nint country, int tag, int hour, int areaFronts, bool main, int othersDesired)
    {
        if (*(int*)(front + MaxWanted) <= 0) return;
        if (!NoLandings(country, tag))
        {
            // Landings stay possible: a floor always guards home. While the garrisons go to the line (a war at home, a short
            // line) or before any land war, only the floor: a division per home port (its naval bases on core ground).
            var divisions = LandDivisions(country);
            var toLine = GarrisonsToLine(country, tag);
            var minimal = toLine || !HasLine(tag);
            // Landings grow later in the war: from LateFrom to LateBy the floor and the share rise.
            var late = Game.Year is var year and > 1900 ? Math.Clamp((year - LateFrom) / (double)(LateBy - LateFrom), 0, 1) : 0;
            var floorPercent = AreaFloorPercent + (int)(late * (AreaLateFloorPercent - AreaFloorPercent));
            var sharePercent = AreaSharePercent + (int)(late * (AreaLateSharePercent - AreaSharePercent));
            var floor = minimal ? Math.Max(1, Math.Min(Math.Max(_homePorts.GetValueOrDefault(tag), (int)(divisions * late * AreaLateFloorPercent / 100)), divisions / 2))
                : Math.Max(1, divisions * floorPercent / 100);
            // The floor is shared: the other area fronts keep what vanilla wants there (a coast it marks threatened), the
            // capital's front gets the rest.
            if (main)
                *(int*)(front + MinWanted) = Math.Max(*(int*)(front + MinWanted), Math.Min(Math.Max(1, floor - othersDesired), *(int*)(front + MaxWanted)));
            else
                *(int*)(front + MinWanted) = Math.Max(*(int*)(front + MinWanted), *(int*)(front + DesiredWanted));
            *(int*)(front + DesiredWanted) = Math.Max(*(int*)(front + DesiredWanted), *(int*)(front + MinWanted));
            var share = main ? floor : Math.Max(1, *(int*)(front + DesiredWanted));
            var cap = minimal ? share : Math.Max(share, divisions * sharePercent / 100 / Math.Max(1, areaFronts));
            if (*(int*)(front + MaxWanted) <= cap) return;
            *(int*)(front + MaxWanted) = cap;
            *(int*)(front + DesiredWanted) = Math.Min(*(int*)(front + DesiredWanted), cap);
            *(int*)(front + MinWanted) = Math.Min(*(int*)(front + MinWanted), cap);
            if (!toLine || UnguardedAt.TryGetValue(tag, out var seen) && hour - seen is >= 0 and < 24) return; // once a day per country in the log
            UnguardedAt[tag] = hour;
            _log?.Debug($"#{tag}: its line was short or a war on its home border is on or coming, so its port and home garrisons (area defence) keep only a division per home port");
            return;
        }
        *(int*)(front + MinWanted) = *(int*)(front + DesiredWanted) = *(int*)(front + MaxWanted) = 0;
        Interlocked.Increment(ref _unguarded);
        if (UnguardedAt.TryGetValue(tag, out var at) && hour - at is >= 0 and < 24) return; // once a day per country in the log
        UnguardedAt[tag] = hour;
        _log?.Debug($"#{tag}: its enemies hold no port, so its port and home garrisons (area defence) go to the land fronts");
    }

    /// <summary>Zeroes the front's requests and armies, so the general draws no front order there (a deleted order was redrawn daily).</summary>
    private static void DropFront(nint front)
    {
        *(int*)(front + MinWanted) = *(int*)(front + DesiredWanted) = *(int*)(front + MaxWanted) = 0;
        *(int*)(front + ArmiesOfAiFront + 0xC) = 0;
    }

    /// <summary>
    /// The line is all in an ally's ground, which no land route links to ours, and the ally isn't losing ground there while our
    /// own line is manned.
    /// </summary>
    private static bool Overseas(nint country, int tag, HashSet<nint> contact, int hour, bool? line = null) =>
        (line ?? OverseasLine(country, tag, contact)) && !AllyHome(contact) && !Spare(tag, hour)
        && !(AllyLosing(country, tag, contact, hour) && !(ShortOfMen.TryGetValue(tag, out var shortAt) && hour - shortAt is >= 0 and < ShortHours));

    /// <summary>The line is all in others' ground, which no land route links to ours.</summary>
    private static bool OverseasLine(nint country, int tag, HashSet<nint> contact) =>
        contact.Count > 0 && contact.All(p => p != 0 && Province.Controller(p) != tag) && !LandLinked(country, tag, contact.First());

    private const int SpareHours = 7 * 24, OverseasHelpPercent = 10;

    /// <summary>
    /// The country's own line was manned all week, so it can spare a little for an ally's line overseas (OverseasHelpPercent,
    /// see <see cref="KeepMinimum"/>).
    /// </summary>
    private static bool Spare(int tag, int hour) => !(ShortOfMen.TryGetValue(tag, out var shortAt) && hour - shortAt is >= 0 and < SpareHours);

    /// <summary>
    /// Part of the line is core ground of the ally holding it: its homeland gets help shipped to it, unlike a colony.
    /// </summary>
    private static bool AllyHome(IEnumerable<nint> line)
    {
        foreach (var p in line)
        {
            var owner = p == 0 ? 0 : Province.Controller(p);
            var state = owner > 0 ? Province.State(p) : 0;
            if (state != 0 && Native.PdxArray<int>(state + CoresOfState).Contains(owner)) return true;
        }
        return false;
    }

    private const int SupplyFullPercent = 80;
    private static int _supplyCapped;

    /// <summary>
    /// A front whose line gets under SupplyFullPercent of the supply it needs asks for no more divisions than stand on it (one per
    /// line province at least): more would only starve. Runs after <see cref="KeepMinimum"/> and overrides it.
    /// </summary>
    private static void CapToSupply(nint front, int tag, HashSet<nint> contact, int hour)
    {
        long need = 0, supplied = 0;
        var standing = 0;
        foreach (var p in contact)
        {
            var flow = Game.SupplyFlow(Game.Id(p));
            if (flow == 0) continue;
            need += *(long*)(flow + Game.NeedOfFlow);
            supplied += *(long*)(flow + Game.SuppliedOfFlow);
            foreach (var u in Province.Units(p)) if (u != 0 && Division.IsLand(u) && Division.Commander(u) == tag) standing++;
        }
        if (need <= 0 || supplied * 100 >= need * SupplyFullPercent) return;
        var cap = Math.Max(contact.Count, standing); // a defender per line province still
        if (*(int*)(front + MaxWanted) <= cap) return;
        *(int*)(front + MaxWanted) = cap;
        *(int*)(front + DesiredWanted) = Math.Min(*(int*)(front + DesiredWanted), cap);
        *(int*)(front + MinWanted) = Math.Min(*(int*)(front + MinWanted), cap);
        Interlocked.Increment(ref _supplyCapped);
    }

    private const int MaxPerLineProvince = 3, NeedHours = 7 * 24, AllyHelpPercent = 25;

    /// <summary>
    /// Every other country whose ground this line is on has at least as many land divisions as <paramref name="tag"/>:
    /// the line is theirs to man, we only help.
    /// </summary>
    private static bool StrongerOwners(int tag, HashSet<nint> line)
    {
        var ours = LandDivisions(Game.Country(tag));
        return line.Select(Province.Controller).Where(c => c > 0 && c != tag).Distinct().All(owner => LandDivisions(Game.Country(owner)) >= ours);
    }

    /// <summary>
    /// Hold the line's minimum: a defender per line province and as many divisions as the enemy across the front (at most
    /// MaxPerLineProvince a province); once twice the enemy, one per line province, so the surplus can go elsewhere. The week's
    /// highest is kept, so fronts don't trade divisions daily. Never more than we have; less on a stronger ally's or an
    /// overseas line.
    /// </summary>
    private void KeepMinimum(nint front, nint section, int tag, Dictionary<nint, bool> enemyHeld, HashSet<nint> contact, int hour, bool? overseasLine)
    {
        var own = contact.Count(p => Province.Controller(p) == tag);
        var helping = own < contact.Count && StrongerOwners(tag, contact);
        var facing = 0;
        foreach (var (p, held) in enemyHeld) if (held) facing += Province.Divisions(p);
        var holding = 0;
        foreach (var p in contact)
        foreach (var unit in Province.Units(p))
            if (unit != 0 && Division.Commander(unit) == tag) holding++;
        var need = holding >= 2 * facing ? contact.Count : Math.Max(contact.Count, Math.Min(facing, MaxPerLineProvince * contact.Count));
        var ours = LandDivisions(Game.Country(tag));
        // Our own stretch gets its full share, a stronger ally's up to AllyHelpPercent of our divisions.
        if (helping) need = Math.Min(need, Math.Max(1, MaxPerLineProvince * own + ours * AllyHelpPercent / 100));
        // An ally's line overseas (kept while we can spare it): a small expeditionary force, not an army shipped away.
        if (own == 0 && !AllyHome(contact) && (overseasLine ?? OverseasLine(Game.Country(tag), tag, contact))) need = Math.Min(need, Math.Max(1, ours * OverseasHelpPercent / 100));
        need = Math.Min(need, Math.Max(1, ours));
        if (Needs.TryGetValue(section, out var kept) && kept.Tag == tag && hour - kept.Hour is >= 0 and < NeedHours && kept.Need >= need)
            need = Math.Min(kept.Need, Math.Max(1, ours)); // still never more than we have now
        else Needs[section] = (hour, tag, need);
        if (*(int*)(front + MinWanted) >= need) return;
        *(int*)(front + MinWanted) = need;
        *(int*)(front + DesiredWanted) = Math.Max(*(int*)(front + DesiredWanted), need);
        *(int*)(front + MaxWanted) = Math.Max(*(int*)(front + MaxWanted), *(int*)(front + DesiredWanted));
        Interlocked.Increment(ref _raised);
        // Logged when the minimum changes, else once a week per front.
        if (RaisedAt.TryGetValue(section, out var last) && last.Need == need && hour - last.Hour is >= 0 and < NeedHours) return;
        RaisedAt[section] = (hour, need);
        _log?.Debug($"#{tag}: a front of {contact.Count} line province(s) facing {facing} enemy division(s) keeps at least {need}");
    }

    /// <summary>Generals and fronts come and go all game: the axes, arms, feints and lines not refreshed lately are dropped.</summary>
    private static void PruneFronts(int hour)
    {
        foreach (var (g, kept) in Axes) if (hour - kept.Hour is < 0 or >= 2 * AxisHours) Axes.TryRemove(g, out _);
        foreach (var (g, kept) in SecondArms) if (hour - kept.Hour is < 0 or >= 2 * AxisHours) SecondArms.TryRemove(g, out _);
        foreach (var (g, kept) in Feints) if (hour - kept.Until is < -FeintHours or >= 2 * AxisHours) Feints.TryRemove(g, out _);
        foreach (var (s, at) in RaisedAt) if (hour - at.Hour is < 0 or >= 2 * NeedHours) RaisedAt.TryRemove(s, out _); // fronts are rebuilt: their sections go
        foreach (var (s, need) in Needs) if (hour - need.Hour is < 0 or >= NeedHours) Needs.TryRemove(s, out _);
        foreach (var entry in Line) if (hour - entry.Value.Hour is < 0 or >= LineFreshHours) Line.TryRemove(entry); // matched by value: one refreshed meanwhile stays
        PublishAxes(); // after this general's Anchor too
    }

    private static readonly Lock AxisGate = new();

    /// <summary>
    /// The axes and second arms as an array for <see cref="OnAnyAxis"/>: the CAS hook looks up every target, and enumerating
    /// the dictionaries allocates. Rebuilt after each change, one at a time, so the last one sees every write before it.
    /// </summary>
    private static volatile (int Tag, int Hour, HashSet<int> Core)[] _axisCores = [];

    private static void PublishAxes()
    {
        lock (AxisGate) _axisCores = [.. Axes.Select(a => (a.Value.Tag, a.Value.Hour, a.Value.Core)), .. SecondArms.Select(s => (s.Value.Tag, s.Value.Hour, s.Value.Core))];
    }

    /// <summary>
    /// The theatre's axis for this hour: an enemy salient's neck, else the game's own spearhead while it attacks across the line,
    /// else a pincer around a massed enemy, else the thinnest enemy sector (held three days, moved on to the next only once taken).
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
                _log?.Debug($"#{tag}: pincer around {pin.Enemies} enemy division(s) at {pin.Mass}: arms at {pin.A} and {pin.B}");
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
    /// The game's liveness test comes first: a lost concentration keeps a stale path and attack flag. Invasion fronts are skipped.
    /// Read on the general's own AI thread, the only one that frees the path.
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
                if (Game.Province(n) is var q && q != 0 && Province.State(q) != 0 && core.Add(n)) next.Add(n); // land only
            ring = next;
        }
        return core;
    }

    /// <summary>
    /// Whether a local attack from <paramref name="from"/> may go to <paramref name="to"/>: anything off a line with a fresh axis;
    /// on it, only targets on the axis, and pinning attacks (passing vanilla's strength test) on enemy stacks next to it, so
    /// they can't shift into the breakthrough.
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

    /// <summary>Whether the province is on one of the tag's breakthrough axes (or second arms) of the last two days (CAS backs it). Allocates nothing.</summary>
    private static bool OnAnyAxis(int tag, int province)
    {
        var hour = Game.Hour;
        foreach (var (owner, at, core) in _axisCores)
            if (owner == tag && hour - at is >= 0 and < 2 * AxisHours && core.Contains(province)) return true;
        return false;
    }
}
