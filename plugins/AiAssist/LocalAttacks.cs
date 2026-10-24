using System.Runtime.InteropServices;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Local attack targets: the micro attack candidate check, patched at its one call site ----

    private const int BlockedOfStateCategory = 0x15C; // vanilla's candidate loop skips such states

    private static delegate* unmanaged<nint, nint, nint, byte> _cantMove;
    private static nint _pocketCrumb;
    private static int _steered;
    // The last source province's alternatives: the candidate loop asks once per target, back to back on one thread. Keyed by
    // tag, not `us`: that is a stack slot of the caller.
    [ThreadStatic] private static (int Tag, nint From, int Hour, int World, int Pocket, int Relief, int Shortest, double Valuable) _alternatives;

    private void LoadPockets()
    {
        var site = Sig.Find(Signatures.MicroAttackCandidate) + 10;
        var cantMove = Sig.Find(Signatures.CantMoveInto);
        if (site + 5 + *(int*)(site + 1) != cantMove) throw new InvalidOperationException("micro attack candidate check: the call moved");
        _cantMove = (delegate* unmanaged<nint, nint, nint, byte>)cantMove;
        if (*(ushort*)(site + 5) != 0xC084) throw new InvalidOperationException("micro attack candidate check: no `test al,al` after the call");
        // `call CantMoveInto; test al,al` (7 bytes; the hook's jump needs more than 5) becomes a call of the filter and the
        // same test, for the `jnz skip` after it. rsp is 16-aligned at the call, home space reserved.
        _pocketCrumb = Breadcrumb.Label($"{Info.Id} pocket filter (asm hook {Breadcrumb.Where(site)})"); // before the hook goes live
        Patches.Asm(site, 7, "use64", $"mov rax, 0x{(nint)(delegate* unmanaged<nint, nint, nint, byte>)&PocketFilter:X}", "call rax", "test al, al");
    }

    /// <summary>
    /// The original check (nonzero = skip), plus: <see cref="RuledOut"/> targets are skipped, and of <paramref name="from"/>'s
    /// targets only the best kind stays, in order: a corridor to a pocket of ours (if it holds at least as many divisions as
    /// the best pocket would cut off), pocket-closing, on the axis, valuable, not lengthening the front. The game's random pick
    /// chooses among those. AI threads, read only.
    /// </summary>
    [UnmanagedCallersOnly]
    private static byte PocketFilter(nint us, nint from, nint to)
    {
        var crumb = Breadcrumb.Enter(_pocketCrumb);
        try
        {
            if (_cantMove(us, from, to) != 0) return 1;
            var tag = *(int*)us;
            var me = Game.Country(tag);
            if (me == 0 || !SheepMod.Active) return 0;
            var ruled = RuledOut(me, tag, from, to);
            if (ruled != 0)
            {
                Interlocked.Increment(ref ruled == Unsupplied ? ref _unsupplied : ref _steered);
                return 1;
            }
            if (!(_attackTargets || _axis)) return 0;
            var (hour, world) = (Game.Hour, Game.Generation);
            var alt = _alternatives;
            if (alt.Tag != tag || alt.From != from || alt.Hour != hour || alt.World != world)
            {
                alt = (tag, from, hour, world, 0, 0, int.MaxValue, 0.0);
                foreach (var id in _attackTargets ? Game.Neighbours(from) : []) // the axis alone needs none of these
                {
                    var c = Game.Province(id);
                    // The alternatives: targets vanilla and the checks above would let through.
                    if (c == 0 || !Province.Enemy(me, tag, c) || !Candidate(from, c) || _cantMove(us, from, c) != 0 || !Strong(from, c) || RuledOut(me, tag, from, c) != 0) continue;
                    alt.Pocket = Math.Max(alt.Pocket, Province.Pocket(me, tag, c));
                    alt.Relief = Math.Max(alt.Relief, Relief(me, tag, c));
                    alt.Shortest = Math.Min(alt.Shortest, Province.FrontChange(me, tag, c));
                    alt.Valuable = Math.Max(alt.Valuable, TargetValue(c));
                }
                _alternatives = alt;
            }
            var (_, _, _, _, pocket, relief, shortest, valuable) = alt;
            var skip = relief > 0 && relief >= pocket ? Relief(me, tag, to) == 0 // corridor to our pocket
                : pocket > 0 ? Province.Pocket(me, tag, to) == 0                          // pocket-closing
                : _axis && !OnAxis(tag, from, to) ? true                         // on the axis
                : valuable >= MinTargetValue ? TargetValue(to) < valuable / 2    // at least half the best value
                : shortest < 0 && Province.FrontChange(me, tag, to) > 0;                  // doesn't lengthen the front
            if (skip) Interlocked.Increment(ref _steered);
            return skip ? (byte)1 : (byte)0;
        }
        catch (Exception e)
        {
            Failed(e);
            return _cantMove(us, from, to);
        }
        finally { Breadcrumb.Leave(crumb); }
    }

    private const int Steered = 1, Unsupplied = 2;

    /// <summary>
    /// Why a local attack from <paramref name="from"/> on <paramref name="to"/> is ruled out, or 0. Steered: on a grad, except
    /// against fought-out spearheads; from a thin front; into terrain that cripples the armor; where an attack was lost lately
    /// (<see cref="BreakOff"/>). Unsupplied: supply can't follow. Pocket-closing and corridor targets pass all but the armor rule.
    /// </summary>
    private static int RuledOut(nint me, int tag, nint from, nint to)
    {
        bool? noPocket = null; // the flood fills run once, only when a rule asks
        bool NoPocket() => noPocket ??= Province.Pocket(me, tag, to) == 0 && Relief(me, tag, to) == 0;
        if (Grads.ContainsKey(tag) && !Exhausted(to) && NoPocket()) return Steered;
        if (_holdLine && OnThinLine(tag, from) && NoPocket()) return Steered;
        if (_armorTerrain && BadForArmor(tag, from, to)) return Steered;
        if (LostLately(tag, to) && NoPocket()) return Steered;
        if (_supplyAware && Province.Starved(me, tag, from, to, _supplyShare) && NoPocket()) return Unsupplied;
        return 0;
    }

    // tag -> (game hour, world, provinces on fronts too thin to attack from). Replaced whole, never mutated.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Hour, int World, HashSet<nint> Provinces)> ThinLines = new();

    /// <summary>
    /// <paramref name="from"/> is on one of the tag's fronts (<see cref="Line"/>) with under 1.2 divisions per held threatened
    /// province: attacking from there widens the holes. Rebuilt hourly; AI threads, read only.
    /// </summary>
    private static bool OnThinLine(int tag, nint from)
    {
        var (hour, world) = (Game.Hour, Game.Generation);
        if (world != _world) return false; // just loaded, not reset yet: Line is the old world's
        if (!ThinLines.TryGetValue(tag, out var thin) || thin.Hour != hour || thin.World != world)
        {
            var country = Game.Country(tag);
            if (country == 0) return false;
            var provinces = new HashSet<nint>();
            foreach (var e in Line)
            {
                if (e.Value.Tag != tag || hour - e.Value.Hour is < 0 or >= LineFreshHours) continue;
                var (held, standing) = (0, 0);
                foreach (var p in e.Value.Provinces)
                    if (Province.Controller(p) == tag)
                    {
                        held++;
                        standing += Standing(country, tag, p);
                    }
                if (held * 6 > standing * 5) provinces.UnionWith(e.Value.Provinces);
            }
            ThinLines[tag] = thin = (hour, world, provinces);
        }
        return thin.Provinces.Contains(from);
    }

    /// <summary>A neighbour vanilla's candidate loop would consider: passable border, enterable, not in a blocked state.</summary>
    private static bool Candidate(nint from, nint to)
    {
        if (Game.AdjacencyType(from, Game.Id(to)) == Province.Impassable || !Province.Enterable(to)) return false;
        var state = Province.State(to);
        var category = state == 0 ? 0 : *(nint*)(state + ResourcesOfState);
        return category == 0 || *(byte*)(category + BlockedOfStateCategory) == 0;
    }

    // ---- Relief: local attacks next to a pocket of ours open the corridor to it (part of AttackTargets) ----

    private const int MaxPocketProvinces = 40;
    private static readonly Dictionary<int, int> NoPockets = [];
    // tag -> (game hour, province id in a pocket of ours -> our divisions in that pocket). Replaced whole, never mutated;
    // ids and counts only, so a stale entry after a load is harmless.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Hour, Dictionary<int, int> Pockets)> Encircled = new();

    /// <summary>
    /// Our divisions in the pocket that taking <paramref name="target"/> links to friendly ground outside (a corridor, or a
    /// breakout from inside). 0 if none or AttackTargets is off.
    /// </summary>
    // ponytail: one-province corridors only; a pocket two enemy provinces deep has no single target that opens it.
    private static int Relief(nint me, int tag, nint target)
    {
        if (!_attackTargets) return 0;
        var pockets = OurPockets(me, tag);
        if (pockets.Count == 0) return 0;
        var (trapped, outside) = (0, false);
        foreach (var id in Game.Neighbours(target))
            if (pockets.TryGetValue(id, out var n)) trapped = Math.Max(trapped, n);
            else if (Game.Province(id) is var p && p != 0 && Province.Controller(p) > 0 && Province.Friendly(me, tag, p)) outside = true; // sea: controller 0
        return outside ? trapped : 0;
    }

    /// <summary>
    /// The tag's pockets, rebuilt hourly: a flood fill over friendly land from each province with our divisions; a region
    /// closed within <see cref="MaxPocketProvinces"/> provinces by enemy (not neutral) land is a pocket. AI threads, read only;
    /// racing threads may build it twice.
    /// </summary>
    // ponytail: bounded fill; encirclements bigger than MaxPocketProvinces are missed, and a separate enclave with none of
    // our divisions counts as "outside". A per-province pocket id if that ever links two pockets.
    private static Dictionary<int, int> OurPockets(nint me, int tag)
    {
        var hour = Game.Hour;
        if (Encircled.TryGetValue(tag, out var cached) && cached.Hour == hour) return cached.Pockets;
        var ours = new Dictionary<nint, int>();
        foreach (var unit in Game.Units(me))
            if (unit != 0 && Division.IsLand(unit) && Division.Location(unit) is var at && at != 0)
                ours[at] = ours.GetValueOrDefault(at) + 1;
        Dictionary<int, int>? pockets = null;
        var fills = new Dictionary<int, int>(); // province id -> the fill that reached it
        var fill = 0;
        foreach (var start in ours.Keys)
        {
            fill++;
            if (Province.Controller(start) <= 0 || !Province.Friendly(me, tag, start) || !fills.TryAdd(Game.Id(start), fill)) continue;
            var region = new List<nint> { start };
            var (open, enemy) = (false, false);
            for (var i = 0; i < region.Count && !open && region.Count <= MaxPocketProvinces; i++)
                foreach (var id in Game.Neighbours(region[i]))
                {
                    var p = Game.Province(id);
                    if (p == 0 || Province.Controller(p) <= 0) continue; // sea, wasteland
                    if (Province.Friendly(me, tag, p))
                    {
                        if (fills.TryAdd(id, fill)) region.Add(p);
                        else if (fills[id] != fill) open = true; // reached an earlier fill's region, which was too big
                    }
                    else if (Province.Enemy(me, tag, p)) enemy = true;
                    else open = true; // neutral land: a way out
                }
            if (open || !enemy || region.Count > MaxPocketProvinces) continue;
            var trapped = 0;
            foreach (var p in region) trapped += ours.GetValueOrDefault(p);
            foreach (var p in region) (pockets ??= [])[Game.Id(p)] = trapped;
        }
        var result = pockets ?? NoPockets;
        Encircled[tag] = (hour, result);
        return result;
    }

    // ---- Armor and terrain: the divisions' own terrain attack (unit data, so mods too) plus the river crossing ----

    private const int IndexOfTerrain = 0x88;
    private const int RiverAttack = 0x390, TerrainModifiers = 0x3F8, TerrainModifierSize = 0x28, AttackOfModifier = 0x10; // CArmy stats
    private const long Crippling = -25_000; // -25 % attack

    private static nint _riverSmall, _riverLarge; // the RIVER_CROSSING_PENALTY(_LARGE) defines
    private static bool _armorTerrain;

    /// <summary>
    /// Our divisions in <paramref name="from"/> are mostly armored or motorized, and their average attack into <paramref name="to"/>
    /// (terrain plus river crossing) is -25 % or worse, or they would cross a river into mountains, marsh, jungle or a city.
    /// </summary>
    private static bool BadForArmor(int tag, nint from, nint to)
    {
        var terrain = Province.Terrain(to);
        var index = terrain == 0 ? 0 : *(int*)(terrain + IndexOfTerrain);
        if (index <= 0) return false;
        var river = Game.AdjacencyType(from, Game.Id(to));
        var crossing = river == Province.LargeRiver ? *(long*)_riverLarge : river == Province.River ? *(long*)_riverSmall : 0;
        int ours = 0, mobile = 0;
        long attack = 0;
        foreach (var unit in Province.Units(from))
        {
            if (Division.Commander(unit) != tag) continue;
            ours++;
            if (!Division.Motorized(unit)) continue;
            var stats = Division.Stats(unit);
            var modifiers = *(nint*)(stats + TerrainModifiers);
            if (modifiers == 0) continue;
            mobile++;
            attack += *(long*)(modifiers + (long)TerrainModifierSize * (index - 1) + AttackOfModifier) + (river >= Province.River ? crossing + *(long*)(stats + RiverAttack) : 0);
        }
        if (mobile == 0 || mobile * 2 < ours) return false;
        return attack / mobile <= Crippling || river >= Province.River && (Province.TerrainCategory(to) & (Province.Mountain | Province.Marsh | Province.Jungle | Province.Urban)) != 0;
    }

    // ---- Target value: victory points, capitals, supply hubs, airfields, resources, and how well it is held ----

    private const int ResourcesOfState = 0x30, ResourceEntries = 0xF8, CapitalOfCountry = 0x1018;
    private const int EntrenchOfUnit = 0x458, MaxEntrenchOfUnit = 0x460; // CArmy: dig-in now and at most (1e5)
    private const int MinTargetValue = 30, FreshStrength = 70;
    private const long CapitalsCacheMs = 60_000;

    private static bool _attackTargets;
    private static int _taken;
    private static (long Until, HashSet<int> States) _capitals = (0, []);

    /// <summary>
    /// What taking the enemy province is worth: victory points, capital, supply hub, the state's airfields and resources (in
    /// full only on its main province), plus empty or worn defences; dug-in defenders count against it.
    /// </summary>
    private static double TargetValue(nint target)
    {
        var state = Province.State(target);
        if (Buildings.Db == 0 || state == 0) return 0;
        var main = State.MainProvince(state) == target;

        double value = 3 * Math.Min(Province.VictoryPoints(target), 30);
        if (Capitals().Contains(State.Id(state))) value += main ? 60 : 15;
        if (Province.BuildingLevel(target, Buildings.SupplyNodeSlot) > 0) value += 20;

        var air = 0;
        foreach (var type in Buildings.AirBaseTypes) air += Buildings.WorkingLevel(State.Buildings(state), type);
        long resources = 0;
        var definition = *(nint*)(state + ResourcesOfState);
        if (definition != 0)
            for (int i = 0, n = *(int*)(definition + ResourceEntries + 0xC); i < n; i++)
                resources += *(long*)(*(nint*)(definition + ResourceEntries) + 16 * i);
        value += (main ? 1 : 0.25) * (2 * air + Math.Min(resources / Native.FixedPoint, 40));

        var defenders = Province.Units(target);
        if (defenders.Length == 0) return value + MinTargetValue; // empty: always worth taking
        long org = 0, strength = 0;
        var dugIn = 0;
        foreach (var unit in defenders)
        {
            org += Division.Organisation(unit);
            strength += Division.Strength(unit);
            if (*(long*)(unit + MaxEntrenchOfUnit) > 0 && *(long*)(unit + EntrenchOfUnit) * 100 >= 60 * *(long*)(unit + MaxEntrenchOfUnit)) dugIn++;
        }
        if (org / defenders.Length < 30_000 || strength / defenders.Length < 50_000) return value + 10;
        return value - 5 * defenders.Length * (1 + Province.FortLevel(target) / 2.0) - 4 * dugIn;
    }

    /// <summary>Every country's capital state id (cached a minute).</summary>
    private static HashSet<int> Capitals()
    {
        var cached = _capitals;
        if (Environment.TickCount64 < cached.Until) return cached.States;
        var states = new HashSet<int>();
        foreach (var country in Game.Countries)
            if (country != 0) states.Add(*(int*)(country + CapitalOfCountry));
        _capitals = (Environment.TickCount64 + CapitalsCacheMs, states);
        return states;
    }

    /// <summary>
    /// Sends one fresh division from a stack of ours into each empty enemy province next to it (one always stays). Vanilla's
    /// local attacks often skip them. A plain move: a front division returns to its front by itself. Main thread.
    /// </summary>
    private static void TakeEmpty(nint country, int tag, nint[] units, long now)
    {
        var taken = 0;
        foreach (var here in units.Where(u => Division.IsLand(u) && Division.Commander(u) == tag && Division.Location(u) != 0 && !Division.IsHeadquarters(u)) // an HQ holds nothing
                     .GroupBy(Division.Location))
        {
            var fresh = here.Where(u => Spare(u, tag) && Percent(Division.Strength(u)) >= FreshStrength).ToList();
            var spare = Math.Min(here.Count() - 1, fresh.Count); // one stays
            foreach (var id in Game.Neighbours(here.Key))
            {
                if (spare == 0) break;
                var target = Game.Province(id);
                if (target == 0 || Gap.ContainsKey(id) || Province.Divisions(target) > 0 || !Province.Enemy(country, tag, target)
                    || _supplyAware && Province.Starved(country, tag, here.Key, target, _supplyShare)) continue; // supply must follow, as for local attacks
                if (!Send(fresh[spare - 1], id, now, SentHours)) continue;
                spare--;
                Interlocked.Increment(ref _taken);
                if (++taken == MaxMoves) return;
            }
        }
    }

    // ---- Supply-aware attacks: no local attacks the attackers' supply can't follow ----

    private static bool _supplyAware;
    private static int _supplyShare, _unsupplied;
}
