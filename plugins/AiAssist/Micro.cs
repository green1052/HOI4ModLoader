using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Rotation and escape: AI divisions come off their front to rest, or before a salient is cut off (hold the line) ----

    private const int WornOrg = 30, WornStrength = 50, RestedOrg = 90, RestedStrength = 80, MaxRestingPercent = 25; // %
    private const int MaxMoves = 2, RestDepth = 2, MaxRestSearch = 6, MaxNeckSearch = 8, MaxRestHours = 168;

    // Divisions we took off their front to rest or escape, with their tag and since when: the theatre shuffle and army balancing
    // leave them until rested, then the AI puts them on a front again. Never dereferenced unless still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> _resting = new();
    private int _rested, _escaped;

    /// <summary>
    /// Every five seconds, per AI country: rested divisions go back to the AI, worn-out ones and ones in a closing salient
    /// come off the line (hold the line); on a grad, fought-out spearheads are counterattacked (defensive postures), else
    /// fresh divisions take empty enemy provinces (attack targets).
    /// </summary>
    private void Micro()
    {
        if (!Game.HasStarted || !SheepMod.Active) return; // menus, lobby, loading: no AI commands (and no mods mounted yet)
        _game.Check(); // a save loaded since the last reset: its caches point into the old world
        var now = Environment.TickCount64;
        Expire(now);
        var hour = Hour;
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            var units = Array.FindAll(Native.PdxArray(country + UnitsOfCountry).ToArray(), u => u != 0);
            if (_holdLine)
            {
                Release(tag, units, hour);
                PullBack(country, tag, units, now, hour);
            }
            if (Grads.TryGetValue(tag, out var grad)) CounterAttack(country, tag, grad, units, now); // on its grad: no piecemeal taking back of given-up ground
            else if (_attackTargets) TakeEmpty(country, tag, units, now);
        }
    }

    /// <summary>
    /// The tag's resting divisions that rested (or rested a week: short of equipment, they won't get better) go back to the
    /// AI, whose theatre shuffle puts them on a front again; gone ones are dropped.
    /// </summary>
    private static void Release(int tag, nint[] units, int hour)
    {
        foreach (var (unit, rest) in _resting)
            if (rest.Tag == tag && (Array.IndexOf(units, unit) < 0 || *(int*)(unit + Path + 0xC) == 0
                    && (Percent(unit, OrgRatioSlot) >= RestedOrg && Percent(unit, StrengthRatioSlot) >= RestedStrength || hour - rest.Hour is < 0 or >= MaxRestHours)))
                _resting.TryRemove(unit, out _);
    }

    /// <summary>
    /// Divisions in a salient whose way back is closing, and worn-out ones next to enemy divisions (while few enough of the
    /// country rest), come off their front to a province a few steps back, where another of ours stays in theirs.
    /// </summary>
    private void PullBack(nint country, int tag, nint[] units, long now, int hour)
    {
        var land = 0;
        foreach (var unit in units) if (*(int*)(unit + KindOfUnit) == 0) land++;
        var resting = _resting.Values.Count(r => r.Tag == tag);
        var moves = 0;
        var narrow = new Dictionary<nint, bool>(); // province -> its way back is closing
        var left = new Dictionary<nint, int>();    // province -> divisions still holding it
        foreach (var unit in units)
        {
            if (moves == MaxMoves) return;
            if (!Movable(unit, tag)) continue;
            var location = *(nint*)(unit + LocationOfUnit);
            if (location == 0) continue;
            if (!left.TryGetValue(location, out var here)) here = left[location] = Standing(country, tag, location);
            if (here < 2) continue; // alone there: it holds on
            if (!narrow.TryGetValue(location, out var closing)) closing = narrow[location] = Narrow(country, tag, location);
            if (closing && Grads.TryGetValue(tag, out var grad) && grad.Fortresses.Contains(Game.Id(location))) closing = false; // a fortress city on the grad holds to the last
            var worn = Percent(unit, OrgRatioSlot) < WornOrg || Percent(unit, StrengthRatioSlot) < WornStrength;
            if (!closing && !(worn && (resting + 1) * 100 <= land * MaxRestingPercent && EnemyDivisionsNext(country, tag, location))) continue;
            var rest = RestProvince(country, tag, location);
            if (rest < 0 || !Rest(unit, rest, tag, now, hour)) continue; // nowhere safe nearby: it stays
            left[location] = here - 1;
            resting++;
            moves++;
            if (closing) Interlocked.Increment(ref _escaped);
            else Interlocked.Increment(ref _rested);
        }
    }

    /// <summary>
    /// Off its orders (on its front, the AI would walk it straight back) and to the rest province, left alone until rested
    /// (<see cref="Release"/>). A resting reserve or garrison isn't one any more: another is posted. Main thread.
    /// </summary>
    private static bool Rest(nint unit, int province, int tag, long now, int hour)
    {
        var group = *(nint*)(unit + GroupOfUnit);
        if (group != 0) Unassign(unit, group);
        if (!Commands.Move(unit, province, true)) return false;
        _sent[unit] = now + SentMs;
        _resting[unit] = (tag, hour);
        _posted.TryRemove(unit, out _);
        return true;
    }

    /// <summary>
    /// Going back from <paramref name="from"/> over our provinces, one ring (provinces at the same distance) is a single
    /// province next to enemy divisions before reaching provinces away from the enemy: the neck of a closing pocket.
    /// </summary>
    // ponytail: rings, not a max-flow of disjoint paths; a wide salient with a one-province gap elsewhere is missed.
    private static bool Narrow(nint me, int tag, nint from)
    {
        if (!NextToEnemy(me, tag, from)) return false;
        var seen = new HashSet<int> { Game.Id(from) };
        var ring = new List<nint> { from };
        for (var depth = 1; depth <= MaxNeckSearch; depth++)
        {
            var next = new List<nint>();
            foreach (var province in ring)
            foreach (var id in Game.Neighbours(province))
                if (seen.Add(id) && Game.Province(id) is var p && p != 0 && *(int*)(p + ControllerOfProvince) == tag) next.Add(p);
            if (next.Count == 0) return false;                               // cut off already, or an island: nowhere to go
            if (next.Any(p => !NextToEnemy(me, tag, p))) return false;       // safe ground within reach
            if (next.Count == 1) return EnemyDivisionsNext(me, tag, next[0]); // the only way back, enemy troops next to it (a coastal strip isn't a pocket)
            ring = next;
        }
        return false;
    }

    /// <summary>
    /// A province of ours at least RestDepth steps back through our provinces, with no enemy-held neighbour and not on the
    /// given-up side of a grad; -1 if none nearby.
    /// </summary>
    // ponytail: looks at who holds provinces, not at enemy units inside ours (paratroopers, a breakthrough in progress).
    private static int RestProvince(nint me, int tag, nint from)
    {
        var seen = new HashSet<int> { Game.Id(from) };
        var frontier = new List<nint> { from };
        for (var depth = 1; depth <= MaxRestSearch && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var province in frontier)
            foreach (var id in Game.Neighbours(province))
            {
                if (!seen.Add(id)) continue;
                var p = Game.Province(id);
                if (p == 0 || *(int*)(p + ControllerOfProvince) != tag) continue;
                if (depth >= RestDepth && !NextToEnemy(me, tag, p) && !GivenUp(tag, id)) return id;
                next.Add(p);
            }
            frontier = next;
        }
        return -1;
    }

    private static bool NextToEnemy(nint me, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Enemy(me, tag, p));

    private static bool EnemyDivisionsNext(nint me, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Divisions(p) > 0 && Enemy(me, tag, p));
}
