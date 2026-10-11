using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Defensive postures: an AI country facing a much stronger enemy gives up exposed ground for its grad, the most
    //      defensible short line between that enemy and its core, holds it, counterattacks only fought-out spearheads
    //      (pinning the enemy stacks that could help them), and goes back to its fronts once strong enough ----
    //
    // Vanilla has fallback lines (CAIFront fallback update 0x141069F50: ShouldFallback 0x14105CB60 reads PLAN_MIN_SIZE_FOR_FALLBACK
    // against the country at 0x14105CB85, the unit send 0x141064A70 reads FALLBACK_LOSING_FACTOR at 0x141064BC0, the line is drawn as
    // COrderNewFallbackCommand), but Sheep Mod turns them off (PLAN_MIN_SIZE_FOR_FALLBACK = 2000, FALLBACK_LOSING_FACTOR = 0), and the
    // front update deletes any fallback order it doesn't want (0x14106A0F1..0x14106A117). So the grad is province ids, held with
    // plain moves and pinned divisions, never an order.

    private const int DecideHours = 24, MinGradHours = 14 * 24, RedrawHours = 7 * 24;
    private const int StrongRatio = 200, PressedRatio = 130, EvenRatio = 100; // enemy power facing a sector per our share of it, %
    private const int GradDepth = 10, MaxGradNodes = 3000, MaxGradMoves = 6;
    // ponytail: hand-tuned trade of ground against line; scale the giving-up cost by the strength ratio if grads run too deep or never form.
    private const int HoldCost = 100, AbandonBase = 20, AbandonPerVp = 5, Inf = 1 << 28;
    private const int FortressVp = 5, ExhaustedOrg = 25, FreshOrg = 70, CounterSize = 3, MaxCounters = 8;
    private const int Hills = 0x4000, Forest = 0x8000; // CTerrainType+0x8C category bits, set by terrain name (ctor 0x14143BCF0: `or dword [r14+8Ch],4000h` at 0x14143C100, 8000h at 0x14143C132)

    private static bool _postures;
    private static int _garrisoned, _countered, _fellBack, _pinning, _brokeOff;

    /// <summary>
    /// A country's grad: province id -> defenders wanted, its fortress cities, the explored ground in front of it it gives up, the
    /// enemies it is drawn against. Published whole, never mutated (AI threads read it).
    /// </summary>
    private sealed record Grad(int Hour, Dictionary<int, int> Want, HashSet<int> Fortresses, HashSet<int> Given, HashSet<int> Strong);

    /// <summary>A country's posture decision: when made, since when it holds a grad, against whom, the line it saw (ids). Main thread only.</summary>
    private sealed record Stance(int Decided, int Since, HashSet<int> Strong, HashSet<int> Border);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Grad> Grads = new();
    private static readonly Dictionary<int, Stance> Stances = [];
    private static readonly Dictionary<nint, int> Attackers = []; // our counter and pinning attackers, with their tag: broken off when worn. Main thread; never dereferenced unless still the tag's unit.

    /// <summary>
    /// The tag's grad if it should hold one (decided daily, kept two weeks at least), redrawn weekly, when the enemies change
    /// or once the enemy broke into it (the next line back); null: its fronts are the AI's. Main thread.
    /// </summary>
    private static Grad? Posture(nint country, int tag, int hour)
    {
        var border = LineOf(tag, hour);
        if (!Stances.TryGetValue(tag, out var stance) || hour - stance.Decided is < 0 or >= DecideHours)
            Stances[tag] = stance = Decide(country, tag, border, stance, hour);
        Grads.TryGetValue(tag, out var grad);
        if (stance.Strong.Count == 0)
        {
            if (grad != null && Grads.TryRemove(tag, out _))
            {
                Unpin(tag); // strong enough again: its divisions go back to the AI's fronts
                foreach (var (unit, owner) in Attackers.ToArray()) if (owner == tag) Attackers.Remove(unit); // only BreakOff, on a grad, drops them
            }
            return null;
        }
        var broken = grad != null && grad.Want.Keys.Any(id => Game.Province(id) is var p && p != 0 && *(int*)(p + ControllerOfProvince) != tag);
        if ((grad == null || broken || !grad.Strong.SetEquals(stance.Strong) || hour - grad.Hour is < 0 or >= RedrawHours)
            && Cut(country, tag, border, grad, stance.Strong, hour) is { } next)
        {
            if (grad == null) Interlocked.Increment(ref _fellBack);
            Grads[tag] = grad = next;
        }
        return grad;
    }

    /// <summary>
    /// The enemies the tag can't hold the open line against: per enemy, its divisions' power within two provinces of our
    /// threatened line over our land power's share for the line provinces facing it. Twice as strong, or 1.3x while we lose
    /// line provinces or have too few divisions for the line, puts it on the list; it comes off at even strength, after two
    /// weeks at the earliest. Out of contact (the enemy walking up to the grad) the list stays for those two weeks.
    /// </summary>
    private static Stance Decide(nint country, int tag, HashSet<nint>? border, Stance? old, int hour)
    {
        var kept = old?.Strong.Where(e => AtWar(country, e)).ToHashSet() ?? [];
        var young = old != null && hour - old.Since is >= 0 and < MinGradHours;
        if (border == null || border.Count == 0)
            return new Stance(hour, old?.Since ?? hour, young ? kept : [], old?.Border ?? []);
        long ours = 0;
        var land = 0;
        foreach (var unit in Native.PdxArray(country + UnitsOfCountry))
            if (unit != 0 && *(int*)(unit + KindOfUnit) == 0)
            {
                land++;
                ours += Power(unit);
            }
        var contact = new Dictionary<int, int>();          // enemy -> our line provinces facing it
        var ground = new Dictionary<int, HashSet<nint>>(); // enemy -> its provinces up to two deep from our line
        foreach (var b in border)
        foreach (var facing in Game.Neighbours(b).Select(Game.Province).Where(p => p != 0 && Enemy(country, tag, p))
                     .GroupBy(p => *(int*)(p + ControllerOfProvince)))
        {
            contact[facing.Key] = contact.GetValueOrDefault(facing.Key) + 1;
            if (!ground.TryGetValue(facing.Key, out var g)) ground[facing.Key] = g = [];
            foreach (var p in facing)
            {
                g.Add(p);
                foreach (var id in Game.Neighbours(p))
                    if (Game.Province(id) is var q && q != 0 && *(int*)(q + ControllerOfProvince) == facing.Key) g.Add(q);
            }
        }
        var lost = old?.Border.Count(id => Game.Province(id) is var p && p != 0 && Enemy(country, tag, p)) ?? 0;
        var thin = land * 5 < border.Count * 6; // under ~1.2 divisions per line province, as Manned
        var strong = new HashSet<int>();
        foreach (var (enemy, provinces) in ground)
        {
            long theirs = 0;
            foreach (var p in provinces)
            foreach (var unit in Native.PdxArray(p + UnitsInProvince))
                if (*(int*)(unit + KindOfUnit) == 0 && AtWar(country, *(int*)(unit + CommanderOfUnit))) theirs += Power(unit);
            var ratio = theirs * 100 / Math.Max(1, ours * contact[enemy] / border.Count);
            if (ratio >= StrongRatio || ratio >= PressedRatio && (lost >= 2 || thin)
                || kept.Contains(enemy) && (ratio > EvenRatio || thin || young))
                strong.Add(enemy);
        }
        if (young) strong.UnionWith(kept); // out of contact for now
        var since = kept.Count > 0 && old != null ? old.Since : hour;
        return new Stance(hour, since, strong, border.Select(Game.Id).ToHashSet());
    }

    /// <summary>A division's fighting power, 0-100: its strength, counted at half to full by its organisation.</summary>
    private static long Power(nint unit) => Percent(unit, StrengthRatioSlot) * (50 + Percent(unit, OrgRatioSlot) / 2) / 100;

    /// <summary>
    /// The grad: a minimum cut between the strong enemies' ground and the tag's core, through its own provinces up to
    /// GradDepth steps from the line. Holding a province against each neighbour it can be attacked from costs (less across
    /// rivers, into rough ground or forts); giving one up costs a little, more for victory points; the capital's state and
    /// the ground past the explored area can't be given up. The held provinces next to the given-up side (or the enemy) are
    /// the line: a defender each, two when attacked from three sides, one more for a fortress city. Null: nothing explored.
    /// </summary>
    private static Grad? Cut(nint country, int tag, HashSet<nint>? border, Grad? old, HashSet<int> strong, int hour)
    {
        var capital = *(int*)(country + CapitalOfCountry);
        var ids = new Dictionary<int, int>(); // province id -> node; node 0 is the enemy, 1 the core
        var provinces = new List<nint> { 0, 0 };
        var ring = new List<nint>();
        foreach (var p in (border ?? []).Concat(old?.Want.Keys.Select(Game.Province) ?? []))
            if (p != 0 && *(int*)(p + ControllerOfProvince) == tag && ids.TryAdd(Game.Id(p), provinces.Count))
            {
                provinces.Add(p);
                ring.Add(p);
            }
        for (var depth = 0; depth < GradDepth && ring.Count > 0 && provinces.Count < MaxGradNodes; depth++)
        {
            var next = new List<nint>();
            foreach (var p in ring)
            foreach (var id in Game.Neighbours(p))
                if (Game.Province(id) is var q && q != 0 && *(int*)(q + ControllerOfProvince) == tag && ids.TryAdd(id, provinces.Count))
                {
                    provinces.Add(q);
                    next.Add(q);
                }
            ring = next;
        }
        if (provinces.Count == 2) return null;
        var edges = new List<(int, int, int)>();
        for (var v = 2; v < provinces.Count; v++)
        {
            var p = provinces[v];
            var state = *(nint*)(p + StateOfProvince);
            var core = state != 0 && *(int*)(state + IdOfState) == capital;
            var attacked = 0;
            foreach (var id in Game.Neighbours(p))
            {
                var q = Game.Province(id);
                if (q == 0) continue;
                var controller = *(int*)(q + ControllerOfProvince);
                if (ids.TryGetValue(id, out var u)) edges.Add((u, v, Hold(q, p, old)));
                else if (controller == tag) core = true;                           // our ground goes on past the explored area
                else if (strong.Contains(controller)) attacked += Hold(q, p, old); // sea, neutrals, allies, weaker enemies: walls
            }
            if (attacked > 0) edges.Add((0, v, attacked));
            edges.Add((v, 1, core ? Inf : AbandonBase + AbandonPerVp * Math.Min(*(int*)(p + VictoryPoints), 30)));
        }
        var given = MinCut.SourceSide(provinces.Count, edges, 0, 1);
        var want = new Dictionary<int, int>();
        var fortresses = new HashSet<int>();
        var abandoned = new HashSet<int>();
        for (var v = 2; v < provinces.Count; v++)
        {
            if (given[v])
            {
                abandoned.Add(Game.Id(provinces[v]));
                continue;
            }
            var p = provinces[v];
            var sides = 0;
            foreach (var id in Game.Neighbours(p))
                if (ids.TryGetValue(id, out var u) ? given[u] : Game.Province(id) is var q && q != 0 && strong.Contains(*(int*)(q + ControllerOfProvince)))
                    sides++;
            if (sides == 0) continue;
            var terrain = Terrain(p);
            var fortress = *(int*)(p + VictoryPoints) >= FortressVp || terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & Urban) != 0;
            if (fortress) fortresses.Add(Game.Id(p));
            want[Game.Id(p)] = 1 + (sides >= 3 ? 1 : 0) + (fortress ? 1 : 0);
        }
        return want.Count == 0 ? null : new Grad(hour, want, fortresses, abandoned, strong);
    }

    /// <summary>What holding <paramref name="to"/> against an attack from <paramref name="from"/> costs: less across a river, into rough ground or forts; nothing across impassable ground.</summary>
    private static int Hold(nint from, nint to, Grad? old)
    {
        var crossing = Game.AdjacencyType(from, Game.Id(to));
        if (crossing == Impassable) return 0;
        long cost = HoldCost;
        if (crossing is River or LargeRiver)
            cost = cost * Math.Max(20_000, Native.FixedPoint + *(long*)(crossing == LargeRiver ? _riverLarge : _riverSmall)) / Native.FixedPoint;
        var terrain = Terrain(to);
        cost = cost * (100 + (terrain == 0 ? 0 : TerrainAttack(*(int*)(terrain + CategoryOfTerrain)))) / 100;
        cost = cost * 100 / (100 + 15 * _fortLevel(to));
        if (old != null && old.Want.ContainsKey(Game.Id(to))) cost = cost * 4 / 5; // the standing grad: no wandering over a near tie
        return (int)Math.Max(10, cost);
    }

    /// <summary>Vanilla's attack penalty into the terrain category, % (common/terrain/00_terrain.txt).</summary>
    // ponytail: vanilla values by category bit; read the terrain's own unit modifier if a mod changes them.
    private static int TerrainAttack(int category) =>
        (category & Mountain) != 0 ? -50 : (category & Marsh) != 0 ? -40 : (category & (Jungle | Urban)) != 0 ? -30
        : (category & Hills) != 0 ? -25 : (category & Forest) != 0 ? -15 : 0;

    /// <summary>The grad's provinces the tag still holds.</summary>
    private static HashSet<nint> Held(Grad grad, int tag) =>
        grad.Want.Keys.Select(Game.Province).Where(p => p != 0 && *(int*)(p + ControllerOfProvince) == tag).ToHashSet();

    /// <summary>Whether the province is on the given-up side of the tag's grad: no reserves or resting divisions go there.</summary>
    private static bool GivenUp(int tag, int province) => Grads.TryGetValue(tag, out var grad) && grad.Given.Contains(province);

    /// <summary>Off its orders (else its front walks it back) and to the grad province, posted there: the AI's regrouping leaves it (<see cref="Pinned"/>). Main thread.</summary>
    private static bool Garrison(nint unit, int province, int tag, long now)
    {
        if (!Relocate(unit, province, now, KeyGapMs)) return false;
        _posted[unit] = tag;
        Interlocked.Increment(ref _garrisoned);
        return true;
    }

    /// <summary>The divisions there have fought themselves out: average organisation under ExhaustedOrg %. AI threads too (read only).</summary>
    private static bool Exhausted(nint province)
    {
        var units = Native.PdxArray(province + UnitsInProvince);
        if (units.Length == 0) return false;
        long org = 0;
        foreach (var unit in units) org += Percent(unit, OrgRatioSlot);
        return org < ExhaustedOrg * units.Length;
    }

    /// <summary>
    /// Elastic defence, every five seconds: an enemy stack next to the grad that has fought itself out is attacked from at least
    /// two provinces of ours, one fresh division each that the province can spare (grad provinces keep their wanted defenders,
    /// others one); every other enemy stack next to it, which could come to its aid, is pinned by one fresh division where our
    /// stack passes vanilla's strength test (a division under attack only moves to retreat). Divisions in combat are never
    /// sent; attackers whose organisation runs low break off. Main thread.
    /// </summary>
    private static void CounterAttack(nint country, int tag, Grad grad, nint[] units, long now)
    {
        BreakOff(country, tag, units, now);
        var sent = 0;
        foreach (var id in grad.Want.Keys)
        {
            var g = Game.Province(id);
            if (g == 0) continue;
            foreach (var n in Game.Neighbours(g))
            {
                var e = Game.Province(n);
                if (e == 0 || _gap.ContainsKey(n) || Divisions(e) == 0 || !Enemy(country, tag, e) || !Exhausted(e)) continue;
                var attackers = new List<nint>();
                foreach (var a in Game.Neighbours(e))
                    if (FreshSpare(country, tag, grad, a) is var unit and not 0) attackers.Add(unit);
                if (attackers.Count < 2) continue; // one-sided, piecemeal attacks are what this posture avoids
                foreach (var unit in attackers.Take(CounterSize))
                    if (Attack(unit, n, tag, now))
                    {
                        sent++;
                        Interlocked.Increment(ref _countered);
                    }
                foreach (var r in Game.Neighbours(e)) // pin its neighbours: they can't shift into the fight while under attack
                {
                    var q = Game.Province(r);
                    if (q == 0 || _gap.ContainsKey(r) || Divisions(q) == 0 || !Enemy(country, tag, q)) continue;
                    foreach (var a in Game.Neighbours(q))
                    {
                        var from = Game.Province(a);
                        var pinner = from == 0 || !Strong(from, q) ? 0 : FreshSpare(country, tag, grad, a);
                        if (pinner == 0) continue;
                        if (Attack(pinner, r, tag, now))
                        {
                            sent++;
                            Interlocked.Increment(ref _pinning);
                        }
                        break;
                    }
                }
                if (sent >= MaxCounters) return;
            }
        }
    }

    /// <summary>A fresh, idle division of the tag in the province that the province can spare (a grad province keeps its wanted defenders, others one), or 0.</summary>
    private static nint FreshSpare(nint country, int tag, Grad grad, int province)
    {
        var p = Game.Province(province);
        if (p == 0 || *(int*)(p + ControllerOfProvince) != tag || Standing(country, tag, p) <= grad.Want.GetValueOrDefault(province, 1)) return 0;
        foreach (var unit in Native.PdxArray(p + UnitsInProvince))
            if (Spare(unit, tag) && Percent(unit, OrgRatioSlot) >= FreshOrg && Percent(unit, StrengthRatioSlot) >= FreshStrength) return unit;
        return 0;
    }

    /// <summary>Sends the division into the enemy province (the move becomes an attack) and keeps it in view to break off when worn.</summary>
    private static bool Attack(nint unit, int province, int tag, long now)
    {
        if (!Send(unit, province, now, SentMs)) return false;
        Attackers[unit] = tag;
        return true;
    }

    /// <summary>
    /// The tag's counter and pinning attackers whose organisation fell under WornOrg % pull back to rest (the owner may always
    /// call off an attack; a move out of combat is a retreat); ones out of combat, or gone, are dropped.
    /// </summary>
    private static void BreakOff(nint country, int tag, nint[] units, long now)
    {
        foreach (var (unit, owner) in Attackers.ToArray())
        {
            if (owner != tag) continue;
            if (Array.IndexOf(units, unit) < 0 || *(int*)(unit + CombatsOfUnit) == 0 && !_sent.ContainsKey(unit))
            {
                Attackers.Remove(unit); // gone, or its attack is over
                continue;
            }
            if (*(int*)(unit + CombatsOfUnit) == 0 || Percent(unit, OrgRatioSlot) >= WornOrg) continue;
            var location = *(nint*)(unit + LocationOfUnit);
            var rest = location == 0 ? -1 : RestProvince(country, tag, location);
            if (rest < 0 || !Rest(unit, rest, tag, now, Hour)) continue; // nowhere safe nearby: it fights on
            Attackers.Remove(unit);
            Interlocked.Increment(ref _brokeOff);
        }
    }
}

/// <summary>Minimum s-t cut of a small directed graph with int capacities (Dinic's max flow).</summary>
public static class MinCut
{
    /// <summary>The nodes on the source side of a minimum cut between <paramref name="s"/> and <paramref name="t"/>.</summary>
    public static bool[] SourceSide(int nodes, IEnumerable<(int From, int To, int Capacity)> edges, int s, int t)
    {
        var head = new List<int>[nodes]; // node -> its edges; edge e's residual twin is e ^ 1
        for (var i = 0; i < nodes; i++) head[i] = [];
        List<int> to = [], cap = [];
        foreach (var (u, v, c) in edges)
        {
            if (c <= 0 || u == v) continue;
            head[u].Add(to.Count);
            to.Add(v);
            cap.Add(c);
            head[v].Add(to.Count);
            to.Add(u);
            cap.Add(0);
        }
        var level = new int[nodes];
        var next = new int[nodes];

        bool Levels()
        {
            Array.Fill(level, -1);
            level[s] = 0;
            var queue = new Queue<int>([s]);
            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                foreach (var e in head[u])
                    if (cap[e] > 0 && level[to[e]] < 0)
                    {
                        level[to[e]] = level[u] + 1;
                        queue.Enqueue(to[e]);
                    }
            }
            return level[t] >= 0;
        }

        int Push(int u, int flow)
        {
            if (u == t) return flow;
            for (; next[u] < head[u].Count; next[u]++)
            {
                var e = head[u][next[u]];
                if (cap[e] <= 0 || level[to[e]] != level[u] + 1) continue;
                var pushed = Push(to[e], Math.Min(flow, cap[e]));
                if (pushed == 0) continue;
                cap[e] -= pushed;
                cap[e ^ 1] += pushed;
                return pushed;
            }
            return 0;
        }

        while (Levels())
        {
            Array.Fill(next, 0);
            while (Push(s, int.MaxValue) > 0) { }
        }
        var side = new bool[nodes]; // the last level pass: what s still reaches in the residual graph
        for (var i = 0; i < nodes; i++) side[i] = level[i] >= 0;
        return side;
    }
}
