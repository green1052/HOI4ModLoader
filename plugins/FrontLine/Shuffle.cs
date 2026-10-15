using HOI4ModLoader;

namespace FrontLine;

public partial class FrontLine
{
    // ---- Fronts: each front order's divisions and line, the ground it lost lately, and whether it is in a crisis ----

    private const int LostHours = 48, LosingProvinces = 3; // several provinces lost (or being lost) in two days: a crisis
    private const int CrisisHours = 24;                    // a crisis is over a day after the front last looked like one
    private const int MaxShufflesPerTurn = 2, ShuffleRadius = 25, MinThreat = 4;
    private const int MaxPins = 6, MaxPinsPerTurn = 2, PinRadius = 2, PinOdds = 2, PinStopOrg = 40;

    /// <summary>A front order of ours, rebuilt every turn. Key: its army or army group and order id (stable across turns).</summary>
    private sealed class Front(nint order, (nint Group, int Id) key)
    {
        public readonly nint Order = order;
        public readonly (nint Group, int Id) Key = key;
        public readonly List<nint> Units = [];
        public readonly HashSet<nint> Line = []; // provinces its divisions hold next to enemy ground
        public int Threat;                       // enemy divisions across that line
    }

    private Dictionary<int, (int Hour, (nint, int) Front)> _lastSeen = []; // line province id -> when it was last ours, on which front (the turn's Side)
    private Dictionary<(nint, int), int> _crisis = [];                     // front -> the last hour it was losing (the turn's Side)

    /// <summary>Each front's line and threat; a front that lost (or is losing) several provinces lately, or faces twice its divisions, is in a crisis.</summary>
    private void Survey(nint country, int tag, List<Front> fronts)
    {
        var hour = Game.Hour;
        foreach (var front in fronts)
        {
            foreach (var unit in front.Units)
                if (Division.Location(unit) is var at && at != 0 && NextToEnemy(country, tag, at)) front.Line.Add(at);
            // Before the war: the AI draws fronts only on borders it expects a war on (its target, the target's guarantors), so
            // the border its divisions hold is the line: the fire brigade takes its posts behind it before the first attack.
            if (front.Line.Count == 0)
                foreach (var unit in front.Units)
                    if (Division.Location(unit) is var at && at != 0 && Game.Neighbours(at).Any(id => Game.Province(id) is var q && q != 0
                            && Province.Controller(q) is var c && c > 0 && c != tag && !Friendly(country, tag, q)))
                        front.Line.Add(at);
            var facing = new HashSet<nint>();
            foreach (var p in front.Line)
            {
                _lastSeen[Game.Id(p)] = (hour, front.Key);
                foreach (var id in Game.Neighbours(p))
                    if (Game.Province(id) is var e && e != 0 && Enemy(country, tag, e)) facing.Add(e);
            }
            front.Threat = facing.Sum(Province.Divisions);
        }
        var lost = new Dictionary<(nint, int), int>();
        foreach (var (id, seen) in _lastSeen.ToArray())
        {
            if (hour - seen.Hour is < 0 or > LostHours) _lastSeen.Remove(id);
            else if (Game.Province(id) is var p && p != 0 && Province.Controller(p) != tag) lost[seen.Front] = lost.GetValueOrDefault(seen.Front) + 1;
        }
        foreach (var (key, since) in _crisis.ToArray()) if (hour - since is < 0 or > 2 * CrisisHours) _crisis.Remove(key);
        foreach (var front in fronts)
            if (lost.GetValueOrDefault(front.Key) + front.Line.Count(_losingNow.ContainsKey) >= LosingProvinces
                || front.Threat >= MinThreat && front.Threat >= 2 * front.Units.Count)
                _crisis[front.Key] = hour;
    }

    // ---- Pocket fronts: the game splits a pocket off a front with a share of its divisions, often far more than the pocket needs ----

    private const int PocketShare = 2; // our divisions per enemy division in the pocket, 2 at least

    /// <summary>A front facing only encircled enemy ground keeps PocketShare per pocket division; the rest go onto its army's biggest open front.</summary>
    private void TrimPocketFronts(nint country, int tag, List<Front> fronts)
    {
        var pockets = new Dictionary<Front, int>(); // pocket front -> enemy divisions it faces
        foreach (var front in fronts)
        {
            var facing = new HashSet<nint>();
            foreach (var unit in front.Units)
                if (Division.Location(unit) is var at && at != 0)
                    foreach (var id in Game.Neighbours(at))
                        if (Game.Province(id) is var e && e != 0 && Enemy(country, tag, e)) facing.Add(e);
            if (facing.Count > 0 && facing.All(e => Province.Encircled(country, tag, e))) pockets[front] = facing.Sum(Province.Divisions);
        }
        foreach (var (front, enemies) in pockets)
        {
            var main = fronts.Where(f => f.Key.Group == front.Key.Group && !pockets.ContainsKey(f)).MaxBy(f => f.Units.Count);
            if (main == null && !_force) continue; // the player's army with no other front: theirs to decide
            var spare = front.Units.Count - Math.Max(2, PocketShare * enemies);
            if (spare <= 0) continue;
            var moved = 0;
            // The ones not in contact with the pocket leave first.
            foreach (var unit in front.Units.Where(u => Free(u, tag) && !Division.Walking(u) && Division.Location(u) != 0).OrderBy(u => NextToEnemy(country, tag, Division.Location(u))).Take(spare))
            {
                // An AI army with only this front: off its order, the theatre distribution gives them to fronts that want them.
                if (!(main != null ? Commands.Assign(unit, Division.Group(unit), main.Key.Id, _force) : Commands.Unassign(unit, Division.Group(unit), _force))) continue;
                _pending[unit] = _now + PendingMs;
                moved++;
            }
            if (moved > 0) Log.Debug($"pocket front: {moved} division(s) beyond what a pocket of {enemies} enemy division(s) needs go to {(main != null ? "their army's main front" : "the AI's regrouping")}");
        }
    }

    private bool Crisis((nint, int) front) => _crisis.TryGetValue(front, out var hour) && Game.Hour - hour is >= 0 and < CrisisHours;

    /// <summary>
    /// A front in a crisis gets spare fresh divisions of quiet fronts (no enemy across, or three times the divisions it faces)
    /// in its most pressed line provinces; they go back to their own front a day after the crisis. Fresh divisions next to
    /// the crisis pin idle enemy stacks with one attacker each. Plain moves within reach over our ground: no naval transfers.
    /// </summary>
    // ponytail: the shuffled divisions stand unassigned on the crisis line rather than joining its battle plan; simpler, and
    // their way home is the order they came from.
    private void Shuffle(nint country, int tag, List<Front> fronts)
    {
        Unpin();
        foreach (var (unit, held) in _held.ToArray())
            if (held.Job == Job.Shuffle && !Crisis(held.Front) && Release(unit, tag))
                Log.Debug("shuffle: the crisis is over, a division goes back to its own front");

        var crises = fronts.Where(f => f.Line.Count > 0 && _crisis.GetValueOrDefault(f.Key, int.MinValue) == Game.Hour).ToList();
        if (crises.Count == 0) return;
        Dictionary<nint, List<nint>>? pool = null;
        var sent = 0;
        var extra = new Dictionary<nint, int>(); // line province -> divisions on their way to it
        foreach (var (unit, held) in _held)
            if (held.Job == Job.Shuffle && Game.Province(held.Post) is var post && post != 0 && Division.Location(unit) != post)
                extra[post] = extra.GetValueOrDefault(post) + 1;
        foreach (var crisis in crises.OrderByDescending(f => f.Threat - f.Units.Count))
        {
            if (pool == null)
            {
                // Quiet fronts may give what they have beyond one per line province and twice their threat.
                var budget = new Dictionary<nint, int>();
                foreach (var f in fronts)
                    if (!crises.Contains(f) && f.Units.Count >= 3 * f.Threat) budget[f.Order] = f.Units.Count - Math.Max(f.Line.Count, 2 * f.Threat);
                bool Spare(nint u)
                {
                    var order = _orders[u + MemberOfUnit]; // members of budget fronts only
                    if (budget[order] <= 0 || Org(u) < FreshOrg || Str(u) < FreshStr) return false;
                    budget[order]--;
                    return true;
                }
                pool = Pool(country, tag, fronts.Where(f => budget.ContainsKey(f.Order)).SelectMany(f => f.Units), false, Spare);
            }
            while (sent < MaxShufflesPerTurn)
            {
                var target = crisis.Line.MaxBy(p => Pressure(country, tag, p) - extra.GetValueOrDefault(p));
                if (target == 0 || Pressure(country, tag, target) - extra.GetValueOrDefault(target) <= 0) break;
                var unit = Take(tag, target, ShuffleRadius, pool);
                if (unit == 0) break; // nothing spare within reach of this crisis
                var id = Game.Id(target);
                Send(unit, id, Job.Shuffle, id, 0, crisis.Key);
                extra[target] = extra.GetValueOrDefault(target) + 1;
                sent++;
                Log.Debug($"shuffle: a spare division from a quiet front reinforces a losing one ({crisis.Threat} enemy division(s) against {crisis.Units.Count})");
            }
            Pin(country, tag, crisis);
        }
    }

    /// <summary>Enemy divisions next to our line province beyond the divisions of ours in it.</summary>
    private int Pressure(nint country, int tag, nint province)
    {
        var enemy = 0;
        foreach (var id in Game.Neighbours(province))
            if (Game.Province(id) is var e && e != 0 && Enemy(country, tag, e)) enemy += Province.Divisions(e);
        return enemy - (_at.TryGetValue(province, out var ours) ? ours.Count : 0);
    }

    /// <summary>
    /// Idle enemy stacks next to the crisis (within PinRadius of its line) that one division of ours can attack without being
    /// hopelessly outmatched (forts counted) get one fresh attacker from a province where another of ours stays: locked in
    /// combat, they can't be moved into the crisis.
    /// </summary>
    private void Pin(nint country, int tag, Front crisis)
    {
        var pins = _held.Values.Count(h => h.Job == Job.Pin);
        var area = new HashSet<nint>(crisis.Line);
        var ring = crisis.Line.ToList();
        for (var depth = 1; depth <= PinRadius; depth++)
        {
            var next = new List<nint>();
            foreach (var p in ring)
            foreach (var id in Game.Neighbours(p))
                if (Game.Province(id) is var q && q != 0 && Province.Controller(q) == tag && area.Add(q)) next.Add(q);
            ring = next;
        }
        var pinned = 0;
        foreach (var from in area)
        {
            if (pins >= MaxPins || pinned == MaxPinsPerTurn) return;
            if (_losingNow.ContainsKey(from) || !_at.TryGetValue(from, out var here) || here.Count < 2) continue; // one stays
            foreach (var id in Game.Neighbours(from))
            {
                if (_sent.ContainsKey(id)) continue;
                var target = Game.Province(id);
                if (target == 0 || !Enemy(country, tag, target)) continue;
                var defenders = Province.Units(target).ToArray().Where(u => u != 0).ToArray();
                if (defenders.Length == 0 || defenders.Length * (Province.FortLevel(target) / 2 + 1) > PinOdds || defenders.Any(Division.Fighting)) continue; // empty: Attack's; busy already
                var unit = here.Where(u => Free(u, tag) && !Division.Walking(u) && Org(u) >= AttackOrg && Str(u) >= AttackStr).OrderByDescending(Org).FirstOrDefault();
                if (unit == 0) break;
                Send(unit, id, Job.Pin, Game.Id(from), id, crisis.Key);
                here.Remove(unit);
                _sent[id] = ([unit], true);
                pins++;
                pinned++;
                Log.Debug($"shuffle: a fresh division pins {defenders.Length} enemy division(s) next to a losing front");
                break;
            }
        }
    }

    /// <summary>A pinning attack stops (back to where it attacked from) once its organisation runs low or the crisis is over; it is done when it stops fighting.</summary>
    private void Unpin()
    {
        foreach (var (unit, held) in _held.ToArray())
        {
            if (held.Job != Job.Pin || _pending.ContainsKey(unit)) continue;
            if (!Division.Fighting(unit))
            {
                if (!Division.Walking(unit)) _held.Remove(unit); // won, or the attack ended: its front order takes it from here
                continue;
            }
            if (Org(unit) >= PinStopOrg && Crisis(held.Front)) continue;
            Move(unit, held.Post); // an attacker may break off
            _held.Remove(unit);
            Log.Debug($"shuffle: a pinning attack breaks off at {Org(unit):0}% organisation");
        }
    }
}
