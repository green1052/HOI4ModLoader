using HOI4ModLoader;

namespace FrontLine;

public unsafe partial class FrontLine
{
    // ---- Escape: a salient whose way back narrows to a neck next to the enemy is left through the neck ----

    private const int NeckWidth = 2, MaxNeckSearch = 8, MaxSalient = 20;

    /// <summary>Front divisions in a salient about to be cut off pull back through its neck, where another of their front holds their province.</summary>
    private void Escape(nint country, int tag, nint[] units)
    {
        var escapes = 0;
        var necks = new Dictionary<nint, int>(); // province -> where its divisions escape to (-1: no closing neck)
        foreach (var unit in units)
        {
            if (escapes == MaxEscapesPerTurn) break;
            if (!_orders.TryGetValue(unit + MemberOfUnit, out var order) || !IsFront(order) || !Free(unit, tag, _now)) continue;
            var location = *(nint*)(unit + LocationOfUnit);
            if (location == 0) continue;
            if (_holding.GetValueOrDefault((order, location)) < 2) continue; // alone there: pulling it out opens the line
            if (!necks.TryGetValue(location, out var to)) necks[location] = to = Neck(country, tag, location);
            if (to < 0) continue;
            Send(unit, to, Job.Rest);
            escapes++;
            Log.Info("escape: a division pulls back from a salient about to be cut off");
        }
    }

    /// <summary>
    /// Going back from <paramref name="from"/> over our provinces ring by ring (provinces at the same distance: every way back
    /// crosses each ring), a ring of at most NeckWidth provinces, all next to enemy ground with enemy troops next to one, within
    /// MaxSalient provinces: the neck of a closing pocket. The way out: the first province past the neck with no enemy next to
    /// it; -1 when there is no neck, or no safe ground behind it (a coastal strip, an enclave).
    /// </summary>
    // ponytail: rings, not a max-flow of disjoint paths; a neck that two rings share unevenly is missed.
    private int Neck(nint country, int tag, nint from)
    {
        if (!NextToEnemy(country, tag, from)) return -1;
        var seen = new HashSet<int> { *(int*)(from + IdOfProvince) };
        var ring = new List<nint> { from };
        var (inside, neck) = (1, 0);
        for (var depth = 1; ; depth++)
        {
            var next = new List<nint>();
            foreach (var province in ring)
            foreach (var id in Game.Neighbours(province))
                if (seen.Add(id) && Game.Province(id) is var p && p != 0 && *(int*)(p + ControllerOfProvince) == tag) next.Add(p);
            if (next.Count == 0) return -1; // cut off already, an island, or a dead end behind the neck
            if (neck == 0)
            {
                if (next.Count <= NeckWidth && next.All(p => NextToEnemy(country, tag, p)) && next.Any(p => EnemyDivisionsNext(country, tag, p))) neck = depth;
                else if ((inside += next.Count) > MaxSalient || depth == MaxNeckSearch) return -1; // too big for a salient, or no neck near
            }
            else if (next.FirstOrDefault(p => !NextToEnemy(country, tag, p)) is var safe && safe != 0) return *(int*)(safe + IdOfProvince);
            else if (depth - neck == MaxRestSearch) return -1;
            ring = next;
        }
    }

    // ---- Fire brigade: mobile reserves a province behind the line counterattack break-ins ----

    private const int StatsOfUnit = 0x138, Hardness = 0xB8, Armor = 0x298; // CArmy stats: hardness 1e5 = 1, armor
    private const long Armored = 40_000;                                    // hardness 0.4: armored or motorized
    private const int ReserveLine = 6, MaxReserves = 3, MinReserveLine = 3, MaxPostsPerTurn = 1, FireRadius = 3, MaxCountersPerTurn = 2;

    private static bool Motorized(nint unit)
    {
        var stats = *(nint*)(unit + StatsOfUnit);
        return stats != 0 && (*(long*)(stats + Hardness) >= Armored || *(long*)(stats + Armor) > 0);
    }

    /// <summary>
    /// Reserves back from a counterattack return to their post; ones whose post the line moved away from (or onto) go back to
    /// their front. A ready reserve with a break-in within FireRadius attacks it, with as many reserves nearby as it takes.
    /// </summary>
    private void Counterattack(nint country, int tag, IEnumerable<Front> all)
    {
        var fronts = all.ToList();
        var ready = new Dictionary<nint, List<nint>>(); // province -> reserves there ready to strike
        foreach (var (unit, held) in _held.ToArray())
        {
            if (held.Job is not (Job.Reserve or Job.Counter) || !Ready(unit, tag, _now) || Walking(unit)) continue;
            var front = fronts.FirstOrDefault(f => f.Key == held.Front);
            var post = Game.Province(held.Post);
            if (front == null || post == 0 || *(int*)(post + ControllerOfProvince) != tag || NextToEnemy(country, tag, post) || !Near(post, front.Line))
            {
                if (Release(unit, tag)) Log.Info("fire brigade: the line moved, a reserve goes back to its front");
                continue;
            }
            var location = *(nint*)(unit + LocationOfUnit);
            if (location != post)
            {
                Commands.Move(unit, held.Post); // back from a counterattack, or pushed off its post
                _pending[unit] = _now + PendingMs;
                _held[unit] = held with { Job = Job.Reserve, Target = 0 };
                continue;
            }
            if (held.Job == Job.Counter) _held[unit] = held with { Job = Job.Reserve, Target = 0 };
            if (Org(unit) < 70 || Str(unit) < 50) continue;
            if (!ready.TryGetValue(location, out var here)) ready[location] = here = [];
            here.Add(unit);
        }

        var counters = 0;
        foreach (var (location, here) in ready.ToArray())
        {
            if (counters == MaxCountersPerTurn) return;
            if (here.Count == 0) continue; // all sent with another
            var target = 0;
            if (BreakIn(country, tag, location) is var inside && inside != 0) target = inside;
            else Search(tag, location, FireRadius, (p, _) => (target = BreakIn(country, tag, p)) != 0);
            if (target == 0) continue;
            var province = Game.Province(target);
            if (province == 0) continue;
            var defenders = Enemy(country, tag, province) ? *(int*)(province + DivisionsInProvince) : Hostiles(country, province);
            var need = Math.Max(1, (defenders * (_fortLevel(province) / 2 + 1) + 1) / 2); // mobile divisions count double
            var team = new List<nint>(here);
            Search(tag, province, FireRadius, (p, _) =>
            {
                if (p != location && ready.TryGetValue(p, out var there)) team.AddRange(there);
                return team.Count >= need;
            });
            if (team.Count < need) continue; // too strong for what is near: the line holds it
            team = team.Take(need).ToList();
            foreach (var unit in team)
            {
                var held = _held[unit];
                Send(unit, target, Job.Counter, held.Post, target, held.Front);
                ready[*(nint*)(unit + LocationOfUnit)].Remove(unit);
            }
            _sent[target] = ([.. team], true);
            counters++;
            Log.Info($"fire brigade: {team.Count} reserve(s) counterattack a break-in ({defenders} enemy division(s))");
        }
    }

    /// <summary>
    /// A break-in at or next to our province <paramref name="p"/> nothing is sent at yet: enemy troops inside it, or an enemy-held
    /// neighbour reaching into our side (at least two of our provinces around it and twice the enemy's) that was ours in the last
    /// two days (a breakthrough) or touches no enemy ground at all (a landing, paratroopers). Its id, or 0.
    /// </summary>
    private int BreakIn(nint country, int tag, nint p)
    {
        var self = *(int*)(p + IdOfProvince);
        if (!_sent.ContainsKey(self) && Hostiles(country, p) > 0) return self;
        foreach (var id in Game.Neighbours(p))
        {
            if (_sent.ContainsKey(id)) continue;
            var e = Game.Province(id);
            if (e == 0 || !Enemy(country, tag, e)) continue;
            var (ours, theirs) = (0, 0);
            foreach (var n in Game.Neighbours(e))
            {
                var q = Game.Province(n);
                if (q == 0) continue;
                if (*(int*)(q + ControllerOfProvince) == tag) ours++;
                else if (Enemy(country, tag, q)) theirs++;
            }
            if (ours >= 2 && ours >= 2 * theirs && (theirs == 0 || _lastSeen.ContainsKey(id))) return id;
        }
        return 0;
    }

    /// <summary>Divisions of countries at war with us in the province.</summary>
    private int Hostiles(nint country, nint province)
    {
        var n = 0;
        foreach (var unit in Native.PdxArray(province + UnitsInProvince))
            if (unit != 0 && AtWar(country, *(int*)(unit + CommanderTag))) n++;
        return n;
    }

    /// <summary>
    /// Fronts with a line of MinReserveLine provinces or more keep one mobile reserve per ReserveLine of them (up to
    /// MaxReserves) on posts a province behind the line covering most of it, taken from spare fresh armored or motorized divisions.
    /// </summary>
    private void PostReserves(nint country, int tag, IEnumerable<Front> all)
    {
        var fronts = all.ToList();
        var posts = Posts();
        Dictionary<nint, List<nint>>? pool = null;
        var posted = 0;
        foreach (var front in fronts.OrderByDescending(f => f.Line.Count))
        {
            if (posted == MaxPostsPerTurn || front.Line.Count < MinReserveLine) return;
            var want = Math.Clamp(front.Line.Count / ReserveLine, 1, MaxReserves);
            if (_held.Values.Count(h => h.Job is Job.Reserve or Job.Counter && h.Front == front.Key) >= want) continue;
            var post = Post(country, tag, front.Line, posts);
            if (post == 0) continue;
            pool ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => Motorized(u) && Org(u) >= 80 && Str(u) >= 70);
            var unit = Take(tag, post, FireRadius + 3, pool);
            if (unit == 0) continue;
            var id = *(int*)(post + IdOfProvince);
            Send(unit, id, Job.Reserve, id, 0, front.Key);
            posts.Add(id);
            posted++;
            Log.Info("fire brigade: a mobile division takes up a reserve post behind the line");
        }
    }

    /// <summary>Provinces our reserves and second-line divisions stand on.</summary>
    private HashSet<int> Posts() => _held.Values.Where(h => h.Job is Job.Reserve or Job.Counter or Job.Depth).Select(h => h.Post).ToHashSet();

    /// <summary>A province of ours right behind <paramref name="line"/> (next to it, no enemy next to it), free of posts, next to the most of it; 0 if none.</summary>
    private nint Post(nint country, int tag, HashSet<nint> line, HashSet<int> taken)
    {
        var (best, cover) = ((nint)0, 0);
        foreach (var p in line)
        foreach (var id in Game.Neighbours(p))
        {
            var q = Game.Province(id);
            if (q == 0 || taken.Contains(id) || line.Contains(q) || *(int*)(q + ControllerOfProvince) != tag || NextToEnemy(country, tag, q)) continue;
            var covers = Game.Neighbours(q).Count(n => Game.Province(n) is var r && r != 0 && line.Contains(r));
            if (covers > cover) (best, cover) = (q, covers);
        }
        return best;
    }

    /// <summary>The province is at most two steps from <paramref name="line"/>.</summary>
    private static bool Near(nint province, HashSet<nint> line) =>
        Game.Neighbours(province).Any(id => Game.Province(id) is var p && p != 0 && (line.Contains(p) || Game.Neighbours(p).Any(n => line.Contains(Game.Province(n)))));

    // ---- Depth defence: key line provinces get a second division right behind them ----

    private const int MinKey = 25, MaxDepthPerTurn = 2, DepthRadius = 4;
    private const int VictoryPoints = 0x38, StaticOfProvince = 0xB8, TerrainOfStatic = 0xA8, CategoryOfTerrain = 0x8C, Mountain = 0x8, Urban = 0x10, River = 2;

    /// <summary>
    /// Second-line divisions stay while their key province is ours and on the line and their post behind it is safe, else
    /// go back to their front. Key line provinces without one get the nearest spare fresh division (not mobile ones while the
    /// fire brigade wants them), most important first.
    /// </summary>
    private void DepthDefense(nint country, int tag, IEnumerable<Front> all)
    {
        var fronts = all.ToList();
        foreach (var (unit, held) in _held.ToArray())
        {
            if (held.Job != Job.Depth || !Ready(unit, tag, _now) || Walking(unit)) continue;
            var key = Game.Province(held.Target);
            var post = Game.Province(held.Post);
            if (key == 0 || post == 0 || *(int*)(key + ControllerOfProvince) != tag || !NextToEnemy(country, tag, key)
                || *(int*)(post + ControllerOfProvince) != tag || NextToEnemy(country, tag, post))
            {
                if (Release(unit, tag)) Log.Info("depth defence: the line moved, a second-line division goes back to its front");
            }
            else if (*(nint*)(unit + LocationOfUnit) != post)
            {
                Commands.Move(unit, held.Post);
                _pending[unit] = _now + PendingMs;
            }
        }

        var backed = _held.Values.Where(h => h.Job == Job.Depth).Select(h => h.Target).ToHashSet();
        var posts = Posts();
        Dictionary<nint, List<nint>>? pool = null;
        var posted = 0;
        foreach (var (province, _) in fronts.SelectMany(f => f.Line).Distinct().Where(p => !backed.Contains(*(int*)(p + IdOfProvince)))
                     .Select(p => (Province: p, Key: Key(country, tag, p))).Where(k => k.Key >= MinKey).OrderByDescending(k => k.Key).ToList())
        {
            if (posted == MaxDepthPerTurn) return;
            var post = Post(country, tag, [province], posts);
            if (post == 0) continue;
            pool ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => !(_fireBrigade && Motorized(u)) && Org(u) >= 70 && Str(u) >= 60);
            var unit = Take(tag, post, DepthRadius, pool);
            if (unit == 0) continue;
            var id = *(int*)(post + IdOfProvince);
            Send(unit, id, Job.Depth, id, *(int*)(province + IdOfProvince));
            posts.Add(id);
            posted++;
            Log.Info("depth defence: a spare division takes up a second-line post behind a key province");
        }
    }

    /// <summary>What holding this province of ours is worth: a port (the fleet and supply), victory points, a river in front of it, city or mountain ground.</summary>
    private int Key(nint country, int tag, nint province)
    {
        var port = Level(province, _navalBaseSlot);
        if (port > 0) return 1000 + port;
        var key = 3 * Math.Min(*(int*)(province + VictoryPoints), 30);
        var info = *(nint*)(province + StaticOfProvince);
        var terrain = info == 0 ? 0 : *(nint*)(info + TerrainOfStatic);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || !Enemy(country, tag, p) || Game.AdjacencyType(province, id) < River) continue;
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }
}
