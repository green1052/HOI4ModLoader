using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Concentration: quiet and weak-enemy fronts ask for fewer divisions, fronts facing a strong enemy for more ----

    private const int MinWanted = 0x90, DesiredWanted = 0x94, MaxWanted = 0x98, FixedRequest = 0x9C, CountryOfFront = 0xE0, FrontOfAiFront = 0xE8;
    private const int SectionOfAiFront = 0xF0, AreaDefense = 0x104, InvasionTarget = 0x120, OtherRequest = 0x1DC;
    private const int FactionMemberOfCountry = 0xF88, FactionOfMember = 0x290; // CCountry -> member status -> its CFaction (CIsInFactionWith compares the pointers)
    private const int SectionsOfFront = 0x58, IdOfSection = 8, PairsOfSection = 0x48, CombatsInProvince = 0x17C; // province: combats count
    private const double QuietFactor = 0.25, HotBoost = 1.25;

    [Function(CallingConventions.Microsoft)] public delegate nint GeneralRetFn(nint general);
    [Function(CallingConventions.Microsoft)] public delegate void BalanceFn(nint front, nint units, nint partitions, nint specs, nint log);

    private IHook<GeneralRetFn>? _unitRequests;
    private IHook<BalanceFn>? _armyBalancing;
    private int _concentrated;
    private static int _stationed;
    private static bool _concentration, _holdLine, _defensePriority, _axis, _reserve, _beachDefense, _stationing;
    private const int FactorHours = 24, CalmHours = 168;
    private const int AxisRetryHours = 24, ThinSectorWeight = 8;

    // Keyed by raw CAIFront / CAIGeneral pointers, which a freed and reallocated front can reuse: entries carry the tag
    // (and the front's war state) they were made for, and only a match is reused.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, bool Hostile, double Factor)> Factors = new(); // AI front -> its factor, for a day
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag)> LastBattle = new();                          // AI front -> its last battle
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<int> Core, HashSet<int> Line)> Axes = new(); // general -> its breakthrough axis, for a day

    private void LoadConcentration()
    {
        _unitRequests = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateFrontUnitRequests), general =>
        {
            var result = _unitRequests!.OriginalFunction(general);
            Safely(() => Concentrate(general));
            return result;
        });
        if (!_holdLine && !_reserve && !_stationing) return;
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
                    if (Pinned(unit) || _stationing && FreshlyArrived(unit)) continue;
                    if (kept != i) Buffer.MemoryCopy((void*)entry, (void*)(data + 16 * kept), 16, 16);
                    kept++;
                }
                *(int*)(list + 0xC) = kept;
            });
            return result;
        });
        if (!_holdLine && !_reserve) return;
        // Army balancing regroups the same divisions into new armies (FrontLine hooks this too; the hooks stack).
        _armyBalancing = Patches.Hook<BalanceFn>(Sig.Find(Signatures.ApplyArmiesBalancing), (front, units, partitions, specs, log) =>
        {
            Safely(() =>
            {
                var unit = (nint*)*(nint*)units;
                for (int p = 0, parts = *(int*)(partitions + 0xC); p < parts; p++)
                {
                    var part = *(nint*)partitions + 0x18 * p; // CPdxArray<int> indices into units, for army p
                    var indices = (int*)*(nint*)part;
                    int n = *(int*)(part + 0xC), kept = 0;
                    for (var k = 0; k < n; k++)
                        if (!Pinned(unit[indices[k]])) indices[kept++] = indices[k];
                    *(int*)(part + 0xC) = kept; // the game skips empty partitions
                }
            });
            _armyBalancing!.OriginalFunction(front, units, partitions, specs, log);
        });
    }

    /// <summary>Rescales each land front's requests by the enemy actually facing it, before the theatre's divisions are shared out.</summary>
    private void Concentrate(nint general)
    {
        var hour = Hour;
        var warFactions = new Dictionary<int, HashSet<nint>>(); // tag -> factions of the countries at war with it
        (int Tag, nint Province, int Weight, HashSet<int> Line)? spot = null; // the thinnest enemy sector on the theatre's line, for the axis
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            var country = *(nint*)(front + CountryOfFront);
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag == 0 || tag == Game.ControlledTag) continue;
            var reference = (int*)(front + FrontOfAiFront);
            if (reference[0] == 0 && reference[1] == 0) continue;
            var frontLine = _resolve((nint)reference);
            if (frontLine == 0) continue;
            nint section = 0;
            foreach (var s in Native.PdxArray(frontLine + SectionsOfFront))
                if (*(int*)(s + IdOfSection) == *(int*)(front + SectionOfAiFront)) { section = s; break; }
            if (section == 0) continue;

            // Factions of the countries at war with us: a member's border may go hot any day, so it keeps its garrison.
            if (!warFactions.TryGetValue(tag, out var factions))
            {
                warFactions[tag] = factions = [];
                foreach (var c in Game.Countries)
                    if (c != 0 && *(int*)(c + 8) != tag && AtWar(country, *(int*)(c + 8)) && FactionOf(c) is var f && f != 0) factions.Add(f);
            }
            // Both sides of the section: enemy provinces (held by a country at war with us) and their divisions; our provinces
            // facing enemy divisions (each needs a defender), and whether any of ours is fighting.
            var enemyHeld = new Dictionary<nint, bool>();
            var contact = new HashSet<nint>();    // ours facing any enemy-held province
            var threatened = new HashSet<nint>(); // ours facing enemy divisions: a hole there lets them through
            var fighting = false;
            var aligned = false;                  // across sits a country of a warring country's faction (not at war with us itself)
            var enemy = 0;
            var pairs = *(nint*)(section + PairsOfSection);
            for (int i = 0, n = *(int*)(section + PairsOfSection + 0xC); i < n; i++)
            {
                nint ours = *(nint*)(pairs + 16 * i), theirs = *(nint*)(pairs + 16 * i + 8);
                if (ours != 0 && *(int*)(ours + CombatsInProvince) > 0) fighting = true;
                if (theirs == 0) continue;
                if (!enemyHeld.TryGetValue(theirs, out var held))
                {
                    var controller = *(int*)(theirs + ControllerOfProvince);
                    held = enemyHeld[theirs] = Enemy(country, tag, theirs);
                    if (held) enemy += Divisions(theirs);
                    else if (controller > 0 && controller != tag && FactionOf(Game.Country(controller)) is var f && f != 0 && factions.Contains(f)) aligned = true;
                }
                if (held && ours != 0)
                {
                    contact.Add(ours);
                    if (Divisions(theirs) > 0) threatened.Add(ours);
                }
            }
            var hostile = enemyHeld.ContainsValue(true);
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
            if (aligned || *(int*)(front + AreaDefense) != 0 || *(nint*)(front + InvasionTarget) != 0) continue; // a warring country's faction border, garrisons and invasions: as the game asks
            if (hostile && (*(byte*)(front + FixedRequest) != 0 || *(byte*)(front + OtherRequest) != 0)) continue; // fixed shares on a front that is at war; quiet ones are thinned
            var enemyProvinces = enemyHeld.Count(e => e.Value);
            int min = *(int*)(front + MinWanted), desired = *(int*)(front + DesiredWanted), max = *(int*)(front + MaxWanted);
            // Requests are redone every hour; a factor that followed every enemy move would march divisions back and forth.
            // So a factor holds for a day, and a front gives divisions away only a week after its last battle.
            if (fighting) LastBattle[front] = (hour, tag);
            var factor = 1.0;
            if (_concentration)
            {
                if (Factors.TryGetValue(front, out var kept) && kept.Tag == tag && kept.Hostile == hostile && hour - kept.Hour is >= 0 and < FactorHours)
                    factor = kept.Factor;
                else Factors[front] = (hour, tag, hostile, factor = !hostile ? QuietFactor : enemy > min ? HotBoost : 1);
                if (fighting || LastBattle.TryGetValue(front, out var battle) && battle.Tag == tag && hour - battle.Hour is >= 0 and < CalmHours)
                    factor = Math.Max(factor, 1);
            }
            // Hold the line: at war, every province of ours on the line keeps a defender, whatever else (a supply
            // crisis, another front) the game weighs; it would ship them off and leave the line empty while fighting.
            var floor = _holdLine && hostile ? contact.Count : 0;
            if (factor == 1 && min >= floor) continue;
            min = (int)Math.Ceiling(min * factor);
            desired = (int)Math.Ceiling(desired * factor);
            if (factor < 1) max = (int)Math.Ceiling(max * factor); // the rest goes to the fronts that need it
            min = Math.Max(min, Math.Max(floor, enemyProvinces > 0 && factor != 1 ? 1 : 0));
            desired = Math.Max(desired, min);
            max = Math.Max(max, desired);
            (*(int*)(front + MinWanted), *(int*)(front + DesiredWanted), *(int*)(front + MaxWanted)) = (min, desired, max);
            Interlocked.Increment(ref _concentrated);
        }
        if (spot is { } best && best.Weight <= ThinSectorWeight // no thin sector anywhere: no axis today
            && !(Axes.TryGetValue(general, out var axis) && axis.Tag == best.Tag && hour - axis.Hour is >= 0 and < AxisRetryHours)) // held a day: divisions don't zig-zag
            Axes[general] = (hour, best.Tag, Rings(Game.Id(best.Province)), best.Line);
        Prune(hour);
    }

    /// <summary>Drops cached entries too old to be used again (fronts come and go all game).</summary>
    private static void Prune(int hour)
    {
        foreach (var (front, kept) in Factors) if (hour - kept.Hour is < 0 or >= FactorHours) Factors.TryRemove(front, out _);
        foreach (var (front, battle) in LastBattle) if (hour - battle.Hour is < 0 or >= CalmHours) LastBattle.TryRemove(front, out _);
        foreach (var (general, axis) in Axes) if (hour - axis.Hour is < 0 or >= 2 * AxisRetryHours) Axes.TryRemove(general, out _);
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

    /// <summary>The breakthrough axis: its target province and its ring; local attacks stay on these.</summary>
    private static HashSet<int> Rings(int target)
    {
        var core = new HashSet<int> { target };
        var ring = new List<int> { target };
        for (var depth = 0; depth < 2; depth++) // the target and its neighbours
        {
            var next = new List<int>();
            foreach (var id in ring)
            foreach (var n in Game.Neighbours(Game.Province(id)))
                if (core.Add(n)) next.Add(n);
            ring = next;
        }
        return core;
    }

    /// <summary>
    /// Whether a local attack from <paramref name="from"/> may go to <paramref name="to"/>: off a theatre's line that has
    /// an axis today, anything; on it, only targets on the axis (the theatre's other sectors hold while it pushes).
    /// </summary>
    private static bool OnAxis(int tag, nint from, nint to)
    {
        var hour = Hour;
        foreach (var axis in Axes.Values)
            if (axis.Tag == tag && hour - axis.Hour is >= 0 and < AxisRetryHours && axis.Line.Contains(Game.Id(from)))
                return axis.Core.Contains(Game.Id(to));
        return true;
    }

    /// <summary>Whether the province is on one of the tag's breakthrough axes of the last two days (CAS backs it).</summary>
    private static bool OnAnyAxis(int tag, int province)
    {
        var hour = Hour;
        foreach (var axis in Axes.Values)
            if (axis.Tag == tag && hour - axis.Hour is >= 0 and < 2 * AxisRetryHours && axis.Core.Contains(province)) return true;
        return false;
    }
}
