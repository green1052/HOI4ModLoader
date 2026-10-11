using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Air retreat: regions the AI is losing badly get a lower score, so its own allocator moves the planes ----

    private const int RegionEntrySize = 0x4E8, RegionOfEntry = 0x00, KindOfEntry = 0x08, ScoreOfEntry = 0x10, IgnoredRegion = 5;
    private const int SuperiorityRecords = 0xE0, SuperiorityRecordSize = 0xA0, OurSuperiority = 0x00, EnemySuperiority = 0x08;
    private const int CombatData = 0x170, OwnWings = 0x08, OtherWings = 0x20, WingDataSize = 0x98, HourOfWingData = 0x0C, DamageTaken = 0x38;
    private const int SuperiorityIdOfRegion = 0x60, CombatIdOfRegion = 0x58, HourOfGameState = 0x468;
    private const int WindowHours = 168, MinLosses = 20;
    private const long MinShare = 35_000, PullOutShare = 15_000; // our share of the region's air superiority
    private const int LossesPerKillPercent = 150;

    [Function(CallingConventions.Microsoft)]
    public delegate void PrioritiesFn(nint aiObj, nint country, nint scratch, nint buffer, nint entries, nint bases, nint totals, nint buffer2);

    private IHook<PrioritiesFn>? _priorities;
    private int _holderOffset, _countOffset, _arrayOffset;
    private int _pulled;
    private static bool _airRetreat;

    private void LoadAirRetreat()
    {
        var at = Sig.FindAll(Signatures.StrategicAir, 2)[0];
        (_holderOffset, _countOffset, _arrayOffset) = (*(int*)(at + 3), *(byte*)(at + 14), *(byte*)(at + 36));
        _ = Sig.Find(Signatures.RegionAirData) + Sig.Find(Signatures.RegionCombatData); // the offsets below are still where they were
        _priorities = Patches.Hook<PrioritiesFn>(Sig.Find(Signatures.UpdateRegionPriorities), (aiObj, country, scratch, buffer, entries, bases, totals, buffer2) =>
        {
            _priorities!.OriginalFunction(aiObj, country, scratch, buffer, entries, bases, totals, buffer2);
            Safely(() => AirRetreat(country, entries));
        });
    }

    /// <summary>Scales down the score of regions where we hold under 35 % of air superiority, lost 20+ planes in a week and 1.5+ per kill; under 15 %: 0.</summary>
    private void AirRetreat(nint country, nint entries)
    {
        var tag = *(int*)(country + 8);
        var holder = tag == Game.ControlledTag ? 0 : *(nint*)(Game.State + _holderOffset);
        if (holder == 0 || *(int*)(holder + _countOffset) == 0) return;
        var air = *(nint*)(*(nint*)(holder + _arrayOffset) + 8 * Game.TagIndex(tag));
        if (air == 0) return;
        var since = Hour - WindowHours;
        var records = Native.PdxArray<byte>(air + SuperiorityRecords); // only its size and data are used
        var combat = Native.PdxArray(air + CombatData);
        var data = *(nint*)entries;
        for (int i = 0, n = *(int*)(entries + 0xC); i < n; i++)
        {
            var entry = data + (nint)i * RegionEntrySize;
            var region = *(nint*)(entry + RegionOfEntry);
            var score = *(long*)(entry + ScoreOfEntry);
            if (region == 0 || score <= 0 || *(int*)(entry + KindOfEntry) == IgnoredRegion) continue;
            var id = *(int*)(region + SuperiorityIdOfRegion);
            if (id <= 0 || id >= records.Length) continue;
            var record = *(nint*)(air + SuperiorityRecords) + (nint)id * SuperiorityRecordSize;
            long ours = *(long*)(record + OurSuperiority), enemy = *(long*)(record + EnemySuperiority);
            if (enemy <= 0) continue;
            var share = ours * Native.FixedPoint / (ours + enemy);
            if (share >= MinShare) continue;
            var cid = *(int*)(region + CombatIdOfRegion);
            var stats = cid > 0 && cid < combat.Length ? combat[cid] : 0;
            if (stats == 0) continue;
            long lost = PlanesLost(stats + OwnWings, since), kills = PlanesLost(stats + OtherWings, since);
            if (lost < MinLosses || lost * 100 < kills * LossesPerKillPercent) continue;
            *(long*)(entry + ScoreOfEntry) = share < PullOutShare ? 0 : score * share / MinShare;
            Interlocked.Increment(ref _pulled);
        }
    }

    /// <summary>Planes shot down (by planes, AA or trains) in the wing records since the hour, newest first.</summary>
    private static long PlanesLost(nint wings, int since)
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

    // ---- Breakthrough axis, in the air: battles on the day's axis get the close air support first ----

    private const int MissionBitsOfAirMission = 0x14, WingOfAirMission = 0x28, PoolOfWing = 0x38, CountryOfPool = 0x20;
    private const int CasMission = 2, TargetSize = 40, ProvinceOfTarget = 0x10; // STacticalTarget: inline, province id within

    [Function(CallingConventions.Microsoft)] public delegate nint TacticalFn(nint mission, nint targets, int chanceBase, int seed, byte includeDivisions, byte firstOnly);

    private IHook<TacticalFn>? _tactical;
    private static int _cas;

    private void LoadCas()
    {
        _tactical = Patches.Hook<TacticalFn>(Sig.Find(Signatures.CollectTacticalTargets), (mission, targets, chanceBase, seed, includeDivisions, firstOnly) =>
        {
            var result = _tactical!.OriginalFunction(mission, targets, chanceBase, seed, includeDivisions, firstOnly);
            if (targets != 0) // 0: the game only counts the targets
                Safely(() =>
                {
                    if ((*(int*)(mission + MissionBitsOfAirMission) & CasMission) != 0) ConcentrateCas(mission, targets);
                });
            return result;
        });
    }

    /// <summary>
    /// The plane budget is spent in the collector's array order, so battles on the axis move to the front of it (stable:
    /// between two axis battles the game's own order decides). Read only: main thread, per wing and hour.
    /// </summary>
    private static void ConcentrateCas(nint mission, nint targets)
    {
        var wing = *(nint*)(mission + WingOfAirMission);
        var pool = wing == 0 ? 0 : *(nint*)(wing + PoolOfWing);
        var country = pool == 0 ? 0 : *(nint*)(pool + CountryOfPool);
        if (country == 0) return;
        var tag = *(int*)(country + 8);
        if (tag == 0 || tag == Game.ControlledTag) return;
        var data = (byte*)*(nint*)targets;
        int n = *(int*)(targets + 0xC);
        if (data == null || n < 2) return;
        var onAxis = new bool[n];
        var count = 0;
        for (var i = 0; i < n; i++)
            if (OnAnyAxis(tag, *(int*)(data + (nint)i * TargetSize + ProvinceOfTarget))) { onAxis[i] = true; count++; }
        if (count == 0 || count == n) return;
        var copy = new byte[(nint)n * TargetSize];
        fixed (byte* dest = copy)
        {
            var k = 0;
            for (var i = 0; i < n; i++) if (onAxis[i]) Buffer.MemoryCopy(data + (nint)i * TargetSize, dest + (nint)k++ * TargetSize, TargetSize, TargetSize);
            for (var i = 0; i < n; i++) if (!onAxis[i]) Buffer.MemoryCopy(data + (nint)i * TargetSize, dest + (nint)k++ * TargetSize, TargetSize, TargetSize);
            Buffer.MemoryCopy(dest, data, copy.Length, copy.Length);
        }
        Interlocked.Increment(ref _cas);
    }
}
