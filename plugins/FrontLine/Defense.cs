using HOI4ModLoader;

namespace FrontLine;

public unsafe partial class FrontLine
{
    // ---- Escape: leave a salient through its neck before it closes; break out of a pocket ----

    private const int NeckWidth = 2, MaxNeckSearch = 8, MaxSalient = 20;
    private const int MaxCutOff = 30, MaxBreakoutDistance = 6, BreakoutHours = 24, BreakoutOrg = 30;
    private readonly Dictionary<(int, int), int> _breakouts = []; // (tag, pocket's lowest province id) -> game hour of its last breakout

    /// <summary>
    /// A friendly pocket cut off by land (up to MaxCutOff provinces, not on the coast, mostly out of supply, friendly lines
    /// within MaxBreakoutDistance) breaks out once a day: every division with BreakoutOrg % organisation attacks the enemy
    /// province on the shortest way out.
    /// </summary>
    private void Breakout(nint country, int tag, nint[] units)
    {
        var hour = Game.Hour;
        foreach (var (key, at) in _breakouts.ToArray()) if (hour - at is < 0 or >= BreakoutHours) _breakouts.Remove(key);
        var walked = new HashSet<int>();
        foreach (var unit in units)
        {
            var location = Division.Location(unit);
            if (location == 0 || walked.Contains(Game.Id(location)) || !Friendly(country, tag, location) || !LowSupply(unit)) continue; // only unsupplied pockets break out (checked below)
            var region = Province.Region(location, MaxCutOff, p => Friendly(country, tag, p), walked);
            if (region == null) continue; // part of the main body
            if (region.Any(p => Game.Neighbours(p).Any(n => Game.Province(n) is var q && q != 0 && Province.Controller(q) == 0)))
                continue; // on the coast: supplied and reachable by sea, not a pocket
            var key = (tag, region.Min(Game.Id));
            if (_breakouts.TryGetValue(key, out var last) && hour - last is >= 0 and < BreakoutHours) continue;
            var target = WayOut(country, tag, region);
            if (target == 0) continue; // no friendly ground near: an overseas holding
            var inside = region.ToHashSet();
            var trapped = units.Where(u => Division.Location(u) is var l && l != 0 && inside.Contains(l)).ToList();
            if (trapped.Count(LowSupply) * 2 <= trapped.Count) continue; // still supplied: it holds
            var breakers = trapped.Where(u => Ready(u, tag) && !Division.Walking(u) && Org(u) >= BreakoutOrg
                                              && !(_held.TryGetValue(u, out var job) && job.Job == Job.Operation)).ToList(); // an operation keeps its own
            if (breakers.Count == 0) continue;
            foreach (var breaker in breakers)
            {
                if (_held.TryGetValue(breaker, out var held)) _held[breaker] = held with { Job = Job.Done }; // back on its order afterwards (Return)
                Arrow(breaker, Game.Id(target), "breakout", (0, 220, 255));
                Move(breaker, Game.Id(target));
            }
            _breakouts[key] = hour;
            Log.Debug($"breakout: {breakers.Count} division(s) cut off in {region.Count} province(s) attack toward friendly lines");
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

    // A province divisions escaped from is left to the front order this long: it refills the salient, and those would escape too.
    private const int EscapeAgainHours = 7 * 24;

    /// <summary>Front divisions in a salient about to be cut off pull back through its neck, where another of their front holds their province.</summary>
    private void Escape(nint country, int tag, nint[] units)
    {
        var escapes = 0;
        var escaped = SideOf(tag).EscapedFrom;
        foreach (var (id, hour) in escaped.ToArray()) if (Game.Hour - hour is < 0 or >= EscapeAgainHours) escaped.Remove(id);
        var necks = new Dictionary<nint, int>(); // province -> where its divisions escape to (-1: no closing neck)
        foreach (var unit in units)
        {
            if (escapes == MaxEscapesPerTurn) break;
            if (!_orders.TryGetValue(unit + MemberOfUnit, out var order) || !IsFront(order) || !Free(unit, tag)) continue;
            var location = Division.Location(unit);
            if (location == 0) continue;
            if (_holding.GetValueOrDefault((order, location)) < 2) continue; // alone there: pulling it out opens the line
            if (escaped.TryGetValue(Game.Id(location), out var since) && since != Game.Hour) continue; // this turn's escapes may go on
            if (!necks.TryGetValue(location, out var to)) necks[location] = to = Neck(country, tag, location);
            if (to < 0) continue;
            if (!Send(unit, to, Job.Rest, Game.Id(location))) continue; // post: where it escaped from
            escaped[Game.Id(location)] = Game.Hour;
            escapes++;
            Log.Debug("escape: a division pulls back from a salient about to be cut off");
        }
    }

    /// <summary>
    /// Walks back from <paramref name="from"/> over our provinces ring by ring (every way back crosses each ring) for a neck:
    /// a ring of at most NeckWidth provinces within MaxSalient, all next to enemy ground, with enemy troops next to one. It
    /// counts only if that enemy is at least as strong as our divisions in the salient (else every coastal strip is a neck).
    /// Returns the first province past the neck with no enemy next to it, or -1 (no neck, or no safe ground behind it).
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
                if (next.Count <= NeckWidth && next.All(p => NextToEnemy(country, tag, p)) && next.Any(p => EnemiesNext(country, tag, p) > 0))
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
            if (seen.Add(id) && Game.Province(id) is var n && n != 0) power += HostilePower(country, n);
        return power;
    }

    /// <summary>The power of the divisions of countries at war with us in the province.</summary>
    private long HostilePower(nint country, nint province)
    {
        long power = 0;
        foreach (var unit in Province.Units(province))
            if (unit != 0 && Division.IsLand(unit) && AtWar(country, Division.Commander(unit))) power += Division.Power(unit);
        return power;
    }

    // ---- Fire brigade: mobile reserves a province behind the line counterattack break-ins ----

    private const int ReserveLine = 4, MaxReserves = 4, MinReserveLine = 3, MaxPostsPerTurn = 2, FireRadius = 3, MaxCountersPerTurn = 2;
    private const int ReserveReach = 12; // provinces a reserve may come from: armour is scarce and often far
    private const int StrikeOrg = 70, StrikeStr = 50; // a reserve on its post still strikes down to this: it fights next door, then rests there

    /// <summary>A ready reserve with a break-in within FireRadius attacks it, with as many reserves nearby as it takes.</summary>
    private void Counterattack(nint country, int tag, List<Front> fronts)
    {
        var ready = ReadyReserves(country, tag, fronts);
        var counters = 0;
        foreach (var (location, here) in ready.ToArray())
        {
            if (counters == MaxCountersPerTurn) return;
            if (here.Count == 0) continue; // all sent with another
            var target = BreakIn(country, tag, location);
            if (target == 0) Search(tag, location, FireRadius, (p, _) => (target = BreakIn(country, tag, p)) != 0);
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
            if (team.Count < need) continue; // too strong for what is near: left to the line
            team = team.Take(need).ToList();
            foreach (var unit in team) ready[Division.Location(unit)].Remove(unit);
            team.RemoveAll(unit => _held[unit] is var held && !Send(unit, target, Job.Counter, held.Post, target, held.Front));
            if (team.Count == 0) continue; // every way there refused
            _sent[target] = ([.. team], true);
            counters++;
            Log.Debug($"fire brigade: {team.Count} reserve(s) counterattack a break-in ({defenders} enemy division(s))");
        }
    }

    /// <summary>
    /// Reserve upkeep: back to the post after a counterattack (or when pushed off), back to the front when the line moved.
    /// Returns the reserves on their post fit to strike, by province.
    /// </summary>
    private Dictionary<nint, List<nint>> ReadyReserves(nint country, int tag, List<Front> fronts)
    {
        var ready = new Dictionary<nint, List<nint>>(); // province -> reserves there ready to strike
        foreach (var (unit, held) in _held.ToArray())
        {
            if (held.Job is not (Job.Reserve or Job.Counter) || !Ready(unit, tag) || Division.Walking(unit)) continue;
            var front = fronts.FirstOrDefault(f => f.Key == held.Front);
            var post = Game.Province(held.Post);
            var overrun = post == 0 || Province.Controller(post) != tag || NextToEnemy(country, tag, post);
            if (overrun || (front == null || !Near(post, front.Line)) && Settled(held))
            {
                if (Release(unit, tag)) Log.Debug("fire brigade: the line moved, a reserve goes back to its front");
                continue;
            }
            if (front == null) continue; // its front redrawn this turn: kept until the post settles
            var location = Division.Location(unit);
            if (location != post && _force && held.Job == Job.Reserve)
            {
                GiveUp(unit, "fire brigade");
                continue;
            }
            if (location != post)
            {
                Move(unit, held.Post); // back from a counterattack, or pushed off its post
                _held[unit] = held with { Job = Job.Reserve, Target = 0 };
                continue;
            }
            if (held.Job == Job.Counter) _held[unit] = held with { Job = Job.Reserve, Target = 0 };
            if (Org(unit) < StrikeOrg || Str(unit) < StrikeStr) continue;
            if (!ready.TryGetValue(location, out var here)) ready[location] = here = [];
            here.Add(unit);
        }
        return ready;
    }

    /// <summary>
    /// A break-in at or next to our province <paramref name="p"/> not yet answered: enemy troops inside it, or an enemy
    /// neighbour bulging into our side (two or more of our provinces around it, twice the enemy's) that was ours in the last
    /// two days or touches no enemy ground (a landing, paratroopers). Its id, or 0.
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
    /// Fronts with MinReserveLine+ line provinces keep one spare fresh mobile division per ReserveLine of them (MaxReserves at
    /// most) on posts a province behind the line. An AI country without spare mobile divisions uses infantry.
    /// </summary>
    private void PostReserves(nint country, int tag, List<Front> fronts)
    {
        if (!PostingDay(tag, "reserve")) return;
        var posts = Posts();
        Dictionary<nint, List<nint>>? pool = null, infantry = null;
        var posted = 0;
        foreach (var front in fronts.OrderByDescending(f => f.Line.Count))
        {
            if (posted == MaxPostsPerTurn || front.Line.Count < MinReserveLine) return;
            var want = Math.Clamp(front.Line.Count / ReserveLine, 1, MaxReserves);
            if (_held.Values.Count(h => h.Job is Job.Reserve or Job.Counter && h.Front == front.Key) >= want) continue;
            var post = Post(country, tag, front.Line, posts);
            if (post == 0) continue;
            pool ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => Division.Motorized(u) && Org(u) >= FreshOrg && Str(u) >= FreshStr);
            var unit = Take(tag, post, ReserveReach, pool);
            if (unit == 0 && _force)
            {
                infantry ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => Org(u) >= FreshOrg && Str(u) >= FreshStr);
                unit = Take(tag, post, FireRadius + 3, infantry);
            }
            if (unit == 0) continue;
            var id = Game.Id(post);
            if (!Send(unit, id, Job.Reserve, id, 0, front.Key)) continue;
            posts.Add(id);
            posted++;
            Log.Debug($"fire brigade: a {(Division.Motorized(unit) ? "mobile" : "infantry")} division takes up a reserve post behind the line");
        }
    }

    // AI posts are kept SettleHours however the line wobbles (an overrun one goes at once), and an AI country takes new posts
    // once a day: each post taken or given up reshuffles its battle plan.
    private const int SettleHours = 7 * 24;
    private readonly Dictionary<(int Tag, string What), int> _postedOn = []; // AI country and feature -> the day it last took posts

    private bool Settled(Held held) => !_force || Game.Hour - held.Since >= SettleHours;

    /// <summary>The player's posts follow the line every turn; an AI country takes new ones once a game day.</summary>
    private bool PostingDay(int tag, string what)
    {
        if (!_force) return true;
        var day = Game.Hour / 24;
        if (_postedOn.TryGetValue((tag, what), out var last) && last == day) return false;
        _postedOn[(tag, what)] = day;
        return true;
    }

    /// <summary>Posts of our reserves, second-line and wall divisions, and posts left lately (<see cref="Vacated"/>).</summary>
    private HashSet<int> Posts() => _held.Values.Where(h => h.Job is Job.Reserve or Job.Counter or Job.Depth or Job.Cordon).Select(h => h.Post)
        .Concat(_vacated.Keys.Where(Vacated)).ToHashSet();

    // ---- Cordon: wall in a breach instead of feeding it ----

    private const int CordonHours = 48, MaxCordonsPerTurn = 4, CordonRadius = 6, CordonKeepHours = 7 * 24;

    /// <summary>
    /// Our empty provinces next to ground the enemy took in the last CordonHours are manned, tip and shoulders first, so the
    /// breakthrough runs into a wall. A wall division holds while the breach is the enemy's, CordonKeepHours at most.
    /// </summary>
    private void Cordon(nint country, int tag, nint[] units)
    {
        foreach (var (unit, held) in _held.ToArray())
        {
            if (held.Job != Job.Cordon || !Ready(unit, tag) || Division.Walking(unit)) continue;
            var breach = Game.Province(held.Target);
            var post = Game.Province(held.Post);
            if (breach == 0 || post == 0 || Province.Controller(post) != tag || !Enemy(country, tag, breach) || Game.Hour - held.Since >= CordonKeepHours)
            {
                if (Release(unit, tag)) Log.Debug("cordon: the breach is closed, a wall division goes back to its front");
            }
            else BackToPost(unit, held, post);
        }

        var hour = Game.Hour;
        var walls = new Dictionary<nint, nint>(); // empty province of ours next to a breach -> the breach
        var manned = _held.Values.Where(h => h.Job == Job.Cordon).Select(h => h.Post).ToHashSet();
        foreach (var (id, seen) in _lastSeen)
        {
            if (hour - seen.Hour is <= 0 or >= CordonHours || Game.Province(id) is not (var breach and not 0) || !Enemy(country, tag, breach)) continue;
            foreach (var n in Game.Neighbours(breach))
                if (Game.Province(n) is var wall && wall != 0 && Province.Controller(wall) == tag && !manned.Contains(n)
                    && !Province.Units(wall).ToArray().Any(u => u != 0 && Division.Commander(u) == tag))
                    walls.TryAdd(wall, breach);
        }
        if (walls.Count == 0) return;
        Dictionary<nint, List<nint>>? pool = null;
        var posted = 0;
        // Most enemy neighbours first: the breach's tip and shoulders.
        foreach (var (wall, breach) in walls.OrderByDescending(w => Game.Neighbours(w.Key).Count(n => Game.Province(n) is var e && e != 0 && Enemy(country, tag, e))))
        {
            if (posted == MaxCordonsPerTurn) break;
            pool ??= Pool(country, tag, units, true, u => Org(u) >= WallOrg && Str(u) >= WallStr);
            var unit = Take(tag, wall, CordonRadius, pool);
            if (unit == 0) continue;
            if (!Send(unit, Game.Id(wall), Job.Cordon, Game.Id(wall), Game.Id(breach))) continue;
            posted++;
            Log.Debug("cordon: a division walls in an enemy breach");
        }
    }

    /// <summary>A post's division not on it: an AI one the AI walked off is given up (<see cref="GiveUp"/>), the player's walks back.</summary>
    private void BackToPost(nint unit, Held held, nint post)
    {
        if (Division.Location(unit) == post) return;
        if (_force) GiveUp(unit, held.Job.ToString());
        else Move(unit, held.Post);
    }

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
    /// Second-line divisions stay while their key province is ours and on the line and their post is safe, else go back to
    /// their front. Key line provinces without one get the nearest spare fresh division (not mobile ones while the fire
    /// brigade is on), most important first.
    /// </summary>
    private void DepthDefense(nint country, int tag, List<Front> fronts)
    {
        var lines = fronts.SelectMany(f => f.Line).ToHashSet();
        foreach (var (unit, held) in _held.ToArray())
        {
            if (held.Job != Job.Depth || !Ready(unit, tag) || Division.Walking(unit)) continue;
            var key = Game.Province(held.Target);
            var post = Game.Province(held.Post);
            // On Survey's line, not just next to enemy ground: before a war no border is, and posts would flip back and forth.
            var overrun = post == 0 || Province.Controller(post) != tag || NextToEnemy(country, tag, post);
            if (overrun || (key == 0 || Province.Controller(key) != tag || !(lines.Contains(key) || NextToEnemy(country, tag, key))) && Settled(held))
            {
                if (Release(unit, tag)) Log.Debug("depth defence: the line moved, a second-line division goes back to its front");
            }
            else BackToPost(unit, held, post);
        }

        if (!PostingDay(tag, "depth")) return;
        var backed = _held.Values.Where(h => h.Job == Job.Depth).Select(h => h.Target).ToHashSet();
        var posts = Posts();
        posts.UnionWith(lines.Select(Game.Id)); // a post on the line itself would be given up the next turn
        Dictionary<nint, List<nint>>? pool = null;
        var posted = 0;
        foreach (var (province, _) in lines.Where(p => !backed.Contains(Game.Id(p)) && !Vacated(Game.Id(p)))
                     .Select(p => (Province: p, Key: Key(country, tag, p))).Where(k => k.Key >= MinKey).OrderByDescending(k => k.Key).ToList())
        {
            if (posted == MaxDepthPerTurn) return;
            var post = Post(country, tag, [province], posts);
            if (post == 0) continue;
            pool ??= Pool(country, tag, fronts.SelectMany(f => f.Units), false, u => !(_fireBrigade && Division.Motorized(u)) && Org(u) >= HelperOrg && Str(u) >= HelperStr);
            var unit = Take(tag, post, DepthRadius, pool);
            if (unit == 0) continue;
            var id = Game.Id(post);
            if (!Send(unit, id, Job.Depth, id, Game.Id(province))) continue;
            posts.Add(id);
            posted++;
            Log.Debug("depth defence: a spare division takes up a second-line post behind a key province");
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
