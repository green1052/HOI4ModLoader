namespace HOI4ModLoader;

/// <summary>
/// CProvince: fields, terrain, buildings, and its relation to a country (enemy, friendly, supply). Offsets for HOI4 1.19.3.
/// Read only, so safe on AI threads.
/// </summary>
public static unsafe class Province
{
    public const int VictoryPointsOffset = 0x38, StaticOffset = 0xB8, StateOffset = 0xC0, UnitsOffset = 0x110, DivisionsOffset = 0x11C;
    public const int RegionOffset = 0xC8, ControllerOffset = 0x188, BuildingsOffset = 0x190;
    private const int TerrainOfStatic = 0xA8, FlagsOfStatic = 0xD2, CategoryOfTerrain = 0x8C;
    private const int RailwaysOfGameState = 0x3E0, RailwaysOfInfo = 0x38; // railway map: per province id an info, its railways at +0x38

    // CTerrainType+0x8C category bits; adjacency types (Game.AdjacencyType).
    public const int Mountain = 0x8, Urban = 0x10, Marsh = 0x2000, Jungle = 0x10000;
    public const int River = 2, LargeRiver = 3, Impassable = 4;

    /// <summary>The controlling country's tag; 0 for sea (and wasteland).</summary>
    public static int Controller(nint province) => *(int*)(province + ControllerOffset);

    /// <summary>Land divisions standing in it (anyone's).</summary>
    public static int Divisions(nint province) => *(int*)(province + DivisionsOffset);

    /// <summary>The units standing in it (CArmy*).</summary>
    public static ReadOnlySpan<nint> Units(nint province) => Native.PdxArray(province + UnitsOffset);

    public static int VictoryPoints(nint province) => *(int*)(province + VictoryPointsOffset);

    /// <summary>Its CState, or 0 (sea).</summary>
    public static nint State(nint province) => *(nint*)(province + StateOffset);

    /// <summary>Its building container, for <see cref="HOI4ModLoader.Buildings.Find"/>.</summary>
    public static nint Buildings(nint province) => province + BuildingsOffset;

    /// <summary>Its CStrategicRegion (<see cref="AirRegions"/>), or 0.</summary>
    public static nint StrategicRegion(nint province) => *(nint*)(province + RegionOffset);

    /// <summary>A railway out of a province, as the game stores it: the neighbour's id and its level (0: none).</summary>
    public readonly record struct Railway(int To, int Level);

    /// <summary>The railways out of province <paramref name="id"/> (the railway map's level per neighbour), empty without a game.</summary>
    public static ReadOnlySpan<Railway> Railways(int id)
    {
        var gs = Game.State;
        var railways = gs == 0 ? 0 : *(nint*)(gs + RailwaysOfGameState);
        var info = railways == 0 ? 0 : *(nint*)(*(nint*)(railways + 8) + 8 * id);
        return info == 0 ? default : Native.PdxArray<Railway>(info + RailwaysOfInfo);
    }

    /// <summary>Any railway leaves the province.</summary>
    public static bool HasRailway(nint province)
    {
        foreach (var railway in Railways(Game.Id(province)))
            if (railway.Level > 0) return true;
        return false;
    }

    /// <summary>Its CTerrainType, or 0.</summary>
    public static nint Terrain(nint province)
    {
        var info = *(nint*)(province + StaticOffset);
        return info == 0 ? 0 : *(nint*)(info + TerrainOfStatic);
    }

    /// <summary>Its terrain's category bits (<see cref="Mountain"/>, <see cref="Urban"/>...), 0 without terrain.</summary>
    public static int TerrainCategory(nint province)
    {
        var terrain = Terrain(province);
        return terrain == 0 ? 0 : *(int*)(terrain + CategoryOfTerrain);
    }

    /// <summary>Units may enter it (static data land flag).</summary>
    public static bool Enterable(nint province)
    {
        var info = *(nint*)(province + StaticOffset);
        return info != 0 && (*(byte*)(info + FlagsOfStatic) & 1) != 0;
    }

    /// <summary>FortLevel(CProvince*) -> int: land fort level.</summary>
    [Signature] public const string FortLevelSig = "4C 63 91 EC 01 00 00 33 C0 4D 85 D2 7E ? 4C 8B 99 E0 01 00 00 44 8B C8 0F 1F 84 00 00 00 00 00 4B 8B 0C CB 49 FF C1 48 8B 91 E0 01 00 00 44 0F BF 41 42 44 0F AF 82 08 03 00 00";

    private static class Forts
    {
        public static readonly delegate* unmanaged<nint, int> Level = (delegate* unmanaged<nint, int>)Sig.Find(FortLevelSig);
    }

    /// <summary>Land fort level. Any thread.</summary>
    public static int FortLevel(nint province) => Forts.Level(province);

    /// <summary>Building level by building database slot (<see cref="HOI4ModLoader.Buildings.NavalBaseSlot"/>...), 0: none.</summary>
    public static int BuildingLevel(nint province, int slot) => HOI4ModLoader.Buildings.Level(Buildings(province), HOI4ModLoader.Buildings.Type(slot));

    /// <summary>Held by a country at war with <paramref name="country"/> (tag <paramref name="tag"/>); never sea.</summary>
    public static bool Enemy(nint country, int tag, nint province)
    {
        var controller = Controller(province);
        return controller != tag && Game.AtWar(country, controller); // 0: sea
    }

    /// <summary>Held by the country or an ally.</summary>
    public static bool Friendly(nint country, int tag, nint province)
    {
        var controller = Controller(province);
        return controller == tag || Game.Allied(country, controller);
    }

    // The ones below take the wars as a predicate (tag -> at war with us): FrontLine remembers them for its turn.

    /// <summary>A port's <see cref="KeyValue"/>, plus its level; the rest stays far below.</summary>
    public const int PortKey = 1000;

    /// <summary>
    /// What holding <paramref name="tag"/>'s province is worth: a port above all, then victory points, city or mountain
    /// ground, a river in front of enemy ground.
    /// </summary>
    public static int KeyValue(int tag, nint province, Func<int, bool> atWar)
    {
        var port = BuildingLevel(province, HOI4ModLoader.Buildings.NavalBaseSlot);
        if (port > 0) return PortKey + port;
        var key = 3 * Math.Min(VictoryPoints(province), 30);
        if ((TerrainCategory(province) & (Mountain | Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || Game.AdjacencyType(province, id) is not (River or LargeRiver) || Controller(p) is var c && (c == tag || !atWar(c))) continue; // impassable: no attack at all
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }

    /// <summary>Divisions of countries at war with us in the province.</summary>
    public static int Hostiles(nint province, Func<int, bool> atWar)
    {
        var n = 0;
        foreach (var unit in Units(province))
            if (unit != 0 && atWar(Division.Commander(unit))) n++;
        return n;
    }

    /// <summary>
    /// The id of a break-in at or next to <paramref name="tag"/>'s province <paramref name="p"/> not yet <paramref name="answered"/>,
    /// or 0: hostile troops inside it, or an enemy neighbour reaching into our side (2+ of ours around it, twice the enemy's)
    /// that is <paramref name="recent"/> (taken lately, landed on) or touches no other enemy ground.
    /// </summary>
    public static int BreakIn(int tag, nint p, Func<int, bool> atWar, Func<int, bool> answered, Func<int, bool> recent)
    {
        var self = Game.Id(p);
        if (!answered(self) && Hostiles(p, atWar) > 0) return self;
        foreach (var id in Game.Neighbours(p))
        {
            if (answered(id)) continue;
            var e = Game.Province(id);
            if (e == 0 || Controller(e) is var c && (c == tag || !atWar(c))) continue;
            var (ours, theirs) = (0, 0);
            foreach (var n in Game.Neighbours(e))
            {
                var q = Game.Province(n);
                if (q == 0) continue;
                if (Controller(q) == tag) ours++;
                else if (atWar(Controller(q))) theirs++;
            }
            if (ours >= 2 && ours >= 2 * theirs && (theirs == 0 || recent(id))) return id;
        }
        return 0;
    }

    // Loops, not LINQ: these run for every front province, and capturing lambdas allocate.

    /// <summary>Next to enemy-held ground.</summary>
    public static bool NextToEnemy(nint country, int tag, nint province)
    {
        foreach (var n in Game.Neighbours(province))
            if (Game.Province(n) is var p && p != 0 && Enemy(country, tag, p)) return true;
        return false;
    }

    /// <summary>Next to enemy-held ground with divisions in it.</summary>
    public static bool EnemyDivisionsNext(nint country, int tag, nint province)
    {
        foreach (var n in Game.Neighbours(province))
            if (Game.Province(n) is var p && p != 0 && Divisions(p) > 0 && Enemy(country, tag, p)) return true;
        return false;
    }

    /// <summary>No land neighbour is enemy-held: divisions in it (an enemy province) can't get out.</summary>
    public static bool Surrounded(nint country, int tag, nint province)
    {
        foreach (var n in Game.Neighbours(province))
            if (Game.Province(n) is var p && p != 0 && Controller(p) != 0 && Enemy(country, tag, p)) return false;
        return true;
    }

    private const int MaxPocket = 12; // enemy provinces a pocket may span

    /// <summary>
    /// Enemy divisions cut off by taking <paramref name="target"/>: those in neighbouring enemy regions of at most
    /// <see cref="MaxPocket"/> provinces that it closes.
    /// </summary>
    public static int Pocket(nint country, int tag, nint target)
    {
        var cut = 0;
        var counted = new HashSet<int>();
        foreach (var id in Game.Neighbours(target)) // a fresh set per walk: one that stops early leaves ids it never expanded
            if (!counted.Contains(id) && Game.Province(id) is var e && e != 0 && Enemy(country, tag, e)
                && Region(e, MaxPocket, p => Enemy(country, tag, p), [Game.Id(target)]) is { } region) // closed and small: cut off
            {
                cut += region.Sum(Divisions);
                foreach (var q in region) counted.Add(Game.Id(q));
            }
        return cut;
    }

    /// <summary>The enemy province is already in a pocket of at most <see cref="MaxPocket"/> provinces.</summary>
    public static bool Encircled(nint country, int tag, nint province) =>
        Enemy(country, tag, province) && Region(province, MaxPocket, p => Enemy(country, tag, p)) != null;

    /// <summary>
    /// The provinces connected to <paramref name="start"/> through ones <paramref name="inside"/> accepts, or null if more
    /// than <paramref name="max"/>. <paramref name="seen"/>: ids to skip; walked ids are added (after a null, only partly
    /// walked: don't reuse it to skip).
    /// </summary>
    public static List<nint>? Region(nint start, int max, Func<nint, bool> inside, HashSet<int>? seen = null)
    {
        seen ??= [];
        seen.Add(Game.Id(start));
        var region = new List<nint> { start };
        for (var i = 0; i < region.Count; i++)
        foreach (var n in Game.Neighbours(region[i]))
        {
            if (!seen.Add(n) || Game.Province(n) is not (var p and not 0) || !inside(p)) continue;
            region.Add(p);
            if (region.Count > max) return null;
        }
        return region;
    }

    /// <summary>
    /// How the front's length changes when <paramref name="target"/> is taken: +1 per enemy neighbour, -1 per friendly one.
    /// Negative = shorter.
    /// </summary>
    public static int FrontChange(nint country, int tag, nint target)
    {
        var change = 0;
        foreach (var id in Game.Neighbours(target))
        {
            var n = Game.Province(id);
            if (n == 0 || Controller(n) == 0) continue; // sea
            if (Enemy(country, tag, n)) change++;
            else if (Friendly(country, tag, n)) change--;
        }
        return change;
    }

    /// <summary>How hard the enemy province is to take from <paramref name="from"/>: divisions and forts, plus harsh terrain or a river.</summary>
    public static int AttackWeight(nint target, nint from)
    {
        var weight = 2 * Divisions(target) + FortLevel(target);
        if ((TerrainCategory(target) & (Mountain | Urban | Marsh | Jungle)) != 0) weight += 3;
        if (Game.AdjacencyType(from, Game.Id(target)) is River or LargeRiver) weight += 2;
        return weight;
    }

    /// <summary>
    /// Whether <paramref name="to"/> would be short of supply once taken: <paramref name="from"/> gets under
    /// <paramref name="sharePercent"/> % of its need, and no other friendly neighbour of the target has that much spare.
    /// Any thread, read only.
    /// </summary>
    public static bool Starved(nint country, int tag, nint from, nint to, int sharePercent)
    {
        var at = Game.SupplyFlow(Game.Id(from));
        if (at == 0) return false;
        var need = *(long*)(at + Game.NeedOfFlow);
        var wanted = need * sharePercent / 100;
        if (need <= 0 || *(long*)(at + Game.SuppliedOfFlow) >= wanted) return false;
        foreach (var id in Game.Neighbours(to))
        {
            var n = Game.Province(id);
            var flow = n == 0 || n == from ? 0 : Game.SupplyFlow(id);
            if (flow == 0 || Controller(n) <= 0 || !Friendly(country, tag, n)) continue; // sea: controller 0
            if (*(long*)(flow + Game.SuppliedOfFlow) - *(long*)(flow + Game.NeedOfFlow) >= wanted) return false;
        }
        return true;
    }
}

/// <summary>CState. Offsets for HOI4 1.19.3.</summary>
public static unsafe class State
{
    public const int ProvincesOffset = 0x18, IdOffset = 0x58, BuildingsOffset = 0x120;
    private const int BestVictoryPoints = 0x85C; // index of its main province in Provinces

    public static int Id(nint state) => *(int*)(state + IdOffset);

    public static ReadOnlySpan<nint> Provinces(nint state) => Native.PdxArray(state + ProvincesOffset);

    /// <summary>Its building container (state buildings: factories, airfields...).</summary>
    public static nint Buildings(nint state) => state + BuildingsOffset;

    /// <summary>Its main province (the one with the most victory points), or 0.</summary>
    public static nint MainProvince(nint state)
    {
        var index = *(int*)(state + BestVictoryPoints);
        var provinces = Provinces(state);
        return (uint)index < (uint)provinces.Length ? provinces[index] : 0;
    }
}

/// <summary>Building types (CBuildingDatabase) and buildings in a container. Offsets for HOI4 1.19.3.</summary>
public static unsafe class Buildings
{
    /// <summary>BuildingContainer::Find(container, CBuildingType*) -> CBuilding* or null (+0x40 level, +0x42 working level).</summary>
    [Signature] public const string FindSig = "80 7A 20 00 74 ? 83 79 2C 00 74 ? 48 63 92 E0 02 00 00 48 8B 41 20";

    /// <summary>A call of the building database getter: `call Get; mov rcx,[rax+3B8h]` (naval base type). Get reads [rip+db].</summary>
    [Signature] public const string DbCallSig = "E8 ? ? ? ? 48 8B 88 B8 03 00 00 E8 ? ? ? ? 8B D8";

    // CBuildingDatabase (0x3F0 bytes): 0x3A0 supply_node, 0x3A8 rail_way, 0x3B0 infrastructure, 0x3B8 naval_base,
    // 0x3C0 arms_factory, 0x3C8 industrial_complex; 0x298 CPdxArray<CBuildingType*> air base types.
    public const int SupplyNodeSlot = 0x3A0, InfrastructureSlot = 0x3B0, CivilianFactorySlot = 0x3C8, AirBaseTypesOffset = 0x298;
    private const int LevelOfBuilding = 0x40, WorkingLevelOfBuilding = 0x42; // short: built, working

    private static class Resolved
    {
        private static readonly nint Call = Sig.Find(DbCallSig);
        private static readonly nint Getter = Call + 5 + *(int*)(Call + 1);
        public static readonly nint* Db = (nint*)(Getter + 18 + *(int*)(Getter + 14)); // set after plugins load
        public static readonly int NavalBase = *(int*)(Call + 8);                      // from `mov rcx,[rax+3B8h]`
        public static readonly delegate* unmanaged<nint, nint, nint> Find = (delegate* unmanaged<nint, nint, nint>)Sig.Find(FindSig);
    }

    /// <summary>The CBuildingDatabase, or 0 before the game made it.</summary>
    public static nint Db => *Resolved.Db;

    /// <summary>The naval base type's slot in the database.</summary>
    public static int NavalBaseSlot => Resolved.NavalBase;

    /// <summary>The building type at a database slot, or 0 (no database yet).</summary>
    public static nint Type(int slot)
    {
        var db = Db;
        return db == 0 ? 0 : *(nint*)(db + slot);
    }

    /// <summary>The air base types, carriers included.</summary>
    public static ReadOnlySpan<nint> AirBaseTypes => Db is var db && db != 0 ? Native.PdxArray(db + AirBaseTypesOffset) : default;

    /// <summary>The container's CBuilding of <paramref name="type"/> (must be non-null), or 0. Any thread.</summary>
    public static nint Find(nint container, nint type) => Resolved.Find(container, type);

    /// <summary>Built level of <paramref name="type"/> in the container (0: none, or no type).</summary>
    public static int Level(nint container, nint type)
    {
        var building = type == 0 ? 0 : Find(container, type);
        return building == 0 ? 0 : *(short*)(building + LevelOfBuilding);
    }

    /// <summary>Working level (bombed-out levels don't count).</summary>
    public static int WorkingLevel(nint container, nint type)
    {
        var building = type == 0 ? 0 : Find(container, type);
        return building == 0 ? 0 : *(short*)(building + WorkingLevelOfBuilding);
    }
}

/// <summary>CArmy: a unit; a land division when <see cref="IsLand"/>. Offsets for HOI4 1.19.3.</summary>
public static unsafe class Division
{
    public const int KindOffset = 0x08, RefOffset = 0x18, GroupOffset = 0xC0, StatsOffset = 0x138, CombatsOffset = 0x1A8;
    public const int CommanderOffset = 0x1D8, OwnerOffset = 0x1E0, LocationOffset = 0x1F0, PathOffset = 0x200, RetreatingOffset = 0x24C, BusyOffset = 0x2F8;
    public const int MemberOffset = 0xB8;                       // its COrdersGroupMember, inline (what COrder members point at)
    private const int StrengthSlot = 34, OrganisationSlot = 39; // vtable: (unit, long* out) -> out
    private const int IsLowSupplySlot = 43;                     // vtable: (unit) -> bool
    private const int Hardness = 0xB8, Armor = 0x298;          // stats
    private const long Armored = 40_000;                       // hardness 0.4

    public static bool IsLand(nint unit) => *(int*)(unit + KindOffset) == 0;

    /// <summary>Its CRef id, which commands use.</summary>
    public static long Ref(nint unit) => *(long*)(unit + RefOffset);

    /// <summary>Its army (COrdersGroup), or 0.</summary>
    public static nint Group(nint unit) => *(nint*)(unit + GroupOffset);

    /// <summary>The tag commanding it (an ally's for an expeditionary force).</summary>
    public static int Commander(nint unit) => *(int*)(unit + CommanderOffset);

    public static int Owner(nint unit) => *(int*)(unit + OwnerOffset);

    /// <summary>The CProvince it stands in, or 0 (e.g. embarked).</summary>
    public static nint Location(nint unit) => *(nint*)(unit + LocationOffset);

    /// <summary>Its stats (template), or 0.</summary>
    public static nint Stats(nint unit) => *(nint*)(unit + StatsOffset);

    /// <summary>It is moving.</summary>
    public static bool Walking(nint unit) => *(int*)(unit + PathOffset + 0xC) != 0;

    /// <summary>In combat.</summary>
    public static bool Fighting(nint unit) => *(int*)(unit + CombatsOffset + 0xC) != 0;

    public static bool Retreating(nint unit) => *(byte*)(unit + RetreatingOffset) != 0;

    /// <summary>Low on supply: the flag the unit icon shows.</summary>
    public static bool LowSupply(nint unit) => ((delegate* unmanaged<nint, byte>)(*(nint**)unit)[IsLowSupplySlot])(unit) != 0;

    /// <summary>Embarked or otherwise taken up.</summary>
    public static bool Busy(nint unit) => *(nint*)(unit + BusyOffset) != 0;

    /// <summary>
    /// A land division of <paramref name="tag"/>, not lent, that can take orders now: not fighting, retreating or embarked.
    /// Army HQs only with <paramref name="headquarters"/>.
    /// </summary>
    public static bool Ready(nint unit, int tag, bool headquarters = false) =>
        IsLand(unit) && Commander(unit) == tag && Owner(unit) == tag && !Fighting(unit) && !Retreating(unit) && !Busy(unit)
        && (headquarters || !IsHeadquarters(unit));

    /// <summary>
    /// The AI skipping Army HQs: `mov rcx,rax; call TemplateData(CArmy*); cmp byte [rax+1B4h],0` (is_army_hq).
    /// TemplateData: [unit+3B8h] (CDivisionTemplate) + 18h, or 0.
    /// </summary>
    [Signature] public const string HeadquartersSig = "48 8B C8 E8 ? ? ? ? 80 B8 B4 01 00 00 00 0F 85";

    private static class Hq
    {
        private static readonly nint At = Sig.Find(HeadquartersSig);
        public static readonly delegate* unmanaged<nint, nint> TemplateData = (delegate* unmanaged<nint, nint>)TemplateDataAt(At);
        public static readonly int Flag = *(int*)(At + 10);
    }

    /// <summary>The template data getter, from a <see cref="HeadquartersSig"/> match (call at +3).</summary>
    internal static nint TemplateDataAt(nint at) => at + 8 + *(int*)(at + 4);

    /// <summary>An Army HQ division (buffs its army from behind the line). Any thread.</summary>
    public static bool IsHeadquarters(nint unit)
    {
        var data = Hq.TemplateData(unit);
        return data != 0 && *(byte*)(data + Hq.Flag) != 0;
    }

    /// <summary>Armored or motorized: hardness 0.4+ or armor.</summary>
    public static bool Motorized(nint unit)
    {
        var stats = Stats(unit);
        return stats != 0 && (*(long*)(stats + Hardness) >= Armored || *(long*)(stats + Armor) > 0);
    }

    /// <summary>Organisation, 1e5 = full.</summary>
    public static long Organisation(nint unit) => Ratio(unit, OrganisationSlot);

    /// <summary>Strength (equipment and manpower), 1e5 = full.</summary>
    public static long Strength(nint unit) => Ratio(unit, StrengthSlot);

    /// <summary>A rough fighting power: strength %, halved at zero organisation (0..100).</summary>
    public static long Power(nint unit) => Strength(unit) * 100 / Native.FixedPoint * (50 + Organisation(unit) * 100 / Native.FixedPoint / 2) / 100;

    private static long Ratio(nint unit, int slot)
    {
        long value;
        ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[slot])(unit, &value);
        return value;
    }
}

/// <summary>COrder: a front, an offensive line, an invasion, a paradrop... of an orders group. Offsets for HOI4 1.19.3.</summary>
public static unsafe class Order
{
    public const int TypeOffset = 0x30, MembersOffset = 0x210, IdOffset = 0x244, ExecutingOffset = 0x248;
    public const int Front = 2, Invasion = 3, Paradrop = 4; // types

    public static int Type(nint order) => *(int*)(order + TypeOffset);

    /// <summary>Its id within its group, which order commands use.</summary>
    public static int Id(nint order) => *(int*)(order + IdOffset);

    /// <summary>Its divisions (COrdersGroupMember*).</summary>
    public static ReadOnlySpan<nint> Members(nint order) => Native.PdxArray(order + MembersOffset);

    /// <summary>Its plan runs (the battle plan's play button is on).</summary>
    public static bool Executing(nint order) => *(byte*)(order + ExecutingOffset) != 0;
}

/// <summary>CProductionLine (military) and its equipment. Offsets for HOI4 1.19.3.</summary>
public static unsafe class ProductionLine
{
    public const int MilitaryLinesOffset = 88; // CCountryProduction: CPdxArray<CProductionLine*>
    private const int VariantOfLine = 0x88, TypeOfVariant = 1008, ArchetypeOfType = 1240;

    /// <summary>The equipment variant it makes, or 0.</summary>
    public static nint Variant(nint line) => *(nint*)(line + VariantOfLine);

    /// <summary>The variant's equipment archetype, or 0.</summary>
    public static nint Archetype(nint variant)
    {
        var type = variant == 0 ? 0 : *(nint*)(variant + TypeOfVariant);
        return type == 0 ? 0 : *(nint*)(type + ArchetypeOfType);
    }
}

/// <summary>CLandCombat: a land battle, its defending side and the attacker's share. Offsets for HOI4 1.19.3.</summary>
public static unsafe class LandCombat
{
    [VTable] public const string Class = "CLandCombat";

    // Combats (land, naval, air): array at gamestate +0x268. CLandCombat: defender side, province, vtable slot of the
    // attacker's share. Side: divisions, reserves.
    private const int CombatsOfGameState = 0x268, DefenderOfCombat = 0x30, ProvinceOfCombat = 0x38, ProgressSlot = 25;
    private const int DivisionsOfSide = 0x20, ReservesOfSide = 0x100;

    private static class Resolved
    {
        public static readonly nint VTable = Sig.VTable(Class);
    }

    /// <summary>The land battles going on (a snapshot), empty without a game. Any thread that may read the game state.</summary>
    public static List<nint> All()
    {
        var all = new List<nint>();
        var gs = Game.State;
        if (gs == 0) return all;
        foreach (var combat in Native.PdxArray(gs + CombatsOfGameState).ToArray())
            if (combat != 0 && *(nint*)combat == Resolved.VTable) all.Add(combat);
        return all;
    }

    /// <summary>The province fought over (the defender's), or 0.</summary>
    public static nint Location(nint combat) => *(nint*)(combat + ProvinceOfCombat);

    /// <summary>The defending side, or 0.</summary>
    public static nint Defender(nint combat) => *(nint*)(combat + DefenderOfCombat);

    /// <summary>A side's divisions in the battle (CArmy*).</summary>
    public static ReadOnlySpan<nint> Divisions(nint side) => Native.PdxArray(side + DivisionsOfSide);

    /// <summary>A side's reserves waiting to step in.</summary>
    public static int Reserves(nint side) => *(int*)(side + ReservesOfSide + 0xC);

    /// <summary>The attacker's share of the battle, fixed point (<see cref="Native.FixedPoint"/>: all of it).</summary>
    public static long AttackerShare(nint combat)
    {
        long progress;
        ((delegate* unmanaged<nint, long*, long*>)(*(nint**)combat)[ProgressSlot])(combat, &progress);
        return progress;
    }
}

/// <summary>
/// A country's construction: where it may build, its construction lines, and the construction tab's command. Offsets for
/// HOI4 1.19.3.
/// </summary>
public static unsafe class Construction
{
    /// <summary>BuildingContainer::GetFreeLevels(container, type) -> int: max level minus planned (built and queued), shared slots included.</summary>
    [Signature] public const string FreeLevelsSig = "48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 4C 63 82 E0 02 00 00 48 8B F9 48 8B 41 20 48 8B F2 4A 63 0C 80 83 F9 FF 74 73 48 8B 47 38 48 8B 2C C8 48 85 ED 74 66 45 33 C0 48 89 5C 24 30 48 8B CF E8";

    /// <summary>CCountry::CanBuildInProvince(country, province, type, bool) -> bool.</summary>
    [Signature] public const string CanBuildInProvinceSig = "48 8B 82 B8 00 00 00 4D 8B D0 4C 8B C1 F6 80 D2 00 00 00 01";

    [VTable] public const string CommandClass = "CAddConstructionCommand";
    [VTable] public const string BuildingReference = "CBuildingReference";
    [VTable] public const string LineClass = "CBuildingProductionLine";

    /// <summary>CCountry: its production status; that holds the construction lines (CBuildingProductionLine among others).</summary>
    public const int ProductionOfCountry = 0xF68, LinesOfProduction = 112;
    private const int BuildingOfLine = 112, ContainerOfBuilding = 472, TypeOfBuilding = 480, TokenOfType = 8;
    private const int CommandSize = 0x50, LocationOfCommand = 56, BuildingOfCommand = 60, LevelsOfCommand = 72, InsertionOfCommand = 76, AtEnd = 1;

    private static class Resolved
    {
        public static readonly delegate* unmanaged<nint, nint, int> FreeLevels = (delegate* unmanaged<nint, nint, int>)Sig.Find(FreeLevelsSig);
        public static readonly delegate* unmanaged<nint, nint, nint, byte, byte> CanBuildInProvince = (delegate* unmanaged<nint, nint, nint, byte, byte>)Sig.Find(CanBuildInProvinceSig);
    }

    /// <summary>Levels of <paramref name="type"/> the container can still take (built and queued count).</summary>
    public static int FreeLevels(nint container, nint type) => Resolved.FreeLevels(container, type);

    /// <summary>The country may build <paramref name="type"/> in the province, as the construction map mode allows.</summary>
    public static bool CanBuildInProvince(nint country, nint province, nint type) => Resolved.CanBuildInProvince(country, province, type, 1) != 0;

    /// <summary>The building type's script token (its name: <see cref="Native.Token"/>).</summary>
    public static int Token(nint type) => *(int*)(type + TokenOfType);

    /// <summary>The CBuilding's container (<see cref="Province.Buildings"/>, <see cref="State.Buildings"/>).</summary>
    public static nint Container(nint building) => *(nint*)(building + ContainerOfBuilding);

    /// <summary>The country's construction lines (repairs too) of <paramref name="type"/>, with the CBuilding each works on.</summary>
    public static List<(nint Line, nint Building)> Lines(nint country, nint type)
    {
        var lines = new List<(nint, nint)>();
        var production = *(nint*)(country + ProductionOfCountry);
        if (production == 0) return lines;
        var lineVTable = Sig.VTable(LineClass);
        foreach (var line in Native.PdxArray(production + LinesOfProduction))
        {
            // Readable first: a line the game already freed takes the vtable check's read off a mapped page (it crashed the game).
            var building = line != 0 && Native.Readable(line) && *(nint*)line == lineVTable ? *(nint*)(line + BuildingOfLine) : 0;
            if (building != 0 && *(nint*)(building + TypeOfBuilding) == type) lines.Add((line, building));
        }
        return lines;
    }

    /// <summary>
    /// Queues one level of <paramref name="type"/> at the end of the construction queue, in a state or province (by id), with
    /// the construction tab's command. <paramref name="force"/> for AI countries (see <see cref="Commands.Post(nint, bool)"/>). Main thread.
    /// </summary>
    public static bool Queue(int tag, int location, nint type, bool force = false)
    {
        var cmd = Commands.New(CommandClass, CommandSize, tag, Sig.VTable(BuildingReference));
        *(int*)(cmd + LocationOfCommand) = location;
        *(int*)(cmd + BuildingOfCommand) = Token(type);
        *(int*)(cmd + LevelsOfCommand) = 1;
        *(byte*)(cmd + InsertionOfCommand) = AtEnd;
        return Commands.TryPost(cmd, force);
    }
}

/// <summary>
/// Divisions a plugin took for its own job (rest, reserve, counterattack...), by plugin id; other plugins leave them alone.
/// Also which plugin runs the AI's front line micro. Keys only, never dereferenced; AI threads read it.
/// </summary>
public static class Jobs
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, string> Held = new();

    /// <summary>The plugin id that micromanages the AI's front lines (rotation, reserves, second line, local attacks), or null.</summary>
    public static volatile string? AiMicro;

    /// <summary>Replaces <paramref name="owner"/>'s divisions with <paramref name="units"/>.</summary>
    public static void Set(string owner, IEnumerable<nint> units)
    {
        var now = units.ToHashSet();
        foreach (var (unit, by) in Held)
            if (by == owner && !now.Contains(unit)) Held.TryRemove(unit, out _);
        foreach (var unit in now) Held.TryAdd(unit, owner);
    }

    internal static void Reset()
    {
        Held.Clear();
        AiMicro = null;
    }

    /// <summary>A new game: forget the held divisions.</summary>
    internal static void Forget() => Held.Clear();

    /// <summary>A plugin other than <paramref name="except"/> has a job for the division.</summary>
    public static bool Taken(nint unit, string? except = null) => Held.TryGetValue(unit, out var by) && by != except;
}
