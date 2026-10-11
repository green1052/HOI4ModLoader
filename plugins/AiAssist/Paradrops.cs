using System.Collections.Concurrent;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Strategic paradrops: the vanilla AI never makes a paradrop order (only the map UI posts the source command) ----

    private const int RefOfGroup = 0x08, TagOfGroup = 0x3C, OrdersOfGroup = 0x98;            // COrdersGroup: CRef id; owner tag; CPdxArray<COrder*> root orders
    private const int RefOfUnit = 0x18, CanParadrop = 0x5B5, UnitOfMemberSlot = 4;           // CArmy: CRef id; its stats' can-paradrop flag; COrdersGroupMember vtable: -> CArmy*
    private const int SourceOfOrder = 0xB8, UnitsOfOrder = 0x210, IdOfOrder = 0x244;         // COrder: source province, CPdxArray<COrdersGroupMember*>, id
    private const int ArgOfOrderCommand = 0x34;                                               // after GroupOfCommand/OrderOfCommand: Execute on (byte), SetParadropTarget province (int)
    private const int UnitsOfAssign = 0x28;                                                   // COrderAssign/UnassignCommand: CPdxArray<CRef id>
    private const int OrderCommandSize = 0x38, SourceCommandSize = 0x50;
    private const int MaxParatroopers = 4, ParadropMaxDefenders = 0, NearbyDefendersMax = 4, PocketBonus = 15, SourceSearchDepth = 12;
    private const int FindOrderHours = 3, GiveUpHours = 14 * 24, LaunchHours = 48, RetryHours = 24, CooldownHours = 7 * 24;

    private static bool _paradrops;
    private static int _paradropped, _activeDrops; // _activeDrops: Drops.Count for AI threads (ConcurrentDictionary.IsEmpty takes every lock)
    private static nint _deleteVTable, _assignVTable, _unassignVTable;
    private static delegate* unmanaged<nint, nint, int, int, nint, nint> _paradropSource; // (mem, group, source province, order id 0, CPdxArray<CArmy*>*)
    private static delegate* unmanaged<nint, nint, byte> _isParadropSource;               // (province, group)
    private static delegate* unmanaged<nint, nint, byte> _isParadropTarget;               // (order, province)
    private static delegate* unmanaged<nint, nint, byte> _orderReady;                     // (order, std::string* reason)

    private sealed class Drop
    {
        public nint General; // the CAIGeneral whose axis it backs: an Axes key, never dereferenced
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
        MainThread.Every(TimeSpan.FromSeconds(30), PlanParadrops);
    }

    private static void ClearParadrops()
    {
        Drops.Clear();
        NextParadrop.Clear();
        Paratroopers.Clear();
        ParadropOrders.Clear();
        Volatile.Write(ref _activeDrops, 0);
    }

    /// <summary>A division on one of our drops: the front AI and our own movers leave it alone. AI threads too.</summary>
    private static bool Paratrooper(nint unit) => Volatile.Read(ref _activeDrops) != 0 && Paratroopers.ContainsKey(*(long*)(unit + RefOfUnit));

    /// <summary>
    /// AI order commands that would undo one of our drops: its order deleted, its divisions assigned back to the front.
    /// False: drop the command. AI threads: reads the command and the CRef sets only; strips our divisions in place.
    /// </summary>
    private static bool KeepsParadrops(nint cmd)
    {
        if (Volatile.Read(ref _activeDrops) == 0) return true;
        var vtable = *(nint*)cmd;
        if (vtable == _deleteVTable) return !ParadropOrders.ContainsKey((*(long*)(cmd + GroupOfCommand), *(int*)(cmd + OrderOfCommand)));
        if (vtable != _assignVTable && vtable != _unassignVTable) return true;
        var units = Native.PdxArray<long>(cmd + UnitsOfAssign);
        var kept = 0;
        foreach (var unit in units)
            if (!Paratroopers.ContainsKey(unit)) units[kept++] = unit;
        if (kept == units.Length) return true;
        Native.SetPdxArraySize(cmd + UnitsOfAssign, kept);
        return kept > 0;
    }

    /// <summary>Every half minute: each AI country runs at most one paradrop at a time, from an army on its breakthrough axis.</summary>
    private void PlanParadrops()
    {
        if (!Game.HasStarted || !SheepMod.Active) return; // menus, lobby, loading: no AI commands (and Sheep Mod is asked once a game runs)
        _game.Check(); // CRef ids of an old game must not resolve into this one
        var hour = Hour;
        var player = Game.ControlledTag;
        try
        {
            foreach (var country in Game.Countries)
            {
                var tag = country == 0 ? 0 : *(int*)(country + 8);
                if (tag <= 0 || tag == player) continue;
                if (Drops.TryGetValue(tag, out var drop))
                {
                    if (!AdvanceDrop(country, tag, drop, hour)) EndDrop(tag, drop, hour);
                    continue;
                }
                if (NextParadrop.TryGetValue(tag, out var next) && hour - next < 0 || Grads.ContainsKey(tag)) continue; // on its grad: no new drops behind the enemy
                NextParadrop[tag] = hour + RetryHours;
                StartDrop(country, tag, hour);
            }
        }
        finally { Volatile.Write(ref _activeDrops, Drops.Count); }
    }

    /// <summary>A live army or army group of the tag by its CRef id, or 0 (gone, or the id is another country's).</summary>
    private static nint GroupOf(long reference, int tag)
    {
        var group = _resolve((nint)(&reference));
        return group != 0 && *(int*)(group + TagOfGroup) == tag ? group : 0;
    }

    /// <summary>The CArmy of an order group member (COrdersGroupMember vtable slot 4), or 0.</summary>
    private static nint UnitOf(nint member) => member == 0 ? 0 : ((delegate* unmanaged<nint, nint>)(*(nint**)member)[UnitOfMemberSlot])(member);

    /// <summary>A division that can paradrop (its template's paratrooper flag).</summary>
    private static bool CanDrop(nint unit)
    {
        var stats = unit == 0 ? 0 : *(nint*)(unit + StatsOfUnit);
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
            if (axis.Tag != tag || hour - axis.Hour is < 0 or >= AxisHours) continue;
            if (!AxisArmies.TryGetValue(general, out var armies) || armies.Tag != tag) continue;
            if (BestTarget(country, tag, axis.Core, 0) == 0) continue; // nothing worth a drop behind this axis today
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
            if (units.Count == 0) continue;
            if (units.Count > MaxParatroopers) units.RemoveRange(MaxParatroopers, units.Count - MaxParatroopers);
            var source = Airbase(tag, group, [.. axis.Line.Where(axis.Core.Contains)]); // the line around the axis: its targets must be in range
            if (source == 0) continue;
            var cmd = Allocator.AllocZeroed(SourceCommandSize);
            Native.WithPdxArray(units.ToArray(), list => _paradropSource(cmd, group, source, 0, list));
            if (!Commands.CanExecute(cmd)) // the allow_paratroopers rule is off (a new order checks only the rule and the group)
            {
                Commands.Free(cmd); // the deleting destructor: frees its unit array too
                NextParadrop[tag] = hour + CooldownHours;
                return;
            }
            if (!Commands.Post(cmd, true)) return;
            var drop = new Drop { General = general, Group = *(long*)(group + RefOfGroup), Source = source, Hour = hour, Units = units.Select(u => *(long*)(u + RefOfUnit)).ToArray() };
            Drops[tag] = drop;
            foreach (var u in drop.Units) Paratroopers[u] = tag;
            return;
        }
    }

    /// <summary>One step of a drop in progress. False: it is over (dropped, deleted, emptied, or given up).</summary>
    private static bool AdvanceDrop(nint country, int tag, Drop drop, int hour)
    {
        var group = GroupOf(drop.Group, tag);
        if (group == 0) return false;
        if (drop.Order == 0) // the source command has run by now: find the order it made
        {
            foreach (var o in Native.PdxArray(group + OrdersOfGroup))
                if (o != 0 && *(int*)(o + TypeOfOrder) == Paradrop && *(int*)(o + SourceOfOrder) == drop.Source && !ParadropOrders.ContainsKey((drop.Group, *(int*)(o + IdOfOrder))))
                {
                    drop.Order = *(int*)(o + IdOfOrder);
                    ParadropOrders[(drop.Group, drop.Order)] = tag;
                    break;
                }
            return drop.Order != 0 || hour - drop.Hour < FindOrderHours;
        }
        var order = _findOrder(group, drop.Order);
        if (order == 0) return false;
        // A standalone paradrop order is not deleted by vanilla when it empties (it deletes only ones tied to a front order):
        // delete it ourselves, and after a launch release whoever is left to the front AI.
        if (*(int*)(order + UnitsOfOrder + 0xC) == 0 || drop.Launched >= 0 && hour - drop.Launched >= LaunchHours || hour - drop.Hour >= GiveUpHours)
        {
            PostOrderCommand(Signatures.DeleteOrderCommand, drop.Group, drop.Order, 0);
            return false;
        }
        var executing = *(byte*)(order + Executing) != 0;
        if (executing && drop.Launched >= 0 && hour > drop.Launched) return true; // jumped: its target fills with our paratroopers, vanilla finishes it
        var target = drop.Target == 0 ? 0 : Game.Province(drop.Target);
        if (target == 0 || !DropTarget(country, tag, target) || _isParadropTarget(order, target) == 0)
        {
            // The axis it was planned for (another theatre's targets are out of its planes' range).
            var core = Axes.TryGetValue(drop.General, out var axis) && axis.Tag == tag && hour - axis.Hour is >= 0 and < AxisHours ? axis.Core : null;
            var next = core == null ? 0 : BestTarget(country, tag, core, order);
            if (next != 0 && PostOrderCommand(Signatures.ParadropTargetCommand, drop.Group, drop.Order, next)) drop.Target = next; // vanilla re-checks the route's air on it
            else if (executing) PostOrderCommand(Signatures.ExecuteCommand, drop.Group, drop.Order, 0); // nothing safe to land on: hold
            return true;
        }
        if (executing) return true; // vanilla launches on its next hourly update
        // Vanilla's check counts only the paratroopers standing at the source: with nobody there it passes, and nobody jumps.
        if (!AtSource(order, drop.Source, tag)) return true;
        if (_orderReady(order, 0) != 0 && PostOrderCommand(Signatures.ExecuteCommand, drop.Group, drop.Order, 1)) // prepared, planes, route air superiority
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
        foreach (var member in Native.PdxArray(order + UnitsOfOrder))
        {
            var unit = UnitOf(member);
            var location = unit == 0 ? 0 : *(nint*)(unit + LocationOfUnit);
            if (location == 0 || *(int*)(unit + CommanderOfUnit) != tag || !CanDrop(unit)) continue;
            if (Game.Id(location) == source) there = true;
            else if (*(int*)(unit + Path + 0xC) == 0 && *(int*)(unit + CombatsOfUnit) == 0 && *(byte*)(unit + Retreating) == 0 && *(nint*)(unit + Busy) == 0)
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

    /// <summary>A 0x38-byte order command {group CRef @0x28, order id @0x30, argument @0x34}: Execute (on), Delete, SetParadropTarget (province).</summary>
    private static bool PostOrderCommand(string className, long group, int order, int argument)
    {
        var cmd = Commands.New(className, OrderCommandSize, 0, 0);
        *(long*)(cmd + GroupOfCommand) = group;
        *(int*)(cmd + OrderOfCommand) = order;
        *(int*)(cmd + ArgOfOrderCommand) = argument;
        return Commands.TryPost(cmd, true);
    }

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
            var score = TargetValue(p) + PocketBonus * Pocket(country, tag, p);
            if (score > bestScore) (best, bestScore) = (id, score);
        }
        return best;
    }

    /// <summary>An empty enemy province behind its line (no neighbour of ours or our allies'), with few enemy divisions next to it.</summary>
    private static bool DropTarget(nint country, int tag, nint p)
    {
        if (!Enemy(country, tag, p) || Divisions(p) > ParadropMaxDefenders) return false; // sea: never an enemy's
        var nearby = 0;
        foreach (var n in Game.Neighbours(p))
        {
            var q = Game.Province(n);
            if (q == 0 || *(int*)(q + ControllerOfProvince) == 0) continue; // sea
            if (Friendly(country, tag, q)) return false;                  // on the line: a local attack's job
            if (Enemy(country, tag, q)) nearby += Divisions(q);
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
                if (p == 0 || *(int*)(p + ControllerOfProvince) != tag) continue;
                if (_isParadropSource(p, group) != 0) return id;
                foreach (var n in Game.Neighbours(p)) if (seen.Add(n)) next.Add(n);
            }
            ring = next;
        }
        return 0;
    }
}
