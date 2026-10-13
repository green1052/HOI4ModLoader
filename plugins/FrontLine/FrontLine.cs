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
/// - Plugging: a defence of ours that is being lost (or whose organisation runs out within two days at its rate of loss) gets
///   fresh divisions that can walk there in time, sent into the battle, where they join the defence; a line province facing
///   twice its divisions gets one before the attack (thicken), and empty ground of ours around a fresh breach is manned
///   (cordon: defence in depth).
/// - Escape: a front division in a salient whose way back narrows to a neck of one or two provinces next to the enemy
///   pulls back through it, before the pocket closes; a pocket mostly out of supply attacks toward friendly lines (breakout).
/// - Lure (AI only): a defence being lost with both shoulders held and mobile divisions near is let go, to cut it off.
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
/// Divisions taken off a front go back onto it once their job is done (rested, crisis over, post given up, the threat gone);
/// one that can't recover goes back after MaxRestHours. The player's are written to cache\frontline_held.txt, so a load puts
/// them back on their plan. With [AI] ApplyToAI (single player), AI countries get the same micro (not rotation: their front
/// orders took the resting ones straight back), each with its own bookkeeping (Side).
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
    private const int MaxSwapsPerArmy = 2;    // sent to rest at a time, so a front never changes all at once
    private const int MaxRestHours = 14 * 24; // resting this long, it goes back whatever its state (supply may never let it recover)
    private const int GuardHours = 7 * 24;    // a plugging or thicken helper holds its defence that long at most
    // Organisation and strength (%) a division needs for a job. Fresh: a post or a reinforcement that may wait a while (reserves,
    // thickening, a quiet front's spare). Attack: an attack of its own (pins too). Helper: into a defence being lost, or a
    // second line (help that arrives beats the best help that doesn't). Wall: an empty province next to a breach.
    private const int FreshOrg = 80, FreshStr = 70, AttackOrg = 70, AttackStr = 70, HelperOrg = 70, HelperStr = 60, WallOrg = 60, WallStr = 50;
    private const long PendingMs = 30_000;    // a unit we gave orders to is left alone meanwhile (multiplayer: a round trip)
    private const long AiBudgetMs = 20;       // AI countries' turns per tick, round robin: the rest wait for the next tick

    private static readonly int[] PlanKeys = ['E', 'H']; // with Ctrl+Alt held: activate, halt
    private readonly bool[] _down = new bool[PlanKeys.Length];

    /// <summary>
    /// What a division we moved does for us. Rest: rests (rotation; an escape, Post = the province it left, while that
    /// salient's neck is still closing) and goes back once rested. Guard: holds a defence it was sent into (plugging,
    /// thicken) while enemy divisions stand next to it. Reserve, Counter, Depth, Cordon: fire brigade, second line and
    /// breach walls (Defense.cs). Shuffle, Pin: a losing front's help (Shuffle.cs). Operation: a blitz's or pincer's
    /// (Operations.cs). Done: its task is over; it goes back as soon as it can take orders.
    /// </summary>
    private enum Job { Rest, Reserve, Counter, Depth, Shuffle, Pin, Operation, Done, Cordon, Guard }

    /// <summary>
    /// A division we moved. Group/Order: the order it goes back onto (0: none). Post: the province it holds (Reserve, Counter,
    /// Depth, Shuffle, Cordon, Guard), attacks from (Pin) or escaped from (Rest). Target: what it attacks (Counter, Pin), backs
    /// (Depth) or walls in (Cordon). Front: the front it serves.
    /// </summary>
    private readonly record struct Held(Job Job, nint Group, int Order, int Since, int Post, int Target, (nint, int) Front);

    /// <summary>One country's bookkeeping: the player's, and each AI country's with ApplyToAI. Begin swaps it in.</summary>
    private sealed class Side
    {
        public readonly Dictionary<nint, long> Pending = [];
        public readonly Dictionary<nint, Held> Held = [];
        public readonly Dictionary<int, (nint[] Units, bool Attack)> Sent = [];
        public readonly Dictionary<int, (int Hour, (nint, int) Front)> LastSeen = [];
        public readonly Dictionary<(nint, int), int> Crisis = [];
        public readonly Dictionary<nint, int> Recent = []; // AI divisions we sent: unit -> game hour
        public readonly Dictionary<int, int> EscapedFrom = []; // province divisions escaped from -> game hour
    }

    private readonly Dictionary<int, Side> _sides = [];                     // tag -> its bookkeeping
    private readonly Dictionary<int, int> _aiTurnHour = [];                 // AI tag -> the game hour of its last turn
    // The turn's country's (Begin):
    private Dictionary<nint, long> _pending = [];                             // unit -> until
    private Dictionary<nint, Held> _held = [];                               // every division we took for a job: one bookkeeping, so no feature takes another's
    private Dictionary<int, (nint[] Units, bool Attack)> _sent = [];          // province we sent divisions into (a battle to help, an attack) -> them
    private bool _force;                                                      // an AI country's turn: commands posted as the AI posts them
    private bool _restore;                                                    // the next player turn puts back the divisions the last game held
    private string _remembered = "";                                          // what Remember last wrote
    private int _nextAi;                                                      // the AI country the last tick's budget stopped at
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
    private bool _rotation, _plugging, _escape, _attack, _fireBrigade, _depth, _shuffle, _ai;
    private double _wornOrg, _wornStrength, _returnOrg, _returnStrength, _losing;
    private int _odds;
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
            "Send fresh divisions into a defence of ours that is being lost (past LosingPercent, or its organisation gone within two days at its rate of loss), "
            + "only ones that can walk there in time, two when it falls within a day; a line province facing twice its divisions gets one before the attack, "
            + "and our empty provinces around ground the enemy took in the last two days are manned (cordon: defence in depth). They go back to their front once the threat is gone.");
        _losing = Math.Clamp(Config.Bind("Plugging", "LosingPercent", 50, "A defence counts as being lost when the attacker holds this share (%) of the battle's organisation (the battle bar)."), 50, 100);
        _plugRadius = Math.Clamp(Config.Bind("Plugging", "PlugRadius", 6, "How far (provinces) a helper may come from."), 1, MaxRestSearch);
        _lure = Config.Bind("Plugging", "Lure", true,
            "AI only (ApplyToAI): a defence being lost is let go, to draw the attacker in, when both its shoulders are held and not being lost and 2+ fresh mobile divisions "
            + "stand within 3 provinces against at most twice as many attackers; AI Assist's encirclement then cuts the salient at its neck and the fire brigade counterattacks.");
        _escape = Config.Bind("Escape", "Escape", true,
            "Pull front divisions back out of a salient whose way back narrows to one or two provinces next to the enemy (and the enemy there is strong enough to close it), through that neck while it is still open. "
            + "Once cut off (up to 30 provinces, friendly lines within 6), the divisions inside attack together once a day toward the nearest friendly ground.");
        _attack = Config.Bind("Attack", "Attack", true,
            "Take empty enemy provinces next to the line with a fresh division, and attack enemy divisions already surrounded with at least as many fresh ones (more behind forts); a province of ours always keeps one. "
            + "Best targets first: closing a pocket, then victory points, supply hubs, airfields and ports, then shortening the front; none our supply can't follow (unless it closes a pocket) or that lengthen the front by more than two provinces for nothing.");
        _odds = Math.Max(0, Config.Bind("Attack", "AttackOdds", 200,
            "Also attack an enemy province its neighbours of ours can outgun this many times (%: 200 = twice its defenders' power, more behind forts, in rough ground or across a river), with fresh divisions from all of them, each keeping one. "
            + "Your divisions only on a battle plan that is running. 0: off (mop-up too)."));
        _fireBrigade = Config.Bind("FireBrigade", "FireBrigade", true,
            "Keep a few armored or motorized divisions per front a province behind the line, off its battle plan. Where the enemy breaks in (a province of ours lost in the last two days, a landing, enemy troops in our provinces), "
            + "the nearest ones counterattack with enough to win (forts counted, each counting double), then go back to their post. An AI country without spare mobile divisions uses infantry.");
        _depth = Config.Bind("DepthDefense", "DepthDefense", true,
            "Key line provinces (ports, victory points, river crossings, cities and mountains) get a second division a province behind them, from spare divisions of a front; it stays while the line holds there.");
        _shuffle = Config.Bind("Shuffle", "Shuffle", true,
            "When a front is losing (its line provinces lost in two days and defences being lost three or more together, or facing twice its divisions), spare fresh divisions of quiet fronts reinforce its most pressed provinces and go back to their own front a day after the crisis. "
            + "Meanwhile fresh divisions next to the crisis attack idle enemy stacks of one or two divisions (one attacker each, while its organisation holds) so they are locked in combat and can't be moved in. "
            + "A front that only faces an encircled pocket keeps twice the pocket's divisions (two at least); the rest go onto their army's biggest other front.");
        if (Config.Bind("Keys", "BattlePlanKeys", true, "Ctrl+Alt+E activates every battle plan at once, Ctrl+Alt+H halts them."))
            MainThread.Every(TimeSpan.FromMilliseconds(30), PollKeys);
        _ai = Config.Bind("AI", "ApplyToAI", false,
            "Single player: AI countries get the same front line micro (plugging, escape, attack, fire brigade, depth defence and shuffle, with the settings above; not rotation: their front orders took resting divisions straight back), and the lure. "
            + "AI Assist's own rotation, escape, reserves, second line and local attacks stand aside meanwhile, and its regrouping hooks leave these divisions alone; without AI Assist the AI's regrouping may take them back.");
        var operations = BindOperations();
        var micro = _rotation || _plugging || _escape || _attack || _fireBrigade || _depth || _shuffle;
        _ai &= micro && !Multiplayer;
        Jobs.AiMicro = _ai ? Info.Id : null;
        if (!micro && !operations) return;

        _ = Sig.Find(Commands.AssignSig);
        // The core's game functions: a broken pattern fails the load, not a turn.
        _ = Sig.Find(Commands.UnassignSig) + Sig.Find(Game.IsAtWarWithSig) + Sig.Find(Game.SameOrAlliedSig) + Sig.Find(Province.FortLevelSig)
            + Sig.Find(Buildings.FindSig) + Sig.Find(Buildings.DbCallSig) + Sig.VTable(Signatures.LandCombat);
        _game = new PerGame(() => // one reset for the operations too: they share _held
        {
            _sides.Clear(); // Begin swaps a new side in
            _breakouts.Clear();
            _aiTurnHour.Clear();
            _lured.Clear();
            _orgSeen.Clear();
            _orders.Clear();
            _operations.Clear();
            _opOrdered.Clear();
            _opLastHour = -1;
            _restore = true; // a save loaded (or the plugin reloaded): put back what the last game had taken off its plans
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

    private Side SideOf(int tag) => _sides.TryGetValue(tag, out var side) ? side : _sides[tag] = new Side();

    /// <summary><see cref="Begin(int, out nint[])"/> for the player's country.</summary>
    private nint Begin(out int tag, out nint[] units) => Begin(tag = Game.ControlledTag, out units);

    /// <summary>
    /// The start of every timer's turn: per-game state reset after a load or a country change, then <paramref name="tag"/>'s
    /// bookkeeping swapped in; outside a running game 0. Else its country, its land divisions (<paramref name="units"/>,
    /// <see cref="_alive"/>, <see cref="_at"/>); divisions gone since (died, merged) are forgotten before anything
    /// dereferences them, and the memos of earlier turns.
    /// </summary>
    private nint Begin(int tag, out nint[] units)
    {
        _game.Check();
        var side = SideOf(tag);
        (_pending, _held, _sent, _lastSeen, _crisis, _recent) = (side.Pending, side.Held, side.Sent, side.LastSeen, side.Crisis, side.Recent);
        foreach (var (unit, hour) in _recent.ToArray()) if (Game.Hour - hour is < 0 or >= RepeatHours) _recent.Remove(unit);
        _force = tag != Game.ControlledTag;
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
        if (!_force) foreach (var op in _operations) Forget(op); // the player's
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
        _game.Check();
        if (!Game.HasStarted) return;
        var player = Game.ControlledTag;
        if (player != 0)
        {
            if (_restore) Restore(player);
            Turn(player); // 0: observer
            Remember(player);
        }
        if (_ai)
        {
            var countries = Game.Countries.ToArray();
            var started = Environment.TickCount64;
            var hour = Game.Hour;
            for (var n = 0; n < countries.Length && Environment.TickCount64 - started < AiBudgetMs; n++)
            {
                _nextAi = (_nextAi + 1) % countries.Length;
                var tag = countries[_nextAi] == 0 ? 0 : Game.Tag(countries[_nextAi]);
                // Once a game hour: combat, supply and control change hourly, so a second turn (paused, or a fast round robin) redid the same work.
                if (tag <= 0 || tag == player || _aiTurnHour.TryGetValue(tag, out var last) && last == hour) continue;
                _aiTurnHour[tag] = hour;
                Turn(tag);
            }
        }
        Jobs.Set(Info.Id, _sides.Values.SelectMany(s => s.Held.Keys));
    }

    private void Turn(int tag)
    {
        var country = Begin(tag, out var units);
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
        if (_force && fronts.Count == 0 && _held.Count == 0) return; // an AI country without fronts: at peace
        _holding.Clear();
        foreach (var unit in units)
        {
            var location = Division.Location(unit);
            if (location == 0 || !_orders.TryGetValue(unit + MemberOfUnit, out var order) || !fronts.TryGetValue(order, out var front)) continue;
            front.Units.Add(unit);
            _holding[(order, location)] = _holding.GetValueOrDefault((order, location)) + 1;
        }
        _eta.Clear();
        _losingNow = _plugging || _shuffle ? LosingDefences(tag) : [];
        if (_fireBrigade || _depth || _shuffle || _plugging) Survey(country, tag, fronts.Values); // the lines, lost ground (cordon's breaches)

        Return(tag);
        // Not for the AI: its front orders take a division off its order straight back, so it walked to rest and back on and
        // on (Japan's in Shandong, every few hours).
        if (_rotation && !_force) foreach (var group in groups) RotateArmy(country, tag, group);
        if (_plugging) Plug(country, tag, units);
        if (_fireBrigade) Counterattack(country, tag, fronts.Values);
        if (_plugging) Cordon(country, tag, units);
        if (_escape)
        {
            Escape(country, tag, units);
            Breakout(country, tag, units);
        }
        if (_shuffle)
        {
            Shuffle(country, tag, fronts.Values);
            TrimPocketFronts(country, tag, fronts.Values);
        }
        if (_depth) DepthDefense(country, tag, fronts.Values);
        if (_fireBrigade) PostReserves(country, tag, fronts.Values);
        if (_attack) Attack(country, tag);
    }

    /// <summary>Our defences being lost, with the attacker's share of the battle (%), where no reserve already waits to step in.</summary>
    private Dictionary<nint, double> LosingDefences(int tag)
    {
        var landCombat = Sig.VTable(Signatures.LandCombat);
        var losing = new Dictionary<nint, double>();
        foreach (var (key, seen) in _orgSeen.ToArray()) if (key.Item1 == tag && Game.Hour - seen.Hour is < 0 or >= 24) _orgSeen.Remove(key); // battles over
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
            var share = progress * 100.0 / Native.FixedPoint;
            var eta = CollapseHours(tag, province, defending);
            if (share >= _losing || eta < EarlyPlugHours) losing[province] = share;
            _eta[province] = eta;
        }
        return losing;
    }

    private const int EarlyPlugHours = 48, UrgentPlugHours = 24, MotorizedHoursPerProvince = 10, InfantryHoursPerProvince = 20;
    private readonly Dictionary<nint, double> _eta = [];                                // the turn's defences -> hours until their organisation runs out
    private readonly Dictionary<(int, nint), (int Hour, double Org)> _orgSeen = [];    // (tag, battle province) -> its defenders' organisation, last hour seen

    /// <summary>
    /// Hours until the defenders' organisation runs out at the rate it fell since the last game hour seen (how hard the attack
    /// breaks through), infinite while it holds or recovers.
    /// </summary>
    private double CollapseHours(int tag, nint province, ReadOnlySpan<nint> defending)
    {
        double org = 0;
        foreach (var unit in defending) if (unit != 0) org += Org(unit);
        var hour = Game.Hour;
        var key = (tag, province);
        var eta = double.PositiveInfinity;
        if (_orgSeen.TryGetValue(key, out var seen) && hour - seen.Hour is > 0 and < 24 && seen.Org > org)
            eta = org / ((seen.Org - org) / (hour - seen.Hour));
        if (!_orgSeen.TryGetValue(key, out seen) || seen.Hour != hour) _orgSeen[key] = (hour, org);
        return eta;
    }

    /// <summary>
    /// The pool's division closest to <paramref name="target"/> that can walk there within <paramref name="hours"/> (MotorizedHoursPerProvince
    /// a province for mobile ones, InfantryHoursPerProvince for the rest), taken out of the pool; 0 if none.
    /// </summary>
    private static nint TakeInTime(int tag, nint target, int radius, Dictionary<nint, List<nint>> pool, double hours)
    {
        if (pool.Count == 0) return 0;
        nint chosen = 0;
        var at = Search(tag, target, radius, (p, depth) =>
        {
            if (!pool.TryGetValue(p, out var here)) return false;
            chosen = here.Where(u => depth * (Division.Motorized(u) ? MotorizedHoursPerProvince : InfantryHoursPerProvince) <= hours).MaxBy(Org);
            return chosen != 0;
        });
        if (at == 0) return 0;
        if (pool[at].Remove(chosen) && pool[at].Count == 0) pool.Remove(at);
        return chosen;
    }

    /// <summary>
    /// Divisions we hold go back onto their front once their job is done: resting ones once rested (or after MaxRestHours,
    /// whatever their state: supply may never let them recover), escaped ones once their salient's neck no longer closes,
    /// guards once no enemy division stands next to their post (or it is lost, or a week has passed). Ones the player gave
    /// other orders (or moved to another army) are forgotten. Run by the operations' turns too (Done), whose orders
    /// (_orders) may be the last turn's, another country's: our commands' round trips (PendingMs) outlast that.
    /// </summary>
    private void Return(int tag)
    {
        var country = Game.Country(tag);
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
            if (Division.Walking(unit) || country == 0) continue;
            var post = Game.Province(held.Post);
            if (held.Job == Job.Guard)
            {
                var holding = post != 0 && Province.Controller(post) == tag && EnemiesNext(country, tag, post) > 0 && Game.Hour - held.Since < GuardHours;
                if (!holding && !Division.Fighting(unit)) Release(unit, tag);
                continue;
            }
            if (held.Job != Job.Rest) continue;
            var overdue = Game.Hour - held.Since >= MaxRestHours;
            if (!overdue && post != 0 && Province.Controller(post) == tag && Neck(country, tag, post) >= 0) continue; // escaped: its salient still closing
            if (!overdue && (Org(unit) < _returnOrg || Str(unit) < _returnStrength)) continue;
            if (Release(unit, tag)) Log.Info($"a division ({Org(unit):0}% organisation, {Str(unit):0}% strength) is back on its front after {Game.Hour - held.Since} hours off it");
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

    /// <summary>
    /// Defences being lost (the attacker past LosingPercent of the battle, or the defenders' organisation running out within
    /// EarlyPlugHours at its rate of loss) get fresh divisions that can walk there before it runs out, the soonest to fall
    /// first: two where it falls within UrgentPlugHours, else one. One sent too late is wasted, a defence that holds needs none.
    /// </summary>
    private void Plug(nint country, int tag, nint[] units)
    {
        var plugs = 0;
        Dictionary<nint, List<nint>>? helpers = null;
        foreach (var (province, progress) in _losingNow.OrderBy(l => _eta.GetValueOrDefault(l.Key, double.PositiveInfinity)).ToList())
        {
            if (plugs >= MaxPlugsPerTurn) break;
            var id = Game.Id(province);
            if (_sent.ContainsKey(id)) continue; // its helpers are on their way
            if (_force && _lure && Lure(country, tag, province)) continue;
            helpers ??= Pool(country, tag, units, true, u => Org(u) >= HelperOrg && Str(u) >= HelperStr);
            var eta = _eta.GetValueOrDefault(province, double.PositiveInfinity);
            var sent = new List<nint>();
            for (var n = eta < UrgentPlugHours ? 2 : 1; n > 0; n--)
            {
                var helper = TakeInTime(tag, province, _plugRadius, helpers, eta);
                if (helper == 0) break;
                if (Send(helper, id, Job.Guard, id)) sent.Add(helper); // arriving, it joins the defence as a reserve
            }
            if (sent.Count == 0) continue;
            _sent[id] = ([.. sent], false);
            plugs += sent.Count;
            Log.Info($"plugging: {sent.Count} fresh division(s) sent into a defence being lost ({progress:0}% for the attacker, its organisation gone in {(double.IsInfinity(eta) ? "-" : $"{eta:0}h")})");
        }
        Thicken(country, tag, units, helpers);
    }

    private const int MinThinThreat = 3, ThinRatio = 2, MaxThickensPerTurn = 4;

    /// <summary>
    /// Before the attack: a line province of ours facing ThinRatio times as many enemy divisions as it holds (MinThinThreat or
    /// more) gets the nearest fresh division within MaxRestSearch (the divisions waiting behind the line for an offensive
    /// too), the most outnumbered first, MaxThickensPerTurn a turn. The enemy masses against one
    /// thinly held province and breaks through before plugging (a defence already being lost) can answer (France out of the
    /// Maginot line against Germany).
    /// </summary>
    private void Thicken(nint country, int tag, nint[] units, Dictionary<nint, List<nint>>? helpers)
    {
        var thin = new List<(nint Province, int Enemy, int Ours)>();
        foreach (var at in units.Select(Division.Location).Where(p => p != 0).Distinct())
        {
            if (_losingNow.ContainsKey(at) || _sent.ContainsKey(Game.Id(at))) continue;
            var enemy = EnemiesNext(country, tag, at);
            if (enemy < MinThinThreat) continue;
            var ours = Province.Units(at).ToArray().Count(u => u != 0 && Division.IsLand(u) && Division.Commander(u) == tag);
            if (ours * ThinRatio < enemy) thin.Add((at, enemy, ours));
        }
        var thickened = 0;
        foreach (var (province, enemy, ours) in thin.OrderByDescending(t => t.Enemy / (double)Math.Max(1, t.Ours)))
        {
            if (thickened == MaxThickensPerTurn) break;
            helpers ??= Pool(country, tag, units, true, u => Org(u) >= FreshOrg && Str(u) >= FreshStr);
            var helper = Take(tag, province, MaxRestSearch, helpers);
            if (helper == 0) continue;
            var id = Game.Id(province);
            if (!Send(helper, id, Job.Guard, id)) continue;
            _sent[id] = ([helper], false);
            thickened++;
            Log.Info($"plugging: a fresh division thickens a thin spot ({ours} of ours against {enemy} enemy division(s) next to it)");
        }
    }

    private const int LureRadius = 3, MinLureReserves = 2, LureHours = 48;
    private bool _lure;
    private readonly Dictionary<int, int> _lured = []; // province id -> game hour it was let go (logged once)

    /// <summary>
    /// An AI defence being lost is let go instead of plugged, to draw the attacker in, when it can be cut off: both shoulders
    /// (our line provinces next to it) are held and not being lost, and at least MinLureReserves fresh mobile divisions of ours
    /// stand within LureRadius, against at most twice as many enemy divisions attacking it. Once in, the attacker's salient is
    /// cut at its neck (AI Assist's Encirclement) and the fire brigade counterattacks it.
    /// </summary>
    private bool Lure(nint country, int tag, nint province)
    {
        var shoulders = Game.Neighbours(province).Select(Game.Province)
            .Where(p => p != 0 && Province.Controller(p) == tag && Province.NextToEnemy(country, tag, p)).ToList();
        if (shoulders.Count < 2 || shoulders.Any(p => _losingNow.ContainsKey(p) || !Province.Units(p).ToArray().Any(u => u != 0 && Division.Commander(u) == tag))) return false;
        var attackers = Game.Neighbours(province).Select(Game.Province).Where(p => p != 0 && Province.Enemy(country, tag, p)).Sum(Province.Divisions);
        var reserves = 0;
        var seen = new HashSet<nint> { province };
        var frontier = new List<nint> { province };
        for (var depth = 0; depth < LureRadius && frontier.Count > 0; depth++)
        {
            frontier = frontier.SelectMany(p => Game.Neighbours(p)).Select(Game.Province).Where(p => p != 0 && seen.Add(p) && Province.Controller(p) == tag).ToList();
            foreach (var p in frontier)
            foreach (var u in Province.Units(p))
                if (u != 0 && Division.IsLand(u) && Division.Commander(u) == tag && Division.Motorized(u) && Org(u) >= AttackOrg && !Division.Fighting(u)) reserves++;
        }
        if (reserves < MinLureReserves || attackers > 2 * reserves) return false;
        var id = Game.Id(province);
        foreach (var (lured, hour) in _lured.ToArray()) if (Game.Hour - hour is < 0 or >= LureHours) _lured.Remove(lured);
        if (!_lured.TryGetValue(id, out var at))
        {
            _lured[id] = Game.Hour;
            Log.Info($"  #{tag} lure: lets {attackers} enemy division(s) into province {id} (both shoulders held, {reserves} mobile division(s) within {LureRadius})");
        }
        return true;
    }

    /// <summary>
    /// Divisions that can leave where they stand, by province: idle, free or resting with us, <paramref name="wanted"/>, and
    /// spare on their front (behind the line any; on it another of that front stays in the province; next to enemy troops,
    /// out of battle with two of that front staying, at least as many as the enemy divisions next to it: the AI stacks its
    /// whole front on the line, so there was never a spare for reserves) or, with
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
            // An AI division between armies (the army balancing regroups them): the AI walks it to its new army, so taken it
            // went back and forth between our post and that army, every few days.
            if (_force && Division.Group(unit) == 0) continue;
            var location = Division.Location(unit);
            if (location == 0) continue;
            if (_orders.TryGetValue(unit + MemberOfUnit, out var order))
            {
                var stay = left.GetValueOrDefault((order, location)) - 1;
                if (!IsFront(order)) continue;
                if (InContact(country, tag, unit) ? stay < 2 || Division.Fighting(unit) || stay < EnemiesNext(country, tag, location)
                    : stay < 1 && Province.NextToEnemy(country, tag, location)) continue; // needed where it is (behind the line nobody is)
                if (!wanted(unit)) continue; // last: it may count the division as taken (Shuffle's budget)
                left[(order, location)]--;
            }
            else if (!(unassigned || resting) || !wanted(unit)) continue;
            if (!pool.TryGetValue(location, out var here)) pool[location] = here = [];
            here.Add(unit);
        }
        return pool;
    }

    /// <summary>The pool's division closest to <paramref name="target"/> (breadth first over our provinces), taken out of the pool; 0 if none in reach.</summary>
    private static nint Take(int tag, nint target, int radius, Dictionary<nint, List<nint>> pool) =>
        TakeInTime(tag, target, radius, pool, double.PositiveInfinity);

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

    /// <summary>
    /// Moves the unit for <paramref name="job"/> (off its order first, unless pinning), booked so no other feature takes it.
    /// False, and nothing done, when its way runs through enemy divisions (<see cref="Commands.ClearWay"/>).
    /// </summary>
    private bool Send(nint unit, int province, Job job, int post = 0, int target = 0, (nint, int) front = default)
    {
        if (!Commands.ClearWay(unit, province)) return false;
        var location = Division.Location(unit);
        _orders.TryGetValue(unit + MemberOfUnit, out var order);
        if (_holding.TryGetValue((order, location), out var n)) _holding[(order, location)] = n - 1; // one fewer holds it
        Book(unit, job, job == Job.Pin ? 0 : order, post, target, front);
        Arrow(unit, province, job.ToString(), job switch
        {
            Job.Rest => (80, 120, 255),
            Job.Counter or Job.Pin => (220, 60, 220),
            _ => (60, 220, 60), // a post: reserve, second line, a front's reinforcement
        });
        Commands.Move(unit, province, _force);
        _pending[unit] = _now + PendingMs;
        if (_force) _recent[unit] = Game.Hour;
        return true;
    }

    /// <summary>Logs who was sent where, for what; [Debug] AiActionArrows: an arrow from where the unit stands to where it was sent.</summary>
    private void Arrow(nint unit, int to, string what, (byte R, byte G, byte B) colour)
    {
        var from = Division.Location(unit);
        var owner = Division.Owner(unit);
        Log.Info($"  {(owner == Game.PlayerTag ? "player" : $"#{owner}")} {what}: province {(from != 0 ? Game.Id(from) : 0)} -> {to}");
        if (from != 0) MapArrows.Flash(Game.Id(from), to, colour.R, colour.G, colour.B);
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
            Commands.Unassign(unit, group, _force); // stays in the army; off its army group's fronts too
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
        if (Commands.Assign(unit, held.Group, held.Order, _force)) _pending[unit] = _now + PendingMs; // its order gone: it stays in the army's reserve
        return true;
    }

    /// <summary>
    /// An AI division the AI's own orders walked off our post: left to them (not put back on its old order, which would be one
    /// more walk) and not taken again for RepeatHours. Moved back each time, it went to and fro between the post and wherever
    /// the AI wanted it.
    /// </summary>
    private void GiveUp(nint unit, string job)
    {
        if (!_held.Remove(unit)) return;
        _recent[unit] = Game.Hour;
        Log.Info($"{job}: the AI moved a division off its post; left to the AI");
    }

    private static bool IsFront(nint order) => Order.Type(order) == Order.Front;

    /// <summary><see cref="Division.Ready"/> (can take orders now), and not just ordered.</summary>
    private bool Ready(nint unit, int tag, long now) =>
        Division.Ready(unit, tag) && !(_pending.TryGetValue(unit, out var until) && now < until) && !Jobs.Taken(unit, Info.Id) // AI Assist's posts stay
        && (_held.ContainsKey(unit) || !_recent.ContainsKey(unit)); // an AI division its front order took back: not again for RepeatHours

    // The AI's front orders put a division we took off straight back on: sent again it walked to and fro (rotation, escape
    // in Shandong every few hours). An AI division we sent isn't sent again for three days from then, unless still ours.
    private const int RepeatHours = 72;
    private Dictionary<nint, int> _recent = [];

    /// <summary>Ready, and doing no job for us.</summary>
    private bool Free(nint unit, int tag, long now) => Ready(unit, tag, now) && !_held.ContainsKey(unit);

    /// <summary>Organisation and strength in % of their maximum, from the division's own ratio getters (what has_unit_organization reads).</summary>
    private static double Org(nint unit) => Division.Organisation(unit) * 100.0 / Native.FixedPoint;
    private static double Str(nint unit) => Division.Strength(unit) * 100.0 / Native.FixedPoint;

    /// <summary>The nearest province of ours behind the unit with no enemy-held neighbour, reached through our own provinces; -1 if none nearby.</summary>
    // ponytail: looks at who holds provinces, not at enemy units inside ours (paratroopers, a breakthrough in progress).
    private int RestProvince(nint country, int tag, nint unit)
    {
        var location = Division.Location(unit);
        var rest = location == 0 ? 0 : Search(tag, location, MaxRestSearch, (p, _) => !NextToEnemy(country, tag, p));
        return rest == 0 ? -1 : Game.Id(rest);
    }

    /// <summary>Enemy divisions in the provinces next to <paramref name="province"/>.</summary>
    private static int EnemiesNext(nint country, int tag, nint province) =>
        Game.Neighbours(province).Sum(n => Game.Province(n) is var p && p != 0 && Province.Enemy(country, tag, p) ? Province.Divisions(p) : 0);

    /// <summary>Enemy divisions stand next to the unit: only then is a worn division at risk on the line (it recovers as fast there otherwise).</summary>
    private bool InContact(nint country, int tag, nint unit)
    {
        var location = Division.Location(unit);
        return location != 0 && Province.EnemyDivisionsNext(country, tag, location);
    }

    private static readonly string HeldFile = Path.Combine(Loader.ModsDir, "cache", "frontline_held.txt");

    /// <summary>
    /// The player's divisions we hold off their battle plan, by ref (unit, army, order id), written when it changes: a save
    /// loaded or a plugin reload forgets them, and they would stay off their plan.
    /// </summary>
    private void Remember(int tag)
    {
        var lines = string.Join("\n", SideOf(tag).Held.Where(h => h.Value.Order != 0)
            .Select(h => $"{Division.Ref(h.Key)} {new OrdersGroup(h.Value.Group).Ref} {h.Value.Order}").Order());
        if (lines == _remembered) return;
        _remembered = lines;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HeldFile)!);
            File.WriteAllText(HeldFile, lines);
        }
        catch (IOException e) { Log.Error($"held divisions: {e.Message}"); }
    }

    /// <summary>
    /// After a load: the divisions the file names that are still the player's, in the same army and on no order go back onto
    /// their order (a different campaign's refs name nothing, or divisions that fail these checks).
    /// </summary>
    private void Restore(int tag)
    {
        _restore = false;
        string[] lines;
        try { lines = File.Exists(HeldFile) ? File.ReadAllLines(HeldFile) : []; }
        catch (IOException) { return; }
        var restored = 0;
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length != 3 || !long.TryParse(parts[0], out var unitRef) || !long.TryParse(parts[1], out var groupRef) || !int.TryParse(parts[2], out var order)) continue;
            var unit = Game.Resolve(unitRef);
            var group = Game.Resolve(groupRef);
            if (unit == 0 || group == 0 || Division.Commander(unit) != tag || Division.Group(unit) != group || OrderOf(unit, group) != 0
                || !Division.Ready(unit, tag, headquarters: true) || !Commands.Assign(unit, group, order)) continue; // an HQ an older version took
            SideOf(tag).Pending[unit] = Environment.TickCount64 + PendingMs; // its side's: Begin hasn't swapped it in yet
            restored++;
        }
        if (restored > 0) Log.Info($"put {restored} division(s) the last game had taken off their battle plan back onto it");
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
}
