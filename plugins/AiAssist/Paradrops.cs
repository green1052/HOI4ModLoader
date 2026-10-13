using System.Collections.Concurrent;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Strategic paradrops: the vanilla AI never makes a paradrop order (only the map UI posts the source command) ----

    private const int TagOfGroup = 0x3C;                                                      // COrdersGroup: owner tag
    private const int CanParadrop = 0x5B5, UnitOfMemberSlot = 4;                             // CArmy stats: can-paradrop flag; COrdersGroupMember vtable: -> CArmy*
    private const int SourceOfOrder = 0xB8;                                                   // COrder: source province
    private const int SourceCommandSize = 0x50;
    private const int MaxParatroopers = 4, ParadropMaxDefenders = 0, NearbyDefendersMax = 4, PocketBonus = 15, SourceSearchDepth = 12;
    private const int FindOrderHours = 3, GiveUpHours = 14 * 24, LaunchHours = 48, RetryHours = 24, CooldownHours = 7 * 24;
    private const int DropHours = 2, PlanHours = 12; // drops in progress advance every two game hours; new ones are planned every twelve

    private static bool _paradrops;
    private static int _paradropped, _activeDrops, _beachDrops; // _activeDrops: Drops.Count for AI threads (ConcurrentDictionary.IsEmpty takes every lock)
    private static int _paratroopersReserved, _reservedCount; // _reservedCount: Reserved.Count, read lock-free on AI threads
    private static int _noParatroopers, _noAirbase, _notReady, _noTarget; // why the planner didn't drop, for the log (Sheep Mod's AI builds no paratroopers or transports)
    private int _dropAt = Never, _planAt = Never;
    private static nint _deleteVTable, _assignVTable, _unassignVTable;
    private static delegate* unmanaged<nint, nint, int, int, nint, nint> _paradropSource; // (mem, group, source province, order id 0, CPdxArray<CArmy*>*)
    private static delegate* unmanaged<nint, nint, byte> _isParadropSource;               // (province, group)
    private static delegate* unmanaged<nint, nint, byte> _isParadropTarget;               // (order, province)
    private static delegate* unmanaged<nint, nint, byte> _orderReady;                     // (order, std::string* reason)

    private sealed class Drop
    {
        public nint General; // the CAIGeneral whose axis it backs: an Axes key, never dereferenced (0: a beachhead's)
        public HashSet<int>? Core; // a beachhead drop: the beachhead it backs (an axis drop reads its axis's core)
        public long Group;
        public int Source, Order, Target, Hour, Launched = -1;
        public long[] Units = [];
    }

    private static readonly Dictionary<int, Drop> Drops = [];       // tag -> its paradrop in progress (main thread)
    private static readonly Dictionary<int, int> NextParadrop = []; // tag -> game hour it may plan again (main thread)
    // Read by AI threads (PostAICommand, Pinned): CRef ids only, never dereferenced there.
    private static readonly ConcurrentDictionary<long, int> Paratroopers = new();
    private static readonly ConcurrentDictionary<(long Group, int Order), int> ParadropOrders = new();

    private void LoadParadrops()
    {
        _paradropSource = (delegate* unmanaged<nint, nint, int, int, nint, nint>)Sig.Find(Signatures.ParadropSourceCommand);
        _isParadropSource = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.IsParadropSource);
        _isParadropTarget = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.IsParadropTarget);
        _orderReady = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.OrderReady);
        _deleteVTable = Sig.VTable(Signatures.DeleteOrderCommand);
        _assignVTable = Sig.VTable(Signatures.AssignOrderCommand);
        _unassignVTable = Sig.VTable(Signatures.UnassignOrderCommand);
        _ = Sig.VTable(Signatures.ParadropTargetCommand); // Commands.New looks it up by name: fail at load, not mid-drop
        MainThread.Every(TimeSpan.FromSeconds(1), PlanParadrops);
    }

    private static void ClearParadrops()
    {
        Drops.Clear();
        NextParadrop.Clear();
        Paratroopers.Clear();
        Reserved.Clear();
        Volatile.Write(ref _reservedCount, 0);
        ParadropOrders.Clear();
        Volatile.Write(ref _activeDrops, 0);
    }

    /// <summary>A division on one of our drops: the front AI and our own movers leave it alone. AI threads too.</summary>
    private static bool Paratrooper(nint unit) => Volatile.Read(ref _activeDrops) != 0 && Paratroopers.ContainsKey(Division.Ref(unit));

    /// <summary>
    /// AI order commands that would undo one of our drops: its order deleted, its divisions assigned back to the front.
    /// False: drop the command. AI threads: reads the command and the CRef sets only; strips our divisions in place.
    /// </summary>
    private static bool KeepsParadrops(nint cmd)
    {
        if (Volatile.Read(ref _activeDrops) == 0 && Volatile.Read(ref _reservedCount) == 0) return true; // IsEmpty takes every lock: every AI command passes here
        var vtable = *(nint*)cmd;
        if (vtable == _deleteVTable) return !ParadropOrders.ContainsKey((*(long*)(cmd + Commands.GroupOfOrderCommand), *(int*)(cmd + Commands.OrderOfOrderCommand)));
        if (vtable != _assignVTable && vtable != _unassignVTable) return true;
        var assign = vtable == _assignVTable; // reserved paratroopers are never assigned to a front
        var units = Native.PdxArray<long>(cmd + UnitsOfAssignCommand);
        var kept = 0;
        foreach (var unit in units)
            if (!Paratroopers.ContainsKey(unit) && !(assign && Reserved.ContainsKey(unit))) units[kept++] = unit;
        if (kept == units.Length) return true;
        Native.SetPdxArraySize(cmd + UnitsOfAssignCommand, kept);
        return kept > 0;
    }

    /// <summary>
    /// Every two game hours: each AI country runs at most one paradrop at a time, from an army on its breakthrough axis; new ones are
    /// planned every twelve game hours.
    /// </summary>
    private void PlanParadrops()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _dropAt, DropHours)) return; // menus, lobby, loading: no AI commands (and Sheep Mod is asked once a game runs)
        _game.Check(); // CRef ids of an old game must not resolve into this one
        var hour = Game.Hour;
        var player = Game.ControlledTag;
        var plan = Due(ref _planAt, PlanHours);
        try
        {
            foreach (var country in Game.Countries)
            {
                var tag = country == 0 ? 0 : Game.Tag(country);
                if (tag <= 0 || tag == player) continue;
                if (Drops.TryGetValue(tag, out var drop))
                {
                    if (!AdvanceDrop(country, tag, drop, hour)) EndDrop(tag, drop, hour);
                    continue;
                }
                ReserveParatroopers(country, tag); // every pass: a new or replaced front takes its army's free divisions again
                if (!plan || NextParadrop.TryGetValue(tag, out var next) && hour - next < 0 || Grads.ContainsKey(tag)) continue; // on its grad: no new drops behind the enemy
                NextParadrop[tag] = hour + RetryHours;
                StartDrop(country, tag, hour);
            }
        }
        finally { Volatile.Write(ref _activeDrops, Drops.Count); }
    }

    /// <summary>A live army or army group of the tag by its CRef id, or 0 (gone, or the id is another country's).</summary>
    private static nint GroupOf(long reference, int tag)
    {
        var group = Game.Resolve(reference);
        return group != 0 && *(int*)(group + TagOfGroup) == tag ? group : 0;
    }

    /// <summary>The CArmy of an order group member (COrdersGroupMember vtable slot 4), or 0.</summary>
    private static nint UnitOf(nint member) => member == 0 ? 0 : ((delegate* unmanaged<nint, nint>)(*(nint**)member)[UnitOfMemberSlot])(member);

    /// <summary>A division that can paradrop (its template's paratrooper flag).</summary>
    private static bool CanDrop(nint unit)
    {
        var stats = unit == 0 ? 0 : Division.Stats(unit);
        return stats != 0 && *(byte*)(stats + CanParadrop) != 0;
    }

    /// <summary>
    /// Paratroopers of the axis theatre's army with the most of them ready go to the airbase nearest the axis that can fly them
    /// (vanilla's own check).
    /// </summary>
    private static void StartDrop(nint country, int tag, int hour)
    {
        foreach (var (general, axis) in Axes)
        {
            if (axis.Tag != tag || hour - axis.Hour is < 0 or >= AxisFreshHours) continue;
            if (!AxisArmies.TryGetValue(general, out var armies) || armies.Tag != tag) continue;
            if (BestTarget(country, tag, axis.Core, 0) == 0)
            {
                Interlocked.Increment(ref _noTarget); // nothing worth a drop behind this axis today
                continue;
            }
            nint group = 0;
            List<nint> units = [];
            foreach (var reference in armies.Armies)
            {
                var g = GroupOf(reference, tag);
                if (g == 0 || new OrdersGroup(g).IsArmyGroup) continue;
                List<nint> ready = [];
                foreach (var member in Native.PdxArray(g + OrdersGroup.MembersOffset))
                    if (UnitOf(member) is var unit && unit != 0 && CanDrop(unit) && Spare(unit, tag)) ready.Add(unit);
                if (ready.Count > units.Count) (group, units) = (g, ready);
            }
            if (units.Count == 0)
            {
                Interlocked.Increment(ref _noParatroopers);
                continue;
            }
            if (units.Count > MaxParatroopers) units.RemoveRange(MaxParatroopers, units.Count - MaxParatroopers);
            var source = Airbase(tag, group, [.. axis.Line.Where(axis.Core.Contains)]); // the line around the axis: its targets must be in range
            if (source == 0)
            {
                Interlocked.Increment(ref _noAirbase); // no airbase in reach with enough transport planes
                continue;
            }
            var cmd = Allocator.AllocZeroed(SourceCommandSize);
            Native.WithPdxArray(units.ToArray(), list => _paradropSource(cmd, group, source, 0, list));
            if (!Commands.CanExecute(cmd)) // the allow_paratroopers rule is off (a new order checks only the rule and the group)
            {
                Commands.Free(cmd); // the deleting destructor: frees its unit array too
                Interlocked.Increment(ref _notReady);
                NextParadrop[tag] = hour + CooldownHours;
                return;
            }
            if (!Commands.Post(cmd, true)) return;
            Begin(tag, new Drop { General = general, Group = new OrdersGroup(group).Ref, Source = source, Hour = hour, Units = units.Select(Division.Ref).ToArray() });
            return;
        }
        StartBeachDrop(country, tag, hour);
    }

    /// <summary>
    /// The paratroopers of AI countries off their grad: kept off the fronts (taken off once, and never assigned again), so they
    /// stand ready for a drop instead of fighting as line infantry in vanilla's offensives, which kept every one of Germany's
    /// worn out. Without transport planes yet (Germany at the start of the war) they are the reserve FrontLine plugs and
    /// thickens the line with (unassigned divisions). AI threads read it.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> Reserved = new(); // paratrooper CRef -> tag

    private static void ReserveParatroopers(nint country, int tag)
    {
        var paratroopers = Game.Units(country).ToArray().Where(u => u != 0 && CanDrop(u)).ToList();
        if (Grads.ContainsKey(tag)) paratroopers.Clear(); // on its grad they hold the line too
        var now = paratroopers.Select(Division.Ref).ToHashSet();
        foreach (var unit in now) Reserved[unit] = tag; // added before the stale ones go: an AI thread never sees a gap
        foreach (var (unit, owner) in Reserved) if (owner == tag && !now.Contains(unit)) Reserved.TryRemove(unit, out _);
        Volatile.Write(ref _reservedCount, Reserved.Count);
        foreach (var unit in paratroopers)
        {
            if (Paratrooper(unit) || Division.Fighting(unit) || !Division.Ready(unit, tag) || Division.Group(unit) is not (var g and not 0)) continue;
            if (OnOrder(unit, g) && Commands.Unassign(unit, g, true)) Interlocked.Increment(ref _paratroopersReserved);
            // Off its order it stays where it stood: it waits at an airbase instead, where a drop can start from.
            else if (!Division.Walking(unit) && Division.Location(unit) is var at && at != 0 && DropBase(country, tag, at, g) is var wait and > 0
                && wait != Game.Id(at)) Commands.Move(unit, wait, true);
        }
    }

    private const int DropBaseSearch = 12; // provinces from the paratrooper: drops are flown from near the front

    /// <summary>
    /// Where a paratrooper off the fronts waits, as a province id: within DropBaseSearch of it and with no enemy next to it, a
    /// province vanilla accepts as a paradrop source (transports there), else the main province of the state with the best
    /// air base, else where it stands when that is safe, else a province behind the line (-1: none).
    /// </summary>
    private static int DropBase(nint country, int tag, nint from, nint group)
    {
        var seen = new HashSet<int> { Game.Id(from) };
        var ring = new List<nint> { from };
        var (bestState, bestLevel) = ((nint)0, 0);
        for (var depth = 0; depth <= DropBaseSearch && ring.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var p in ring)
            {
                if (Province.Controller(p) != tag) continue;
                if (!Province.NextToEnemy(country, tag, p))
                {
                    if (_isParadropSource(p, group) != 0) return Game.Id(p);
                    if (Province.State(p) is var s && s != 0)
                    {
                        var level = 0;
                        foreach (var type in Buildings.AirBaseTypes) level += Buildings.WorkingLevel(State.Buildings(s), type);
                        if (level > bestLevel) (bestState, bestLevel) = (s, level);
                    }
                }
                foreach (var id in Game.Neighbours(p))
                    if (seen.Add(id) && Game.Province(id) is var q && q != 0) next.Add(q);
            }
            ring = next;
        }
        if (bestState != 0 && State.MainProvince(bestState) is var main && main != 0 && Province.Controller(main) == tag && !Province.NextToEnemy(country, tag, main))
            return Province.State(from) == bestState ? Game.Id(from) : Game.Id(main);
        return Province.NextToEnemy(country, tag, from) ? RestProvince(country, tag, from) : Game.Id(from);
    }

    /// <summary>The unit is a member of one of its army's or army group's orders.</summary>
    private static bool OnOrder(nint unit, nint group)
    {
        var member = unit + MemberOfUnit;
        foreach (var owner in (ReadOnlySpan<nint>)[group, *(nint*)(group + OrdersGroup.ParentOffset)])
            if (owner != 0)
                foreach (var order in Native.PdxArray(owner + OrdersGroup.RootOrdersOffset))
                    if (order != 0 && Order.Members(order).Contains(member)) return true;
        return false;
    }

    private static void Begin(int tag, Drop drop)
    {
        Drops[tag] = drop;
        foreach (var u in drop.Units) Paratroopers[u] = tag;
    }

    /// <summary>
    /// No axis drop: behind a beachhead of ours (ground taken from the sea in the last LandedHours) instead, with the paratroopers
    /// of whichever army has the most ready, from the airbase nearest it on the map that vanilla accepts as their source (a
    /// beachhead is overseas: home airbases are no land walk away). Vanilla's range check sorts out targets too far for it.
    /// </summary>
    private static void StartBeachDrop(nint country, int tag, int hour)
    {
        var beach = new HashSet<int>();
        for (var id = 1; id < _landed.Length; id++)
            if (Landed(id, hour) && _controllers[id] == tag) beach.Add(id);
        if (beach.Count == 0 || BestTarget(country, tag, beach, 0) == 0) return;
        nint group = 0;
        List<nint> units = [];
        foreach (var g in Game.Units(country).ToArray().Where(u => u != 0 && CanDrop(u) && Spare(u, tag)).GroupBy(Division.Group))
            if (g.Key != 0 && !new OrdersGroup(g.Key).IsArmyGroup && g.Count() > units.Count) (group, units) = (g.Key, g.ToList());
        if (units.Count == 0)
        {
            Interlocked.Increment(ref _noParatroopers);
            return;
        }
        if (units.Count > MaxParatroopers) units.RemoveRange(MaxParatroopers, units.Count - MaxParatroopers);
        var (bx, by) = beach.Select(id => MapArrows.Position(Game.Province(id))).Aggregate((0L, 0L), (s, p) => (s.Item1 + p.X, s.Item2 + p.Y));
        (bx, by) = (bx / beach.Count, by / beach.Count);
        var (source, best) = (0, long.MaxValue);
        var states = new HashSet<nint>();
        for (var id = 1; id <= _provinceCount; id++)
        {
            var p = Game.Province(id);
            var state = p == 0 || Province.Controller(p) != tag ? 0 : Province.State(p);
            if (state == 0 || !states.Add(state)) continue; // one airbase a state: its main province's
            var main = State.MainProvince(state);
            if (main == 0 || Province.Controller(main) != tag || _isParadropSource(main, group) == 0) continue;
            var (x, y) = MapArrows.Position(main);
            var d = (x - bx) * (x - bx) + (y - by) * (y - by);
            if (d < best) (source, best) = (Game.Id(main), d);
        }
        if (source == 0)
        {
            Interlocked.Increment(ref _noAirbase);
            return;
        }
        var cmd = Allocator.AllocZeroed(SourceCommandSize);
        Native.WithPdxArray(units.ToArray(), list => _paradropSource(cmd, group, source, 0, list));
        if (!Commands.CanExecute(cmd))
        {
            Commands.Free(cmd);
            Interlocked.Increment(ref _notReady);
            NextParadrop[tag] = hour + CooldownHours;
            return;
        }
        if (!Commands.Post(cmd, true)) return;
        Begin(tag, new Drop { Core = beach, Group = new OrdersGroup(group).Ref, Source = source, Hour = hour, Units = units.Select(Division.Ref).ToArray() });
        Interlocked.Increment(ref _beachDrops);
    }

    /// <summary>One step of a drop in progress. False: it is over (dropped, deleted, emptied, or given up).</summary>
    private static bool AdvanceDrop(nint country, int tag, Drop drop, int hour)
    {
        var group = GroupOf(drop.Group, tag);
        if (group == 0) return false;
        if (drop.Order == 0) // the source command has run by now: find the order it made
        {
            foreach (var o in Native.PdxArray(group + OrdersGroup.RootOrdersOffset))
                if (o != 0 && Order.Type(o) == Order.Paradrop && *(int*)(o + SourceOfOrder) == drop.Source && !ParadropOrders.ContainsKey((drop.Group, Order.Id(o))))
                {
                    drop.Order = Order.Id(o);
                    ParadropOrders[(drop.Group, drop.Order)] = tag;
                    break;
                }
            return drop.Order != 0 || hour - drop.Hour < FindOrderHours;
        }
        var order = _findOrder(group, drop.Order);
        if (order == 0) return false;
        // A standalone paradrop order is not deleted by vanilla when it empties (it deletes only ones tied to a front order):
        // delete it ourselves, and after a launch release whoever is left to the front AI.
        if (Order.Members(order).Length == 0 || drop.Launched >= 0 && hour - drop.Launched >= LaunchHours || hour - drop.Hour >= GiveUpHours)
        {
            PostOrderCommand(Signatures.DeleteOrderCommand, drop.Group, drop.Order, 0);
            return false;
        }
        var executing = Order.Executing(order);
        if (executing && drop.Launched >= 0 && hour > drop.Launched) return true; // jumped: its target fills with our paratroopers, vanilla finishes it
        var target = drop.Target == 0 ? 0 : Game.Province(drop.Target);
        if (target == 0 || !DropTarget(country, tag, target) || _isParadropTarget(order, target) == 0)
        {
            // The axis it was planned for (another theatre's targets are out of its planes' range).
            var core = drop.Core ?? (Axes.TryGetValue(drop.General, out var axis) && axis.Tag == tag && hour - axis.Hour is >= 0 and < AxisFreshHours ? axis.Core : null);
            var next = core == null ? 0 : BestTarget(country, tag, core, order);
            if (next != 0 && PostOrderCommand(Signatures.ParadropTargetCommand, drop.Group, drop.Order, next)) drop.Target = next; // vanilla re-checks the route's air on it
            else if (executing) PostOrderCommand(Commands.ExecuteOrder, drop.Group, drop.Order, 0); // nothing safe to land on: hold
            return true;
        }
        if (executing) return true; // vanilla launches on its next hourly update
        // Vanilla's check counts only the paratroopers standing at the source: with nobody there it passes, and nobody jumps.
        if (!AtSource(order, drop.Source, tag)) return true;
        if (_orderReady(order, 0) == 0) Interlocked.Increment(ref _notReady); // not prepared, too few planes, or no air superiority on the route
        else if (PostOrderCommand(Commands.ExecuteOrder, drop.Group, drop.Order, 1))
        {
            drop.Launched = hour;
            Interlocked.Increment(ref _paradropped);
        }
        // ponytail: gives up after two weeks without superiority; retarget to a route with air cover if that wastes too many.
        return true;
    }

    /// <summary>
    /// Whether a paratrooper of the order stands at its source airbase; idle ones elsewhere are walked there (a plain move,
    /// as the UI sends it) in case vanilla doesn't move a paradrop order's divisions itself.
    /// </summary>
    private static bool AtSource(nint order, int source, int tag)
    {
        var there = false;
        foreach (var member in Order.Members(order))
        {
            var unit = UnitOf(member);
            var location = unit == 0 ? 0 : Division.Location(unit);
            if (location == 0 || Division.Commander(unit) != tag || !CanDrop(unit)) continue;
            if (Game.Id(location) == source) there = true;
            else if (!Division.Walking(unit) && !Division.Fighting(unit) && !Division.Retreating(unit) && !Division.Busy(unit))
                Commands.Move(unit, source, true);
        }
        return there;
    }

    private static void EndDrop(int tag, Drop drop, int hour)
    {
        foreach (var u in drop.Units) Paratroopers.TryRemove(u, out _);
        ParadropOrders.TryRemove((drop.Group, drop.Order), out _);
        Drops.Remove(tag);
        NextParadrop[tag] = hour + CooldownHours;
    }

    /// <summary>An order command posted as the AI posts: Execute (on), Delete, SetParadropTarget (province).</summary>
    private static bool PostOrderCommand(string className, long group, int order, int argument) =>
        Commands.TryPost(Commands.NewOrderCommand(className, group, order, argument), true);

    /// <summary>
    /// The best drop target behind the enemy line around the axis (its core and one more ring): worth taking or cutting off
    /// a pocket, lightly held; with an order, also in its planes' range.
    /// </summary>
    // ponytail: axis surroundings only; supply hubs or ports deep in the enemy rear are never looked at.
    private static int BestTarget(nint country, int tag, HashSet<int> core, nint order)
    {
        var area = new HashSet<int>(core);
        foreach (var id in core)
            if (Game.Province(id) is var p && p != 0) area.UnionWith(Game.Neighbours(p));
        var (best, bestScore) = (0, 0.0);
        foreach (var id in area)
        {
            var p = Game.Province(id);
            if (p == 0 || !DropTarget(country, tag, p) || order != 0 && _isParadropTarget(order, p) == 0) continue;
            var score = TargetValue(p) + PocketBonus * Province.Pocket(country, tag, p);
            if (score > bestScore) (best, bestScore) = (id, score);
        }
        return best;
    }

    /// <summary>An empty enemy province behind its line (no neighbour of ours or our allies'), with few enemy divisions next to it.</summary>
    private static bool DropTarget(nint country, int tag, nint p)
    {
        if (!Province.Enemy(country, tag, p) || Province.Divisions(p) > ParadropMaxDefenders) return false; // sea: never an enemy's
        var nearby = 0;
        foreach (var n in Game.Neighbours(p))
        {
            var q = Game.Province(n);
            if (q == 0 || Province.Controller(q) == 0) continue; // sea
            if (Province.Friendly(country, tag, q)) return false; // on the line: a local attack's job
            if (Province.Enemy(country, tag, q)) nearby += Province.Divisions(q);
        }
        return nearby <= NearbyDefendersMax;
    }

    /// <summary>Our province nearest the line (breadth first over our land) that vanilla accepts as this army's paradrop source.</summary>
    private static int Airbase(int tag, nint group, HashSet<int> line)
    {
        var seen = new HashSet<int>(line);
        var ring = new List<int>(line);
        for (var depth = 0; depth < SourceSearchDepth && ring.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var id in ring)
            {
                var p = Game.Province(id);
                if (p == 0 || Province.Controller(p) != tag) continue;
                if (_isParadropSource(p, group) != 0) return id;
                foreach (var n in Game.Neighbours(p)) if (seen.Add(n)) next.Add(n);
            }
            ring = next;
        }
        return 0;
    }
}
