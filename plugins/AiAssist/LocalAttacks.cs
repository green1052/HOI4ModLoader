using System.Runtime.InteropServices;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Local attack targets: the micro attack candidate check, patched at its one call site ----

    private const int ControllerOfProvince = 0x188, DivisionsInProvince = 0x11C;

    private const int FlagsOfStatic = 0xD2, BlockedOfStateCategory = 0x15C, Impassable = 4; // what vanilla's candidate loop skips

    private static delegate* unmanaged<nint, nint, nint, byte> _cantMove;
    private static nint _pocketCrumb;
    private static int _steered;

    private void LoadPockets()
    {
        var site = Sig.Find(Signatures.MicroAttackCandidate) + 10;
        var cantMove = Sig.Find(Signatures.CantMoveInto);
        if (site + 5 + *(int*)(site + 1) != cantMove) throw new InvalidOperationException("micro attack candidate check: the call moved");
        _cantMove = (delegate* unmanaged<nint, nint, nint, byte>)cantMove;
        var river = Sig.Find(Signatures.RiverPenalty);
        _riverSmall = river + 14 + *(int*)(river + 10); // mov rdx,[rip+disp] (7 bytes at +7)
        _riverLarge = river + 22 + *(int*)(river + 18); // cmovnz rdx,[rip+disp] (8 bytes at +14)
        if (*(ushort*)(site + 5) != 0xC084) throw new InvalidOperationException("micro attack candidate check: no `test al,al` after the call");
        // `call CantMoveInto; test al,al` (7 bytes: the hook's jump takes more than the call's 5) becomes a call of the filter
        // and the same test, for the `jnz skip` right after. rsp is 16-aligned at the call and its home space reserved.
        _pocketCrumb = Breadcrumb.Label($"{Info.Id} pocket filter (asm hook {Breadcrumb.Where(site)})"); // before the hook goes live
        Patches.Asm(site, 7, "use64", $"mov rax, 0x{(nint)(delegate* unmanaged<nint, nint, nint, byte>)&PocketFilter:X}", "call rax", "test al, al");
    }

    /// <summary>
    /// Like the original check (nonzero = skip this target), but only the best kind of target of <paramref name="from"/>
    /// stays: pocket-closing ones, else ones on the axis, else valuable ones, else none that lengthen the front; the game's
    /// random pick then takes one of those. Vanilla's own rejections stay. AI threads: read only.
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
            if (_armorTerrain && BadForArmor(tag, from, to))
            {
                Interlocked.Increment(ref _steered);
                return 1;
            }
            if (_supplyAware && Starved(me, tag, from, to) && Pocket(me, tag, to) == 0)
            {
                Interlocked.Increment(ref _unsupplied);
                return 1;
            }
            if (!(_attackTargets || _axis)) return 0;
            var (pocket, shortest, valuable) = (0, int.MaxValue, 0.0);
            foreach (var id in _attackTargets ? Game.Neighbours(from) : []) // the axis alone needs none of these
            {
                var c = Game.Province(id);
                if (c == 0 || !Enemy(me, tag, c) || !Candidate(from, c) || _cantMove(us, from, c) != 0 || _armorTerrain && BadForArmor(tag, from, c)
                    || _supplyAware && Starved(me, tag, from, c) && Pocket(me, tag, c) == 0) continue;
                if (Divisions(from) < Divisions(c) * (_fortLevel(c) / 2 + 1)) continue; // fails vanilla's strength test anyway
                pocket = Math.Max(pocket, Pocket(me, tag, c));
                shortest = Math.Min(shortest, FrontChange(me, tag, c));
                valuable = Math.Max(valuable, TargetValue(c));
            }
            var skip = pocket > 0 ? Pocket(me, tag, to) == 0                  // pocket targets only
                : _axis && !OnAxis(tag, from, to) ? true                        // nothing off the breakthrough axis
                : valuable >= MinTargetValue ? TargetValue(to) < valuable / 2   // worth at least half the best
                : shortest < 0 && FrontChange(me, tag, to) > 0;                 // nothing that lengthens the front
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

    /// <summary>A neighbour vanilla's candidate loop would weigh at all (before its own CantMoveInto): not behind an impassable
    /// border, a province units can enter, in no blocked state. Others must not make the real targets look worse.</summary>
    private static bool Candidate(nint from, nint to)
    {
        if (Game.AdjacencyType(from, Game.Id(to)) == Impassable) return false;
        var info = *(nint*)(to + StaticOfProvince);
        if (info == 0 || (*(byte*)(info + FlagsOfStatic) & 1) == 0) return false;
        var state = *(nint*)(to + StateOfProvince);
        var category = state == 0 ? 0 : *(nint*)(state + ResourcesOfState);
        return category == 0 || *(byte*)(category + BlockedOfStateCategory) == 0;
    }

    /// <summary>Enemy divisions that taking <paramref name="target"/> cuts off: in enemy provinces next to it whose other land neighbours are all ours or allied.</summary>
    // ponytail: one-province pockets only; a flood fill bounded by NAI.POCKET_DISTANCE_MAX if bigger encirclements are missed.
    private static int Pocket(nint me, int tag, nint target)
    {
        var cut = 0;
        var targetId = Game.Id(target);
        foreach (var id in Game.Neighbours(target))
        {
            var e = Game.Province(id);
            if (e == 0 || Divisions(e) == 0 || !Enemy(me, tag, e)) continue;
            if (Game.Neighbours(e).All(n => n == targetId || Game.Province(n) is var q && (q == 0 || *(int*)(q + ControllerOfProvince) == 0 || Friendly(me, tag, q))))
                cut += Divisions(e);
        }
        return cut;
    }

    /// <summary>
    /// How the front's length changes when <paramref name="target"/> is taken: its enemy neighbours become new front, its
    /// neighbours of ours stop being front. Negative = shorter.
    /// </summary>
    private static int FrontChange(nint me, int tag, nint target)
    {
        var change = 0;
        foreach (var id in Game.Neighbours(target))
        {
            var n = Game.Province(id);
            if (n == 0 || *(int*)(n + ControllerOfProvince) == 0) continue; // sea
            if (Enemy(me, tag, n)) change++;
            else if (Friendly(me, tag, n)) change--;
        }
        return change;
    }

    // ---- Armor and terrain: the divisions' own terrain attack (unit data: modded games too) plus the river crossing ----

    private const int UnitsInProvince = 0x110, StaticOfProvince = 0xB8, TerrainOfStatic = 0xA8;
    private const int IndexOfTerrain = 0x88, CategoryOfTerrain = 0x8C;
    private const int CommanderOfUnit = 0x1D8, StatsOfUnit = 0x138;
    private const int Hardness = 0xB8, Armor = 0x298, RiverAttack = 0x390, TerrainModifiers = 0x3F8, TerrainModifierSize = 0x28, AttackOfModifier = 0x10;
    private const int Mountain = 0x8, Urban = 0x10, Marsh = 0x2000, Jungle = 0x10000;
    private const int River = 2, LargeRiver = 3;
    private const long Armored = 40_000, Crippling = -25_000; // hardness 0.4; -25 % attack

    private static nint _riverSmall, _riverLarge; // the RIVER_CROSSING_PENALTY(_LARGE) defines
    private static bool _armorTerrain;

    /// <summary>
    /// Mostly armored or motorized divisions of ours in <paramref name="from"/> (hardness 0.4+ or armor), whose average
    /// attack into <paramref name="to"/>'s terrain, river crossing included, is -25 % or worse, or that would cross a river
    /// into mountains, marsh, jungle or a city.
    /// </summary>
    private static bool BadForArmor(int tag, nint from, nint to)
    {
        var terrain = Terrain(to);
        var index = terrain == 0 ? 0 : *(int*)(terrain + IndexOfTerrain);
        if (index <= 0) return false;
        var river = Game.AdjacencyType(from, Game.Id(to));
        var crossing = river == LargeRiver ? *(long*)_riverLarge : river == River ? *(long*)_riverSmall : 0;
        int ours = 0, mobile = 0;
        long attack = 0;
        foreach (var unit in Native.PdxArray(from + UnitsInProvince))
        {
            if (*(int*)(unit + CommanderOfUnit) != tag) continue;
            ours++;
            var stats = *(nint*)(unit + StatsOfUnit);
            if (stats == 0 || *(long*)(stats + Hardness) < Armored && *(long*)(stats + Armor) <= 0) continue;
            var modifiers = *(nint*)(stats + TerrainModifiers);
            if (modifiers == 0) continue;
            mobile++;
            attack += *(long*)(modifiers + (long)TerrainModifierSize * (index - 1) + AttackOfModifier) + (river >= River ? crossing + *(long*)(stats + RiverAttack) : 0);
        }
        if (mobile == 0 || mobile * 2 < ours) return false;
        return attack / mobile <= Crippling || river >= River && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Marsh | Jungle | Urban)) != 0;
    }

    // ---- Target value: victory points, capitals, supply hubs, airfields, resources, and how well it is held ----

    private const int VictoryPoints = 0x38, StateOfProvince = 0xC0, BuildingsOfProvince = 0x190;
    private const int ProvincesOfState = 0x18, IdOfState = 0x58, BestVictoryPoints = 0x85C, BuildingsOfState = 0x120, ResourcesOfState = 0x30;
    // CBuildingDatabase (0x3F0 bytes): 0x3A0 supply_node, 0x3A8 rail_way, 0x3B0 infrastructure, 0x3B8 naval_base (the slot
    // Signatures.BuildingDbCall reads), 0x3C0 arms_factory, 0x3C8 industrial_complex; 0x298 the air base types.
    private const int ResourceEntries = 0xF8, SupplyNodeType = 0x3A0, AirBaseTypes = 0x298, CapitalOfCountry = 0x1018;
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
        var db = *(nint*)_buildingDb;
        var state = *(nint*)(target + StateOfProvince);
        if (db == 0 || state == 0) return 0;
        var index = *(int*)(state + BestVictoryPoints);
        var main = index >= 0 && index < *(int*)(state + ProvincesOfState + 0xC) && ((nint*)*(nint*)(state + ProvincesOfState))[index] == target;

        double value = 3 * Math.Min(*(int*)(target + VictoryPoints), 30);
        if (Capitals().Contains(*(int*)(state + IdOfState))) value += main ? 60 : 15;
        var hub = _findBuilding(target + BuildingsOfProvince, *(nint*)(db + SupplyNodeType)); // nodes (and air bases below) are always defined
        if (hub != 0 && *(short*)(hub + 0x40) > 0) value += 20;

        var air = 0;
        var types = (nint*)*(nint*)(db + AirBaseTypes);
        for (int i = 0, n = *(int*)(db + AirBaseTypes + 0xC); i < n; i++)
            if (_findBuilding(state + BuildingsOfState, types[i]) is var b && b != 0) air += *(short*)(b + 0x42);
        long resources = 0;
        var definition = *(nint*)(state + ResourcesOfState);
        if (definition != 0)
            for (int i = 0, n = *(int*)(definition + ResourceEntries + 0xC); i < n; i++)
                resources += *(long*)(*(nint*)(definition + ResourceEntries) + 16 * i);
        value += (main ? 1 : 0.25) * (2 * air + Math.Min(resources / Native.FixedPoint, 40));

        // How it is held.
        var defenders = Native.PdxArray(target + UnitsInProvince);
        if (defenders.Length == 0) return value + MinTargetValue; // free for the taking: always a target worth going for
        long org = 0, strength = 0;
        var dugIn = 0;
        foreach (var unit in defenders)
        {
            org += Ratio(unit, OrgRatioSlot);
            strength += Ratio(unit, StrengthRatioSlot);
            if (*(long*)(unit + MaxEntrenchOfUnit) > 0 && *(long*)(unit + EntrenchOfUnit) * 100 >= 60 * *(long*)(unit + MaxEntrenchOfUnit)) dugIn++;
        }
        if (org / defenders.Length < 30_000 || strength / defenders.Length < 50_000) return value + 10;
        return value - 5 * defenders.Length * (1 + _fortLevel(target) / 2.0) - 4 * dugIn;
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
    /// attacks often skip them. A plain move; a front division goes back to its front by itself afterwards. Main thread.
    /// </summary>
    private static void TakeEmpty(nint country, int tag, nint[] units, long now)
    {
        var taken = 0;
        foreach (var here in units.Where(u => *(int*)(u + KindOfUnit) == 0 && *(int*)(u + CommanderOfUnit) == tag && *(nint*)(u + LocationOfUnit) != 0)
                     .GroupBy(u => *(nint*)(u + LocationOfUnit)))
        {
            var fresh = here.Where(u => Spare(u, tag) && Percent(u, StrengthRatioSlot) >= FreshStrength).ToList();
            var spare = Math.Min(here.Count() - 1, fresh.Count); // one stays
            foreach (var id in Game.Neighbours(here.Key))
            {
                if (spare == 0) break;
                var target = Game.Province(id);
                if (target == 0 || _gap.ContainsKey(id) || Divisions(target) > 0 || !Enemy(country, tag, target)) continue;
                if (!Send(fresh[--spare], id, now, SentMs)) continue;
                Interlocked.Increment(ref _taken);
                if (++taken == MaxMoves) return;
            }
        }
    }

    // ---- Supply-aware attacks: no local attacks the attackers' supply can't follow ----

    // CSupplySystem (gamestate+0x3D8): per-province records by id at +0x138 (0xD8 bytes, count at +0x144).
    // Record +0 _TotalNeed (left after local nodes), +8 supply flowing in (all entries); zeroed and rebuilt each UpdateSupply.
    private const int SupplyOfGameState = 0x3D8, FlowOfSupply = 0x138, FlowSize = 0xD8;
    private const int NeedOfFlow = 0, SuppliedOfFlow = 8;

    private static bool _supplyAware;
    private static int _supplyShare, _unsupplied;

    /// <summary>
    /// Supply only flows through our side's provinces, so once <paramref name="to"/> is taken its supply comes over its land
    /// neighbours held by us or allies. Starved: <paramref name="from"/> gets less than SupplyAwareMinPercent % of what it
    /// needs and no other friendly neighbour of the target has that much spare (flow in minus its own need).
    /// AI threads, read only: the game rebuilds the flow on the main thread each hour, before the AI pass.
    /// </summary>
    private static bool Starved(nint me, int tag, nint from, nint to)
    {
        var state = Game.State;
        var supply = state == 0 ? 0 : *(nint*)(state + SupplyOfGameState);
        if (supply == 0) return false;
        var flows = *(nint*)(supply + FlowOfSupply);
        var count = *(int*)(supply + FlowOfSupply + 0xC);
        var fromId = Game.Id(from);
        if (flows == 0 || (uint)fromId >= (uint)count) return false;
        var at = flows + (nint)FlowSize * fromId;
        var need = *(long*)(at + NeedOfFlow); // the attackers and whatever else draws supply there
        var wanted = need * _supplyShare / 100;
        if (need <= 0 || *(long*)(at + SuppliedOfFlow) >= wanted) return false;
        foreach (var id in Game.Neighbours(to))
        {
            var n = Game.Province(id);
            if (n == 0 || n == from || (uint)id >= (uint)count || *(int*)(n + ControllerOfProvince) <= 0 || !Friendly(me, tag, n)) continue; // sea: controller 0
            var flow = flows + (nint)FlowSize * id;
            if (*(long*)(flow + SuppliedOfFlow) - *(long*)(flow + NeedOfFlow) >= wanted) return false;
        }
        return true;
    }
}
