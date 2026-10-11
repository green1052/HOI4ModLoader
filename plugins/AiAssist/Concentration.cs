using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Concentration: quiet and weak-enemy fronts ask for fewer divisions, fronts facing a strong enemy for more ----

    private const int MinWanted = 0x90, DesiredWanted = 0x94, MaxWanted = 0x98, FixedRequest = 0x9C, CountryOfFront = 0xE0, FrontOfAiFront = 0xE8;
    private const int SectionOfAiFront = 0xF0, AreaDefense = 0x104, InvasionTarget = 0x120, AttackingFront = 0x218 + 0x84, OtherRequest = 0x1DC;
    private const int FactionMemberOfCountry = 0xF88, FactionOfMember = 0x290; // CCountry -> member status -> its CFaction (CIsInFactionWith compares the pointers)
    private const int SectionsOfFront = 0x58, IdOfSection = 8, PairsOfSection = 0x48, CombatsInProvince = 0x17C; // province: combats count
    private const double QuietFactor = 0.25, HotBoost = 1.25;

    [Function(CallingConventions.Microsoft)] public delegate nint GeneralRetFn(nint general);

    private IHook<GeneralRetFn>? _unitRequests;
    private int _concentrated;
    private static int _stationed;
    private static bool _concentration, _holdLine, _defensePriority, _axis, _reserve, _beachDefense, _stationing;
    private const int FactorHours = 24, CalmHours = 168;
    private const int AxisRetryHours = 24, ThinSectorWeight = 8;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, double Factor)> Factors = new(); // AI front -> its factor, for a day
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> LastBattle = new();                 // AI front -> hour of its last battle
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Hour, HashSet<int> Core)> Axes = new(); // tag -> its breakthrough axis, for a day

    private void LoadConcentration()
    {
        _unitRequests = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateFrontUnitRequests), general =>
        {
            var result = _unitRequests!.OriginalFunction(general);
            Safely(() =>
            {
                if (SheepMod.Active) Concentrate(general);
            });
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
                if (!SheepMod.Active) return;
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
        // FrontLine owns the only ApplyArmiesBalancing hook and applies this through it: a second hook stacks detours, which crashes Reloaded.
        Patches.KeepFromBalancing += unit => SheepMod.Active && Pinned(unit);
    }

    /// <summary>Rescales each land front's requests by the enemy actually facing it, before the theatre's divisions are shared out.</summary>
    private void Concentrate(nint general)
    {
        var other = stackalloc int[2]; // {tag, tag index} of a province's controller
        var spots = new Dictionary<int, (nint Province, int Weight)>(); // tag -> the thinnest enemy sector on its line, for the axis
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
            var warFactions = new HashSet<nint>();
            foreach (var c in Game.Countries)
            {
                var t = c == 0 ? 0 : *(int*)(c + 8);
                if (t <= 0 || t == tag) continue;
                (other[0], other[1]) = (t, Game.TagIndex(t));
                if (_atWar(country, other) != 0) warFactions.Add(FactionOf(c));
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
                    (other[0], other[1]) = (controller, controller > 0 ? Game.TagIndex(controller) : 0);
                    held = enemyHeld[theirs] = controller > 0 && controller != tag && _atWar(country, other) != 0;
                    if (held) enemy += *(int*)(theirs + DivisionsInProvince);
                    else if (controller > 0 && controller != tag && warFactions.Contains(FactionOf(Game.Country(controller)))) aligned = true;
                }
                if (held && ours != 0)
                {
                    contact.Add(ours);
                    if (*(int*)(theirs + DivisionsInProvince) > 0) threatened.Add(ours);
                }
            }
            var hostile = enemyHeld.ContainsValue(true);
            var hour = *(int*)(Game.State + HourOfGameState);
            if (threatened.Count > 0) _line[front] = (hour, tag, threatened); // the attack gate and the backfill read this
            if (_axis && hostile) // the day's breakthrough axis: the enemy province on this line that is easiest to take
                foreach (var our in threatened)
                foreach (var id in Game.Neighbours(our))
                {
                    var p = Game.Province(id);
                    if (p == 0 || !Enemy(country, tag, p)) continue;
                    var weight = SpotWeight(p, our);
                    if (!spots.TryGetValue(tag, out var s) || weight < s.Weight) spots[tag] = (p, weight);
                }
            if (aligned || *(int*)(front + AreaDefense) != 0 || *(nint*)(front + InvasionTarget) != 0) continue; // a warring country's faction border, garrisons and invasions: as the game asks
            if (hostile && (*(byte*)(front + FixedRequest) != 0 || *(byte*)(front + OtherRequest) != 0)) continue; // fixed shares on a front that is at war; quiet ones are thinned
            var enemyProvinces = enemyHeld.Count(e => e.Value);
            int min = *(int*)(front + MinWanted), desired = *(int*)(front + DesiredWanted), max = *(int*)(front + MaxWanted);
            // Requests are redone every hour; a factor that followed every enemy move would march divisions back and forth.
            // So a factor holds for a day, and a front gives divisions away only a week after its last battle.
            if (fighting) LastBattle[front] = hour;
            var factor = 1.0;
            if (_concentration)
            {
                if (Factors.TryGetValue(front, out var kept) && hour - kept.Hour is >= 0 and < FactorHours) factor = kept.Factor; // (negative: another save)
                else Factors[front] = (hour, factor = !hostile ? QuietFactor : enemy > min ? HotBoost : 1);
                if (fighting || LastBattle.TryGetValue(front, out var battle) && hour - battle is >= 0 and < CalmHours) factor = Math.Max(factor, 1);
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
        var now = *(int*)(Game.State + HourOfGameState);
        foreach (var (tag, (province, weight)) in spots)
        {
            if (weight > ThinSectorWeight) continue;                                  // no thin sector anywhere: no axis today
            if (Axes.TryGetValue(tag, out var axis) && now - axis.Hour is >= 0 and < AxisRetryHours) continue; // held a day: divisions don't zig-zag
            Axes[tag] = (now, Rings(Id(province)));
        }
    }

    /// <summary>How hard the enemy province is to take from ours: its divisions and forts, more for harsh terrain or a river in front of it.</summary>
    private static int SpotWeight(nint target, nint from)
    {
        var weight = 2 * Divisions(target) + _fortLevel(target);
        var terrain = *(nint*)(*(nint*)(target + StaticOfProvince) + TerrainOfStatic);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban | Marsh | Jungle)) != 0) weight += 3;
        if (Adjacency(from, Id(target)) >= River) weight += 2;
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

    /// <summary>Whether a local attack target is on the tag's breakthrough axis (always true when there is none).</summary>
    private static bool OnAxis(int tag, nint to)
    {
        if (!Axes.TryGetValue(tag, out var axis)) return true;
        if (*(int*)(Game.State + HourOfGameState) - axis.Hour >= AxisRetryHours) return true; // stale: the next Concentrate replaces it
        return axis.Core.Contains(Id(to));
    }
}
