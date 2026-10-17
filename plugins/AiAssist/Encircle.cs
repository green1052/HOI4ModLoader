using System.Collections.Concurrent;
using HOI4ModLoader;

namespace AiAssist;

public partial class AiAssist
{
    // ---- Encirclement: the breakthrough axis cuts an enemy salient at its neck instead of pushing it head-on ----

    private const int SalientHours = 7 * 24, SalientSearchHours = 24; // a neck is kept a week at most; searched and re-checked daily
    private const int MaxSalient = 30, MinTrapped = 3, MaxNecks = 80;     // max pocket provinces; min enemy divisions cut off; max neck candidates

    private static bool _encircle;
    private static int _salients, _closed;
    // general -> its salient: last check, tag, world (Game.Generation), since, neck (empty: none found), and the axis core around
    // the neck. Province ids only; an entry is never changed once published.
    private static readonly ConcurrentDictionary<nint, (int Hour, int Tag, int World, int Since, int[] Neck, HashSet<int>? Core)> Salients = new();

    private void LoadEncircle() => _encircle = true;

    /// <summary>
    /// The enemy salient to cut off today, or null. A salient is kept for a week at most while what is left of its neck still
    /// traps enough (re-checked daily); otherwise the best one on the line is searched, once a day. AI thread of the general.
    /// </summary>
    private static (int[] Neck, HashSet<int> Core)? Salient(nint general, nint country, int tag, HashSet<nint> enemyLine, int hour)
    {
        var world = Game.Generation;
        if (!Salients.ContainsKey(general)) // a new general: drop stale entries
            foreach (var (g, old) in Salients)
                if (old.World != world || hour - old.Hour is < 0 or >= SalientHours) Salients.TryRemove(g, out _);
        if (Salients.TryGetValue(general, out var s) && s.Tag == tag && s.World == world && hour - s.Hour >= 0)
        {
            if (s.Neck.Length == 0 && hour - s.Hour < SalientSearchHours) return null; // none today
            if (s.Neck.Length > 0 && hour - s.Since < SalientHours)
            {
                if (hour - s.Hour < SalientSearchHours) return (s.Neck, s.Core!);
                var left = Array.FindAll(s.Neck, id => Game.Province(id) is var p && p != 0 && Province.Enemy(country, tag, p));
                if (left.Length > 0 && new SalientMap(country, tag).Cut(left) >= MinTrapped)
                {
                    Salients[general] = (hour, tag, world, s.Since, left, s.Core);
                    return (left, s.Core!);
                }
                if (left.Length == 0) Interlocked.Increment(ref _closed); // the neck is ours: closed
            }
        }
        var neck = Search(country, tag, enemyLine);
        if (neck == null)
        {
            Salients[general] = (hour, tag, world, hour, [], null);
            return null;
        }
        var core = new HashSet<int>(neck); // the neck and its neighbours, where our flanks attack from
        foreach (var id in neck)
            if (Game.Province(id) is var p && p != 0) core.UnionWith(Game.Neighbours(p));
        Salients[general] = (hour, tag, world, hour, neck, core);
        Interlocked.Increment(ref _salients);
        return (neck, core);
    }

    /// <summary>
    /// The best neck among the enemy provinces on our line: one province, two adjacent, or three in a row (the middle one may
    /// be off our line). Score: 2 per enemy division cut off minus the defence (<see cref="Province.AttackWeight"/> from our
    /// easiest side). Null when none cuts off <see cref="MinTrapped"/> divisions with a positive score.
    /// </summary>
    // ponytail: necks of up to three provinces in a row, pockets up to MaxSalient provinces; a wider neck is missed.
    private static int[]? Search(nint country, int tag, HashSet<nint> enemyLine)
    {
        var map = new SalientMap(country, tag);
        var candidates = new HashSet<int>();
        foreach (var p in enemyLine)
            if (candidates.Count < MaxNecks) candidates.Add(Game.Id(p));
        (int[]? Neck, int Score) best = (null, 0);
        foreach (var c in candidates)
        {
            Try([c]);
            foreach (var n in map.Neighbours(c))
            {
                if (n > c && candidates.Contains(n)) Try([c, n]);
                if (!map.IsTheirs(n)) continue;
                foreach (var m in map.Neighbours(n))
                    if (m > c && m != n && candidates.Contains(m)) Try([c, n, m]);
            }
        }
        return best.Neck;

        void Try(int[] neck)
        {
            var cut = map.Cut(neck);
            if (cut < MinTrapped) return;
            var score = 2 * cut - map.Defence(neck);
            if (score > best.Score) best = (neck, score);
        }
    }

    /// <summary>One search's view of the map: each province's side is read once.</summary>
    private sealed class SalientMap(nint country, int tag)
    {
        private readonly Dictionary<int, int> _sides = [];

        public int[] Neighbours(int id) => Game.Province(id) is var p && p != 0 ? Game.Neighbours(p) : [];

        /// <summary>Ours or allied, enemy-held, neutral land, or none (sea, wasteland).</summary>
        private int Side(int id)
        {
            if (_sides.TryGetValue(id, out var side)) return side;
            var p = Game.Province(id);
            side = p == 0 || Province.Controller(p) <= 0 ? SalientFill.None
                : Province.Enemy(country, tag, p) ? SalientFill.Theirs : Province.Friendly(country, tag, p) ? SalientFill.Ours : SalientFill.Neutral;
            return _sides[id] = side;
        }

        public bool IsTheirs(int id) => Side(id) == SalientFill.Theirs;

        /// <summary>See <see cref="SalientFill.Cut"/>.</summary>
        public int Cut(int[] neck) => SalientFill.Cut(neck, Neighbours, Side, id => Province.Divisions(Game.Province(id)), MaxSalient);

        /// <summary>What taking the neck costs: each province's attack weight from our easiest side of it.</summary>
        public int Defence(int[] neck)
        {
            var total = 0;
            foreach (var id in neck)
            {
                var p = Game.Province(id);
                var weight = int.MaxValue;
                foreach (var n in Neighbours(id))
                    if (Side(n) == SalientFill.Ours && Game.Province(n) is var from && from != 0) weight = Math.Min(weight, Province.AttackWeight(p, from));
                total += weight == int.MaxValue ? 0 : weight;
            }
            return total;
        }
    }
}

/// <summary>The flood fill behind salient detection, free of game memory (tests\Signatures.Tests\SalientFillTests.cs).</summary>
public static class SalientFill
{
    public const int None = -1, Ours = 0, Theirs = 1, Neutral = 2; // sea or wasteland, ours or allied, enemy-held, neutral land

    /// <summary>
    /// Enemy divisions cut off once the <paramref name="neck"/> is ours: those in the enemy regions next to it that close within
    /// <paramref name="maxSize"/> provinces without touching neutral land. 0 unless another region next to the neck stays open
    /// (otherwise it is a pocket already).
    /// </summary>
    public static int Cut(int[] neck, Func<int, int[]> neighbours, Func<int, int> side, Func<int, int> divisions, int maxSize)
    {
        var fills = new Dictionary<int, int>(); // province id -> the fill that reached it (0: the neck)
        foreach (var id in neck) fills[id] = 0;
        var (cut, fill, open) = (0, 0, false);
        var region = new List<int>();
        foreach (var n in neck)
        foreach (var start in neighbours(n))
        {
            if (side(start) != Theirs || !fills.TryAdd(start, ++fill)) continue;
            region.Clear();
            region.Add(start);
            var leaks = false;
            for (var i = 0; i < region.Count && !leaks; i++)
                foreach (var id in neighbours(region[i]))
                {
                    var s = side(id);
                    if (s == Neutral) leaks = true; // a way out
                    else if (s != Theirs) continue;
                    else if (fills.TryAdd(id, fill))
                    {
                        region.Add(id);
                        leaks = region.Count > maxSize; // the enemy's main ground
                    }
                    else leaks = fills[id] != fill && fills[id] != 0; // joins a region an earlier fill found open
                    if (leaks) break;
                }
            if (leaks)
            {
                open = true;
                continue;
            }
            foreach (var id in region) cut += divisions(id);
        }
        return open ? cut : 0;
    }
}
