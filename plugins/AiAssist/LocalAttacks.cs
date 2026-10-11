using System.Runtime.InteropServices;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Local attack targets: the micro attack candidate check, patched at its one call site ----

    private const int BlockedOfStateCategory = 0x15C; // what vanilla's candidate loop skips

    private static delegate* unmanaged<nint, nint, nint, byte> _cantMove;
    private static nint _pocketCrumb;
    private static int _steered;

    private void LoadPockets()
    {
        var site = Sig.Find(Signatures.MicroAttackCandidate) + 10;
        var cantMove = Sig.Find(Signatures.CantMoveInto);
        if (site + 5 + *(int*)(site + 1) != cantMove) throw new InvalidOperationException("micro attack candidate check: the call moved");
        _cantMove = (delegate* unmanaged<nint, nint, nint, byte>)cantMove;
        if (*(ushort*)(site + 5) != 0xC084) throw new InvalidOperationException("micro attack candidate check: no `test al,al` after the call");
        // `call CantMoveInto; test al,al` (7 bytes: the hook's jump takes more than the call's 5) becomes a call of the filter
        // and the same test, for the `jnz skip` right after. rsp is 16-aligned at the call and its home space reserved.
        _pocketCrumb = Breadcrumb.Label($"{Info.Id} pocket filter (asm hook {Breadcrumb.Where(site)})"); // before the hook goes live
        Patches.Asm(site, 7, "use64", $"mov rax, 0x{(nint)(delegate* unmanaged<nint, nint, nint, byte>)&PocketFilter:X}", "call rax", "test al, al");
    }

    /// <summary>
    /// Like the original check (nonzero = skip this target), but targets <see cref="RuledOut"/> are skipped (and don't count
    /// as alternatives), and only the best kind of target of <paramref name="from"/> stays: ones opening a corridor to a pocket of ours (when it holds at least as many of our divisions as the best
    /// pocket-closing target cuts off), else pocket-closing ones, else ones on the axis, else valuable ones, else none that
    /// lengthen the front; the game's random pick then takes one of those. Vanilla's own rejections stay. AI threads: read only.
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
            var (pocket, relief, shortest, valuable) = (0, 0, int.MaxValue, 0.0);
            foreach (var id in _attackTargets ? Game.Neighbours(from) : []) // the axis alone needs none of these
            {
                var c = Game.Province(id);
                // The other targets this attack could take instead, as vanilla and the checks above would let them through;
                // a strength test failure fails vanilla's candidate loop anyway.
                if (c == 0 || !Province.Enemy(me, tag, c) || !Candidate(from, c) || _cantMove(us, from, c) != 0 || !Strong(from, c) || RuledOut(me, tag, from, c) != 0) continue;
                pocket = Math.Max(pocket, Province.Pocket(me, tag, c));
                relief = Math.Max(relief, Relief(me, tag, c));
                shortest = Math.Min(shortest, Province.FrontChange(me, tag, c));
                valuable = Math.Max(valuable, TargetValue(c));
            }
            var skip = relief > 0 && relief >= pocket ? Relief(me, tag, to) == 0 // the corridor to our own pocket first
                : pocket > 0 ? Province.Pocket(me, tag, to) == 0                          // pocket targets only
                : _axis && !OnAxis(tag, from, to) ? true                         // nothing off the breakthrough axis
                : valuable >= MinTargetValue ? TargetValue(to) < valuable / 2    // worth at least half the best
                : shortest < 0 && Province.FrontChange(me, tag, to) > 0;                  // nothing that lengthens the front
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
    /// Why a local attack from <paramref name="from"/> on <paramref name="to"/> is off whatever else it could take (0: it isn't):
    /// on its grad only fought-out spearheads, pockets and the way to its own (Steered); from a thinly manned front only pockets
    /// and the way to its own (Steered); not into terrain that cripples the armor
    /// there (Steered); not where supply can't follow, unless it closes a pocket or opens the way to ours (Unsupplied).
    /// </summary>
    private static int RuledOut(nint me, int tag, nint from, nint to)
    {
        if (Grads.ContainsKey(tag) && !Exhausted(to) && Province.Pocket(me, tag, to) == 0 && Relief(me, tag, to) == 0) return Steered;
        if (_holdLine && OnThinLine(tag, from) && Province.Pocket(me, tag, to) == 0 && Relief(me, tag, to) == 0) return Steered;
        if (_armorTerrain && BadForArmor(tag, from, to)) return Steered;
        if (_supplyAware && Province.Starved(me, tag, from, to, _supplyShare) && Province.Pocket(me, tag, to) == 0 && Relief(me, tag, to) == 0) return Unsupplied;
        return 0;
    }

    // tag -> (game hour, world, our provinces on fronts too thinly manned to attack from). Published whole, never changed.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Hour, int World, HashSet<nint> Provinces)> ThinLines = new();

    /// <summary>
    /// <paramref name="from"/> is on one of the tag's fronts (<see cref="_line"/>) that stands under ~1.2 divisions per threatened
    /// province it holds: attacking from there widens the holes. Rebuilt once per game hour; AI threads, read only.
    /// </summary>
    private static bool OnThinLine(int tag, nint from)
    {
        var (hour, world) = (Game.Hour, Game.Generation);
        if (world != _world) return false; // a load the reset hasn't caught up with: _line is the old world's
        if (!ThinLines.TryGetValue(tag, out var thin) || thin.Hour != hour || thin.World != world)
        {
            var country = Game.Country(tag);
            if (country == 0) return false;
            var provinces = new HashSet<nint>();
            foreach (var e in _line)
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

    /// <summary>A neighbour vanilla's candidate loop would weigh at all (before its own CantMoveInto): not behind an impassable
    /// border, a province units can enter, in no blocked state. Others must not make the real targets look worse.</summary>
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
    // tag -> (game hour, province id in a pocket of ours -> the tag's divisions in that pocket). Published whole, never
    // mutated; ids and counts only, so a stale entry after a load is harmless (cleared anyway).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Hour, Dictionary<int, int> Pockets)> Encircled = new();

    /// <summary>
    /// Our divisions in a pocket that taking the enemy <paramref name="target"/> connects to ground of ours or an ally's
    /// outside every pocket (it is the corridor; also a breakout from inside); 0 when it opens none or AttackTargets is off.
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
    /// The tag's encircled ground, rebuilt once per game hour: from each province holding its land divisions, a flood fill
    /// over our and allied land; a region that closes within <see cref="MaxPocketProvinces"/> provinces with enemy land
    /// around it (no neutral land) is a pocket. AI threads: read only; a racing thread may build the same map twice.
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
                        else if (fills[id] != fill) open = true; // joins a region an earlier fill gave up on: too big
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

    // ---- Armor and terrain: the divisions' own terrain attack (unit data: modded games too) plus the river crossing ----

    private const int IndexOfTerrain = 0x88;
    private const int RiverAttack = 0x390, TerrainModifiers = 0x3F8, TerrainModifierSize = 0x28, AttackOfModifier = 0x10; // CArmy stats
    private const long Crippling = -25_000; // -25 % attack

    private static nint _riverSmall, _riverLarge; // the RIVER_CROSSING_PENALTY(_LARGE) defines
    private static bool _armorTerrain;

    /// <summary>
    /// Mostly armored or motorized divisions of ours in <paramref name="from"/> (hardness 0.4+ or armor), whose average
    /// attack into <paramref name="to"/>'s terrain, river crossing included, is -25 % or worse, or that would cross a river
    /// into mountains, marsh, jungle or a city.
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
    /// What taking the enemy province is worth: its victory points, a capital, a supply hub, the state's airfields and
    /// resources (in full on the state's main province), plus empty or worn defenders; a dug-in stack counts against it.
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

        // How it is held.
        var defenders = Province.Units(target);
        if (defenders.Length == 0) return value + MinTargetValue; // free for the taking: always a target worth going for
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
    /// An empty enemy province next to a stack of ours gets one fresh division from it (one always stays): vanilla's local
    /// attacks often skip them (not where supply can't follow, under SupplyAwareAttacks). A plain move; a front division goes
    /// back to its front by itself afterwards. Main thread.
    /// </summary>
    private static void TakeEmpty(nint country, int tag, nint[] units, long now)
    {
        var taken = 0;
        foreach (var here in units.Where(u => Division.IsLand(u) && Division.Commander(u) == tag && Division.Location(u) != 0)
                     .GroupBy(Division.Location))
        {
            var fresh = here.Where(u => Spare(u, tag) && Percent(Division.Strength(u)) >= FreshStrength).ToList();
            var spare = Math.Min(here.Count() - 1, fresh.Count); // one stays
            foreach (var id in Game.Neighbours(here.Key))
            {
                if (spare == 0) break;
                var target = Game.Province(id);
                if (target == 0 || _gap.ContainsKey(id) || Province.Divisions(target) > 0 || !Province.Enemy(country, tag, target)
                    || _supplyAware && Province.Starved(country, tag, here.Key, target, _supplyShare)) continue; // supply must follow it there, as with local attacks
                if (!Send(fresh[--spare], id, now, SentHours)) continue;
                Interlocked.Increment(ref _taken);
                if (++taken == MaxMoves) return;
            }
        }
    }

    // ---- Supply-aware attacks: no local attacks the attackers' supply can't follow ----

    private static bool _supplyAware;
    private static int _supplyShare, _unsupplied;
}
