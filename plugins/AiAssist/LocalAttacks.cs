using System.Runtime.InteropServices;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Pocket closing: the micro attack candidate check, patched at its one call site ----

    private const int ControllerOfProvince = 0x188, DivisionsInProvince = 0x11C;

    private static delegate* unmanaged<nint, nint, nint, byte> _cantMove;
    private static int _steered, _pocketFailed;
    private static Logger? _log;

    private void LoadPockets()
    {
        var site = Sig.Find(Signatures.MicroAttackCandidate) + 10;
        var cantMove = Sig.Find(Signatures.CantMoveInto);
        if (site + 5 + *(int*)(site + 1) != cantMove) throw new InvalidOperationException("micro attack candidate check: the call moved");
        _cantMove = (delegate* unmanaged<nint, nint, nint, byte>)cantMove;
        var river = Sig.Find(Signatures.RiverPenalty);
        _riverSmall = river + 14 + *(int*)(river + 10); // mov rdx,[rip+disp] (7 bytes at +7)
        _riverLarge = river + 22 + *(int*)(river + 18); // cmovnz rdx,[rip+disp] (8 bytes at +14)
        _log = Log;
        if (*(ushort*)(site + 5) != 0xC084) throw new InvalidOperationException("micro attack candidate check: no `test al,al` after the call");
        // `call CantMoveInto; test al,al` (7 bytes: the hook's jump takes more than the call's 5) becomes a call of the filter
        // and the same test, for the `jnz skip` right after. rsp is 16-aligned at the call and its home space reserved.
        // Its first call comes from an AI thread: compile it here first.
        Patches.Warmup(typeof(AiAssist).GetMethod(nameof(PocketFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        Patches.Asm(site, 7, "use64", $"mov rax, 0x{(nint)(delegate* unmanaged<nint, nint, nint, byte>)&PocketFilter:X}", "call rax", "test al, al");
    }

    /// <summary>
    /// Like the original check (nonzero = skip this target), but while some target of <paramref name="from"/> would cut off
    /// enemy divisions, every target that wouldn't is skipped too; the game's random pick then takes a pocket-closing one.
    /// Vanilla's own rejections stay. AI threads: read only.
    /// </summary>
    [UnmanagedCallersOnly]
    private static byte PocketFilter(nint us, nint from, nint to)
    {
        try
        {
            if (_cantMove(us, from, to) != 0) return 1;
            var tag = *(int*)us;
            var me = Game.Country(tag);
            if (me == 0 || !SheepMod.Active) return 0;
            if (_armorTerrain && BadForArmor(tag, from, to) || _roles && LineOnly(tag, from) && Divisions(to) > 0 && Pocket(me, tag, to) == 0 && HasBreakthrough(me, tag))
            {
                Interlocked.Increment(ref _steered);
                return 1;
            }
            if (!(_pocketClosing || _frontShortening || _targetValue || _axis)) return 0;
            var (pocket, shortest, valuable) = (0, int.MaxValue, 0.0);
            foreach (var id in Game.Neighbours(from))
            {
                var c = Game.Province(id);
                if (c == 0 || !Enemy(me, tag, c) || _cantMove(us, from, c) != 0 || _armorTerrain && BadForArmor(tag, from, c)) continue;
                if (Divisions(from) < Divisions(c) * (_fortLevel(c) / 2 + 1)) continue; // fails vanilla's strength test anyway
                if (_pocketClosing) pocket = Math.Max(pocket, Pocket(me, tag, c));
                if (_frontShortening) shortest = Math.Min(shortest, FrontChange(me, tag, c));
                if (_targetValue) valuable = Math.Max(valuable, TargetValue(c));
            }
            var skip = pocket > 0 ? Pocket(me, tag, to) == 0                                   // pocket targets only
                : _axis && !OnAxis(tag, to) ? true                                                   // nothing off the breakthrough axis
                : _targetValue && valuable >= MinTargetValue ? TargetValue(to) < valuable / 2         // worth at least half the best
                : _frontShortening && shortest < 0 && FrontChange(me, tag, to) > 0;              // nothing that lengthens the front
            if (skip) Interlocked.Increment(ref _steered);
            return skip ? (byte)1 : (byte)0;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _pocketFailed, 1) == 0) _log?.Error($"pocket filter: {e}");
            return _cantMove(us, from, to);
        }
    }

    /// <summary>Enemy divisions that taking <paramref name="target"/> cuts off: in enemy provinces next to it whose other land neighbours are all ours or allied.</summary>
    // ponytail: one-province pockets only; a flood fill bounded by NAI.POCKET_DISTANCE_MAX if bigger encirclements are missed.
    private static int Pocket(nint me, int tag, nint target)
    {
        var cut = 0;
        var targetId = Id(target);
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

    private const int UnitsInProvince = 0x110, StaticOfProvince = 0xB8, TerrainOfStatic = 0xA8, AdjacenciesOfStatic = 0x70;
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
        var terrain = *(nint*)(*(nint*)(to + StaticOfProvince) + TerrainOfStatic);
        var index = terrain == 0 ? 0 : *(int*)(terrain + IndexOfTerrain);
        if (index <= 0) return false;
        var river = Adjacency(from, Id(to));
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

    // ---- Roles: line infantry holds, breakthrough divisions push ----

    private const int Defense = 0xA8, Breakthrough = 0xB0, UnitsOfCountry = 0x290, KindOfUnit = 8;
    private const long RolesCacheMs = 60_000;
    private static bool _roles;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (long Until, bool Has)> Breakthroughs = new();

    /// <summary>Armored, or more breakthrough than defense: made to attack.</summary>
    private static bool IsBreakthrough(nint unit)
    {
        var stats = *(nint*)(unit + StatsOfUnit);
        return stats != 0 && (*(long*)(stats + Hardness) >= Armored || *(long*)(stats + Armor) > 0 || *(long*)(stats + Breakthrough) > *(long*)(stats + Defense));
    }

    /// <summary>Our divisions in the province are all line infantry.</summary>
    private static bool LineOnly(int tag, nint province)
    {
        var any = false;
        foreach (var unit in Native.PdxArray(province + UnitsInProvince))
        {
            if (*(int*)(unit + CommanderOfUnit) != tag) continue;
            if (IsBreakthrough(unit)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>The country has breakthrough divisions at all (cached a minute): an all-infantry army still attacks with infantry.</summary>
    private static bool HasBreakthrough(nint country, int tag)
    {
        var now = Environment.TickCount64;
        if (Breakthroughs.TryGetValue(tag, out var cached) && now < cached.Until) return cached.Has;
        var has = false;
        foreach (var unit in Native.PdxArray(country + UnitsOfCountry))
            if (*(int*)(unit + KindOfUnit) == 0 && IsBreakthrough(unit)) { has = true; break; }
        Breakthroughs[tag] = (now + RolesCacheMs, has);
        return has;
    }

    // ---- Target value: victory points, capitals, supply hubs, airfields, resources, and how well it is held ----

    private const int VictoryPoints = 0x38, StateOfProvince = 0xC0, BuildingsOfProvince = 0x190;
    private const int ProvincesOfState = 0x18, IdOfState = 0x58, BestVictoryPoints = 0x85C, BuildingsOfState = 0x120, ResourcesOfState = 0x30;
    private const int ResourceEntries = 0xF8, SupplyNodeType = 0x3A0, AirBaseTypes = 0x298, CapitalOfCountry = 0x1018;
    private const int NavalBaseType = 0x400;
    private const int EntrenchOfUnit = 0x458, MaxEntrenchOfUnit = 0x460; // CArmy: dig-in now and at most (1e5)
    private const int MinTargetValue = 30;
    private const long CapitalsCacheMs = 60_000;

    private static bool _targetValue, _entrenchment;
    private static nint _buildingDb; // address of the building database global
    private static delegate* unmanaged<nint, nint, nint> _findBuilding;
    private static (long Until, HashSet<int> States) _capitals = (0, []);

    private void LoadTargetValue()
    {
        _findBuilding = (delegate* unmanaged<nint, nint, nint>)Sig.Find(Signatures.FindBuilding);
        var at = Sig.Find(Signatures.BuildingDbCall);
        var getter = at + 5 + *(int*)(at + 1);
        _buildingDb = getter + 18 + *(int*)(getter + 14);
    }

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
        var hub = _findBuilding(target + BuildingsOfProvince, *(nint*)(db + SupplyNodeType));
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
        if (defenders.Length == 0) return value + 25;
        long org = 0, strength = 0;
        var dugIn = 0;
        foreach (var unit in defenders)
        {
            long o, s;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[OrgRatioSlot])(unit, &o);
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[StrengthRatioSlot])(unit, &s);
            org += o;
            strength += s;
            if (_entrenchment && *(long*)(unit + MaxEntrenchOfUnit) > 0 && *(long*)(unit + EntrenchOfUnit) * 100 >= 60 * *(long*)(unit + MaxEntrenchOfUnit)) dugIn++;
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
}
