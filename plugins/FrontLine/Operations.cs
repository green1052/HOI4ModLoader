using HOI4ModLoader;

namespace FrontLine;

/// <summary>Byte patterns for the operations (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class OperationSignatures
{
    /// <summary>
    /// UI_AreAirWingsSelected: `mov rcx,[rip+ui]; test rcx,rcx; jz; add rcx,538h`: the player's selection list (CSelectionList)
    /// inside the UI object. List +0x30 first node {item, ?, next at +0x10}, +0x40 count; an item's kind at +8 (0 = a land
    /// division, the CArmy itself; 1 ship, 2 air wing).
    /// </summary>
    [Signature] public const string SelectionList = "48 83 EC 28 48 8B 0D ? ? ? ? 48 85 C9 74 ? 48 81 C1 38 05 00 00";

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
/// plan go back onto it. Vanilla commands only (multiplayer works).
/// </summary>
public unsafe partial class FrontLine
{
    private static class Op
    {
        public const int SupportTarget = 0x1E8, Ref = 0x18;                                 // CArmy: support attack province, CRef
        public const int FlagsOfStatic = 0xD2, Harsh = Mountain | Urban | 0x2000 | 0x10000, Impassable = 4; // + marsh, jungle
        public const int FirstNode = 0x30, NextOfNode = 0x10, SupportAttackSize = 0x38, CancelSize = 0x48;
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
    private PerGame _opGame = null!;
    private nint _selectionUi;
    private int _selectionOffset, _opDepth, _opLastHour = -1;
    private double _stopOrg;

    private void LoadOperations()
    {
        var keys = Config.Bind("Operations", "OperationKeys", true,
            "Ctrl+Alt+B: blitz with the selected divisions (armor breaks through the weakest sector to a deep objective, infantry holds the corridor). "
            + "Ctrl+Alt+P: pincer with two selected armies or an army group (two spearheads meet behind the enemy, infantry holds the ring). "
            + "Meanwhile fresh divisions of those armies lock enemy stacks next to the breakthrough in combat. The same key again cancels.");
        _opDepth = Math.Clamp(Config.Bind("Operations", "OperationDepth", 8, "How deep (provinces) behind the enemy line an operation's objective may lie."), 2, 15);
        _stopOrg = Math.Clamp(Config.Bind("Operations", "SpearheadOrganisation", 35, "Spearheads stop advancing below this organisation (%); the operation ends when all are."), 5, 90);
        if (!keys) return;
        var at = Sig.Find(OperationSignatures.SelectionList);
        _selectionUi = at + 11 + *(int*)(at + 7);
        _selectionOffset = *(int*)(at + 19);
        _ = Sig.VTable(OperationSignatures.SupportAttack);
        var cancel = Sig.Find(OperationSignatures.CancelMovement) + 28;
        _cancelMovement = (delegate* unmanaged<nint, nint, byte, nint>)(cancel + 5 + *(int*)(cancel + 1));
        _ = Sig.VTable(Signatures.LandCombat);
        // FrontLine's other features resolve these only when on.
        if (_assign == null) _assign = (delegate* unmanaged<nint, nint, nint, int, byte, nint>)Sig.Find(Signatures.AssignCommand);
        if (_unassign == null) _unassign = (delegate* unmanaged<nint, nint, nint, byte, byte, nint>)Sig.Find(Signatures.UnassignCommand);
        if (_atWar == null) _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        if (_fortLevel == null) _fortLevel = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FortLevel);
        if (_findBuilding == null) _findBuilding = (delegate* unmanaged<nint, nint, nint>)Sig.Find(Signatures.FindBuilding);
        if (_buildingDb == 0)
        {
            var call = Sig.Find(Signatures.BuildingDbCall);
            var getter = call + 5 + *(int*)(call + 1);
            _buildingDb = getter + 18 + *(int*)(getter + 14);
            _navalBaseSlot = *(int*)(call + 8);
        }
        _opGame = new PerGame(() =>
        {
            foreach (var unit in _operations.SelectMany(o => o.Units)) _held.Remove(unit);
            _operations.Clear();
            _opOrdered.Clear();
            _opLastHour = -1;
        });
        MainThread.Every(TimeSpan.FromMilliseconds(30), PollOperationKeys);
        MainThread.Every(TimeSpan.FromSeconds(1), RunOperations);
    }

    private void PollOperationKeys()
    {
        var hot = Gui.CtrlHeld && Gui.AltHeld && Gui.GameFocused;
        var hit = -1;
        for (var i = 0; i < Op.Keys.Length; i++)
        {
            var down = Gui.KeyHeld(Op.Keys[i]);
            if (hot && down && !_opDown[i]) hit = i;
            _opDown[i] = down;
        }
        if (hit < 0) return;
        _opGame.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var pincer = hit == 1;
        FreshMemos();
        var selected = Selected(country, tag);
        var running = _operations.Where(o => o.Pincer == pincer && (selected.Count == 0 || o.Units.Any(selected.Contains))).ToList();
        if (running.Count > 0)
        {
            foreach (var op in running) End(op, tag, "cancelled");
            return;
        }
        selected.ExceptWith(_held.Keys); // another feature's, or another operation's
        if (selected.Count == 0)
        {
            Log.Info($"{(pincer ? "pincer" : "blitz")}: select an army or divisions first");
            return;
        }
        var started = pincer ? StartPincer(country, tag, selected) : StartBlitz(country, tag, selected);
        if (started != null) _operations.Add(started);
    }

    /// <summary>Our land divisions in the player's selection (checked against the country's own list before anything else is read).</summary>
    private HashSet<nint> Selected(nint country, int tag)
    {
        var ui = *(nint*)_selectionUi;
        var result = new HashSet<nint>();
        if (ui == 0) return result;
        var ours = Native.PdxArray(country + UnitsOfCountry).ToArray().ToHashSet();
        var node = *(nint*)(ui + _selectionOffset + Op.FirstNode);
        for (var n = 0; node != 0 && n < 5000; n++, node = *(nint*)(node + Op.NextOfNode))
        {
            var item = *(nint*)node;
            if (ours.Contains(item) && *(int*)(item + KindOfUnit) == 0 && *(int*)(item + CommanderTag) == tag && *(nint*)(item + LocationOfUnit) != 0) result.Add(item);
        }
        return result;
    }

    private Operation? StartBlitz(nint country, int tag, HashSet<nint> force)
    {
        var spearhead = force.Where(u => Ready(u, tag, Environment.TickCount64) && Motorized(u) && Org(u) >= Op.StartOrg).ToList();
        if (spearhead.Count == 0) return Fail("blitz", "no fresh armored or motorized division free to move among them");
        var reach = Reach(country, tag, force);
        List<int>? best = null;
        var bestScore = double.MinValue;
        foreach (var (id, (cost, _, depth)) in reach)
        {
            if (depth < 1) continue; // deep: at least one province behind the sector
            var path = PathTo(reach, id);
            var score = Value(country, tag, path, id) - cost;
            if (score > bestScore) (best, bestScore) = (path, score);
        }
        if (best == null) return Fail("blitz", "no enemy ground to break into next to them");
        var op = new Operation(false, Armies(force));
        op.Arms.Add(new Arm(best, spearhead));
        foreach (var unit in spearhead) Book(op, unit, 0, 0, true);
        Log.Info($"blitz: {spearhead.Count} armored/motorized division(s) break through at province {best[0]} toward province {best[^1]}, {best.Count} provinces deep");
        return op;
    }

    private Operation? StartPincer(nint country, int tag, HashSet<nint> force)
    {
        var (a, b) = Wings(force);
        var spearA = a.Where(u => Ready(u, tag, Environment.TickCount64) && Motorized(u) && Org(u) >= Op.StartOrg).ToList();
        var spearB = b.Where(u => Ready(u, tag, Environment.TickCount64) && Motorized(u) && Org(u) >= Op.StartOrg).ToList();
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
            var score = Value(country, tag, pathA.Concat(pathB), id) - costA - atB.Cost;
            if (score > bestScore) (best, bestScore) = ((pathA, pathB), score);
        }
        if (best is not { } arms) return Fail("pincer", "no objective both wings can reach");
        var op = new Operation(true, Armies(force));
        op.Arms.Add(new Arm(arms.A, spearA));
        op.Arms.Add(new Arm(arms.B, spearB));
        foreach (var unit in spearA.Concat(spearB)) Book(op, unit, 0, 0, true);
        Log.Info($"pincer: {spearA.Count} + {spearB.Count} armored/motorized division(s) break through at provinces {arms.A[0]} and {arms.B[0]}, meeting at province {arms.A[^1]}");
        return op;
    }

    private Operation? Fail(string name, string why)
    {
        Log.Info($"{name}: not started, {why}");
        return null;
    }

    private static HashSet<nint> Armies(IEnumerable<nint> units) => units.Select(u => *(nint*)(u + GroupOfUnit)).Where(g => g != 0).ToHashSet();

    /// <summary>
    /// Two wings: two armies or more go whole to the nearer of the two farthest-apart divisions; one army's divisions each.
    /// </summary>
    // ponytail: hop distance over land from two seeds, no real geometry; an L-shaped front may pair the wrong flanks.
    private static (List<nint> A, List<nint> B) Wings(HashSet<nint> force)
    {
        nint Location(nint u) => *(nint*)(u + LocationOfUnit);
        var units = force.ToList();
        var fromFirst = Hops(Location(units[0]));
        var seedB = units.MaxBy(u => Hop(fromFirst, u));
        var fromB = Hops(Location(seedB));
        var fromA = Hops(Location(units.MaxBy(u => Hop(fromB, u))));
        var lean = units.ToDictionary(u => u, u => Hop(fromA, u) - Hop(fromB, u)); // <= 0: wing A
        var armies = units.GroupBy(u => *(nint*)(u + GroupOfUnit)).ToList();
        var toA = armies.Count >= 2
            ? armies.Where(g => g.Sum(u => lean[u]) <= 0).SelectMany(g => g).ToHashSet()
            : units.Where(u => lean[u] <= 0).ToHashSet();
        return (units.Where(toA.Contains).ToList(), units.Where(u => !toA.Contains(u)).ToList());
    }

    private static int Hop(Dictionary<int, int> hops, nint unit) => hops.GetValueOrDefault(*(int*)(*(nint*)(unit + LocationOfUnit) + IdOfProvince), 99);

    /// <summary>Hops over land from <paramref name="from"/>, up to 40.</summary>
    private static Dictionary<int, int> Hops(nint from)
    {
        var hops = new Dictionary<int, int> { [*(int*)(from + IdOfProvince)] = 0 };
        var ring = new List<nint> { from };
        for (var depth = 1; depth <= 40 && ring.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var p in ring)
            foreach (var id in Game.Neighbours(p))
            {
                if (hops.ContainsKey(id)) continue;
                var q = Game.Province(id);
                if (q == 0 || *(int*)(q + ControllerOfProvince) <= 0) continue; // sea
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
        foreach (var location in from.Select(u => *(nint*)(u + LocationOfUnit)).Distinct())
        foreach (var id in Game.Neighbours(location))
            if (Game.Province(id) is var p && p != 0 && Enemy(country, tag, p) && Enterable(location, p)) queue.Enqueue((id, -1, 0), 1 + Weight(p, location));
        while (queue.TryDequeue(out var at, out var cost))
        {
            if (!best.TryAdd(at.Id, (cost, at.Prev, at.Depth)) || at.Depth >= _opDepth - 1) continue;
            var here = Game.Province(at.Id);
            foreach (var id in Game.Neighbours(here))
                if (!best.ContainsKey(id) && Game.Province(id) is var p && p != 0 && Enemy(country, tag, p) && Enterable(here, p))
                    queue.Enqueue((id, at.Id, at.Depth + 1), cost + 1 + Weight(p, here));
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

    /// <summary>How hard <paramref name="target"/> is to take from <paramref name="from"/> (AiAssist's SpotWeight).</summary>
    private int Weight(nint target, nint from)
    {
        var weight = 2 * *(int*)(target + DivisionsInProvince) + _fortLevel(target);
        var info = *(nint*)(target + StaticOfProvince);
        var terrain = info == 0 ? 0 : *(nint*)(info + TerrainOfStatic);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & Op.Harsh) != 0) weight += 3;
        if (Game.AdjacencyType(from, *(int*)(target + IdOfProvince)) >= River) weight += 2;
        return weight;
    }

    /// <summary>Units can walk from <paramref name="from"/> into <paramref name="to"/>: no impassable border, a province units may enter.</summary>
    private static bool Enterable(nint from, nint to)
    {
        var info = *(nint*)(to + StaticOfProvince);
        return info != 0 && (*(byte*)(info + Op.FlagsOfStatic) & 1) != 0 && Game.AdjacencyType(from, *(int*)(to + IdOfProvince)) != Op.Impassable;
    }

    /// <summary>
    /// What reaching <paramref name="objective"/> along <paramref name="path"/> is worth: what taking it is worth (Attack's
    /// value: victory points, supply hub, port, airfields) and enemy divisions next to the path (flanked or cut off once ours).
    /// </summary>
    private double Value(nint country, int tag, IEnumerable<int> path, int objective)
    {
        var value = Value(Game.Province(objective));
        var on = path.ToHashSet();
        var seen = new HashSet<int>();
        foreach (var id in on)
        foreach (var n in Around(id))
            if (!on.Contains(n) && seen.Add(n) && Game.Province(n) is var p && p != 0 && Enemy(country, tag, p))
                value += 2 * *(int*)(p + DivisionsInProvince);
        return value;
    }

    // ---- Running operations, once per game hour ----

    private void RunOperations()
    {
        _opGame.Check();
        if (_operations.Count == 0 || Game.State == 0) return;
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var units = Native.PdxArray(country + UnitsOfCountry).ToArray();
        var alive = units.ToHashSet();
        foreach (var op in _operations) Forget(op, u => !alive.Contains(u)); // died, merged: never dereferenced again
        var hour = Hour;
        if (hour == _opLastHour) return;
        _opLastHour = hour;
        foreach (var unit in _opOrdered.Keys.Where(u => !alive.Contains(u)).ToList()) _opOrdered.Remove(unit);
        FreshMemos();

        var at = new Dictionary<nint, List<nint>>(); // province -> our land divisions there
        foreach (var unit in units)
        {
            var location = *(nint*)(unit + LocationOfUnit);
            if (*(int*)(unit + KindOfUnit) != 0 || *(int*)(unit + CommanderTag) != tag || location == 0) continue;
            if (!at.TryGetValue(location, out var here)) at[location] = here = [];
            here.Add(unit);
        }
        foreach (var op in _operations.ToList())
        {
            var end = Step(op, country, tag, at);
            if (end != null) End(op, tag, end);
        }
    }

    /// <summary>One hour of an operation; why it ends, or null.</summary>
    private string? Step(Operation op, nint country, int tag, Dictionary<nint, List<nint>> at)
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
        if (LosingDefences(tag).Keys.Select(p => *(int*)(p + IdOfProvince)).FirstOrDefault(held.Contains) is var lost && lost != 0)
            return $"the corridor's defence at province {lost} is being lost";

        var reached = op.Arms.All(a => a.Head == a.Corridor.Count - 1);
        if (reached && !op.Pincer) return $"objective province {op.Arms[0].Corridor[^1]} taken";
        if (reached && op.Closed < 0)
        {
            op.Closed = Hour;
            foreach (var unit in op.Arms.SelectMany(a => a.Spearhead).ToList()) GiveBack(op, unit);
            foreach (var arm in op.Arms) arm.Spearhead.Clear();
            Log.Info($"pincer: the ring closed at province {op.Arms[0].Corridor[^1]}; the spearheads go back, infantry holds the ring");
        }
        if (op.Closed >= 0)
        {
            if (Hour - op.Closed >= Op.RingHoldHours) return "the ring was held a week";
            if (!held.Any(id => Around(id).Any(n => Game.Province(n) is var p && p != 0 && *(int*)(p + DivisionsInProvince) > 0 && Enemy(country, tag, p))))
                return "no enemy division left next to the ring";
        }
        else
        {
            foreach (var arm in op.Arms.Where(a => a.Head < a.Corridor.Count - 1))
            {
                if (arm.Spearhead.All(u => Org(u) < _stopOrg)) return "the spearhead is worn out";
                var next = arm.Corridor[arm.Head + 1];
                foreach (var unit in arm.Spearhead.Where(u => Ready(u, tag, Environment.TickCount64) && !Walking(u) && Org(u) >= _stopOrg && !Ordered(u)))
                {
                    if (Order(unit, () => Commands.Move(unit, next))) op.Refused = 0;
                    else if (++op.Refused >= Op.MaxRefused) return $"province {next} can't be reached";
                }
            }
        }
        Hold(op, country, tag, at, held);
        Lock(op, country, tag, at, held);
        return null;
    }

    /// <summary>Corridor provinces next to the enemy with none of ours in them get the nearest fresh infantry of the operation's armies.</summary>
    private void Hold(Operation op, nint country, int tag, Dictionary<nint, List<nint>> at, HashSet<int> held)
    {
        var sent = 0;
        var holding = op.Units.Select(u => _held[u].Post).ToHashSet();
        foreach (var id in held)
        {
            if (sent == Op.MaxHoldsPerStep) return;
            var province = Game.Province(id);
            if (province == 0 || at.ContainsKey(province) || holding.Contains(id) || !NextToEnemy(country, tag, province)) continue;
            // A spare one: where another of ours stays, or away from the enemy.
            nint unit = 0;
            Search(tag, province, Op.Radius, (p, _) => at.TryGetValue(p, out var here) && (at[p].Count >= 2 || !NextToEnemy(country, tag, p))
                && (unit = Freshest(here.Where(u => Candidate(op, u, tag) && !Motorized(u) && Org(u) >= Op.HoldOrg))) != 0);
            if (unit == 0) continue;
            var location = *(nint*)(unit + LocationOfUnit);
            Book(op, unit, id, 0, true);
            if (!Order(unit, () => Commands.Move(unit, id)))
            {
                GiveBack(op, unit);
                continue;
            }
            at[location].Remove(unit);
            sent++;
            Log.Info($"{op.Name}: an infantry division goes to hold the corridor at province {id}");
        }
    }

    /// <summary>
    /// Pinning: idle enemy stacks next to the sector and the corridor get a support attack from one fresh division of the
    /// operation's armies standing next to them; a division being attacked can't be moved, so the enemy can't shift them
    /// into the gap. A worn attacker breaks off and another takes over.
    /// </summary>
    private void Lock(Operation op, nint country, int tag, Dictionary<nint, List<nint>> at, HashSet<int> held)
    {
        var pins = op.Units.Where(u => _held[u].Target != 0).ToList();
        foreach (var unit in pins.ToList())
        {
            var pin = _held[unit];
            var target = Game.Province(pin.Target);
            var over = target == 0 || EnemyUnits(country, tag, target) == 0 || !Fighting(unit) && *(nint*)(unit + Op.SupportTarget) == 0 && Hour - pin.Since >= 4;
            if (!over && Org(unit) >= Op.BreakOffOrg) continue;
            if (!over) BreakOff(unit);
            GiveBack(op, unit);
            pins.Remove(unit);
            if (!over) Log.Info($"{op.Name}: a worn pinning attack on province {pin.Target} breaks off");
        }
        var corridor = op.Arms.SelectMany(a => a.Corridor).ToHashSet();
        var front = op.Closed >= 0 ? held : op.Arms.SelectMany(a => a.Corridor.Take(a.Head + 2)).ToHashSet();
        var pinned = pins.Select(u => _held[u].Target).ToHashSet();
        var targets = front.SelectMany(Around).Distinct()
            .Where(id => !corridor.Contains(id) && !pinned.Contains(id))
            .Select(Game.Province).Where(p => p != 0 && EnemyUnits(country, tag, p) > 0 && !Native.PdxArray(p + UnitsInProvince).ToArray().Any(Fighting)) // fighting: locked already
            .OrderByDescending(p => *(int*)(p + DivisionsInProvince)).ToList();
        var started = 0;
        foreach (var target in targets)
        {
            if (pins.Count >= Op.MaxPins || started == Op.MaxPinsPerStep) return;
            var id = *(int*)(target + IdOfProvince);
            var unit = Freshest(Game.Neighbours(target).Select(Game.Province).Where(p => p != 0 && at.ContainsKey(p)).SelectMany(p => at[p])
                .Where(u => Candidate(op, u, tag) && Org(u) >= Op.PinOrg));
            if (unit == 0 || !Order(unit, () => SupportAttack(tag, unit, id))) continue;
            Book(op, unit, 0, id, false); // stays on its order: a support attack doesn't move it
            pins.Add(unit);
            started++;
            Log.Info($"{op.Name}: a division pins {*(int*)(target + DivisionsInProvince)} enemy division(s) at province {id} with a support attack");
        }
    }

    /// <summary>A division of the operation's armies doing no job (for us or any other feature) that can move now.</summary>
    private bool Candidate(Operation op, nint unit, int tag) =>
        op.Armies.Contains(*(nint*)(unit + GroupOfUnit)) && !_held.ContainsKey(unit) && Ready(unit, tag, Environment.TickCount64) && !Walking(unit) && !Ordered(unit);

    private static nint Freshest(IEnumerable<nint> units) => units.DefaultIfEmpty().MaxBy(u => u == 0 ? -1 : Org(u));

    /// <summary>Divisions taken off their order go back onto it, pinning attacks break off.</summary>
    private void End(Operation op, int tag, string why)
    {
        var tagNow = Game.ControlledTag;
        var country = Game.Country(tagNow);
        var alive = country == 0 || tagNow != tag ? [] : Native.PdxArray(country + UnitsOfCountry).ToArray().ToHashSet();
        Forget(op, u => !alive.Contains(u));
        foreach (var unit in op.Units.ToList())
        {
            if (_held[unit].Target != 0) BreakOff(unit);
            GiveBack(op, unit);
        }
        _operations.Remove(op);
        Log.Info($"{op.Name} ended: {why}");
    }

    /// <summary>
    /// Booked for the operation in FrontLine's bookkeeping (no other feature takes it); with <paramref name="offOrder"/> off
    /// its order first, remembering where it goes back. Not Send: that reads the orders FrontLine's turn gathers, and its
    /// other features may all be off.
    /// </summary>
    private void Book(Operation op, nint unit, int post, int target, bool offOrder)
    {
        var group = *(nint*)(unit + GroupOfUnit);
        var order = offOrder && group != 0 ? OrderIdOf(unit, group) : 0;
        if (order != 0) Unassign(unit, group);
        _held[unit] = new Held(Job.Operation, group, order, Hour, post, target, default);
        op.Units.Add(unit);
    }

    /// <summary>Back onto the order it came from (when it had one, and is still in that army). Not Release: a spearhead may be fighting.</summary>
    private void GiveBack(Operation op, nint unit)
    {
        op.Units.Remove(unit);
        foreach (var arm in op.Arms) arm.Spearhead.Remove(unit);
        if (!_held.Remove(unit, out var held)) return;
        if (held.Order != 0 && *(nint*)(unit + GroupOfUnit) == held.Group) Assign(unit, held.Group, held.Order); // in another army now: the player's
        _pending[unit] = Environment.TickCount64 + PendingMs;
    }

    private void Forget(Operation op, Func<nint, bool> gone)
    {
        foreach (var unit in op.Units.Where(gone).ToList())
        {
            op.Units.Remove(unit);
            _held.Remove(unit);
        }
        foreach (var arm in op.Arms) arm.Spearhead.RemoveAll(u => gone(u));
    }

    /// <summary>The id of the order of its army (or army group) the division is on, or 0.</summary>
    private static int OrderIdOf(nint unit, nint group)
    {
        var member = unit + MemberOfUnit;
        var parent = *(nint*)(group + OrdersGroup.ParentOffset);
        nint[] owners = parent == 0 ? [group] : [group, parent];
        foreach (var owner in owners)
        foreach (var list in OrderLists)
        foreach (var order in Native.PdxArray(owner + list))
            if (order != 0 && Native.PdxArray(order + MembersOfOrder).Contains(member)) return *(int*)(order + IdOfOrder);
        return 0;
    }

    /// <summary>Support attack, as the map's: it attacks from where it stands and stays there.</summary>
    private static bool SupportAttack(int tag, nint unit, int province)
    {
        var cmd = Commands.New(OperationSignatures.SupportAttack, Op.SupportAttackSize, tag, 0);
        *(long*)(cmd + 0x28) = *(long*)(unit + Op.Ref); // the ctor's layout: unit CRef at +0x28 (over New's tag), province at +0x30
        *(long*)(cmd + 0x30) = province;
        return Commands.TryPost(cmd);
    }

    /// <summary>Stops an attack, as a right-click on the province it stands in does. ponytail: a refused one leaks its small unit array.</summary>
    private void BreakOff(nint unit)
    {
        if (!Fighting(unit) && *(nint*)(unit + Op.SupportTarget) == 0) return;
        var cmd = Allocator.AllocZeroed(Op.CancelSize);
        _cancelMovement(cmd, unit, 1);
        Commands.TryPost(cmd);
    }

    private bool Order(nint unit, Func<bool> command)
    {
        if (!command()) return false;
        _opOrdered[unit] = Hour;
        return true;
    }

    private bool Ordered(nint unit) => _opOrdered.TryGetValue(unit, out var hour) && Hour - hour < 2;

    /// <summary>Who is at war with whom and the line can change between FrontLine's turns (which may not run at all): recheck.</summary>
    private void FreshMemos()
    {
        _war.Clear();
        _edge.Clear();
    }

    private static List<int> Around(int id) => Game.Province(id) is var p && p != 0 ? Game.Neighbours(p) : [];

    private int EnemyUnits(nint country, int tag, nint province) =>
        Enemy(country, tag, province) ? *(int*)(province + DivisionsInProvince) : 0;
}
