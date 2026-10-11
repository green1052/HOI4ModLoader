using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Rotation, escape and the fire brigade: AI divisions come off their front to rest or before a salient is cut off, and
    //      reserves counterattack break-ins (hold the line) ----

    private const int WornOrg = 30, WornStrength = 50, RestedOrg = 90, RestedStrength = 80, MaxRestingPercent = 25; // %
    private const int MaxMoves = 2, RestDepth = 2, MaxRestSearch = 6, MaxNeckSearch = 8, MaxRestHours = 168, RestArea = 3;
    private const int MicroHours = 2, CombatHours = 48; // the pass runs at most every two game hours; worn counts only after fighting in the last two days
    private const int FireRadius = 3, MaxFireBrigades = 2, ReadyOrg = 70, ReadyStrength = 50, BreakInHours = 48;

    // Divisions we took off their front to rest or escape, with their tag and since when: the theatre shuffle and army balancing
    // leave them until rested, then they go back on their order. Never dereferenced unless still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> _resting = new();
    private static readonly Dictionary<nint, int> _combat = []; // unit -> last game hour seen in combat (main thread; never dereferenced)
    private int _microAt = Never;
    private static int _rested, _escaped, _fireBrigade;

    /// <summary>
    /// Every two game hours, per AI country: rested divisions go back on their orders, worn-out ones and ones in a closing salient
    /// come off the line, ready reserves counterattack break-ins (hold the line); on a grad, fought-out spearheads are
    /// counterattacked (defensive postures), else fresh divisions take empty enemy provinces (attack targets).
    /// </summary>
    private void Micro()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _microAt, MicroHours)) return; // menus, lobby, loading: no AI commands
        _game.Check(); // a save loaded since the last reset: its caches point into the old world
        var hour = Game.Hour;
        Expire(hour);
        if (_holdLine) TrackControllers(hour);
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player) continue;
            var units = Array.FindAll(Game.Units(country).ToArray(), u => u != 0);
            if (_holdLine)
            {
                foreach (var unit in units) if (Division.Fighting(unit)) _combat[unit] = hour;
                Release(tag, units, hour);
                BreakOff(country, tag, units, hour);
                FireBrigade(country, tag, units, hour);
                PullBack(country, tag, units, hour);
            }
            if (Grads.TryGetValue(tag, out var grad)) CounterAttack(country, tag, grad, hour); // on its grad: no piecemeal taking back of given-up ground
            else if (_attackTargets) TakeEmpty(country, tag, units, hour);
        }
    }

    /// <summary>
    /// The tag's resting divisions that rested (or rested a week: short of equipment, they won't get better) go back on the order
    /// we took them off; gone ones are dropped.
    /// </summary>
    private static void Release(int tag, nint[] units, int hour)
    {
        foreach (var (unit, rest) in _resting)
        {
            if (rest.Tag != tag) continue;
            if (Array.IndexOf(units, unit) < 0) _resting.TryRemove(unit, out _);
            else if (!Division.Walking(unit) && (Percent(Division.Organisation(unit)) >= RestedOrg && Percent(Division.Strength(unit)) >= RestedStrength || hour - rest.Hour is < 0 or >= MaxRestHours)
                     && _resting.TryRemove(unit, out _))
                Return(unit, tag);
        }
    }

    /// <summary>
    /// Divisions in a salient whose way back is closing under an enemy at least as strong, and divisions worn out by fighting next
    /// to enemy divisions (while few enough of those around rest), come off their front to a province a few steps back, where
    /// another of ours stays in theirs. Not where we outgun the enemy two to one.
    /// </summary>
    private void PullBack(nint country, int tag, nint[] units, int hour)
    {
        var moves = 0;
        var narrow = new Dictionary<nint, bool>();   // province -> its way back is closing
        var strong = new Dictionary<nint, bool>();   // province -> twice the enemy's power around it
        var left = new Dictionary<nint, int>();      // province -> divisions still holding it
        foreach (var unit in units)
        {
            if (moves == MaxMoves) return;
            if (!Movable(unit, tag)) continue;
            var location = Division.Location(unit);
            if (location == 0) continue;
            if (!left.TryGetValue(location, out var here)) here = left[location] = Standing(country, tag, location);
            if (here < 2) continue; // alone there: it holds on
            var worn = (Percent(Division.Organisation(unit)) < WornOrg || Percent(Division.Strength(unit)) < WornStrength)
                && _combat.TryGetValue(unit, out var fought) && hour - fought is >= 0 and < CombatHours; // worn by battle, not by supply
            if (!narrow.TryGetValue(location, out var closing)) closing = narrow[location] = Closing(country, tag, location);
            if (closing && Grads.TryGetValue(tag, out var grad) && grad.Fortresses.Contains(Game.Id(location))) closing = false; // a fortress city on the grad holds to the last
            if (!closing && !(worn && Province.EnemyDivisionsNext(country, tag, location))) continue;
            if (!strong.TryGetValue(location, out var dominant)) dominant = strong[location] = Dominant(country, tag, [location]);
            if (dominant || !closing && !RestRoom(country, tag, location)) continue;
            var rest = RestProvince(country, tag, location);
            if (rest < 0 || !Rest(unit, rest, tag, hour)) continue; // nowhere safe nearby: it stays
            left[location] = here - 1;
            moves++;
            if (closing) Interlocked.Increment(ref _escaped);
            else Interlocked.Increment(ref _rested);
        }
    }

    /// <summary>Fewer than MaxRestingPercent of the tag's divisions within RestArea provinces of ours rest (one more counted).</summary>
    private static bool RestRoom(nint country, int tag, nint location)
    {
        int all = 0, resting = 0;
        foreach (var p in Within([location], RestArea, q => Province.Controller(q) == tag))
        foreach (var unit in Province.Units(p))
        {
            if (unit == 0 || Division.Commander(unit) != tag || !Division.IsLand(unit)) continue;
            all++;
            if (_resting.ContainsKey(unit)) resting++;
        }
        return (resting + 1) * 100 <= all * MaxRestingPercent;
    }

    /// <summary>
    /// Off its orders (on its front, the AI would walk it straight back) and to the rest province, left alone until rested
    /// (<see cref="Release"/>). A resting post isn't one any more: another is posted. Main thread.
    /// </summary>
    private static bool Rest(nint unit, int province, int tag, int hour)
    {
        TakeOff(unit);
        if (!Commands.Move(unit, province, true)) return false;
        _sent[unit] = hour + SentHours;
        _resting[unit] = (tag, hour);
        _posted.TryRemove(unit, out _);
        return true;
    }

    /// <summary>
    /// Going back from <paramref name="from"/> over our provinces, one ring (provinces at the same distance) is a single province
    /// next to enemy divisions before reaching provinces away from the enemy (the neck of a closing pocket), and the enemy's power
    /// next to the neck is at least ours inside (a thin coastal strip facing a weak enemy isn't a trap).
    /// </summary>
    // ponytail: rings, not a max-flow of disjoint paths; a wide salient with a one-province gap elsewhere is missed.
    private static bool Closing(nint me, int tag, nint from)
    {
        if (!Province.NextToEnemy(me, tag, from)) return false;
        var seen = new HashSet<int> { Game.Id(from) };
        var ring = new List<nint> { from };
        var inside = new List<nint> { from };
        for (var depth = 1; depth <= MaxNeckSearch; depth++)
        {
            var next = new List<nint>();
            foreach (var province in ring)
            foreach (var id in Game.Neighbours(province))
                if (seen.Add(id) && Game.Province(id) is var p && p != 0 && Province.Controller(p) == tag) next.Add(p);
            if (next.Count == 0) return false;                                       // cut off already, or an island: nowhere to go
            if (next.Any(p => !Province.NextToEnemy(me, tag, p))) return false;      // safe ground within reach
            if (next.Count == 1)
            {
                var neck = next[0];
                if (!Province.EnemyDivisionsNext(me, tag, neck)) return false;      // the only way back, but no enemy troops at it
                long ours = 0, theirs = 0;
                foreach (var p in inside)
                foreach (var unit in Province.Units(p))
                    if (unit != 0 && Division.IsLand(unit) && Division.Commander(unit) == tag) ours += Power(unit);
                foreach (var id in Game.Neighbours(neck))
                    if (Game.Province(id) is var e && e != 0 && Province.Enemy(me, tag, e))
                        foreach (var unit in Province.Units(e))
                            if (unit != 0 && Division.IsLand(unit) && Game.AtWar(me, Division.Commander(unit))) theirs += Power(unit);
                return theirs >= ours;
            }
            inside.AddRange(next);
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
                if (p == 0 || Province.Controller(p) != tag) continue;
                if (depth >= RestDepth && !Province.NextToEnemy(me, tag, p) && !GivenUp(tag, id)) return id;
                next.Add(p);
            }
            frontier = next;
        }
        return -1;
    }

    // ---- Fire brigade: posted reserves counterattack break-ins near them, then go back to their post ----

    /// <summary>
    /// The tag's reserves and second-line divisions that are off their post (back from a counterattack, pushed off) walk back; ready
    /// ones (organisation 70 %+, strength 50 %+) attack a break-in within FireRadius provinces with as many of them nearby as it takes.
    /// The attackers are tracked (<see cref="BreakOff"/>) and keep their post. Main thread.
    /// </summary>
    private static void FireBrigade(nint country, int tag, nint[] units, int hour)
    {
        var ready = new Dictionary<nint, List<nint>>(); // province -> reserves there ready to strike
        foreach (var unit in units)
        {
            if (!_posted.TryGetValue(unit, out var post) || post.Tag != tag || post.Kind is not (PostKind.Reserve or PostKind.Depth)
                || Attackers.ContainsKey(unit) || !Movable(unit, tag) || Division.Walking(unit)) continue;
            var location = Division.Location(unit);
            if (location == 0) continue;
            if (Game.Id(location) != post.Province)
            {
                if (Commands.Move(unit, post.Province, true)) _sent[unit] = hour + SentHours;
                continue;
            }
            if (Percent(Division.Organisation(unit)) < ReadyOrg || Percent(Division.Strength(unit)) < ReadyStrength) continue;
            if (!ready.TryGetValue(location, out var here)) ready[location] = here = [];
            here.Add(unit);
        }
        var counters = 0;
        foreach (var (location, here) in ready)
        {
            if (counters == MaxFireBrigades) return;
            if (here.Count == 0) continue; // all sent with another
            var target = 0;
            foreach (var p in Within([location], FireRadius, q => Province.Controller(q) == tag))
                if ((target = BreakIn(country, tag, p, hour)) != 0) break;
            var province = target == 0 ? 0 : Game.Province(target);
            if (province == 0) continue;
            var defenders = Province.Enemy(country, tag, province) ? Province.Divisions(province) : Hostiles(country, province);
            var need = Math.Max(1, defenders * (Province.FortLevel(province) / 2 + 1)); // vanilla's local attack test: as many as they are, more against forts
            var team = new List<nint>();
            var power = 0;
            foreach (var p in Within([province], FireRadius, q => Province.Controller(q) == tag))
            {
                if (!ready.TryGetValue(p, out var there)) continue;
                foreach (var unit in there)
                {
                    if (power >= need) break;
                    team.Add(unit);
                    power += Division.Motorized(unit) ? 2 : 1;
                }
            }
            if (power < need) continue; // too strong for what is near: the line holds it
            foreach (var unit in team)
                if (Attack(unit, target, tag, hour))
                    Interlocked.Increment(ref _fireBrigade);
            foreach (var there in ready.Values) there.RemoveAll(team.Contains);
            counters++;
        }
    }

    /// <summary>
    /// A break-in at or next to our province <paramref name="p"/> nothing is sent at yet: enemy troops inside it, or an enemy-held
    /// neighbour reaching into our side (at least two of our provinces around it and twice the enemy's) taken in the last two days,
    /// landed on, or touching no enemy ground at all (paratroopers). Its id, or 0.
    /// </summary>
    private static int BreakIn(nint country, int tag, nint p, int hour)
    {
        var self = Game.Id(p);
        if (!_gap.ContainsKey(self) && Hostiles(country, p) > 0) return self;
        foreach (var id in Game.Neighbours(p))
        {
            if (_gap.ContainsKey(id)) continue;
            var e = Game.Province(id);
            if (e == 0 || !Province.Enemy(country, tag, e)) continue;
            var (ours, theirs) = (0, 0);
            foreach (var n in Game.Neighbours(e))
            {
                var q = Game.Province(n);
                if (q == 0) continue;
                if (Province.Controller(q) == tag) ours++;
                else if (Province.Enemy(country, tag, q)) theirs++;
            }
            if (ours >= 2 && ours >= 2 * theirs && (theirs == 0 || Taken(id, hour, BreakInHours) || Landed(id, hour))) return id;
        }
        return 0;
    }

    /// <summary>Divisions of countries at war with us in the province.</summary>
    private static int Hostiles(nint country, nint province)
    {
        var n = 0;
        foreach (var unit in Province.Units(province))
            if (unit != 0 && Game.AtWar(country, Division.Commander(unit))) n++;
        return n;
    }
}
