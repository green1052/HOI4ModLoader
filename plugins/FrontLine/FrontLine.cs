using HOI4ModLoader;

namespace FrontLine;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{

    [VTable] public const string LandCombat = "CLandCombat";
}

/// <summary>
/// Front line micro a player does by hand, with the same commands the battle plan UI sends (multiplayer works):
/// - Rotation: a worn-out division on a front line (low organisation or strength, not fighting, enemy next to it) comes off
///   the battle plan and rests a few provinces back, when another of our divisions on that front holds the same province
///   (so no province is left empty) and few enough of that front are resting.
/// - Plugging: a defence of ours that is being lost gets the nearest fresh division (resting, unassigned, or from a quiet
///   part of a front where another of ours stays) sent into the battle, where it joins the defence.
/// - Escape: a front division in a salient whose way back narrows to a neck of one or two provinces next to the enemy
///   pulls back through it, before the pocket closes.
/// - Attack: fresh divisions take empty enemy provinces next to the line and attack enemy divisions already surrounded with
///   at least as many of them (more behind forts), best targets first: pocket closing, then victory points, supply hubs,
///   airfields and ports, then front shortening; none their supply can't follow or that lengthen the front for nothing.
/// - Fire brigade: a few armored or motorized divisions per front wait a province behind the line and counterattack where
///   the enemy breaks in (ground lost in the last two days, a landing, enemy troops in our provinces), then go back to their post.
/// - Depth defence: key line provinces (ports, victory points, river crossings, cities, mountains) get a second division a
///   province behind them while the line holds there.
/// - Shuffle: spare divisions of quiet fronts reinforce a losing front until a day after its crisis; fresh divisions near
///   the crisis pin idle enemy stacks next to it with cheap attacks so they can't be moved in.
/// - Ctrl+Alt+E activates every battle plan, Ctrl+Alt+H halts them (the plan window's own play button, for every group).
/// - Ctrl+Alt+B / Ctrl+Alt+P: a blitz or a pincer with the selected armies (Operations.cs).
/// Divisions taken off a front go back onto it once their job is done (rested, crisis over, post given up); one short of
/// equipment or manpower goes back after MaxRestHours once its organisation is back. A reload forgets them: they stay where they are.
/// </summary>
[Plugin("com.green1052.frontline", "Front Line", "1.0.0", GameVersion = "1.19.3")]
public unsafe partial class FrontLine : Plugin
{
    // Layouts for HOI4 1.19.3 (the rest: the loader's Division, Province, Order, OrdersGroup).
    private const int MemberOfUnit = 0xB8; // CArmy: its COrdersGroupMember
    private const int BorderWar = 0x1B0;   // COrdersGroup
    private static readonly int[] OrderLists = [OrdersGroup.RootOrdersOffset, 0xB0, 0xC8]; // root (fronts, offensive lines), fallback lines, invasions/paradrops
    // Land combat: every combat at gamestate +0x268; CLandCombat sides and their divisions.
    private const int CombatsOfGameState = 0x268, DefenderOfCombat = 0x30, ProvinceOfCombat = 0x38, ProgressSlot = 25;
    private const int DivisionsOfSide = 0x20, ReservesOfSide = 0x100;
    private const int MaxRestSearch = 6, MaxPlugsPerTurn = 2, MaxEscapesPerTurn = 2, MaxAttacksPerTurn = 2;
    private const int RestDepth = 2;          // provinces behind the line a division rests (none of its neighbours enemy)
    private const int MaxSwapsPerArmy = 2;    // sent to rest at a time, so a front never changes all at once
    private const int MaxRestHours = 14 * 24; // short of equipment or manpower, strength may never come back: organisation is enough then
    private const long PendingMs = 30_000;    // a unit we gave orders to is left alone meanwhile (multiplayer: a round trip)

    private static readonly int[] PlanKeys = ['E', 'H']; // with Ctrl+Alt held: activate, halt
    private readonly bool[] _down = new bool[PlanKeys.Length];


    /// <summary>
    /// What a division we moved does for us. Rest: rests (rotation, escape, plugging) and goes back once rested. Operation:
    /// a blitz's or pincer's (Operations.cs). Done: its operation is over; it goes back as soon as it can take orders.
    /// </summary>
    private enum Job { Rest, Reserve, Counter, Depth, Shuffle, Pin, Operation, Done }

    /// <summary>
    /// A division we moved. Group/Order: the order it goes back onto (0: none). Post: the province it holds (Reserve, Counter,
    /// Depth, Shuffle) or attacks from (Pin). Target: what it attacks (Counter, Pin) or backs (Depth). Front: the front it serves.
    /// </summary>
    private readonly record struct Held(Job Job, nint Group, int Order, int Since, int Post, int Target, (nint, int) Front);

    private readonly Dictionary<nint, long> _pending = [];                     // unit -> until
    private readonly Dictionary<nint, Held> _held = [];                       // every division we took for a job: one bookkeeping, so no feature takes another's
    private readonly Dictionary<int, (nint[] Units, bool Attack)> _sent = []; // province we sent divisions into (a battle to help, an attack) -> them
    // Rebuilt every turn: who is on which order, how many of a front stand in a province, our divisions, memos.
    private readonly Dictionary<nint, nint> _orders = [];                     // member -> order (Manage's turns only)
    private readonly Dictionary<(nint Front, nint Province), int> _holding = [];
    private readonly HashSet<nint> _alive = [];                               // our land divisions
    private readonly Dictionary<nint, List<nint>> _at = [];                   // province -> our land divisions there (that we command)
    private readonly Dictionary<int, bool> _war = [], _allies = [];
    private readonly Dictionary<nint, bool> _edge = [];                       // province -> next to enemy ground
    private Dictionary<nint, double> _losingNow = [];                          // our defences being lost -> attacker's share (%)
    private long _now;
    private PerGame _game = null!;
    private bool _rotation, _plugging, _escape, _attack, _fireBrigade, _depth, _shuffle;
    private double _wornOrg, _wornStrength, _returnOrg, _returnStrength, _losing;
    private int _maxResting, _plugRadius;

    public override void Load()
    {
        _rotation = Config.Bind("Rotation", "Rotation", true,
            "Worn-out divisions in contact with the enemy rest behind the line and come back rested, where another of ours holds their province.");
        _wornOrg = Math.Clamp(Config.Bind("Rotation", "WornOrganisation", 30, "A front line division below this organisation (%) goes to rest."), 0, 100);
        _wornStrength = Math.Clamp(Config.Bind("Rotation", "WornStrength", 50, "Or below this strength (%: equipment and manpower)."), 0, 100);
        _returnOrg = Math.Clamp(Config.Bind("Rotation", "ReturnOrganisation", 90, "A resting division goes back to its front at this organisation (%)."), 0, 100);
        _returnStrength = Math.Clamp(Config.Bind("Rotation", "ReturnStrength", 80,
            "And this strength (%); after two weeks resting, organisation alone is enough (equipment or manpower may be short)."), 0, 100);
        _maxResting = Math.Clamp(Config.Bind("Rotation", "MaxRestingPercent", 25, "At most this share (%) of a front's divisions rests at once."), 1, 100);
        _plugging = Config.Bind("Plugging", "Plugging", true,
            "Send the nearest fresh division into a defence of ours that is being lost; it joins the battle and goes back to its front afterwards.");
        _losing = Math.Clamp(Config.Bind("Plugging", "LosingPercent", 60, "A defence counts as being lost when the attacker holds this share (%) of the battle's organisation (the battle bar)."), 50, 100);
        _plugRadius = Math.Clamp(Config.Bind("Plugging", "PlugRadius", 4, "How far (provinces) a helper may come from."), 1, MaxRestSearch);
        _escape = Config.Bind("Escape", "Escape", true,
            "Pull front divisions back out of a salient whose way back narrows to one or two provinces next to the enemy, through that neck while it is still open.");
        _attack = Config.Bind("Attack", "Attack", true,
            "Take empty enemy provinces next to the line with a fresh division, and attack enemy divisions already surrounded with at least as many fresh ones (more behind forts); a province of ours always keeps one. "
            + "Best targets first: closing a pocket, then victory points, supply hubs, airfields and ports, then shortening the front; none our supply can't follow (unless it closes a pocket) or that lengthen the front for nothing.");
        _fireBrigade = Config.Bind("FireBrigade", "FireBrigade", true,
            "Keep a few armored or motorized divisions per front a province behind the line, off its battle plan. Where the enemy breaks in (a province of ours lost in the last two days, a landing, enemy troops in our provinces), "
            + "the nearest ones counterattack with enough to win (forts counted, each counting double), then go back to their post.");
        _depth = Config.Bind("DepthDefense", "DepthDefense", true,
            "Key line provinces (ports, victory points, river crossings, cities and mountains) get a second division a province behind them, from spare divisions of a front; it stays while the line holds there.");
        _shuffle = Config.Bind("Shuffle", "Shuffle", true,
            "When a front is losing (three provinces lost in two days or defences being lost, or facing twice its divisions), spare fresh divisions of quiet fronts reinforce its most pressed provinces and go back to their own front a day after the crisis. "
            + "Meanwhile fresh divisions next to the crisis attack idle enemy stacks of one or two divisions (one attacker each, while its organisation holds) so they are locked in combat and can't be moved in.");
        if (Config.Bind("Keys", "BattlePlanKeys", true, "Ctrl+Alt+E activates every battle plan at once, Ctrl+Alt+H halts them."))
            MainThread.Every(TimeSpan.FromMilliseconds(30), PollKeys);
        var operations = BindOperations();
        var micro = _rotation || _plugging || _escape || _attack || _fireBrigade || _depth || _shuffle;
        if (!micro && !operations) return;

        _ = Sig.Find(Commands.AssignSig);
        // The core's game functions: a broken pattern fails the load, not a turn.
        _ = Sig.Find(Commands.UnassignSig) + Sig.Find(Game.IsAtWarWithSig) + Sig.Find(Game.SameOrAlliedSig) + Sig.Find(Province.FortLevelSig)
            + Sig.Find(Buildings.FindSig) + Sig.Find(Buildings.DbCallSig) + Sig.VTable(Signatures.LandCombat);
        _game = new PerGame(() => // one reset for the operations too: they share _held
        {
            _pending.Clear();
            _held.Clear();
            _sent.Clear();
            _orders.Clear();
            _lastSeen.Clear();
            _crisis.Clear();
            _operations.Clear();
            _opOrdered.Clear();
            _opLastHour = -1;
        });
        if (operations) LoadOperations();
        if (micro) MainThread.Every(TimeSpan.FromSeconds(2), Manage);
    }

    private void PollKeys()
    {
        var hit = Pressed(PlanKeys, _down);
        if (hit < 0 || !Game.HasStarted) return;
        var tag = Game.ControlledTag;
        if (tag != 0) Execute(tag, hit == 0); // 0: observer
    }

    /// <summary>
    /// The index of the key of <paramref name="keys"/> just pressed with Ctrl+Alt held while the game has focus, or -1:
    /// once per press (a key held across a modifier change does not fire). <paramref name="down"/>: each key's last state.
    /// </summary>
    private static int Pressed(int[] keys, bool[] down)
    {
        var hit = -1;
        for (var i = 0; i < keys.Length; i++)
            if (Gui.HotkeyPressed(keys[i], ref down[i])) hit = i;
        return hit;
    }

    /// <summary>Every army and army group of <paramref name="tag"/> whose plan runs counter to <paramref name="activate"/> gets the play (or stop) button's command.</summary>
    private void Execute(int tag, bool activate)
    {
        var country = Game.Country(tag);
        if (country == 0) return;
        var changed = 0;
        foreach (var group in OrdersGroup.OfCountry(country))
        {
            if (!OrderLists.Any(list => Native.PdxArray(group + list).ToArray().Any(o => o != 0 && Order.Executing(o) != activate))) continue;
            var cmd = Commands.NewOrderCommand(Commands.ExecuteOrder, new OrdersGroup(group).Ref, 0, activate ? 1 : 0); // order id 0: the whole group
            *(byte*)(cmd + Commands.AlsoArmiesOfOrderCommand) = 1; // an army group's command also reaches its armies
            if (Commands.TryPost(cmd)) changed++;
        }
        Log.Info($"{(activate ? "activated" : "halted")} {changed} battle plan(s)");
    }

    /// <summary>
    /// The start of every timer's turn: per-game state reset after a load or a country change; outside a running game 0.
    /// Else our country, its land divisions (<paramref name="units"/>, <see cref="_alive"/>, <see cref="_at"/>); divisions
    /// gone since (died, merged) are forgotten before anything dereferences them, and the memos of earlier turns.
    /// </summary>
    private nint Begin(out int tag, out nint[] units)
    {
        _game.Check();
        tag = Game.ControlledTag;
        var country = Game.Country(tag);
        units = [];
        if (country == 0 || !Game.HasStarted) return 0;
        _now = Environment.TickCount64;
        units = Game.Units(country).ToArray().Where(u => u != 0 && Division.IsLand(u)).ToArray();
        _alive.Clear();
        _alive.UnionWith(units);
        foreach (var (unit, until) in _pending.ToArray())
            if (_now >= until) _pending.Remove(unit);
        foreach (var unit in _held.Keys.Where(u => !_alive.Contains(u)).ToList()) _held.Remove(unit);
        foreach (var op in _operations) Forget(op);
        _war.Clear();
        _allies.Clear();
        _edge.Clear();
        _at.Clear();
        foreach (var unit in units)
        {
            var location = Division.Location(unit);
            if (location == 0 || Division.Commander(unit) != tag) continue;
            if (!_at.TryGetValue(location, out var here)) _at[location] = here = [];
            here.Add(unit);
        }
        return country;
    }

    private void Manage()
    {
        var country = Begin(out var tag, out var units);
        if (country == 0) return;
        foreach (var (id, sent) in _sent.ToArray()) // done once none of them is on the way (or, attacking, fighting) any more
            if (!sent.Units.Any(u => _alive.Contains(u) && (_pending.ContainsKey(u) || Division.Walking(u) || sent.Attack && Division.Fighting(u))))
                _sent.Remove(id);

        // Every order a division is on, of its army and of its army group (a field marshal's front holds his armies' divisions).
        _orders.Clear();
        var fronts = new Dictionary<nint, Front>(); // front order -> its divisions and line
        var groups = units.Select(Division.Group).Where(g => g != 0 && !new OrdersGroup(g).IsArmyGroup && *(nint*)(g + BorderWar) == 0).Distinct().ToList();
        foreach (var owner in groups.Concat(groups.Select(g => *(nint*)(g + OrdersGroup.ParentOffset)).Where(p => p != 0)).Distinct())
        foreach (var list in OrderLists)
        foreach (var order in Native.PdxArray(owner + list))
        {
            if (order == 0) continue;
            foreach (var member in Order.Members(order)) _orders[member] = order;
            if (IsFront(order)) fronts.TryAdd(order, new Front(order, (owner, Order.Id(order))));
        }
        _holding.Clear();
        foreach (var unit in units)
        {
            var location = Division.Location(unit);
            if (location == 0 || !_orders.TryGetValue(unit + MemberOfUnit, out var order) || !fronts.TryGetValue(order, out var front)) continue;
            front.Units.Add(unit);
            _holding[(order, location)] = _holding.GetValueOrDefault((order, location)) + 1;
        }
        _losingNow = _plugging || _shuffle ? LosingDefences(tag) : [];
        if (_fireBrigade || _depth || _shuffle) Survey(country, tag, fronts.Values);

        Return(tag);
        if (_rotation) foreach (var group in groups) RotateArmy(country, tag, group);
        if (_plugging) Plug(country, tag, units);
        if (_fireBrigade) Counterattack(country, tag, fronts.Values);
        if (_escape) Escape(country, tag, units);
        if (_shuffle) Shuffle(country, tag, fronts.Values);
        if (_depth) DepthDefense(country, tag, fronts.Values);
        if (_fireBrigade) PostReserves(country, tag, fronts.Values);
        if (_attack) Attack(country, tag);
    }

    /// <summary>Our defences being lost, with the attacker's share of the battle (%), where no reserve already waits to step in.</summary>
    private Dictionary<nint, double> LosingDefences(int tag)
    {
        var landCombat = Sig.VTable(Signatures.LandCombat);
        var losing = new Dictionary<nint, double>();
        foreach (var combat in Native.PdxArray(Game.State + CombatsOfGameState))
        {
            if (combat == 0 || *(nint*)combat != landCombat) continue;
            var defender = *(nint*)(combat + DefenderOfCombat);
            var province = *(nint*)(combat + ProvinceOfCombat);
            if (defender == 0 || province == 0) continue;
            var defending = Native.PdxArray(defender + DivisionsOfSide);
            if (defending.Length == 0 || defending[0] == 0 || Division.Commander(defending[0]) != tag) continue;
            if (*(int*)(defender + ReservesOfSide + 0xC) > 0) continue; // a reserve already waits to step in
            long progress;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)combat)[ProgressSlot])(combat, &progress); // attacker's share
            if (progress * 100.0 / Native.FixedPoint >= _losing) losing[province] = progress * 100.0 / Native.FixedPoint;
        }
        return losing;
    }

    /// <summary>
    /// Divisions we hold go back onto their front once their job is done; resting ones once rested. Ones the player gave
    /// other orders (or moved to another army) are forgotten. Run by the operations' turns too (Done), whose orders
    /// (_orders) may be a turn old or empty: our commands' round trips (PendingMs) outlast that.
    /// </summary>
    private void Return(int tag)
    {
        foreach (var (unit, held) in _held.ToArray())
        {
            if (_pending.ContainsKey(unit)) continue; // our command is still on its way
            if (held.Job == Job.Operation) continue; // its operation's
            if (held.Job != Job.Pin && (_orders.ContainsKey(unit + MemberOfUnit) || Division.Group(unit) != held.Group))
            {
                _held.Remove(unit); // back on an order, or into another army: the player's now
                continue;
            }
            if (held.Job == Job.Done)
            {
                Release(unit, tag);
                continue;
            }
            if (held.Job != Job.Rest || Division.Walking(unit) || Org(unit) < _returnOrg) continue;
            if (Str(unit) < _returnStrength && Game.Hour - held.Since < MaxRestHours) continue;
            if (Release(unit, tag)) Log.Info($"a rested division ({Org(unit):0}% organisation, {Str(unit):0}% strength) is back on its front");
        }
    }

    private void RotateArmy(nint country, int tag, nint group)
    {
        var parent = *(nint*)(group + OrdersGroup.ParentOffset);
        nint[] owners = parent == 0 ? [group] : [group, parent];
        if (!owners.SelectMany(o => Native.PdxArray(o + OrdersGroup.RootOrdersOffset).ToArray()).Any(o => o != 0 && IsFront(o))) return;

        // Worn and in contact: off the front to rest, where another of ours on that front holds the province.
        var swaps = 0;
        foreach (var unit in Native.PdxArray(group + OrdersGroup.MembersOffset).ToArray()
                     .Where(m => _orders.TryGetValue(m, out var o) && IsFront(o)).Select(m => m - MemberOfUnit)
                     .Where(u => Free(u, tag, _now) && (Org(u) < _wornOrg || Str(u) < _wornStrength) && InContact(country, tag, u)).OrderBy(Org).ToList())
        {
            if (swaps == MaxSwapsPerArmy) break;
            var front = _orders[unit + MemberOfUnit];
            if (_holding.GetValueOrDefault((front, Division.Location(unit))) < 2) continue; // alone there: it holds on
            var id = Order.Id(front);
            var resting = _held.Values.Count(h => h.Job == Job.Rest && h.Group == group && h.Order == id);
            if ((resting + 1) * 100 > (Order.Members(front).Length + resting) * _maxResting) continue;
            var rest = RestProvince(country, tag, unit);
            if (rest < 0) continue; // nowhere safe to rest: it stays
            Send(unit, rest, Job.Rest);
            swaps++;
            Log.Info($"rotation: a division at {Org(unit):0}% organisation, {Str(unit):0}% strength goes to rest");
        }
    }

    /// <summary>Our defences being lost get the nearest fresh division: resting, unassigned, or spare in a quiet part of a front.</summary>
    private void Plug(nint country, int tag, nint[] units)
    {
        var plugs = 0;
        Dictionary<nint, List<nint>>? helpers = null;
        foreach (var (province, progress) in _losingNow)
        {
            if (plugs == MaxPlugsPerTurn) break;
            var id = Game.Id(province);
            if (_sent.ContainsKey(id)) continue; // one helper per battle on its way at a time
            helpers ??= Pool(country, tag, units, true, u => Org(u) >= 80 && Str(u) >= 70);
            var helper = Take(tag, province, _plugRadius, helpers);
            if (helper == 0) continue;
            Send(helper, id, Job.Rest); // arriving, it joins the defence as a reserve
            _sent[id] = ([helper], false);
            plugs++;
            Log.Info($"plugging: a fresh division sent into a defence being lost ({progress:0}% for the attacker)");
        }
    }

    /// <summary>
    /// Divisions that can leave where they stand, by province: idle, free or resting with us, <paramref name="wanted"/>, and
    /// spare on their front (another of that front stays in the province, no enemy troops next to it) or, with
    /// <paramref name="unassigned"/>, on no order at all.
    /// </summary>
    private Dictionary<nint, List<nint>> Pool(nint country, int tag, IEnumerable<nint> units, bool unassigned, Func<nint, bool> wanted)
    {
        var left = new Dictionary<(nint Front, nint Province), int>(_holding);
        var pool = new Dictionary<nint, List<nint>>();
        foreach (var unit in units)
        {
            var resting = _held.TryGetValue(unit, out var held);
            if (resting && held.Job != Job.Rest || !Ready(unit, tag, _now) || Division.Walking(unit)) continue;
            var location = Division.Location(unit);
            if (location == 0) continue;
            if (_orders.TryGetValue(unit + MemberOfUnit, out var order))
            {
                if (!IsFront(order) || left.GetValueOrDefault((order, location)) < 2 || InContact(country, tag, unit) || !wanted(unit)) continue; // needed where it is
                left[(order, location)]--;
            }
            else if (!(unassigned || resting) || !wanted(unit)) continue;
            if (!pool.TryGetValue(location, out var here)) pool[location] = here = [];
            here.Add(unit);
        }
        return pool;
    }

    /// <summary>The pool's division closest to <paramref name="target"/> (breadth first over our provinces), taken out of the pool; 0 if none in reach.</summary>
    private static nint Take(int tag, nint target, int radius, Dictionary<nint, List<nint>> pool)
    {
        var at = Search(tag, target, radius, (p, _) => pool.TryGetValue(p, out var here) && here.Count > 0);
        if (at == 0) return 0;
        var unit = pool[at].MaxBy(Org);
        pool[at].Remove(unit);
        return unit;
    }

    /// <summary>Our provinces breadth first from <paramref name="from"/> (not itself), up to <paramref name="radius"/> steps: the first one <paramref name="found"/> (given its depth), or 0.</summary>
    private static nint Search(int tag, nint from, int radius, Func<nint, int, bool> found)
    {
        var seen = new HashSet<int> { Game.Id(from) };
        var frontier = new List<nint> { from };
        for (var depth = 1; depth <= radius && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var province in frontier)
            foreach (var id in Game.Neighbours(province))
            {
                if (!seen.Add(id)) continue;
                var p = Game.Province(id);
                if (p == 0 || Province.Controller(p) != tag) continue;
                if (found(p, depth)) return p;
                next.Add(p);
            }
            frontier = next;
        }
        return 0;
    }

    /// <summary>Moves the unit for <paramref name="job"/> (off its order first, unless pinning), booked so no other feature takes it.</summary>
    private void Send(nint unit, int province, Job job, int post = 0, int target = 0, (nint, int) front = default)
    {
        var location = Division.Location(unit);
        _orders.TryGetValue(unit + MemberOfUnit, out var order);
        if (_holding.TryGetValue((order, location), out var n)) _holding[(order, location)] = n - 1; // one fewer holds it
        Book(unit, job, job == Job.Pin ? 0 : order, post, target, front);
        Commands.Move(unit, province);
        _pending[unit] = _now + PendingMs;
    }

    /// <summary>
    /// Booked for <paramref name="job"/> so no other feature takes it, taken off <paramref name="order"/> (0: none) to go
    /// back onto it later. One already ours keeps the order it goes back onto.
    /// </summary>
    private void Book(nint unit, Job job, nint order, int post = 0, int target = 0, (nint, int) front = default)
    {
        var group = Division.Group(unit);
        var id = 0;
        if (_held.TryGetValue(unit, out var was)) (group, id) = (was.Group, was.Order);
        else if (order != 0)
        {
            Commands.Unassign(unit, group); // stays in the army; off its army group's fronts too
            id = Order.Id(order);
        }
        _held[unit] = new Held(job, group, id, Game.Hour, post, target, front);
    }

    /// <summary>
    /// Back onto the order it came from (when it had one, and is still in that army); no longer ours. False: it can't take
    /// orders now.
    /// </summary>
    private bool Release(nint unit, int tag)
    {
        if (!Ready(unit, tag, _now)) return false;
        var held = _held[unit];
        _held.Remove(unit);
        if (held.Order == 0 || Division.Group(unit) != held.Group) return true; // in another army now: the player's
        if (Assign(unit, held.Group, held.Order)) _pending[unit] = _now + PendingMs; // its order gone: it stays in the army's reserve
        return true;
    }

    private static bool IsFront(nint order) => Order.Type(order) == Order.Front;

    /// <summary><see cref="Division.Ready"/> (can take orders now), and not just ordered.</summary>
    private bool Ready(nint unit, int tag, long now) => Division.Ready(unit, tag) && !(_pending.TryGetValue(unit, out var until) && now < until);

    /// <summary>Ready, and doing no job for us.</summary>
    private bool Free(nint unit, int tag, long now) => Ready(unit, tag, now) && !_held.ContainsKey(unit);

    /// <summary>Organisation and strength in % of their maximum, from the division's own ratio getters (what has_unit_organization reads).</summary>
    private static double Org(nint unit) => Division.Organisation(unit) * 100.0 / Native.FixedPoint;
    private static double Str(nint unit) => Division.Strength(unit) * 100.0 / Native.FixedPoint;

    /// <summary>A province of ours at least RestDepth steps behind the unit, with no enemy-held neighbour, reached through our own provinces; -1 if none nearby.</summary>
    // ponytail: looks at who holds provinces, not at enemy units inside ours (paratroopers, a breakthrough in progress).
    private int RestProvince(nint country, int tag, nint unit)
    {
        var location = Division.Location(unit);
        var rest = location == 0 ? 0 : Search(tag, location, MaxRestSearch, (p, depth) => depth >= RestDepth && !NextToEnemy(country, tag, p));
        return rest == 0 ? -1 : Game.Id(rest);
    }

    /// <summary>Enemy divisions stand next to the unit: only then is a worn division at risk on the line (it recovers as fast there otherwise).</summary>
    private bool InContact(nint country, int tag, nint unit)
    {
        var location = Division.Location(unit);
        return location != 0 && Province.EnemyDivisionsNext(country, tag, location);
    }

    /// <summary><see cref="Province.NextToEnemy"/>, remembered for the turn.</summary>
    private bool NextToEnemy(nint country, int tag, nint province)
    {
        if (!_edge.TryGetValue(province, out var edge)) _edge[province] = edge = Province.NextToEnemy(country, tag, province);
        return edge;
    }

    /// <summary><see cref="Province.Enemy"/>, with the wars remembered for the turn.</summary>
    private bool Enemy(nint country, int tag, nint province)
    {
        var controller = Province.Controller(province);
        return controller != tag && AtWar(country, controller); // 0: sea
    }

    /// <summary><see cref="Province.Friendly"/>, with the alliances remembered for the turn.</summary>
    private bool Friendly(nint country, int tag, nint province)
    {
        var controller = Province.Controller(province);
        if (controller == tag) return true;
        if (controller <= 0) return false;
        if (!_allies.TryGetValue(controller, out var allied)) _allies[controller] = allied = Game.Allied(country, controller);
        return allied;
    }

    private bool AtWar(nint country, int other)
    {
        if (other <= 0) return false;
        if (!_war.TryGetValue(other, out var war)) _war[other] = war = Game.AtWar(country, other);
        return war;
    }

    private static bool Assign(nint unit, nint group, int order) => Commands.Assign(unit, group, order);
}
