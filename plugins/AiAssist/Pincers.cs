using HOI4ModLoader;

namespace AiAssist;

public partial class AiAssist
{
    // ---- Pincers and feints: two arms around a massed enemy; a feint elsewhere before a new push ----

    private const int PincerHead = -2;                             // Axes.Head of a pincer's first arm
    private const int MinCluster = 3, PincerNear = 2, PincerFar = 5; // divisions to count as massed; arms' distance from the mass (line provinces)
    private const int FeintHours = 36, FeintDistance = 5;

    private static bool _pincers, _feints;
    private static int _pincered, _feinted;

    // general -> the pincer's second arm (Core as in Axes); general -> the feint before its push.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<int> Core, int Anchor)> SecondArms = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Until, HashSet<int> Core, int Anchor)> Feints = new();

    /// <summary>
    /// The enemy line's mass (MinCluster+ divisions and twice the line's average) and the thinnest enemy line province on
    /// each side of it, PincerNear..PincerFar away. A side is the first step out of the mass along the line. Null if none.
    /// </summary>
    private static (int A, int B, int Mass, int Enemies)? Pincer(HashSet<nint> enemyLine)
    {
        if (enemyLine.Count < 2 * PincerNear + 1) return null;
        var mass = enemyLine.MaxBy(Province.Divisions);
        var enemies = Province.Divisions(mass);
        if (enemies < MinCluster || enemies * enemyLine.Count < 2 * enemyLine.Sum(Province.Divisions)) return null;
        // Along the enemy line from the mass: each province's distance and side.
        var side = new Dictionary<nint, (int Depth, nint Side)> { [mass] = (0, 0) };
        var frontier = new List<nint> { mass };
        for (var depth = 1; depth <= PincerFar && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var p in frontier)
            foreach (var id in Game.Neighbours(p))
                if (Game.Province(id) is var q && q != 0 && enemyLine.Contains(q) && !side.ContainsKey(q))
                {
                    side[q] = (depth, depth == 1 ? q : side[p].Side);
                    next.Add(q);
                }
            frontier = next;
        }
        int Weight(nint p) => 3 * Province.Divisions(p) + Province.FortLevel(p);
        var arms = side.Where(e => e.Value.Depth >= PincerNear).GroupBy(e => e.Value.Side)
            .Select(g => g.MinBy(e => (Weight(e.Key), e.Value.Depth)).Key).OrderBy(Weight).Take(2).ToList();
        return arms.Count == 2 ? (Game.Id(arms[0]), Game.Id(arms[1]), Game.Id(mass), enemies) : null;
    }

    /// <summary>
    /// Before a push at <paramref name="anchor"/>: a FeintHours feint at the enemy line province with the most divisions at
    /// least FeintDistance away, to draw reserves there. The push waits meanwhile.
    /// </summary>
    private static void StartFeint(nint general, int tag, int anchor, HashSet<nint> enemyLine, int hour)
    {
        Feints.TryRemove(general, out _);
        if (!_feints || Game.Province(anchor) is var at && at == 0) return;
        var near = new HashSet<nint> { at };
        var frontier = new List<nint> { at };
        for (var depth = 1; depth < FeintDistance && frontier.Count > 0; depth++)
            frontier = frontier.SelectMany(p => Game.Neighbours(p)).Select(Game.Province).Where(q => q != 0 && near.Add(q)).ToList();
        var held = enemyLine.Where(p => !near.Contains(p) && Province.Divisions(p) > 0).ToList();
        if (held.Count == 0) return;
        var feint = held.MaxBy(Province.Divisions);
        Feints[general] = (tag, hour + FeintHours, Rings(Game.Id(feint)), Game.Id(feint));
        Interlocked.Increment(ref _feinted);
        _log?.Debug($"#{tag}: feints at {Game.Id(feint)} ({Province.Divisions(feint)} enemy division(s)) for {FeintHours} hours before its push at {anchor}");
    }

    /// <summary>The general's feint running now, or null.</summary>
    private static HashSet<int>? FeintCore(nint general, int tag, int hour) =>
        Feints.TryGetValue(general, out var f) && f.Tag == tag && hour - f.Until < 0 ? f.Core : null;

    /// <summary>The general's current pincer second arm, or null.</summary>
    private static HashSet<int>? SecondCore(nint general, int tag, int hour) =>
        SecondArms.TryGetValue(general, out var s) && s.Tag == tag && hour - s.Hour is >= 0 and < AxisFreshHours ? s.Core : null;
}
