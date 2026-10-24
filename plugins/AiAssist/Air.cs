using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Air retreat: regions the AI is losing get a lower score, so its allocator moves the planes ----

    private const int RegionEntrySize = 0x4E8, RegionOfEntry = 0x00, KindOfEntry = 0x08, ScoreOfEntry = 0x10, IgnoredRegion = 5;
    private const int WindowHours = 168, MinLosses = 20;
    private const long MinShare = 35_000, PullOutShare = 15_000; // our share of the region's air superiority
    private const int LossesPerKillPercent = 150;

    [Function(CallingConventions.Microsoft)]
    public delegate void PrioritiesFn(nint aiObj, nint country, nint scratch, nint buffer, nint entries, nint bases, nint totals, nint buffer2);

    private IHook<PrioritiesFn>? _priorities;
    private int _pulled;
    private static bool _airRetreat;

    /// <summary>Region scoring hook shared by air retreat, bombing targets and invasion escorts, each under its own setting.</summary>
    private void LoadRegionPriorities()
    {
        _ = Sig.FindAll(Game.StrategicAirSig, 2)[0] + Sig.Find(AirRegions.RegionAirDataSig) + Sig.Find(AirRegions.RegionCombatDataSig) // the functions AirRegions' offsets come from
            + Sig.Find(Signatures.RegionNeighbours) + Sig.Find(Signatures.StrBombWishes);
        _priorities = Patches.Hook<PrioritiesFn>(Sig.Find(Signatures.UpdateRegionPriorities), (aiObj, country, scratch, buffer, entries, bases, totals, buffer2) =>
        {
            _priorities!.OriginalFunction(aiObj, country, scratch, buffer, entries, bases, totals, buffer2);
            Safely(() =>
            {
                if (_bombingTargets) BombingTargets(country, entries, totals); // first, so air retreat can still lower a lost region
                if (_strategicInvasions) EscortInvasions(country, entries, totals);
                if (_airRetreat) AirRetreat(country, entries);
            });
        });
    }

    /// <summary>Regions with under 35 % of air superiority, 20+ planes lost in a week and 1.5+ lost per kill get a scaled-down score; under 15 %: 0.</summary>
    private void AirRetreat(nint country, nint entries)
    {
        var tag = Game.Tag(country);
        var air = tag == Game.ControlledTag ? 0 : Game.StrategicAir(tag);
        if (air == 0) return;
        var since = Game.Hour - WindowHours;
        var data = *(nint*)entries;
        for (int i = 0, n = *(int*)(entries + 0xC); i < n; i++)
        {
            var entry = data + (nint)i * RegionEntrySize;
            var region = *(nint*)(entry + RegionOfEntry);
            var score = *(long*)(entry + ScoreOfEntry);
            if (region == 0 || score <= 0 || *(int*)(entry + KindOfEntry) == IgnoredRegion) continue;
            if (AirRegions.Superiority(air, region) is not (var ours, var enemy) || enemy <= 0) continue;
            var share = ours * Native.FixedPoint / (ours + enemy);
            if (share >= MinShare) continue;
            var (lost, kills) = AirRegions.Losses(air, region, since);
            if (lost < MinLosses || lost * 100 < kills * LossesPerKillPercent) continue;
            *(long*)(entry + ScoreOfEntry) = share < PullOutShare ? 0 : score * share / MinShare;
            Interlocked.Increment(ref _pulled);
        }
    }

    // ---- Bombing targets: strategic bombers go for the enemy regions next to the front ----

    private const int StrategicBombingRegion = 4;                      // entry kind the STR_BOMB_* rules score: enemy-held region
    private const int WishesOfEntry = 0x68, StrategicBombing = 3;      // planes wanted per mission (fixed point), by mission bit
    private const int WaterOfRegion = 0xA0, NeighboursOfRegion = 0xB0; // water share (> 50 %: sea region); CPdxArray<CStrategicRegion*>
    private const long SeaShare = 50_000, MinScore = 100;              // MinScore: vanilla's floor for a scored region
    private const int NextToFrontWishPercent = 200, FarPercent = 50;
    private static bool _bombingTargets;
    private static int _bombing;

    /// <summary>
    /// Scales strategic bombing targets by distance to our land front (land regions both sides hold ground in): next to it
    /// twice the bombers, one further as is, farther half the score and bombers. Scores are never raised, as that would take
    /// CAS and fighters from the front. No land front: vanilla. Runs on AI worker threads, on this country's entries only.
    /// </summary>
    private static void BombingTargets(nint country, nint entries, nint totals)
    {
        var tag = Game.Tag(country);
        if (tag == 0 || tag == Game.ControlledTag) return;
        var data = *(nint*)entries;
        var n = *(int*)(entries + 0xC);
        if (data == 0 || n <= 0) return;
        Span<byte> ring = n <= 4096 ? stackalloc byte[n] : new byte[n]; // 1 front, 2 next to it, 3 one further, 0 farther
        Span<int> queue = n <= 4096 ? stackalloc int[n] : new int[n];
        ring.Clear();
        int head = 0, tail = 0;
        for (var i = 0; i < n; i++)
        {
            var entry = data + (nint)i * RegionEntrySize;
            if (*(int*)(entry + KindOfEntry) is 0 or 2 && LandRegion(*(nint*)(entry + RegionOfEntry))) { ring[i] = 1; queue[tail++] = i; }
        }
        if (tail == 0) return;
        while (head < tail)
        {
            var i = queue[head++];
            var region = *(nint*)(data + (nint)i * RegionEntrySize + RegionOfEntry);
            if (ring[i] == 3 || region == 0) continue;
            foreach (var next in Native.PdxArray(region + NeighboursOfRegion))
            {
                if (!LandRegion(next)) continue;
                var j = AirRegions.Id(next) - 1; // entry 0 is region 1
                if ((uint)j >= (uint)n || ring[j] != 0) continue;
                ring[j] = (byte)(ring[i] + 1);
                queue[tail++] = j;
            }
        }
        long delta = 0;
        var changed = false;
        for (var i = 0; i < n; i++)
        {
            var entry = data + (nint)i * RegionEntrySize;
            var score = *(long*)(entry + ScoreOfEntry);
            if (*(int*)(entry + KindOfEntry) != StrategicBombingRegion || score <= 0 || ring[i] == 3 || !LandRegion(*(nint*)(entry + RegionOfEntry))) continue;
            var wish = (long*)(entry + WishesOfEntry + 8 * StrategicBombing);
            long scaled;
            if (ring[i] == 2) scaled = *wish * NextToFrontWishPercent / 100;
            else
            {
                *(long*)(entry + ScoreOfEntry) = Math.Max(MinScore, score * FarPercent / 100);
                scaled = *wish * FarPercent / 100;
                if (scaled < Native.FixedPoint) scaled = 0; // under a plane: vanilla then drops its escorts too
            }
            delta += scaled - *wish;
            *wish = scaled;
            changed = true;
        }
        if (totals != 0) *(long*)(totals + 8 * StrategicBombing) += delta; // the per-mission sums the original already made
        if (changed) Interlocked.Increment(ref _bombing);
    }

    private static bool LandRegion(nint region) => region != 0 && *(long*)(region + WaterOfRegion) <= SeaShare;

    // ---- Breakthrough axis, in the air: battles on the axis get close air support first ----

    // The wing's owner as the game reads it (0x140F5AAD0): wing+0x9C4 when set, else *(*(wing+0x38)+0x40)+0x90.
    private const int MissionBitsOfAirMission = 0x14, WingOfAirMission = 0x28, OwnerOfWing = 0x9C4, EquipmentOfWing = 0x38, OwnerOfEquipment = 0x40, TagOfOwner = 0x90;
    private const int CasMission = 2, TargetSize = 40, ProvinceOfTarget = 0x10; // STacticalTarget: inline, province id within

    [Function(CallingConventions.Microsoft)] public delegate nint TacticalFn(nint mission, nint targets, int chanceBase, int seed, byte includeDivisions, byte firstOnly);

    private IHook<TacticalFn>? _tactical;
    private static int _cas;

    private void LoadCas()
    {
        _tactical = Patches.Hook<TacticalFn>(Sig.Find(Signatures.CollectTacticalTargets), (mission, targets, chanceBase, seed, includeDivisions, firstOnly) =>
        {
            var result = _tactical!.OriginalFunction(mission, targets, chanceBase, seed, includeDivisions, firstOnly);
            // 0 targets: the game only counts them. No closure: every air mission, hourly.
            if (targets == 0 || !SheepMod.Active || (*(int*)(mission + MissionBitsOfAirMission) & CasMission) == 0) return result;
            try { ConcentrateCas(mission, targets); }
            catch (Exception e) { Failed(e); }
            return result;
        });
    }

    /// <summary>
    /// Planes go to targets in array order, so battles on the axis move to the front (stable sort). Game thread, hourly.
    /// </summary>
    private static void ConcentrateCas(nint mission, nint targets)
    {
        var wing = *(nint*)(mission + WingOfAirMission);
        if (wing == 0) return;
        var tag = WingOwner(wing);
        if (tag <= 0 || tag == Game.ControlledTag) return;
        var data = (byte*)*(nint*)targets;
        int n = *(int*)(targets + 0xC);
        if (data == null || n < 2) return;
        Span<bool> onAxis = n <= 1024 ? stackalloc bool[n] : new bool[n];
        var count = 0;
        for (var i = 0; i < n; i++)
            if (OnAnyAxis(tag, *(int*)(data + (nint)i * TargetSize + ProvinceOfTarget))) { onAxis[i] = true; count++; }
        if (count == 0 || count == n) return;
        var size = n * TargetSize;
        Span<byte> copy = size <= 16384 ? stackalloc byte[size] : new byte[size];
        fixed (byte* dest = copy)
        {
            var k = 0;
            for (var i = 0; i < n; i++) if (onAxis[i]) Buffer.MemoryCopy(data + (nint)i * TargetSize, dest + (nint)k++ * TargetSize, TargetSize, TargetSize);
            for (var i = 0; i < n; i++) if (!onAxis[i]) Buffer.MemoryCopy(data + (nint)i * TargetSize, dest + (nint)k++ * TargetSize, TargetSize, TargetSize);
            Buffer.MemoryCopy(dest, data, copy.Length, copy.Length);
        }
        Interlocked.Increment(ref _cas);
    }

    /// <summary>The wing's country tag, as the game reads it; 0 when it has none.</summary>
    private static int WingOwner(nint wing)
    {
        var tag = *(int*)(wing + OwnerOfWing);
        if (tag > 0) return tag;
        var equipment = *(nint*)(wing + EquipmentOfWing);
        var owner = equipment == 0 ? 0 : *(nint*)(equipment + OwnerOfEquipment);
        return owner == 0 ? 0 : *(int*)(owner + TagOfOwner);
    }
}
