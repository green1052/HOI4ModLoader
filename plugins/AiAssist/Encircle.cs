using System.Collections.Concurrent;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Encirclement: an enemy salient is cut at its neck from both flanks instead of pushed head-on (part of the breakthrough axis) ----

    private const int SalientHours = 7 * 24, SalientSearchHours = 24; // a neck is kept a week at most; searched (and re-checked) daily
    private const int MaxSalient = 30, MinTrapped = 3, MaxNecks = 80;     // provinces a pocket may have; enemy divisions it must cut off; neck candidates

    private static bool _encircle;
    private static int _salients, _closed;
    // general -> its salient: checked when, for which tag and world (Game.Generation), since when, its neck (none: no salient found
    // at Hour), and the axis core around the neck. Ids only, never changed once published.
    private static readonly ConcurrentDictionary<nint, (int Hour, int Tag, int World, int Since, int[] Neck, HashSet<int>? Core)> Salients = new();

    private void LoadEncircle() => _encircle = true;

    /// <summary>
    /// The theatre's enemy salient to cut off today, or null: kept while what is left of its neck still cuts off a pocket worth
    /// it (re-checked daily) for a week at most, else the best one on its line (searched once a day). AI thread of the general.
    /// </summary>
    private static (int[] Neck, HashSet<int> Core)? Salient(nint general, nint country, int tag, HashSet<nint> enemyLine, int hour)
    {
        var world = Game.Generation;
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
        var core = new HashSet<int>(neck); // the neck and the ground on both sides of it: our flanks attack into it from there
        foreach (var id in neck)
            if (Game.Province(id) is var p && p != 0) core.UnionWith(Game.Neighbours(p));
        Salients[general] = (hour, tag, world, hour, neck, core);
        Interlocked.Increment(ref _salients);
        return (neck, core);
    }

    /// <summary>
    /// The best neck among the enemy provinces on our line: one province, or two next to each other, whose taking cuts off the
    /// most enemy divisions (2 per division) for the least defence (<see cref="Province.AttackWeight"/>, from our easiest side); null
    /// when none cuts off <see cref="MinTrapped"/> divisions for more than it costs.
    /// </summary>
    // ponytail: necks of one or two adjacent provinces on the line, pockets up to MaxSalient provinces; a deeper or wider neck is missed.
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
                if (n > c && candidates.Contains(n)) Try([c, n]);
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

    /// <summary>One search's view of the map around a theatre's line: neighbours and sides, read once per province.</summary>
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

/// <summary>The flood fill behind salient detection, apart from the game's memory (tests\Signatures.Tests\SalientFillTests.cs).</summary>
public static class SalientFill
{
    public const int None = -1, Ours = 0, Theirs = 1, Neutral = 2; // sides: sea or wasteland, ours or allied, enemy-held, neutral land

    /// <summary>
    /// Enemy divisions cut off once the <paramref name="neck"/> is ours: in the regions of enemy land next to it that close within
    /// <paramref name="maxSize"/> provinces without neutral land, provided another region next to it stays open (the neck is what
    /// links them to the enemy's main ground: no pocket already). 0 otherwise.
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
