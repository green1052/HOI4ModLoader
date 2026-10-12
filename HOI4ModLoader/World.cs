namespace HOI4ModLoader;

/// <summary>
/// CProvince: fields, terrain, buildings, and how it stands to a country (enemy, friendly, supply). Offsets for HOI4 1.19.3.
/// Readers only: safe from AI threads where the game itself reads the same data there.
/// </summary>
public static unsafe class Province
{
    public const int VictoryPointsOffset = 0x38, StaticOffset = 0xB8, StateOffset = 0xC0, UnitsOffset = 0x110, DivisionsOffset = 0x11C;
    public const int ControllerOffset = 0x188, BuildingsOffset = 0x190;
    private const int TerrainOfStatic = 0xA8, FlagsOfStatic = 0xD2, CategoryOfTerrain = 0x8C;

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

    /// <summary>Its building container (what <see cref="HOI4ModLoader.Buildings.Find"/> takes).</summary>
    public static nint Buildings(nint province) => province + BuildingsOffset;

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

    /// <summary>Units may enter it (its static data's land flag).</summary>
    public static bool Enterable(nint province)
    {
        var info = *(nint*)(province + StaticOffset);
        return info != 0 && (*(byte*)(info + FlagsOfStatic) & 1) != 0;
    }

    /// <summary>FortLevel(CProvince*) -> int: land forts, as the AI's micro attacks weigh them.</summary>
    [Signature] public const string FortLevelSig = "4C 63 91 EC 01 00 00 33 C0 4D 85 D2 7E ? 4C 8B 99 E0 01 00 00 44 8B C8 0F 1F 84 00 00 00 00 00 4B 8B 0C CB 49 FF C1 48 8B 91 E0 01 00 00 44 0F BF 41 42 44 0F AF 82 08 03 00 00";

    private static class Forts
    {
        public static readonly delegate* unmanaged<nint, int> Level = (delegate* unmanaged<nint, int>)Sig.Find(FortLevelSig);
    }

    /// <summary>Land fort level. Any thread.</summary>
    public static int FortLevel(nint province) => Forts.Level(province);

    /// <summary>Building level by its type's slot in the building database (<see cref="HOI4ModLoader.Buildings.NavalBaseSlot"/>...), 0: none.</summary>
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

    /// <summary>Next to enemy-held ground.</summary>
    public static bool NextToEnemy(nint country, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Enemy(country, tag, p));

    /// <summary>Next to enemy-held ground with divisions in it.</summary>
    public static bool EnemyDivisionsNext(nint country, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Divisions(p) > 0 && Enemy(country, tag, p));

    /// <summary>No land neighbour is enemy-held: divisions in it (an enemy province) can't get out.</summary>
    public static bool Surrounded(nint country, int tag, nint province) =>
        Game.Neighbours(province).All(n => Game.Province(n) is var p && (p == 0 || Controller(p) == 0 || !Enemy(country, tag, p)));

    private const int MaxPocket = 12; // enemy provinces a pocket may span

    /// <summary>
    /// Enemy divisions that taking <paramref name="target"/> cuts off: in the enemy ground next to it that no longer reaches
    /// the rest of the enemy's without it (a salient whose neck it is), up to <see cref="MaxPocket"/> provinces.
    /// </summary>
    public static int Pocket(nint country, int tag, nint target)
    {
        var cut = 0;
        var seen = new HashSet<int> { Game.Id(target) };
        foreach (var id in Game.Neighbours(target))
            if (!seen.Contains(id) && Game.Province(id) is var e && e != 0 && Enemy(country, tag, e)
                && Region(e, MaxPocket, p => Enemy(country, tag, p), seen) is { } region) // the enemy ground beyond, closed and small: cut off
                cut += region.Sum(Divisions);
        return cut;
    }

    /// <summary>The enemy province is in a pocket already: its enemy ground, up to <see cref="MaxPocket"/> provinces, is closed.</summary>
    public static bool Encircled(nint country, int tag, nint province) =>
        Enemy(country, tag, province) && Region(province, MaxPocket, p => Enemy(country, tag, p)) != null;

    /// <summary>
    /// The land provinces connected to <paramref name="start"/> through ones <paramref name="inside"/> accepts, if they are no
    /// more than <paramref name="max"/>; null when the region is bigger (open). <paramref name="seen"/>: ids to leave out, and
    /// it gets the ones walked.
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
    /// How the front's length changes when <paramref name="target"/> is taken: its enemy neighbours become new front, its
    /// neighbours of ours stop being front. Negative = shorter.
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

    /// <summary>How hard the enemy province is to take from <paramref name="from"/>: its divisions and forts, more for harsh terrain or a river in front of it.</summary>
    public static int AttackWeight(nint target, nint from)
    {
        var weight = 2 * Divisions(target) + FortLevel(target);
        if ((TerrainCategory(target) & (Mountain | Urban | Marsh | Jungle)) != 0) weight += 3;
        if (Game.AdjacencyType(from, Game.Id(target)) >= River) weight += 2;
        return weight;
    }

    /// <summary>
    /// Supply only flows through our side's provinces, so once <paramref name="to"/> is taken its supply comes over its land
    /// neighbours held by us or allies. Starved: <paramref name="from"/> gets less than <paramref name="sharePercent"/> % of what
    /// it needs and no other friendly neighbour of the target has that much spare (flow in minus its own need). Any thread, read
    /// only: the game rebuilds the flow on the main thread each hour, before the AI pass.
    /// </summary>
    public static bool Starved(nint country, int tag, nint from, nint to, int sharePercent)
    {
        var at = Game.SupplyFlow(Game.Id(from));
        if (at == 0) return false;
        var need = *(long*)(at + Game.NeedOfFlow); // the attackers and whatever else draws supply there
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

/// <summary>Buildings: the CBuildingDatabase's types and a container's CBuilding of a type. Offsets for HOI4 1.19.3.</summary>
public static unsafe class Buildings
{
    /// <summary>BuildingContainer::Find(container, CBuildingType*) -> CBuilding* or null (+0x40 level, +0x42 working level).</summary>
    [Signature] public const string FindSig = "80 7A 20 00 74 ? 83 79 2C 00 74 ? 48 63 92 E0 02 00 00 48 8B 41 20";

    /// <summary>A call of the building database getter, `call Get; mov rcx,[rax+3B8h]` (naval base type); the getter reads [rip+db].</summary>
    [Signature] public const string DbCallSig = "E8 ? ? ? ? 48 8B 88 B8 03 00 00 E8 ? ? ? ? 8B D8";

    // CBuildingDatabase (0x3F0 bytes): 0x3A0 supply_node, 0x3A8 rail_way, 0x3B0 infrastructure, 0x3B8 naval_base (the slot
    // DbCallSig reads), 0x3C0 arms_factory, 0x3C8 industrial_complex; 0x298 CPdxArray<CBuildingType*> the air base types.
    public const int SupplyNodeSlot = 0x3A0, InfrastructureSlot = 0x3B0, CivilianFactorySlot = 0x3C8, AirBaseTypesOffset = 0x298;
    private const int LevelOfBuilding = 0x40, WorkingLevelOfBuilding = 0x42; // short levels, built and working (bombed-out ones don't count)

    private static class Resolved
    {
        private static readonly nint Call = Sig.Find(DbCallSig);
        private static readonly nint Getter = Call + 5 + *(int*)(Call + 1);
        public static readonly nint* Db = (nint*)(Getter + 18 + *(int*)(Getter + 14)); // the global; the database is made after the plugins load
        public static readonly int NavalBase = *(int*)(Call + 8);                      // `mov rcx,[rax+3B8h]`: as the game reads it
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

    /// <summary>The air base types (airfields, carriers' too).</summary>
    public static ReadOnlySpan<nint> AirBaseTypes => Db is var db && db != 0 ? Native.PdxArray(db + AirBaseTypesOffset) : default;

    /// <summary>The container's CBuilding of <paramref name="type"/> (non-null: Find reads it at once), or 0. Any thread.</summary>
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
    private const int StrengthSlot = 34, OrganisationSlot = 39; // vtable: (unit, long* out) -> out, what has_unit_organization reads
    private const int Hardness = 0xB8, Armor = 0x298;          // stats
    private const long Armored = 40_000;                       // hardness 0.4

    public static bool IsLand(nint unit) => *(int*)(unit + KindOffset) == 0;

    /// <summary>Its CRef id (what commands name it by).</summary>
    public static long Ref(nint unit) => *(long*)(unit + RefOffset);

    /// <summary>Its army (COrdersGroup), or 0.</summary>
    public static nint Group(nint unit) => *(nint*)(unit + GroupOffset);

    /// <summary>The tag commanding it (an ally's for an expeditionary force).</summary>
    public static int Commander(nint unit) => *(int*)(unit + CommanderOffset);

    public static int Owner(nint unit) => *(int*)(unit + OwnerOffset);

    /// <summary>The CProvince it stands in, or 0 (embarked...).</summary>
    public static nint Location(nint unit) => *(nint*)(unit + LocationOffset);

    /// <summary>Its stats (template), or 0.</summary>
    public static nint Stats(nint unit) => *(nint*)(unit + StatsOffset);

    /// <summary>It has a path: on its way somewhere.</summary>
    public static bool Walking(nint unit) => *(int*)(unit + PathOffset + 0xC) != 0;

    /// <summary>In combat.</summary>
    public static bool Fighting(nint unit) => *(int*)(unit + CombatsOffset + 0xC) != 0;

    public static bool Retreating(nint unit) => *(byte*)(unit + RetreatingOffset) != 0;

    /// <summary>Embarked or otherwise taken up.</summary>
    public static bool Busy(nint unit) => *(nint*)(unit + BusyOffset) != 0;

    /// <summary>A land division of <paramref name="tag"/> (not lent) that can take orders now: not fighting (a defender can only retreat), retreating or embarked.</summary>
    public static bool Ready(nint unit, int tag) =>
        IsLand(unit) && Commander(unit) == tag && Owner(unit) == tag && !Fighting(unit) && !Retreating(unit) && !Busy(unit);

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

    /// <summary>A rough fighting power: strength %, worth half again with full organisation (0..100).</summary>
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

    /// <summary>Its id within its group (what order commands take).</summary>
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

/// <summary>
/// Divisions a plugin took for a job of its own (rest, reserve, counterattack...), by plugin id: other plugins leave them, and
/// AiAssist keeps the AI's regrouping off them. Also which plugin runs the AI's front line micro, if any. Keys only, never
/// dereferenced; AI threads read it.
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

    /// <summary>A plugin other than <paramref name="except"/> has a job for the division.</summary>
    public static bool Taken(nint unit, string? except = null) => Held.TryGetValue(unit, out var by) && by != except;
}
