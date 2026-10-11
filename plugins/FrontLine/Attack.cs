using HOI4ModLoader;

namespace FrontLine;

public unsafe partial class FrontLine
{
    // ---- Attack: empty and surrounded enemy provinces next to the line, best targets first ----

    private const int StateOfProvince = 0xC0, BuildingsOfProvince = 0x190;
    private const int ProvincesOfState = 0x18, BestVictoryPoints = 0x85C, BuildingsOfState = 0x120;
    private const int SupplyNodeType = 0x3A0, AirBaseTypes = 0x298; // CBuildingDatabase: supply_node, the air base types
    private const int MinValue = 15;
    // CSupplySystem (gamestate+0x3D8): per-province records by id at +0x138 (0xD8 bytes): +0 need left after local nodes, +8 supply flowing in.
    private const int SupplyOfGameState = 0x3D8, FlowOfSupply = 0x138, FlowSize = 0xD8, NeedOfFlow = 0, SuppliedOfFlow = 8;
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
            var ready = fresh[from] = here.Where(u => Free(u, tag, _now) && !Walking(u) && Org(u) >= 70 && Str(u) >= 70).OrderByDescending(Org).ToList();
            if (ready.Count == 0) continue;
            foreach (var id in Game.Neighbours(from))
            {
                if (_sent.ContainsKey(id)) continue;
                var target = Game.Province(id);
                if (target == 0 || !Enemy(country, tag, target)) continue;
                var defenders = *(int*)(target + DivisionsInProvince);
                var need = defenders == 0 ? 1 : Surrounded(country, tag, target) ? defenders * (_fortLevel(target) / 2 + 1) : 0;
                if (need == 0 || need > ready.Count || need >= here.Count) continue; // not a pocket, or too strong for what can leave here
                var pocket = defenders > 0 ? defenders : Pocket(country, tag, target);
                if (pocket == 0 && Starved(country, tag, from, target)) continue; // our supply can't follow
                var (value, change) = (Value(target), FrontChange(country, tag, target));
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
                Commands.Move(unit, t.To);
                _pending[unit] = _now + PendingMs;
                here.Remove(unit);
            }
            _sent[t.To] = ([.. sent], true);
            attacks++;
            Log.Info(t.Defenders == 0 ? $"opportunity: a fresh division takes an empty enemy province (value {t.Value:0}, cuts off {t.Pocket} enemy division(s))"
                : $"mop-up: {t.Need} fresh division(s) attack {t.Defenders} surrounded enemy division(s)");
        }
    }

    /// <summary>No land neighbour of the enemy province is held by a country at war with us: its divisions can't get out.</summary>
    private bool Surrounded(nint country, int tag, nint province) =>
        Game.Neighbours(province).All(n => Game.Province(n) is var p && (p == 0 || *(int*)(p + ControllerOfProvince) == 0 || !Enemy(country, tag, p)));

    /// <summary>Enemy divisions that taking <paramref name="target"/> cuts off: in enemy provinces next to it whose other land neighbours are all ours or allied.</summary>
    // ponytail: one-province pockets only; a bounded flood fill if bigger encirclements matter.
    private int Pocket(nint country, int tag, nint target)
    {
        var cut = 0;
        var targetId = *(int*)(target + IdOfProvince);
        foreach (var id in Game.Neighbours(target))
        {
            var e = Game.Province(id);
            if (e == 0 || *(int*)(e + DivisionsInProvince) == 0 || !Enemy(country, tag, e)) continue;
            if (Game.Neighbours(e).All(n => n == targetId || Game.Province(n) is var q && (q == 0 || *(int*)(q + ControllerOfProvince) == 0 || Friendly(country, tag, q))))
                cut += *(int*)(e + DivisionsInProvince);
        }
        return cut;
    }

    /// <summary>How the front's length changes when <paramref name="target"/> is taken: its enemy neighbours become front, ours stop being front. Negative = shorter.</summary>
    private int FrontChange(nint country, int tag, nint target)
    {
        var change = 0;
        foreach (var id in Game.Neighbours(target))
        {
            var n = Game.Province(id);
            if (n == 0 || *(int*)(n + ControllerOfProvince) == 0) continue; // sea
            if (Enemy(country, tag, n)) change++;
            else if (Friendly(country, tag, n)) change--;
        }
        return change;
    }

    /// <summary>What taking the province is worth: victory points, a supply hub, a port, the state's airfields (in full on its main province).</summary>
    private double Value(nint target)
    {
        double value = 3 * Math.Min(*(int*)(target + VictoryPoints), 30);
        if (Level(target, SupplyNodeType) > 0) value += 20;
        if (Level(target, _navalBaseSlot) > 0) value += 15;
        var db = *(nint*)_buildingDb;
        var state = *(nint*)(target + StateOfProvince);
        if (db == 0 || state == 0) return value;
        var index = *(int*)(state + BestVictoryPoints);
        var main = index >= 0 && index < *(int*)(state + ProvincesOfState + 0xC) && ((nint*)*(nint*)(state + ProvincesOfState))[index] == target;
        var types = (nint*)*(nint*)(db + AirBaseTypes);
        for (int i = 0, n = *(int*)(db + AirBaseTypes + 0xC); i < n; i++)
            if (types[i] != 0 && _findBuilding(state + BuildingsOfState, types[i]) is var b && b != 0) value += (main ? 2 : 0.5) * *(short*)(b + 0x42);
        return value;
    }

    /// <summary>The level of a building in the province (0: none), by its type's slot in the building database.</summary>
    private int Level(nint province, int slot)
    {
        var db = *(nint*)_buildingDb;
        var type = db == 0 ? 0 : *(nint*)(db + slot);
        var building = type == 0 ? 0 : _findBuilding(province + BuildingsOfProvince, type);
        return building == 0 ? 0 : *(short*)(building + 0x40);
    }

    /// <summary>
    /// Supply only flows through our side's provinces, so once <paramref name="to"/> is taken its supply comes over its land
    /// neighbours held by us or allies. Starved: <paramref name="from"/> gets less than SupplyShare % of what it needs and no
    /// other friendly neighbour of the target has that much spare (flow in minus its own need).
    /// </summary>
    private bool Starved(nint country, int tag, nint from, nint to)
    {
        var supply = *(nint*)(Game.State + SupplyOfGameState);
        if (supply == 0) return false;
        var flows = *(nint*)(supply + FlowOfSupply);
        var count = *(int*)(supply + FlowOfSupply + 0xC);
        var fromId = *(int*)(from + IdOfProvince);
        if (flows == 0 || (uint)fromId >= (uint)count) return false;
        var at = flows + (nint)FlowSize * fromId;
        var need = *(long*)(at + NeedOfFlow); // the attackers and whatever else draws supply there
        var wanted = need * SupplyShare / 100;
        if (need <= 0 || *(long*)(at + SuppliedOfFlow) >= wanted) return false;
        foreach (var id in Game.Neighbours(to))
        {
            var n = Game.Province(id);
            if (n == 0 || n == from || (uint)id >= (uint)count || *(int*)(n + ControllerOfProvince) <= 0 || !Friendly(country, tag, n)) continue; // sea: controller 0
            var flow = flows + (nint)FlowSize * id;
            if (*(long*)(flow + SuppliedOfFlow) - *(long*)(flow + NeedOfFlow) >= wanted) return false;
        }
        return true;
    }
}
