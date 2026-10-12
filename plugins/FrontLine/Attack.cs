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
    /// shortening the front; skipped: ones our supply can't follow and worthless ones that lengthen the front (unless they
    /// close a pocket). Nothing more goes where ours are still on their way or fighting. Plain moves: front divisions go back
    /// to their front by themselves afterwards.
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
                if (pocket == 0 && value < MinValue && change > 0) continue;      // a longer front for nothing
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
