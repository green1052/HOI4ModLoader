namespace HOI4ModLoader;

/// <summary>Strategic (air) regions and a country's air war in them. Offsets for HOI4 1.19.3.</summary>
public static unsafe class AirRegions
{
    /// <summary>CStrategicAir::GetRegionAirData: superiority records at sa+0xE0, 0xA0 each, by region+0x60 (pins those offsets).</summary>
    [Signature] public const string RegionAirDataSig = "48 85 D2 75 ? 48 8D 05 ? ? ? ? C3 48 63 42 60 48 8D 04 80 48 C1 E0 05 48 03 81 E0 00 00 00 C3";

    /// <summary>CStrategicAir::GetOrCreateRegionCombatData: losses and kills per region at sa+0x170, by region+0x58 (pins that offset; never called).</summary>
    [Signature] public const string RegionCombatDataSig = "48 89 74 24 ? 57 48 83 EC 20 48 63 C2 48 8B F9 48 8D 34 C5 00 00 00 00 48 8B 81 70 01 00 00";

    private const int RegionsOfGameState = 0x2E0; // CPdxArray<CStrategicRegion*> by region id (pinned by GroundCrews.CanExecuteSig)
    private const int SuperiorityRecords = 0xE0, SuperiorityRecordSize = 0xA0, OurSuperiority = 0x00, EnemySuperiority = 0x08;
    private const int CombatData = 0x170, OwnWings = 0x08, OtherWings = 0x20, WingDataSize = 0x98, HourOfWingData = 0x0C, DamageTaken = 0x38;
    private const int SuperiorityIdOfRegion = 0x60, CombatIdOfRegion = 0x58;

    /// <summary>Every CStrategicRegion by id (id 0 is none), empty without a game.</summary>
    public static ReadOnlySpan<nint> All => Game.State is var gs && gs != 0 ? Native.PdxArray(gs + RegionsOfGameState) : default;

    /// <summary>The region's id (+0x60), which indexes the air superiority records.</summary>
    public static int Id(nint region) => *(int*)(region + SuperiorityIdOfRegion);

    /// <summary>
    /// The air superiority of the country with CStrategicAir <paramref name="air"/> and of its enemies in the region (fixed
    /// point), or null without a record. Any thread that may read the game state.
    /// </summary>
    public static (long Ours, long Enemy)? Superiority(nint air, nint region)
    {
        var id = region == 0 ? 0 : Id(region);
        if (id <= 0 || id >= Native.PdxArray<byte>(air + SuperiorityRecords).Length) return null; // only its size is used
        var record = *(nint*)(air + SuperiorityRecords) + (nint)id * SuperiorityRecordSize;
        return (*(long*)(record + OurSuperiority), *(long*)(record + EnemySuperiority));
    }

    /// <summary>Planes the country with CStrategicAir <paramref name="air"/> lost and shot down in the region since game hour <paramref name="since"/>.</summary>
    public static (long Lost, long Kills) Losses(nint air, nint region, int since)
    {
        var combat = Native.PdxArray(air + CombatData);
        var cid = *(int*)(region + CombatIdOfRegion);
        var stats = cid > 0 && cid < combat.Length ? combat[cid] : 0;
        return stats == 0 ? (0, 0) : (PlanesLost(stats + OwnWings, since), PlanesLost(stats + OtherWings, since));
    }

    /// <summary>
    /// Region id -> planes <paramref name="tag"/> lost and shot down there since game hour <paramref name="since"/>, for
    /// regions with air combat. <paramref name="contested"/>: only regions where the enemy has air superiority. Any thread
    /// that may read the game state.
    /// </summary>
    public static Dictionary<int, (long Lost, long Kills)> Combat(int tag, int since, bool contested)
    {
        var fights = new Dictionary<int, (long, long)>();
        var air = Game.StrategicAir(tag);
        if (air == 0) return fights;
        var regions = All;
        for (var id = 1; id < regions.Length; id++)
        {
            var region = regions[id];
            if (region == 0 || contested && Superiority(air, region) is not { Enemy: > 0 }) continue;
            var (lost, kills) = Losses(air, region, since);
            if (lost + kills > 0) fights[id] = (lost, kills);
        }
        return fights;
    }

    /// <summary>Planes shot down (by planes, AA or trains) since the hour, from a region's wing records (combat data +8 own, +0x20 others).</summary>
    public static long PlanesLost(nint wings, int since)
    {
        long planes = 0;
        var records = *(nint*)wings;
        for (var j = *(int*)(wings + 0xC) - 1; j >= 0; j--)
        {
            var record = records + (nint)j * WingDataSize;
            if (*(int*)(record + HourOfWingData) < since) break;
            var damage = *(nint*)(record + DamageTaken);
            for (int k = 0, m = *(int*)(record + DamageTaken + 0xC); k < m; k++)
                if (*(int*)(damage + k * 0x18 + 8) is 4 or 5 or 10) planes += *(long*)(damage + k * 0x18 + 0x10);
        }
        return planes / Native.FixedPoint;
    }
}

/// <summary>
/// More Ground Crews: NAir.AIR_MORE_GROUND_CREWS_COST command power buys AIR_MORE_GROUND_CREWS_BOOST air efficiency in one
/// strategic region. While on, the cost is spent and the command power cap is lower by as much; turning it off refunds it.
/// Offsets for HOI4 1.19.3.
/// </summary>
public static unsafe class GroundCrews
{
    /// <summary>
    /// The air region view's button: 0x38 bytes, ctor (mem, int* tag, int region id, bool on) at 0x141940880.
    /// Tag +0x28, region id +0x2C, on +0x30 (byte; Commands.New's target).
    /// </summary>
    [VTable] public const string CommandClass = "CMoreGroundCrewsCommand";

    /// <summary>
    /// CMoreGroundCrewsCommand::CanExecute (vtable slot 9, 0x141948200), after the country check: region id in
    /// [1, gamestate+0x2EC) from gamestate+0x2E0; turning on needs command power (country+0x1F0) &gt;= the cost
    /// (`mov r8,[rip+AIR_MORE_GROUND_CREWS_COST]` at +58); `call IsOn(country, region)` at +89 must differ from "on".
    /// </summary>
    [Signature] public const string CanExecuteSig = "48 63 43 2C 83 F8 01 7C ? 48 8B 15 ? ? ? ? 3B 82 EC 02 00 00 7D ? 48 8B C8 48 8B 82 E0 02 00 00 48 8B 3C C8 48 85 FF 74 ? 80 7B 30 00 74 ? 48 8D 4B 28 E8 ? ? ? ? 4C 8B 05 ? ? ? ? 4C 39 80 F0 01 00 00 7C ? 48 8D 4B 28 E8 ? ? ? ? 48 8B D7 48 8B C8 E8 ? ? ? ? 3A 43 30 0F 95 C0";

    private const int CommandSize = 0x38, RegionOfCommand = 0x2C;
    private const int CommandPowerOfCountry = 0x1F0; // long, fixed point

    private static class Resolved
    {
        private static readonly nint At = Sig.Find(CanExecuteSig);
        public static readonly long* Cost = (long*)CostAt(At);
        public static readonly delegate* unmanaged<nint, nint, byte> IsOn = (delegate* unmanaged<nint, nint, byte>)IsOnAt(At);
    }

    /// <summary>The cost define's address, from a <see cref="CanExecuteSig"/> match.</summary>
    internal static nint CostAt(nint at) => at + 65 + *(int*)(at + 61);

    /// <summary>IsOn(country, CStrategicRegion*) -> bool, from the call at +89 (reads country+0x4D8: region -> state, on at +0x38).</summary>
    internal static nint IsOnAt(nint at) => at + 94 + *(int*)(at + 90);

    /// <summary>Command power it costs to turn on (fixed point; read from the define, which mods may change).</summary>
    public static long Cost => *Resolved.Cost;

    /// <summary>The country's command power (fixed point).</summary>
    public static long CommandPower(nint country) => *(long*)(country + CommandPowerOfCountry);

    /// <summary>The region ids where the country has them on.</summary>
    public static List<int> On(nint country)
    {
        var on = new List<int>();
        var regions = AirRegions.All;
        for (var id = 1; id < regions.Length; id++)
            if (regions[id] != 0 && Resolved.IsOn(country, regions[id]) != 0) on.Add(id);
        return on;
    }

    /// <summary>
    /// Turns them on or off in region <paramref name="id"/> for <paramref name="tag"/>, if the game accepts it.
    /// <paramref name="force"/> for AI countries (see <see cref="Commands.Post(nint, bool)"/>). Main thread.
    /// </summary>
    public static bool Set(int tag, int id, bool on, bool force)
    {
        var cmd = Commands.New(CommandClass, CommandSize, tag, on ? 1 : 0);
        *(int*)(cmd + RegionOfCommand) = id;
        return Commands.TryPost(cmd, force);
    }

    /// <summary>
    /// One pass for the country. Main thread. Returns the regions turned on and off.
    /// - Off: regions with no air combat since <paramref name="quietSince"/>, and one more if command power is under <paramref name="reserve"/>.
    /// - On: regions with the most planes lost and shot down since <paramref name="fightSince"/> where the enemy has air
    ///   superiority (at least <paramref name="minPlanes"/>), up to <paramref name="max"/>, keeping command power at the reserve.
    /// <paramref name="ours"/>: the regions this caller turned on, the only ones it turns off (null: any).
    /// <paramref name="handsOff"/>: regions never turned on; regions of <paramref name="ours"/> turned off by hand are added.
    /// </summary>
    public static (List<int> On, List<int> Off) Manage(nint country, long reserve, int max, int fightSince, int quietSince, int minPlanes,
        bool force, HashSet<int>? ours = null, HashSet<int>? handsOff = null)
    {
        var tag = Game.Tag(country);
        List<int> turnedOn = [], turnedOff = [];
        var on = On(country);
        if (ours != null)
        {
            handsOff?.UnionWith(ours.Except(on)); // turned off by hand since: left alone from now on
            ours.IntersectWith(on);
        }
        var active = AirRegions.Combat(tag, quietSince, false);
        var power = CommandPower(country);
        var mine = on.Where(id => ours == null || ours.Contains(id)).ToList();
        foreach (var id in mine.Where(id => !active.ContainsKey(id)).ToList())
            if (Set(tag, id, false, force)) { turnedOff.Add(id); mine.Remove(id); power += Cost; }
        if (power < reserve && mine.Count > 0)
        {
            var least = mine.MinBy(id => active.TryGetValue(id, out var f) ? f.Lost + f.Kills : 0);
            if (Set(tag, least, false, force)) { turnedOff.Add(least); power += Cost; }
        }
        var count = on.Count - turnedOff.Count;
        if (count < max && power - Cost >= reserve)
        {
            var hardest = AirRegions.Combat(tag, fightSince, true)
                .Where(f => f.Value.Lost + f.Value.Kills >= minPlanes && !on.Contains(f.Key) && handsOff?.Contains(f.Key) != true)
                .OrderByDescending(f => f.Value.Lost + f.Value.Kills).Select(f => f.Key);
            foreach (var id in hardest)
            {
                if (count >= max || power - Cost < reserve) break;
                if (!Set(tag, id, true, force)) continue;
                turnedOn.Add(id);
                count++;
                power -= Cost;
            }
        }
        foreach (var id in turnedOff) ours?.Remove(id);
        foreach (var id in turnedOn) ours?.Add(id);
        return (turnedOn, turnedOff);
    }
}
