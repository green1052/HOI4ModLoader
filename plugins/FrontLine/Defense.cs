using HOI4ModLoader;

namespace FrontLine;

public unsafe partial class FrontLine
{
    // ---- Escape: a salient whose way back narrows to a neck next to the enemy is left through the neck ----

    private const int NeckWidth = 2, MaxNeckSearch = 8, MaxSalient = 20;
    private const int MaxCutOff = 30, MaxBreakoutDistance = 6, BreakoutHours = 24, BreakoutOrg = 30;
    private readonly Dictionary<int, int> _breakouts = []; // pocket (its lowest province id) -> game hour of its last breakout

    /// <summary>
    /// Our divisions in ground of ours (and allies') cut off from the rest by land and sea, up to MaxCutOff provinces, most of
    /// them out of supply, with friendly lines within MaxBreakoutDistance provinces: once a day, all of them with some organisation attack the enemy province that starts the
    /// shortest way out. Pockets far from any friendly ground (overseas colonies) are left alone.
    /// </summary>
    private void Breakout(nint country, int tag, nint[] units)
    {
        var hour = Game.Hour;
        foreach (var (key, at) in _breakouts.ToArray()) if (hour - at is < 0 or > 30 * 24) _breakouts.Remove(key);
        var walked = new HashSet<int>();
        foreach (var unit in units)
        {
            var location = Division.Location(unit);
            if (location == 0 || walked.Contains(Game.Id(location)) || !Friendly(country, tag, location) || !LowSupply(unit)) continue; // a pocket breaks out only when mostly unsupplied (below)
            var region = Province.Region(location, MaxCutOff, p => Friendly(country, tag, p), walked);
            if (region == null) continue; // part of the main body
            if (region.Any(p => Game.Neighbours(p).Any(n => Game.Province(n) is var q && q != 0 && Province.Controller(q) == 0)))
                continue; // on the coast: the sea keeps it supplied and reachable (a colony, a beachhead), no pocket
            var key = region.Min(Game.Id);
            if (_breakouts.TryGetValue(key, out var last) && hour - last is >= 0 and < BreakoutHours) continue;
            var target = WayOut(country, tag, region);
            if (target == 0) continue; // nothing friendly near: an overseas holding, not a pocket
            var inside = region.ToHashSet();
            var trapped = units.Where(u => Division.Location(u) is var l && l != 0 && inside.Contains(l)).ToList();
            if (trapped.Count(LowSupply) * 2 <= trapped.Count) continue; // still supplied: it holds (attacking out daily left it bare)
            var breakers = trapped.Where(u => Ready(u, tag, _now) && !Division.Walking(u) && Org(u) >= BreakoutOrg
                                              && !(_held.TryGetValue(u, out var job) && job.Job == Job.Operation)).ToList(); // an operation keeps its own
            if (breakers.Count == 0) continue;
            foreach (var breaker in breakers)
            {
                if (_held.TryGetValue(breaker, out var held)) _held[breaker] = held with { Job = Job.Done }; // whatever it was doing: back on its order afterwards (Return)
                Arrow(breaker, Game.Id(target), "breakout", (0, 220, 255));
                Commands.Move(breaker, Game.Id(target), _force);
                _pending[breaker] = _now + PendingMs;
            }
            _breakouts[key] = hour;
            Log.Info($"breakout: {breakers.Count} division(s) cut off in {region.Count} province(s) attack toward friendly lines");
        }
    }

    private const int IsLowSupplySlot = 43; // CArmy vtable: the low supply flag the unit icon shows

    private static bool LowSupply(nint unit) => ((delegate* unmanaged<nint, byte>)(*(nint**)unit)[IsLowSupplySlot])(unit) != 0;

    /// <summary>The enemy province next to the pocket on the shortest land way to friendly ground outside it (fewest enemy divisions on a tie), or 0.</summary>
    private nint WayOut(nint country, int tag, List<nint> region)
    {
        var inside = region.Select(Game.Id).ToHashSet();
        var from = new Dictionary<int, nint>(); // province id -> the pocket's neighbour it was reached through
        var frontier = new List<nint>();
        foreach (var p in region)
        foreach (var id in Game.Neighbours(p))
            if (!inside.Contains(id) && !from.ContainsKey(id) && Game.Province(id) is var n && n != 0 && Enemy(country, tag, n)) // not through neutrals
            {
                from[id] = n;
                frontier.Add(n);
            }
        for (var depth = 1; depth <= MaxBreakoutDistance && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            nint best = 0;
            foreach (var p in frontier)
            foreach (var id in Game.Neighbours(p))
            {
                if (inside.Contains(id) || Game.Province(id) is not (var n and not 0) || Province.Controller(n) == 0) continue;
                var exit = from[Game.Id(p)];
                if (Friendly(country, tag, n))
                {
                    if (best == 0 || Province.Divisions(exit) < Province.Divisions(best)) best = exit;
                }
                else if (Enemy(country, tag, n) && from.TryAdd(id, exit)) next.Add(n);
            }
            if (best != 0) return best;
            frontier = next;
        }
        return 0;
    }

    /// <summary>Front divisions in a salient about to be cut off pull back through its neck, where another of their front holds their province.</summary>
    private void Escape(nint country, int tag, nint[] units)
    {
        var escapes = 0;
        var necks = new Dictionary<nint, int>(); // province -> where its divisions escape to (-1: no closing neck)
        foreach (var unit in units)
        {
            if (escapes == MaxEscapesPerTurn) break;
            if (!_orders.TryGetValue(unit + MemberOfUnit, out var order) || !IsFront(order) || !Free(unit, tag, _now)) continue;
            var location = Division.Location(unit);
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
    /// MaxSalient provinces: the neck of a closing pocket, if the enemy next to it is at least as strong as all of ours in the
    /// salient (else it can't close it: along a coastal strip, every narrow stretch is a "neck"). The way out: the first
    /// province past the neck with no enemy next to it; -1 when there is no neck, or no safe ground behind it (an enclave).
    /// </summary>
    // ponytail: rings, not a max-flow of disjoint paths; a neck that two rings share unevenly is missed.
    private int Neck(nint country, int tag, nint from)
    {
        if (!NextToEnemy(country, tag, from)) return -1;
        var seen = new HashSet<int> { Game.Id(from) };
        var ring = new List<nint> { from };
        var salient = new List<nint> { from };
        var (inside, neck) = (1, 0);
        for (var depth = 1; ; depth++)
        {
            var next = new List<nint>();
            foreach (var province in ring)
            foreach (var id in Game.Neighbours(province))
                if (seen.Add(id) && Game.Province(id) is var p && p != 0 && Province.Controller(p) == tag) next.Add(p);
            if (next.Count == 0) return -1; // cut off already, an island, or a dead end behind the neck
            if (neck == 0)
            {
                if (next.Count <= NeckWidth && next.All(p => NextToEnemy(country, tag, p)) && next.Any(p => Province.EnemyDivisionsNext(country, tag, p)))
                {
                    if (EnemyPower(country, next) < OurPower(tag, salient.Concat(next))) return -1; // we hold it
                    neck = depth;
                }
                else if ((inside += next.Count) > MaxSalient || depth == MaxNeckSearch) return -1; // too big for a salient, or no neck near
                else salient.AddRange(next);
            }
            else if (next.FirstOrDefault(p => !NextToEnemy(country, tag, p)) is var safe && safe != 0) return Game.Id(safe);
            else if (depth - neck == MaxRestSearch) return -1;
            ring = next;
        }
    }

    /// <summary>Our divisions' power in the provinces.</summary>
    private static long OurPower(int tag, IEnumerable<nint> provinces) =>
        provinces.SelectMany(p => Province.Units(p).ToArray()).Where(u => u != 0 && Division.IsLand(u) && Division.Commander(u) == tag).Sum(Division.Power);

    /// <summary>The power of the enemy divisions next to the provinces.</summary>
    private long EnemyPower(nint country, IEnumerable<nint> provinces)
    {
        var seen = new HashSet<int>();
        long power = 0;
        foreach (var p in provinces)
        foreach (var id in Game.Neighbours(p))
        {
            if (!seen.Add(id) || Game.Province(id) is not (var n and not 0)) continue;
            foreach (var unit in Province.Units(n))
                if (unit != 0 && Division.IsLand(unit) && AtWar(country, Division.Commander(unit))) power += Division.Power(unit);
        }
        return power;
    }

    // ---- Fire brigade: mobile reserves a province behind the line counterattack break-ins ----

    private const int ReserveLine = 6, MaxReserves = 3, MinReserveLine = 3, MaxPostsPerTurn = 1, FireRadius = 3, MaxCountersPerTurn = 2;

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
            if (held.Job is not (Job.Reserve or Job.Counter) || !Ready(unit, tag, _now) || Division.Walking(unit)) continue;
            var front = fronts.FirstOrDefault(f => f.Key == held.Front);
            var post = Game.Province(held.Post);
            if (front == null || post == 0 || Province.Controller(post) != tag || NextToEnemy(country, tag, post) || !Near(post, front.Line))
            {
                if (Release(unit, tag)) Log.Info("fire brigade: the line moved, a reserve goes back to its front");
                continue;
            }
            var location = Division.Location(unit);
            if (location != post)
            {
                Commands.Move(unit, held.Post, _force); // back from a counterattack, or pushed off its post
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
            var defenders = Enemy(country, tag, province) ? Province.Divisions(province) : Hostiles(country, province);
            var need = Math.Max(1, (defenders * (Province.FortLevel(province) / 2 + 1) + 1) / 2); // mobile divisions count double
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
                ready[Division.Location(unit)].Remove(unit);
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
        var self = Game.Id(p);
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
                if (Province.Controller(q) == tag) ours++;
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
        foreach (var unit in Province.Units(province))
            if (unit != 0 && AtWar(country, Division.Commander(unit))) n++;
        return n;
    }

    /// <summary>
    /// Fronts with a line of MinReserveLine provinces or more keep one mobile reserve per ReserveLine of them (up to
    /// MaxReserves) on posts a province behind the line covering most of it, taken from spare fresh armored or motorized divisions;
    /// an AI country without spare mobile ones uses infantry (Spain's: else nothing ever answers a break-in).
    /// </summary>
    private void PostReserves(nint country, int tag, IEnumerable<Front> all)
    {
        var fronts = all.ToList();
        var posts = Posts();
        Dictionary<nint, List<nint>>? pool = null;
        var posted = 0;
        Dictionary<nint, List<nint>>? infantry = null;
        foreach (var front in fronts.OrderByDescending(f => f.Line.Count))
        {
            if (posted == MaxPostsPerTurn || front.Line.Count < MinReserveLine) return;
            var want = Math.Clamp(front.Line.Count / ReserveLine, 1, MaxReserves);
            if (_held.Values.Count(h => h.Job is Job.Reserve or Job.Counter && h.Front == front.Key) >= want) continue;
            var post = Post(country, tag, front.Line, posts);
            if (post == 0) continue;
            pool ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => Division.Motorized(u) && Org(u) >= 80 && Str(u) >= 70);
            var unit = Take(tag, post, FireRadius + 3, pool);
            if (unit == 0 && _force)
            {
                infantry ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => Org(u) >= 80 && Str(u) >= 70);
                unit = Take(tag, post, FireRadius + 3, infantry);
            }
            if (unit == 0) continue;
            var id = Game.Id(post);
            Send(unit, id, Job.Reserve, id, 0, front.Key);
            posts.Add(id);
            posted++;
            Log.Info($"fire brigade: a {(Division.Motorized(unit) ? "mobile" : "infantry")} division takes up a reserve post behind the line");
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
            if (q == 0 || taken.Contains(id) || line.Contains(q) || Province.Controller(q) != tag || NextToEnemy(country, tag, q)) continue;
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
            if (held.Job != Job.Depth || !Ready(unit, tag, _now) || Division.Walking(unit)) continue;
            var key = Game.Province(held.Target);
            var post = Game.Province(held.Post);
            if (key == 0 || post == 0 || Province.Controller(key) != tag || !NextToEnemy(country, tag, key)
                || Province.Controller(post) != tag || NextToEnemy(country, tag, post))
            {
                if (Release(unit, tag)) Log.Info("depth defence: the line moved, a second-line division goes back to its front");
            }
            else if (Division.Location(unit) != post)
            {
                Commands.Move(unit, held.Post, _force);
                _pending[unit] = _now + PendingMs;
            }
        }

        var backed = _held.Values.Where(h => h.Job == Job.Depth).Select(h => h.Target).ToHashSet();
        var posts = Posts();
        Dictionary<nint, List<nint>>? pool = null;
        var posted = 0;
        foreach (var (province, _) in fronts.SelectMany(f => f.Line).Distinct().Where(p => !backed.Contains(Game.Id(p)))
                     .Select(p => (Province: p, Key: Key(country, tag, p))).Where(k => k.Key >= MinKey).OrderByDescending(k => k.Key).ToList())
        {
            if (posted == MaxDepthPerTurn) return;
            var post = Post(country, tag, [province], posts);
            if (post == 0) continue;
            pool ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => !(_fireBrigade && Division.Motorized(u)) && Org(u) >= 70 && Str(u) >= 60);
            var unit = Take(tag, post, DepthRadius, pool);
            if (unit == 0) continue;
            var id = Game.Id(post);
            Send(unit, id, Job.Depth, id, Game.Id(province));
            posts.Add(id);
            posted++;
            Log.Info("depth defence: a spare division takes up a second-line post behind a key province");
        }
    }

    /// <summary>What holding this province of ours is worth: a port (the fleet and supply), victory points, a river in front of it, city or mountain ground.</summary>
    private int Key(nint country, int tag, nint province)
    {
        var port = Province.BuildingLevel(province, Buildings.NavalBaseSlot);
        if (port > 0) return 1000 + port;
        var key = 3 * Math.Min(Province.VictoryPoints(province), 30);
        if ((Province.TerrainCategory(province) & (Province.Mountain | Province.Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || !Enemy(country, tag, p) || Game.AdjacencyType(province, id) is not (Province.River or Province.LargeRiver)) continue; // impassable: no attack at all
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }
}
