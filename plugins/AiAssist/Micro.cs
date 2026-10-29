using HOI4ModLoader;

namespace AiAssist;

public partial class AiAssist
{
    // ---- Hold the line's micro: worn divisions rest, divisions in a closing salient escape, reserves counterattack break-ins ----

    private const int WornOrg = 30, WornStrength = 50, RestedOrg = 90, RestedStrength = 80, MaxRestingPercent = 25; // %
    private const int MaxMoves = 2, RestDepth = 2, MaxRestSearch = 6, MaxNeckSearch = 8, MaxRestHours = 168, RestArea = 3;
    private const int MicroHours = 2, CombatHours = 48; // pass interval; worn counts only after fighting within CombatHours
    private const int FireRadius = 3, MaxFireBrigades = 2, ReadyOrg = 70, ReadyStrength = 50, BreakInHours = 48;

    // Divisions resting or escaping, with tag and start hour; left alone until rested, then back on their order.
    // Dereferenced only while still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> Resting = new();
    private static readonly Dictionary<nint, int> Combat = []; // unit -> last game hour seen in combat (main thread; never dereferenced)
    private int _microAt = Never;
    private static int _rested, _escaped, _fireBrigade;

    /// <summary>
    /// Every two game hours, per AI country:
    /// - hold the line: rested divisions go back, losing attacks break off, reserves counterattack, worn or trapped divisions pull back;
    /// - on a grad, fought-out spearheads are counterattacked (defensive postures);
    /// - else fresh divisions take empty enemy provinces (attack targets).
    /// </summary>
    private void Micro()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _microAt, MicroHours)) return;
        _game.Check(); // drop a previous game's caches first
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
                foreach (var unit in units) if (Division.Fighting(unit)) Combat[unit] = hour;
                Release(tag, units, hour);
                BreakOff(country, tag, units, hour);
                if (!MicroElsewhere)
                {
                    FireBrigade(country, tag, units, hour);
                    PullBack(country, tag, units, hour);
                }
            }
            if (Grads.TryGetValue(tag, out var grad)) CounterAttack(country, tag, grad, hour); // on a grad: no piecemeal retaking of given-up ground
            else if (_attackTargets && !MicroElsewhere) TakeEmpty(country, tag, units, hour);
        }
        ShareJobs();
    }

    /// <summary>
    /// Resting divisions that recovered, or rested a week (short of equipment, they won't recover), go back on their order;
    /// gone ones are dropped.
    /// </summary>
    private static void Release(int tag, nint[] units, int hour)
    {
        foreach (var (unit, rest) in Resting)
        {
            if (rest.Tag != tag) continue;
            if (Array.IndexOf(units, unit) < 0) Resting.TryRemove(unit, out _);
            else if (!Division.Walking(unit) && (Percent(Division.Organisation(unit)) >= RestedOrg && Percent(Division.Strength(unit)) >= RestedStrength || hour - rest.Hour is < 0 or >= MaxRestHours)
                     && Resting.TryRemove(unit, out _))
                Return(unit, tag);
        }
    }

    /// <summary>
    /// Pulls back divisions in a closing salient (<see cref="Closing"/>) and divisions worn by fighting next to the enemy (while
    /// <see cref="RestRoom"/>) to a safe province a few steps back. Another of ours must stay behind; not where we outgun the
    /// enemy two to one.
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
                && Combat.TryGetValue(unit, out var fought) && hour - fought is >= 0 and < CombatHours; // worn by battle, not by supply
            if (!narrow.TryGetValue(location, out var closing)) closing = narrow[location] = Closing(country, tag, location);
            if (closing && Grads.TryGetValue(tag, out var grad) && grad.Fortresses.Contains(Game.Id(location))) closing = false; // a fortress city on the grad holds to the last
            if (!closing && !(worn && Province.EnemyDivisionsNext(country, tag, location))) continue;
            if (!strong.TryGetValue(location, out var dominant)) dominant = strong[location] = Dominant(country, tag, [location]);
            if (dominant || !closing && !RestRoom(tag, location)) continue;
            var rest = RestProvince(country, tag, location);
            if (rest < 0 || !Rest(unit, rest, tag, hour)) continue; // nowhere safe nearby: it stays
            left[location] = here - 1;
            moves++;
            if (closing) Interlocked.Increment(ref _escaped);
            else Interlocked.Increment(ref _rested);
        }
    }

    /// <summary>Whether one more can rest: at most MaxRestingPercent of the tag's divisions within RestArea provinces rest.</summary>
    private static bool RestRoom(int tag, nint location)
    {
        int all = 0, resting = 0;
        foreach (var p in Within([location], RestArea, q => Province.Controller(q) == tag))
        foreach (var unit in Province.Units(p))
        {
            if (unit == 0 || Division.Commander(unit) != tag || !Division.IsLand(unit)) continue;
            all++;
            if (Resting.ContainsKey(unit)) resting++;
        }
        return (resting + 1) * 100 <= all * MaxRestingPercent;
    }

    /// <summary>
    /// Takes the division off its orders (or the AI walks it straight back) and sends it to rest until <see cref="Release"/>.
    /// It stops being a post. Main thread.
    /// </summary>
    private static bool Rest(nint unit, int province, int tag, int hour)
    {
        TakeOff(unit);
        var from = Division.Location(unit);
        if (!Commands.Move(unit, province, true)) return false;
        if (from != 0) MapArrows.Flash(Game.Id(from), province, 80, 120, 255);
        Sent[unit] = hour + SentHours;
        Resting[unit] = (tag, hour);
        Posted.TryRemove(unit, out _);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="from"/>'s way back is closing: walking back ring by ring over our provinces, a ring narrows to one
    /// province next to enemy divisions (the neck) before safe ground, and the enemy's power at the neck is at least ours inside.
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
                    if (unit != 0 && Division.IsLand(unit) && Division.Commander(unit) == tag) ours += Division.Power(unit);
                foreach (var id in Game.Neighbours(neck))
                    if (Game.Province(id) is var e && e != 0 && Province.Enemy(me, tag, e))
                        foreach (var unit in Province.Units(e))
                            if (unit != 0 && Division.IsLand(unit) && Game.AtWar(me, Division.Commander(unit))) theirs += Division.Power(unit);
                return theirs >= ours;
            }
            inside.AddRange(next);
            ring = next;
        }
        return false;
    }

    /// <summary>A province of ours at least RestDepth steps back, not next to the enemy nor given up by a grad; -1 if none nearby.</summary>
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
    /// Reserves and second-line divisions off their post walk back. Ready ones (ReadyOrg, ReadyStrength) attack a break-in within
    /// FireRadius provinces, as many as it takes; they keep their post and <see cref="BreakOff()"/> tracks them. Main thread.
    /// </summary>
    private static void FireBrigade(nint country, int tag, nint[] units, int hour)
    {
        var ready = new Dictionary<nint, List<nint>>(); // province -> reserves there ready to strike
        foreach (var unit in units)
        {
            if (!Posted.TryGetValue(unit, out var post) || post.Tag != tag || post.Kind is not (PostKind.Reserve or PostKind.Depth)
                || Attackers.ContainsKey(unit) || !Movable(unit, tag) || Division.Walking(unit)) continue;
            var location = Division.Location(unit);
            if (location == 0) continue;
            if (Game.Id(location) != post.Province)
            {
                if (Commands.Move(unit, post.Province, true)) Sent[unit] = hour + SentHours;
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
            var need = Math.Max(1, defenders * (Province.FortLevel(province) / 2 + 1)); // vanilla's local attack test; mobile divisions count double below
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
            if (power < need) continue; // too strong for the reserves near: left to the line
            foreach (var unit in team)
                if (Attack(unit, target, tag, hour))
                    Interlocked.Increment(ref _fireBrigade);
            foreach (var there in ready.Values) there.RemoveAll(team.Contains);
            counters++;
        }
    }

    /// <summary>
    /// The id of a break-in at or next to our province <paramref name="p"/> with nothing sent at it yet, or 0: enemy troops inside
    /// it, or an enemy-held neighbour reaching into our side (2+ of ours around it, twice the enemy's) that was taken in the last
    /// BreakInHours, landed on, or touches no other enemy ground.
    /// </summary>
    private static int BreakIn(nint country, int tag, nint p, int hour) =>
        Province.BreakIn(tag, p, t => Game.AtWar(country, t), Gap.ContainsKey, id => Taken(id, hour, BreakInHours) || Landed(id, hour));

    /// <summary>Divisions of countries at war with us in the province.</summary>
    private static int Hostiles(nint country, nint province) => Province.Hostiles(province, t => Game.AtWar(country, t));
}
