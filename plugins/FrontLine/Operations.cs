using HOI4ModLoader;

namespace FrontLine;

/// <summary>Byte patterns for the operations (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class OperationSignatures
{
    /// <summary>The map's support attack: +0x28 the division's CRef (CArmy+0x18), +0x30 the province id; it attacks from where it stands and doesn't move in.</summary>
    [VTable] public const string SupportAttack = "CSupportAttackCommand";

    /// <summary>
    /// The map's right-click on the province a division stands in: `mov ecx,48h; call malloc; ...; mov r8b,1; mov rdx,rsi;
    /// mov rcx,rax; call CCancelMovementCommand ctor (mem[0x48], CArmy*, bool)`: stops its move or attack, support attacks too.
    /// </summary>
    [Signature] public const string CancelMovement = "B9 48 00 00 00 E8 ? ? ? ? 48 89 45 ? 48 85 C0 74 ? 41 B0 01 48 8B D6 48 8B C8 E8";
}

/// <summary>
/// Player operations, from the selected divisions (an army card or a field marshal's card selects all of theirs):
/// - Ctrl+Alt+B, blitz: the armored and motorized divisions break through the cheapest enemy sector and push province by
///   province to a deep objective (victory points, a supply hub, many enemy divisions along the way); infantry holds the
///   corridor behind them, one division per corridor province next to the enemy.
/// - Ctrl+Alt+P, pincer: the selection splits in two wings (by army, or by position), each with a spearhead, both aiming at
///   the same objective behind the enemy; once the ring closes the spearheads go back and the infantry holds the ring a week.
/// While one runs, fresh divisions of the same armies support-attack idle enemy stacks next to the breakthrough and the
/// corridor (one each), so they are locked in combat and can't be shifted into the gap; worn ones break off.
/// It ends when the objective is taken, the corridor is cut or a defence of it is being lost, or the spearheads are worn
/// out; the same key again (with any of its divisions selected, or nothing) cancels it. Divisions taken off their battle
/// plan go back onto it (a fighting one once its battle is over). A loaded save forgets running operations: their divisions
/// stay where they are, off their battle plan. Vanilla commands only (multiplayer works).
/// </summary>
public unsafe partial class FrontLine
{
    private static class Op
    {
        public const int SupportTarget = 0x1E8;                                             // CArmy: support attack province
        public const int SupportAttackSize = 0x38, CancelSize = 0x48;
        public const int StartOrg = 70, HoldOrg = 50, PinOrg = 60, BreakOffOrg = 25;
        public const int MaxHoldsPerStep = 2, MaxPins = 6, MaxPinsPerStep = 2, MaxRefused = 3, Radius = 8, RingHoldHours = 7 * 24;
        public static readonly int[] Keys = ['B', 'P']; // with Ctrl+Alt: blitz, pincer
    }

    private sealed class Arm(List<int> corridor, List<nint> spearhead)
    {
        public readonly List<int> Corridor = corridor;   // enemy provinces, breakthrough sector first, the objective last
        public readonly List<nint> Spearhead = spearhead;
        public int Head = -1;                            // last corridor province taken (in a row from the sector)
    }

    /// <summary>A running operation. Its divisions are in _held (Job.Operation): Post = the corridor province a holder holds, Target = the enemy province a pin attacks.</summary>
    private sealed class Operation(bool pincer, HashSet<nint> armies)
    {
        public readonly bool Pincer = pincer;
        public readonly HashSet<nint> Armies = armies; // pinning attackers and corridor holders come from these
        public readonly List<Arm> Arms = [];
        public readonly HashSet<nint> Units = [];      // spearheads, holders, pins
        public int Closed = -1, Refused;
        public string Name => Pincer ? "pincer" : "blitz";
    }

    private readonly List<Operation> _operations = [];
    private readonly Dictionary<nint, int> _opOrdered = []; // unit -> game hour we last ordered it
    private readonly bool[] _opDown = new bool[Op.Keys.Length];
    private delegate* unmanaged<nint, nint, byte, nint> _cancelMovement;
    private int _opDepth, _opLastHour = -1;
    private double _stopOrg;

    /// <summary>The operations' settings; true when their keys are on.</summary>
    private bool BindOperations()
    {
        var keys = Config.Bind("Operations", "OperationKeys", true,
            "Ctrl+Alt+B: blitz with the selected divisions (armor breaks through the weakest sector to a deep objective, infantry holds the corridor). "
            + "Ctrl+Alt+P: pincer with two selected armies or an army group (two spearheads meet behind the enemy, infantry holds the ring). "
            + "Meanwhile fresh divisions of those armies lock enemy stacks next to the breakthrough in combat. The same key again cancels.");
        _opDepth = Math.Clamp(Config.Bind("Operations", "OperationDepth", 8, "How deep (provinces) behind the enemy line an operation's objective may lie."), 2, 15);
        _stopOrg = Math.Clamp(Config.Bind("Operations", "SpearheadOrganisation", 35, "Spearheads stop advancing below this organisation (%); the operation ends when all of a wing's are."), 5, 90);
        return keys;
    }

    /// <summary>The operations' own game functions and timers (FrontLine.Load resolved the shared ones).</summary>
    private void LoadOperations()
    {
        _ = Sig.Find(Gui.AirWingsSelectedSig) + Sig.VTable(OperationSignatures.SupportAttack);
        var cancel = Sig.Find(OperationSignatures.CancelMovement) + 28;
        _cancelMovement = (delegate* unmanaged<nint, nint, byte, nint>)(cancel + 5 + *(int*)(cancel + 1));
        MainThread.Every(TimeSpan.FromMilliseconds(30), PollOperationKeys);
        MainThread.Every(TimeSpan.FromSeconds(1), RunOperations);
    }

    private void PollOperationKeys()
    {
        var hit = Pressed(Op.Keys, _opDown);
        if (hit < 0) return;
        var country = Begin(out var tag, out _);
        if (country == 0) return;
        var pincer = hit == 1;
        var selected = Selected(tag);
        var running = _operations.Where(o => o.Pincer == pincer && (selected.Count == 0 || o.Units.Any(selected.Contains))).ToList();
        if (running.Count > 0)
        {
            foreach (var op in running) End(op, tag, "cancelled");
            return;
        }
        if (selected.Count == 0)
        {
            Log.Info($"{(pincer ? "pincer" : "blitz")}: select an army or divisions first");
            return;
        }
        selected.ExceptWith(_held.Keys); // another feature's, or another operation's
        if (selected.Count == 0)
        {
            Log.Info($"{(pincer ? "pincer" : "blitz")}: the selected divisions all have a job already");
            return;
        }
        var started = pincer ? StartPincer(country, tag, selected) : StartBlitz(country, tag, selected);
        if (started != null) _operations.Add(started);
    }

    /// <summary>Our land divisions in the player's selection (checked against the country's own list before anything else is read).</summary>
    private HashSet<nint> Selected(int tag)
    {
        var result = new HashSet<nint>();
        foreach (var item in Gui.Selection())
            if (_alive.Contains(item) && Division.Commander(item) == tag && Division.Location(item) != 0) result.Add(item);
        return result;
    }

    private Operation? StartBlitz(nint country, int tag, HashSet<nint> force)
    {
        var spearhead = Spearhead(force, tag);
        if (spearhead.Count == 0) return Fail("blitz", "no fresh armored or motorized division free to move among them");
        var reach = Reach(country, tag, force);
        List<int>? best = null;
        var bestScore = double.MinValue;
        foreach (var (id, (cost, _, depth)) in reach)
        {
            if (depth < 1) continue; // deep: at least one province behind the sector
            var path = PathTo(reach, id);
            var score = Worth(country, tag, path, id) - cost;
            if (score > bestScore) (best, bestScore) = (path, score);
        }
        if (best == null) return Fail("blitz", "no enemy ground to break into next to them");
        var op = new Operation(false, Armies(force));
        op.Arms.Add(new Arm(best, spearhead));
        foreach (var unit in spearhead) Enlist(op, unit, 0, 0, true);
        Log.Info($"blitz: {spearhead.Count} armored/motorized division(s) break through at province {best[0]} toward province {best[^1]}, {best.Count} provinces deep");
        return op;
    }

    private Operation? StartPincer(nint country, int tag, HashSet<nint> force)
    {
        var (a, b) = Wings(force);
        var spearA = Spearhead(a, tag);
        var spearB = Spearhead(b, tag);
        if (spearA.Count == 0 || spearB.Count == 0) return Fail("pincer", "it needs two groups apart, each with a fresh armored or motorized division");
        var reachA = Reach(country, tag, a);
        var reachB = Reach(country, tag, b);
        (List<int> A, List<int> B)? best = null;
        var bestScore = double.MinValue;
        foreach (var (id, (costA, _, depthA)) in reachA)
        {
            if (depthA < 1 || !reachB.TryGetValue(id, out var atB) || atB.Depth < 1) continue;
            var pathA = PathTo(reachA, id);
            var pathB = PathTo(reachB, id);
            if (pathA.Take(pathA.Count - 1).Intersect(pathB).Any()) continue; // one corridor, not two arms
            var score = Worth(country, tag, pathA.Concat(pathB), id) - costA - atB.Cost;
            if (score > bestScore) (best, bestScore) = ((pathA, pathB), score);
        }
        if (best is not { } arms) return Fail("pincer", "no objective both wings can reach");
        var op = new Operation(true, Armies(force));
        op.Arms.Add(new Arm(arms.A, spearA));
        op.Arms.Add(new Arm(arms.B, spearB));
        foreach (var unit in spearA.Concat(spearB)) Enlist(op, unit, 0, 0, true);
        Log.Info($"pincer: {spearA.Count} + {spearB.Count} armored/motorized division(s) break through at provinces {arms.A[0]} and {arms.B[0]}, meeting at province {arms.A[^1]}");
        return op;
    }

    /// <summary>The fresh armored and motorized divisions among <paramref name="force"/> that can move now.</summary>
    private List<nint> Spearhead(IEnumerable<nint> force, int tag) => force.Where(u => Ready(u, tag, _now) && Division.Motorized(u) && Org(u) >= Op.StartOrg).ToList();

    private Operation? Fail(string name, string why)
    {
        Log.Info($"{name}: not started, {why}");
        return null;
    }

    private static HashSet<nint> Armies(IEnumerable<nint> units) => units.Select(Division.Group).Where(g => g != 0).ToHashSet();

    /// <summary>
    /// Two wings: two armies or more go whole to the nearer of the two farthest-apart divisions; one army's divisions each.
    /// </summary>
    // ponytail: hop distance over land from two seeds, no real geometry; an L-shaped front may pair the wrong flanks.
    private static (List<nint> A, List<nint> B) Wings(HashSet<nint> force)
    {
        var units = force.ToList();
        var fromFirst = Hops(Division.Location(units[0]));
        var seedB = units.MaxBy(u => Hop(fromFirst, u));
        var fromB = Hops(Division.Location(seedB));
        var fromA = Hops(Division.Location(units.MaxBy(u => Hop(fromB, u))));
        var lean = units.ToDictionary(u => u, u => Hop(fromA, u) - Hop(fromB, u)); // <= 0: wing A
        var armies = units.GroupBy(Division.Group).ToList();
        var toA = armies.Count >= 2
            ? armies.Where(g => g.Sum(u => lean[u]) <= 0).SelectMany(g => g).ToHashSet()
            : units.Where(u => lean[u] <= 0).ToHashSet();
        return (units.Where(toA.Contains).ToList(), units.Where(u => !toA.Contains(u)).ToList());
    }

    private static int Hop(Dictionary<int, int> hops, nint unit) => hops.GetValueOrDefault(Game.Id(Division.Location(unit)), 99);

    /// <summary>Hops over land from <paramref name="from"/>, up to 40.</summary>
    private static Dictionary<int, int> Hops(nint from)
    {
        var hops = new Dictionary<int, int> { [Game.Id(from)] = 0 };
        var ring = new List<nint> { from };
        for (var depth = 1; depth <= 40 && ring.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var p in ring)
            foreach (var id in Game.Neighbours(p))
            {
                if (hops.ContainsKey(id)) continue;
                var q = Game.Province(id);
                if (q == 0 || Province.Controller(q) <= 0) continue; // sea
                hops[id] = depth;
                next.Add(q);
            }
            ring = next;
        }
        return hops;
    }

    /// <summary>
    /// Cheapest ways into enemy ground from where <paramref name="from"/> stand (Dijkstra, up to the depth setting): each
    /// province costs 1 plus its divisions (twice), forts, harsh terrain and a river in front, as the AI's axis weighs a sector.
    /// </summary>
    private Dictionary<int, (int Cost, int Prev, int Depth)> Reach(nint country, int tag, IEnumerable<nint> from)
    {
        var best = new Dictionary<int, (int Cost, int Prev, int Depth)>();
        var queue = new PriorityQueue<(int Id, int Prev, int Depth), int>();
        foreach (var location in from.Select(Division.Location).Distinct())
        foreach (var id in Game.Neighbours(location))
            if (Game.Province(id) is var p && p != 0 && Enemy(country, tag, p) && Enterable(location, p)) queue.Enqueue((id, -1, 0), 1 + Province.AttackWeight(p, location));
        while (queue.TryDequeue(out var at, out var cost))
        {
            if (!best.TryAdd(at.Id, (cost, at.Prev, at.Depth)) || at.Depth >= _opDepth - 1) continue;
            var here = Game.Province(at.Id);
            foreach (var id in Game.Neighbours(here))
                if (!best.ContainsKey(id) && Game.Province(id) is var p && p != 0 && Enemy(country, tag, p) && Enterable(here, p))
                    queue.Enqueue((id, at.Id, at.Depth + 1), cost + 1 + Province.AttackWeight(p, here));
        }
        return best;
    }

    private static List<int> PathTo(Dictionary<int, (int Cost, int Prev, int Depth)> reach, int id)
    {
        var path = new List<int>();
        for (var at = id; at != -1; at = reach[at].Prev) path.Add(at);
        path.Reverse();
        return path;
    }

    /// <summary>Units can walk from <paramref name="from"/> into <paramref name="to"/>: no impassable border, a province units may enter.</summary>
    private static bool Enterable(nint from, nint to) => Province.Enterable(to) && Game.AdjacencyType(from, Game.Id(to)) != Province.Impassable;

    /// <summary>
    /// What reaching <paramref name="objective"/> along <paramref name="path"/> is worth: what taking it is worth (Attack's
    /// value: victory points, supply hub, port, airfields) and enemy divisions next to the path (flanked or cut off once ours).
    /// </summary>
    private double Worth(nint country, int tag, IEnumerable<int> path, int objective)
    {
        var value = Value(Game.Province(objective));
        var on = path.ToHashSet();
        var seen = new HashSet<int>();
        foreach (var id in on)
        foreach (var n in Around(id))
            if (!on.Contains(n) && seen.Add(n) && Game.Province(n) is var p && p != 0 && Enemy(country, tag, p))
                value += 2 * Province.Divisions(p);
        return value;
    }

    // ---- Running operations, once per game hour ----

    /// <summary>Gives back what ended operations left (Return: FrontLine's turns may not run), then runs each operation once per game hour.</summary>
    private void RunOperations()
    {
        if (_operations.Count == 0 && _held.Count == 0) return;
        var country = Begin(out var tag, out _);
        if (country == 0) return;
        Return(tag);
        var hour = Game.Hour;
        if (hour == _opLastHour) return;
        _opLastHour = hour;
        foreach (var unit in _opOrdered.Keys.Where(u => !Ordered(u)).ToList()) _opOrdered.Remove(unit);
        foreach (var op in _operations.ToList())
            if (Step(op, country, tag) is { } end) End(op, tag, end);
    }

    /// <summary>One hour of an operation; why it ends, or null.</summary>
    private string? Step(Operation op, nint country, int tag)
    {
        foreach (var arm in op.Arms)
        {
            var was = arm.Head;
            while (arm.Head + 1 < arm.Corridor.Count && Game.Province(arm.Corridor[arm.Head + 1]) is var p && p != 0 && !Enemy(country, tag, p)) arm.Head++;
            if (arm.Head > was) Log.Info($"{op.Name}: spearhead took province {arm.Corridor[arm.Head]} ({arm.Head + 1}/{arm.Corridor.Count})");
            for (var i = 0; i <= arm.Head; i++)
                if (Game.Province(arm.Corridor[i]) is var p && p != 0 && Enemy(country, tag, p)) return $"the corridor was cut at province {arm.Corridor[i]}";
        }
        var held = op.Arms.SelectMany(a => a.Corridor.Take(a.Head + 1)).ToHashSet();
        if (LosingDefences(tag).Keys.Select(Game.Id).FirstOrDefault(held.Contains) is var lost && lost != 0)
            return $"the corridor's defence at province {lost} is being lost";

        var reached = op.Arms.All(a => a.Head == a.Corridor.Count - 1);
        if (reached && !op.Pincer) return $"objective province {op.Arms[0].Corridor[^1]} taken";
        if (reached && op.Closed < 0)
        {
            op.Closed = Game.Hour;
            foreach (var unit in op.Arms.SelectMany(a => a.Spearhead).ToList()) GiveBack(op, unit, tag);
            Log.Info($"pincer: the ring closed at province {op.Arms[0].Corridor[^1]}; the spearheads go back, infantry holds the ring");
        }
        if (op.Closed >= 0)
        {
            if (Game.Hour - op.Closed >= Op.RingHoldHours) return "the ring was held a week";
            if (!held.Any(id => Around(id).Any(n => Game.Province(n) is var p && p != 0 && Province.Divisions(p) > 0 && Enemy(country, tag, p))))
                return "no enemy division left next to the ring";
        }
        else
        {
            foreach (var arm in op.Arms.Where(a => a.Head < a.Corridor.Count - 1))
            {
                if (arm.Spearhead.All(u => Org(u) < _stopOrg)) return "the spearhead is worn out";
                var next = arm.Corridor[arm.Head + 1];
                foreach (var unit in arm.Spearhead.Where(u => Ready(u, tag, _now) && !Division.Walking(u) && Org(u) >= _stopOrg && !Ordered(u)))
                {
                    if (Issue(unit, () => Commands.Move(unit, next))) op.Refused = 0;
                    else if (++op.Refused >= Op.MaxRefused) return $"province {next} can't be reached";
                }
            }
        }
        Hold(op, country, tag, held);
        Lock(op, country, tag, held);
        return null;
    }

    /// <summary>Corridor provinces next to the enemy with none of ours in them get the nearest fresh infantry of the operation's armies.</summary>
    private void Hold(Operation op, nint country, int tag, HashSet<int> held)
    {
        var sent = 0;
        var holding = op.Units.Select(u => _held[u].Post).ToHashSet();
        foreach (var id in held)
        {
            if (sent == Op.MaxHoldsPerStep) return;
            var province = Game.Province(id);
            if (province == 0 || _at.ContainsKey(province) || holding.Contains(id) || !NextToEnemy(country, tag, province)) continue;
            // A spare one: where another of ours stays, or away from the enemy.
            nint unit = 0;
            Search(tag, province, Op.Radius, (p, _) => _at.TryGetValue(p, out var here) && (here.Count >= 2 || !NextToEnemy(country, tag, p))
                && (unit = Freshest(here.Where(u => Candidate(op, u, tag) && !Division.Motorized(u) && Org(u) >= Op.HoldOrg))) != 0);
            if (unit == 0) continue;
            var location = Division.Location(unit);
            Enlist(op, unit, id, 0, true);
            if (!Issue(unit, () => Commands.Move(unit, id)))
            {
                GiveBack(op, unit, tag);
                continue;
            }
            _at[location].Remove(unit);
            sent++;
            Log.Info($"{op.Name}: an infantry division goes to hold the corridor at province {id}");
        }
    }

    /// <summary>
    /// Pinning: idle enemy stacks next to the sector and the corridor get a support attack from one fresh division of the
    /// operation's armies standing next to them; a division being attacked can't be moved, so the enemy can't shift them
    /// into the gap. A worn attacker breaks off and another takes over.
    /// </summary>
    private void Lock(Operation op, nint country, int tag, HashSet<int> held)
    {
        var pins = op.Units.Where(u => _held[u].Target != 0).ToList();
        foreach (var unit in pins.ToList())
        {
            var pin = _held[unit];
            var target = Game.Province(pin.Target);
            var over = target == 0 || EnemyUnits(country, tag, target) == 0 || !Division.Fighting(unit) && *(nint*)(unit + Op.SupportTarget) == 0 && Game.Hour - pin.Since >= 4;
            if (!over && Org(unit) >= Op.BreakOffOrg) continue;
            if (!over) BreakOff(unit);
            GiveBack(op, unit, tag);
            pins.Remove(unit);
            if (!over) Log.Info($"{op.Name}: a worn pinning attack on province {pin.Target} breaks off");
        }
        var corridor = op.Arms.SelectMany(a => a.Corridor).ToHashSet();
        var front = op.Closed >= 0 ? held : op.Arms.SelectMany(a => a.Corridor.Take(a.Head + 2)).ToHashSet();
        var pinned = pins.Select(u => _held[u].Target).ToHashSet();
        var targets = front.SelectMany(Around).Distinct()
            .Where(id => !corridor.Contains(id) && !pinned.Contains(id))
            .Select(Game.Province).Where(p => p != 0 && EnemyUnits(country, tag, p) > 0 && !Province.Units(p).ToArray().Any(u => u != 0 && Division.Fighting(u))) // fighting: locked already
            .OrderByDescending(Province.Divisions).ToList();
        var started = 0;
        foreach (var target in targets)
        {
            if (pins.Count >= Op.MaxPins || started == Op.MaxPinsPerStep) return;
            var id = Game.Id(target);
            var unit = Freshest(Game.Neighbours(target).Select(Game.Province).Where(p => p != 0 && _at.ContainsKey(p)).SelectMany(p => _at[p])
                .Where(u => Candidate(op, u, tag) && Org(u) >= Op.PinOrg));
            if (unit == 0 || !Issue(unit, () => SupportAttack(tag, unit, id))) continue;
            Enlist(op, unit, 0, id, false); // stays on its order: a support attack doesn't move it
            pins.Add(unit);
            started++;
            Log.Info($"{op.Name}: a division pins {Province.Divisions(target)} enemy division(s) at province {id} with a support attack");
        }
    }

    /// <summary>A division of the operation's armies doing no job (for us or any other feature) that can move now.</summary>
    private bool Candidate(Operation op, nint unit, int tag) =>
        op.Armies.Contains(Division.Group(unit)) && Free(unit, tag, _now) && !Division.Walking(unit) && !Ordered(unit);

    private static nint Freshest(IEnumerable<nint> units) => units.DefaultIfEmpty().MaxBy(u => u == 0 ? -1 : Org(u));

    /// <summary>Pinning attacks break off, every division leaves the operation.</summary>
    private void End(Operation op, int tag, string why)
    {
        foreach (var unit in op.Units.ToList())
        {
            if (_held[unit].Target != 0) BreakOff(unit);
            GiveBack(op, unit, tag);
        }
        _operations.Remove(op);
        Log.Info($"{op.Name} ended: {why}");
    }

    /// <summary>Booked for the operation (FrontLine's bookkeeping: no other feature takes it); with <paramref name="offOrder"/> off its order first.</summary>
    private void Enlist(Operation op, nint unit, int post, int target, bool offOrder)
    {
        var group = Division.Group(unit);
        Book(unit, Job.Operation, offOrder && group != 0 ? OrderOf(unit, group) : 0, post, target);
        op.Units.Add(unit);
    }

    /// <summary>
    /// Leaves the operation, back onto the order it came from: now if it can take orders, else (a spearhead fighting) once it
    /// can (Return), after FrontLine's view of the orders (_orders) caught up with our commands.
    /// </summary>
    private void GiveBack(Operation op, nint unit, int tag)
    {
        op.Units.Remove(unit);
        foreach (var arm in op.Arms) arm.Spearhead.Remove(unit);
        if (!_held.TryGetValue(unit, out var held)) return;
        _held[unit] = held with { Job = Job.Done };
        if (!Release(unit, tag)) _pending[unit] = _now + PendingMs;
    }

    /// <summary>Drops the operation's divisions that are gone (died, merged): never dereferenced again.</summary>
    private void Forget(Operation op)
    {
        op.Units.RemoveWhere(u => !_alive.Contains(u));
        foreach (var arm in op.Arms) arm.Spearhead.RemoveAll(u => !_alive.Contains(u));
    }

    /// <summary>The order of its army (or army group) the division is on, or 0.</summary>
    private static nint OrderOf(nint unit, nint group)
    {
        var member = unit + MemberOfUnit;
        var parent = *(nint*)(group + OrdersGroup.ParentOffset);
        nint[] owners = parent == 0 ? [group] : [group, parent];
        foreach (var owner in owners)
        foreach (var list in OrderLists)
        foreach (var order in Native.PdxArray(owner + list))
            if (order != 0 && Order.Members(order).Contains(member)) return order;
        return 0;
    }

    /// <summary>Support attack, as the map's: it attacks from where it stands and stays there.</summary>
    private static bool SupportAttack(int tag, nint unit, int province)
    {
        var cmd = Commands.New(OperationSignatures.SupportAttack, Op.SupportAttackSize, tag, 0);
        *(long*)(cmd + 0x28) = Division.Ref(unit); // the ctor's layout: unit CRef at +0x28 (over New's tag), province at +0x30
        *(long*)(cmd + 0x30) = province;
        return Commands.TryPost(cmd);
    }

    /// <summary>Stops an attack, as a right-click on the province it stands in does.</summary>
    private void BreakOff(nint unit)
    {
        if (!Division.Fighting(unit) && *(nint*)(unit + Op.SupportTarget) == 0) return;
        var cmd = Allocator.AllocZeroed(Op.CancelSize);
        _cancelMovement(cmd, unit, 1);
        Commands.TryPost(cmd);
    }

    private bool Issue(nint unit, Func<bool> command)
    {
        if (!command()) return false;
        _opOrdered[unit] = Game.Hour;
        return true;
    }

    /// <summary>We ordered it in the last two game hours: that order is still being carried out.</summary>
    private bool Ordered(nint unit) => _opOrdered.TryGetValue(unit, out var hour) && Game.Hour - hour is >= 0 and < 2;

    private static List<int> Around(int id) => Game.Province(id) is var p && p != 0 ? Game.Neighbours(p) : [];

    private int EnemyUnits(nint country, int tag, nint province) =>
        Enemy(country, tag, province) ? Province.Divisions(province) : 0;
}
