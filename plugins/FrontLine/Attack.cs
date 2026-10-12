using HOI4ModLoader;

namespace FrontLine;

public unsafe partial class FrontLine
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
        var targets = new List<(nint From, int To, int Need, int Defenders, int Pocket, double Value, int Change)>();
        var fresh = new Dictionary<nint, List<nint>>();
        foreach (var (from, here) in _at)
        {
            if (here.Count < 2) continue; // one stays
            var ready = fresh[from] = here.Where(u => Free(u, tag, _now) && !Division.Walking(u) && Org(u) >= 70 && Str(u) >= 70).OrderByDescending(Org).ToList();
            if (ready.Count == 0) continue;
            foreach (var id in Game.Neighbours(from))
            {
                if (_sent.ContainsKey(id)) continue;
                var target = Game.Province(id);
                if (target == 0 || !Enemy(country, tag, target)) continue;
                var defenders = Province.Divisions(target);
                var need = defenders == 0 ? 1 : Province.Surrounded(country, tag, target) ? defenders * (Province.FortLevel(target) / 2 + 1) : 0;
                if (need == 0 || need > ready.Count || need >= here.Count) continue; // not a pocket, or too strong for what can leave here
                var pocket = defenders > 0 ? defenders : Province.Pocket(country, tag, target);
                if (pocket == 0 && Province.Starved(country, tag, from, target, SupplyShare)) continue; // our supply can't follow
                var (value, change) = (Value(target), Province.FrontChange(country, tag, target));
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
    /// An enemy province next to the line whose defenders the fresh divisions of our neighbouring provinces outgun _odds % (each
    /// province keeping one; more behind forts, in mountains, cities, marshes and jungles, across a river): they all attack it.
    /// The player's divisions only on a battle plan that is running. Supply and front length filters as for empty provinces;
    /// the strongest odds first.
    /// </summary>
    private void Assault(nint country, int tag, int max)
    {
        var candidates = new Dictionary<int, nint>(); // enemy province id -> it, next to one of ours with spare fresh divisions
        foreach (var (from, here) in _at)
        {
            if (here.Count < 2) continue;
            foreach (var id in Game.Neighbours(from))
                if (!_sent.ContainsKey(id) && Game.Province(id) is var p && p != 0 && Enemy(country, tag, p) && Province.Divisions(p) > 0) candidates.TryAdd(id, p);
        }
        var plans = new List<(int To, List<nint> Units, double Odds, double Value)>();
        foreach (var (id, target) in candidates)
        {
            long defence = 0;
            foreach (var unit in Province.Units(target))
                if (unit != 0 && Division.IsLand(unit) && AtWar(country, Division.Commander(unit))) defence += Division.Power(unit);
            if (defence == 0) continue;
            var hard = 1 + Province.FortLevel(target) / 2.0;
            if ((Province.TerrainCategory(target) & (Province.Mountain | Province.Urban | Province.Marsh | Province.Jungle)) != 0) hard *= 1.5;
            var (value, change) = (Value(target), Province.FrontChange(country, tag, target));
            if (value < MinValue && change > 2) continue;
            var attackers = new List<(nint Unit, double Power)>();
            foreach (var nid in Game.Neighbours(target))
            {
                var from = Game.Province(nid);
                if (from == 0 || !_at.TryGetValue(from, out var here) || here.Count < 2 || Province.Starved(country, tag, from, target, SupplyShare)) continue;
                var river = Game.AdjacencyType(from, id) >= Province.River ? 1.5 : 1;
                attackers.AddRange(here.Where(u => Free(u, tag, _now) && !Division.Walking(u) && Org(u) >= 70 && Str(u) >= 70 && (_force || OnRunningPlan(u)))
                    .OrderByDescending(Division.Power).Take(here.Count - 1).Select(u => (u, Division.Power(u) / river)));
            }
            var need = defence * hard * _odds / 100;
            var sent = new List<nint>();
            double power = 0;
            foreach (var (unit, p) in attackers.OrderByDescending(a => a.Power))
            {
                if (power >= need) break;
                sent.Add(unit);
                power += p;
            }
            if (power >= need) plans.Add((id, sent, power / (defence * hard), value));
        }

        var used = new HashSet<nint>();
        foreach (var plan in plans.OrderByDescending(p => p.Odds).ThenByDescending(p => p.Value))
        {
            if (max == 0) break;
            if (plan.Units.Any(used.Contains)) continue; // promised to a better one this turn
            if (plan.Units.GroupBy(Division.Location).Any(g => _at[g.Key].Count - g.Count() < 1)) continue; // a better one left that province one
            foreach (var unit in plan.Units)
            {
                Commands.Move(unit, plan.To, _force);
                _pending[unit] = _now + PendingMs;
                used.Add(unit);
                _at[Division.Location(unit)].Remove(unit);
            }
            _sent[plan.To] = ([.. plan.Units], true);
            max--;
            Log.Info($"assault: {plan.Units.Count} fresh division(s) attack a province they outgun {plan.Odds:0.0} to 1");
        }
    }

    /// <summary>On an order of a battle plan that is running (the player's go-ahead to attack).</summary>
    private bool OnRunningPlan(nint unit) => _orders.TryGetValue(unit + MemberOfUnit, out var order) && Order.Executing(order);

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
