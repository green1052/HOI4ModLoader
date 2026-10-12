using HOI4ModLoader;

namespace FrontLine;

public partial class FrontLine
{
    // ---- Attack: empty and surrounded enemy provinces next to the line, best targets first ----

    private const int MinValue = 15;
    private const int SupplyShare = 25; // % of what the attacking province needs it must get, or a friendly neighbour of the target must have spare

    /// <summary>
    /// Empty enemy provinces next to the line get one fresh division; surrounded enemy divisions at least as many as they are,
    /// more behind forts (as vanilla's local attacks weigh them), or none. A province of ours always keeps one. Targets in
    /// order: closing a pocket (and mopping one up), then value (victory points, supply hubs, airfields, ports), then
    /// shortening the front; skipped: ones our supply can't follow and worthless ones that lengthen the front by more than two
    /// provinces (unless they close a pocket). Then assaults (<see cref="Assault"/>). Nothing more goes where ours are still on
    /// their way or fighting. Plain moves: front divisions go back to their front by themselves afterwards.
    /// </summary>
    private void Attack(nint country, int tag)
    {
        _beachheads.Clear();
        var targets = new List<(nint From, int To, int Need, int Defenders, int Pocket, double Value, int Change)>();
        var fresh = new Dictionary<nint, List<nint>>();
        foreach (var (from, here) in _at)
        {
            if (here.Count < 2) continue; // one stays
            var ready = fresh[from] = here.Where(u => Free(u, tag, _now) && !Division.Walking(u) && Org(u) >= 70 && Str(u) >= 70).OrderByDescending(Org).ToList();
            if (ready.Count == 0) continue;
            foreach (var id in Game.Neighbours(from))
            {
                if (_sent.ContainsKey(id) || Game.AdjacencyType(from, id) == Province.Impassable) continue;
                var target = Game.Province(id);
                if (target == 0 || !Enemy(country, tag, target)) continue;
                var defenders = Province.Divisions(target);
                var need = defenders == 0 ? 1 : Province.Surrounded(country, tag, target) ? defenders * (Province.FortLevel(target) / 2 + 1) : 0;
                if (need == 0 || need > ready.Count || need >= here.Count) continue; // not a pocket, or too strong for what can leave here
                var pocket = defenders > 0 ? defenders : Province.Pocket(country, tag, target);
                if (pocket == 0 && Province.Starved(country, tag, from, target, SupplyShare)) continue; // our supply can't follow
                var (value, change) = (Value(target) + PortPull(country, tag, from, target), Province.FrontChange(country, tag, target));
                if (pocket == 0 && value < MinValue && change > 2) continue;      // a much longer front for nothing
                targets.Add((from, id, need, defenders, pocket, value, change));
            }
        }

        var attacks = 0;
        foreach (var t in targets.OrderByDescending(t => t.Pocket).ThenByDescending(t => t.Value).ThenBy(t => t.Change))
        {
            if (attacks == MaxAttacksPerTurn) break;
            if (_sent.ContainsKey(t.To)) continue;
            var (here, ready) = (_at[t.From], fresh[t.From]);
            if (t.Need > ready.Count || t.Need >= here.Count) continue;
            var sent = ready.GetRange(0, t.Need);
            ready.RemoveRange(0, t.Need);
            foreach (var unit in sent)
            {
                Arrow(unit, t.To, "assault", t.Pocket > 0 ? ((byte)255, (byte)40, (byte)40) : ((byte)255, (byte)140, (byte)0));
                Commands.Move(unit, t.To, _force);
                _pending[unit] = _now + PendingMs;
                here.Remove(unit);
            }
            _sent[t.To] = ([.. sent], true);
            attacks++;
            Log.Info(t.Defenders == 0 ? $"opportunity: a fresh division takes an empty enemy province (value {t.Value:0}, cuts off {t.Pocket} enemy division(s))"
                : $"mop-up: {t.Need} fresh division(s) attack {t.Defenders} surrounded enemy division(s)");
        }
        if (_odds > 0 && attacks < MaxAttacksPerTurn) Assault(country, tag, MaxAttacksPerTurn - attacks);
    }

    /// <summary>
    /// An enemy province next to the line, or enemy divisions inside our ground (cut off, landed, broken through), whose
    /// defenders the fresh divisions of our neighbouring provinces outgun _odds % (more behind forts, in mountains, cities,
    /// marshes and jungles, across a river): they all attack it. Provinces on the line keep one; ones behind it may all go.
    /// The player's divisions only on a battle plan that is running. Enemy divisions in a pocket already (or inside our ground)
    /// are finished off first, by any of ours, as soon as they outgun them. Supply and front length filters as for empty
    /// provinces; then the ones that cut off the most enemy divisions (a salient's neck), then the strongest odds.
    /// </summary>
    private void Assault(nint country, int tag, int max)
    {
        int Keep(nint from) => NextToEnemy(country, tag, from) ? 1 : 0; // a province on the line keeps one
        var candidates = new Dictionary<int, nint>(); // province id -> it: enemy with divisions, or ours with enemy divisions in it
        foreach (var (from, here) in _at)
        {
            if (here.Count <= Keep(from)) continue;
            foreach (var id in Game.Neighbours(from))
                if (!_sent.ContainsKey(id) && Game.Province(id) is var p && p != 0
                    && (Enemy(country, tag, p) ? Province.Divisions(p) > 0 : Friendly(country, tag, p) && Hostiles(country, p) > 0))
                    candidates.TryAdd(id, p);
        }
        var plans = new List<(int To, List<nint> Units, int Pocket, double Odds, double Value, int Port)>();
        foreach (var (id, target) in candidates)
        {
            long defence = 0;
            foreach (var unit in Province.Units(target))
                if (unit != 0 && Division.IsLand(unit) && AtWar(country, Division.Commander(unit))) defence += Division.Power(unit);
            if (defence == 0) continue;
            var intruders = !Enemy(country, tag, target);
            var encircled = !intruders && Province.Encircled(country, tag, target);
            var mopUp = intruders || encircled; // cut off already: any of ours may finish them, at even odds
            var hard = 1 + Province.FortLevel(target) / 2.0;
            if ((Province.TerrainCategory(target) & (Province.Mountain | Province.Urban | Province.Marsh | Province.Jungle)) != 0) hard *= 1.5;
            var (value, change) = (Value(target), Province.FrontChange(country, tag, target));
            var port = Game.Neighbours(target).Select(Game.Province).Where(_at.ContainsKey).Select(from => PortPull(country, tag, from, target)).DefaultIfEmpty(0).Max();
            value += port;
            var pocket = mopUp ? Hostiles(country, target) : Province.Pocket(country, tag, target);
            if (!mopUp && pocket == 0 && value < MinValue && change > 2) continue;
            var attackers = new List<(nint Unit, double Power)>();
            foreach (var nid in Game.Neighbours(target))
            {
                var from = Game.Province(nid);
                if (from == 0 || !_at.TryGetValue(from, out var here) || here.Count <= Keep(from) || Game.AdjacencyType(from, id) == Province.Impassable) continue;
                if (!mopUp && pocket == 0 && Province.Starved(country, tag, from, target, SupplyShare)) continue;
                var river = Game.AdjacencyType(from, id) is Province.River or Province.LargeRiver ? 1.5 : 1;
                attackers.AddRange(here.Where(u => Free(u, tag, _now) && !Division.Walking(u) && Org(u) >= 70 && Str(u) >= 70 && (_force || mopUp || OnRunningPlan(u)))
                    .OrderByDescending(Division.Power).Take(here.Count - Keep(from)).Select(u => (u, Division.Power(u) / river)));
            }
            var need = defence * hard * (mopUp ? 100 : _odds) / 100;
            var sent = new List<nint>();
            double power = 0;
            foreach (var (unit, p) in attackers.OrderByDescending(a => a.Power))
            {
                if (power >= need) break;
                sent.Add(unit);
                power += p;
            }
            if (power >= need) plans.Add((id, sent, pocket, power / (defence * hard), value, port));
        }

        var used = new HashSet<nint>();
        foreach (var plan in plans.OrderByDescending(p => p.Pocket).ThenByDescending(p => p.Port).ThenByDescending(p => p.Odds).ThenByDescending(p => p.Value))
        {
            if (max == 0) break;
            if (plan.Units.Any(used.Contains)) continue; // promised to a better one this turn
            if (plan.Units.GroupBy(Division.Location).Any(g => _at[g.Key].Count - g.Count() < Keep(g.Key))) continue; // a better one left that province its last
            foreach (var unit in plan.Units)
            {
                Arrow(unit, plan.To, "assault", plan.Pocket > 0 ? ((byte)255, (byte)40, (byte)40) : ((byte)255, (byte)140, (byte)0));
                Commands.Move(unit, plan.To, _force);
                _pending[unit] = _now + PendingMs;
                used.Add(unit);
                _at[Division.Location(unit)].Remove(unit);
            }
            _sent[plan.To] = ([.. plan.Units], true);
            max--;
            Log.Info($"assault: {plan.Units.Count} fresh division(s) attack a province they outgun {plan.Odds:0.0} to 1 (cuts off {plan.Pocket} enemy division(s))");
        }
    }

    /// <summary>On an order of a battle plan that is running (the player's go-ahead to attack).</summary>
    private bool OnRunningPlan(nint unit) => _orders.TryGetValue(unit + MemberOfUnit, out var order) && Order.Executing(order);

    private const int BeachheadSize = 12, PortSearch = 4, PortPullValue = 50;
    private readonly Dictionary<nint, bool> _beachheads = []; // province -> in a landing without a port: per Attack call

    /// <summary>
    /// From a beachhead (our ground around <paramref name="from"/> is cut off, up to BeachheadSize provinces, with no naval base:
    /// supply only over the beach) the way to the nearest enemy port is worth the most: PortPullValue for the port, less with
    /// each step to it through enemy ground (up to PortSearch). 0 elsewhere.
    /// </summary>
    private int PortPull(nint country, int tag, nint from, nint target)
    {
        if (!_beachheads.TryGetValue(from, out var beachhead))
            _beachheads[from] = beachhead = Province.Region(from, BeachheadSize, p => Province.Controller(p) == tag) is { } ours
                                            && ours.All(p => Province.BuildingLevel(p, Buildings.NavalBaseSlot) == 0);
        if (!beachhead) return 0;
        var seen = new HashSet<nint> { target };
        var frontier = new List<nint> { target };
        for (var depth = 0; depth < PortSearch && frontier.Count > 0; depth++)
        {
            if (frontier.Any(p => Province.BuildingLevel(p, Buildings.NavalBaseSlot) > 0)) return PortPullValue - depth * PortPullValue / PortSearch;
            frontier = frontier.SelectMany(p => Game.Neighbours(p)).Select(Game.Province)
                .Where(p => p != 0 && Enemy(country, tag, p) && seen.Add(p)).ToList();
        }
        return 0;
    }

    /// <summary>What taking the province is worth: victory points, a supply hub, a port, the state's airfields (in full on its main province).</summary>
    private static double Value(nint target)
    {
        double value = 3 * Math.Min(Province.VictoryPoints(target), 30);
        if (Province.BuildingLevel(target, Buildings.SupplyNodeSlot) > 0) value += 20;
        if (Province.BuildingLevel(target, Buildings.NavalBaseSlot) > 0) value += 15;
        var state = Province.State(target);
        if (state == 0) return value;
        var main = State.MainProvince(state) == target;
        foreach (var type in Buildings.AirBaseTypes) value += (main ? 2 : 0.5) * Buildings.WorkingLevel(State.Buildings(state), type);
        return value;
    }
}
