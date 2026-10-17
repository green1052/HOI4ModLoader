using HOI4ModLoader;

namespace FrontLine;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    [VTable] public const string LandCombat = "CLandCombat";
}

/// <summary>
/// Does the front line micro a player does by hand, with the battle plan UI's own commands (works in multiplayer):
/// - Rotation: worn divisions in contact rest behind the line while another division of their front holds the province.
/// - Plugging: a defence being lost gets fresh divisions that arrive in time; an outnumbered line province gets one before
///   the attack (thicken); empty provinces around a fresh breach are manned (cordon).
/// - Escape: divisions leave a salient through its closing neck; a cut-off pocket out of supply breaks out.
/// - Lure (AI only): a defence that can be cut off is let go to draw the attacker in.
/// - Attack: empty and surrounded enemy provinces next to the line, and assaults at good odds.
/// - Fire brigade: mobile reserves behind the line counterattack break-ins.
/// - Depth defence: a second division behind key line provinces.
/// - Shuffle: quiet fronts reinforce a losing one; idle enemy stacks next to it are pinned.
/// - Ctrl+Alt+E / Ctrl+Alt+H: activate / halt every battle plan. Ctrl+Alt+B / Ctrl+Alt+P: blitz / pincer (Operations.cs).
/// Divisions taken off a front go back onto it when their job ends. The player's are saved to cache\frontline_held.txt, so
/// a reload puts them back. ApplyToAI gives AI countries the same micro except rotation, each with its own bookkeeping (Side).
/// </summary>
[Plugin("com.green1052.frontline", "Front Line", "1.0.0", GameVersion = "1.19.3")]
public unsafe partial class FrontLine : Plugin
{
    // Layouts for HOI4 1.19.3 (the rest are in the loader's Division, Province, Order, OrdersGroup).
    private const int MemberOfUnit = 0xB8; // CArmy: its COrdersGroupMember
    private const int BorderWar = 0x1B0;   // COrdersGroup
    private static readonly int[] OrderLists = [OrdersGroup.RootOrdersOffset, 0xB0, 0xC8]; // root (fronts, offensive lines), fallback lines, invasions/paradrops
    // Combats: array at gamestate +0x268. CLandCombat: defender side, province, vtable slot of the attacker's share. Side: divisions, reserves.
    private const int CombatsOfGameState = 0x268, DefenderOfCombat = 0x30, ProvinceOfCombat = 0x38, ProgressSlot = 25;
    private const int DivisionsOfSide = 0x20, ReservesOfSide = 0x100;
    private const int MaxRestSearch = 6, MaxPlugsPerTurn = 2, MaxEscapesPerTurn = 2, MaxAttacksPerTurn = 2;
    private const int MaxSwapsPerArmy = 2;    // sent to rest per turn, so a front never changes all at once
    private const int MaxRestHours = 14 * 24; // then back regardless: supply may never let it recover
    private const int GuardHours = 7 * 24;    // longest a plugging or thicken helper holds its defence
    // Minimum organisation and strength (%) per job. Fresh: posts and reinforcements that may wait. Attack: own attacks and
    // pins. Helper: into a defence being lost, or a second line. Wall: next to a breach.
    private const int FreshOrg = 80, FreshStr = 70, AttackOrg = 70, AttackStr = 70, HelperOrg = 70, HelperStr = 60, WallOrg = 60, WallStr = 50;
    private const long PendingMs = 30_000;    // a unit we ordered is left alone this long (multiplayer: the command's round trip)
    private const long AiBudgetMs = 20;       // AI turns per tick, round robin; the rest wait for the next tick

    private static readonly int[] PlanKeys = ['E', 'H']; // with Ctrl+Alt held: activate, halt
    private readonly bool[] _down = new bool[PlanKeys.Length];

    /// <summary>
    /// What a division we took does. Rest: rotation, or an escape (Post = the province it left). Guard: holds a defence it
    /// was sent into (plugging, thicken). Reserve, Counter, Depth, Cordon: Defense.cs. Shuffle, Pin: Shuffle.cs. Operation:
    /// Operations.cs. Done: job over; it goes back once it can take orders.
    /// </summary>
    private enum Job { Rest, Reserve, Counter, Depth, Shuffle, Pin, Operation, Done, Cordon, Guard }

    /// <summary>
    /// A division we took. Group/Order: the order it goes back onto (0: none). Post: the province it holds, attacks from (Pin)
    /// or escaped from (Rest). Target: what it attacks (Counter, Pin), backs (Depth) or walls in (Cordon). Front: the front it serves.
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
        public readonly Dictionary<nint, int> Recent = []; // divisions we let go (and AI ones we sent): unit -> game hour
        public readonly Dictionary<int, int> EscapedFrom = []; // province divisions escaped from -> game hour
    }

    private readonly Dictionary<int, Side> _sides = [];                     // tag -> its bookkeeping
    private readonly Dictionary<int, int> _aiTurnHour = [];                 // AI tag -> the game hour of its last turn
    // The current turn's country (set by Begin):
    private Dictionary<nint, long> _pending = [];                             // unit -> until
    private Dictionary<nint, Held> _held = [];                               // every division we took; shared so no feature takes another's
    private Dictionary<int, (nint[] Units, bool Attack)> _sent = [];          // province we sent divisions into (to help or attack) -> them
    private bool _force;                                                      // an AI country's turn: commands posted as the AI posts them
    private bool _restore;                                                    // the next player turn puts back what the last game held
    private string _remembered = "";                                          // what Remember last wrote
    private int _nextAi;                                                      // the AI country the last tick's budget stopped at
    // Rebuilt every turn.
    private readonly Dictionary<nint, nint> _orders = [];                     // member -> order (rebuilt by Turn only)
    private readonly Dictionary<(nint Front, nint Province), int> _holding = [];
    private readonly HashSet<nint> _alive = [];                               // our land divisions
    private readonly Dictionary<nint, List<nint>> _at = [];                   // province -> land divisions we command there
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
            "Worn divisions in contact with the enemy rest behind the line and return once recovered. Only where another division of their front holds the province.");
        _wornOrg = Math.Clamp(Config.Bind("Rotation", "WornOrganisation", 30, "A front division below this organisation (%) goes to rest."), 0, 100);
        _wornStrength = Math.Clamp(Config.Bind("Rotation", "WornStrength", 50, "Or below this strength (%, equipment and manpower)."), 0, 100);
        _returnOrg = Math.Clamp(Config.Bind("Rotation", "ReturnOrganisation", 90, "A resting division goes back to its front at this organisation (%)."), 0, 100);
        _returnStrength = Math.Clamp(Config.Bind("Rotation", "ReturnStrength", 80,
            "And at this strength (%). After two weeks resting, organisation alone is enough."), 0, 100);
        _maxResting = Math.Clamp(Config.Bind("Rotation", "MaxRestingPercent", 25, "At most this share (%) of a front's divisions rests at once."), 1, 100);
        _plugging = Config.Bind("Plugging", "Plugging", true,
            "Sends fresh divisions that arrive in time into a defence being lost, and one to a line province facing twice its divisions. "
            + "Empty provinces around a fresh breach are manned (cordon).");
        _losing = Math.Clamp(Config.Bind("Plugging", "LosingPercent", 50, "A defence counts as being lost when the attacker holds this share (%) of the battle bar."), 50, 100);
        _plugRadius = Math.Clamp(Config.Bind("Plugging", "PlugRadius", 6, "How far (provinces) a helper may come from."), 1, MaxRestSearch);
        _lure = Config.Bind("Plugging", "Lure", true,
            "AI only (ApplyToAI): a defence being lost is let go to draw the attacker in, when both shoulders hold and 2+ fresh mobile divisions are within 3 provinces. "
            + "AI Assist's encirclement and the fire brigade then cut it off.");
        _escape = Config.Bind("Escape", "Escape", true,
            "Pulls front divisions out of a salient whose way back narrows to 1-2 provinces next to an enemy strong enough to close it. "
            + "A cut-off pocket out of supply (up to 30 provinces, friendly lines within 6) breaks out once a day.");
        _attack = Config.Bind("Attack", "Attack", true,
            "Takes empty enemy provinces next to the line, and attacks surrounded enemy divisions with at least as many fresh ones (more behind forts); a province always keeps one. "
            + "Best targets first; skips ones supply can't follow or that lengthen the front for nothing.");
        _odds = Math.Max(0, Config.Bind("Attack", "AttackOdds", 200,
            "Also attacks an enemy province our neighbouring fresh divisions outgun by this much (%: 200 = twice; more for forts, rough terrain, rivers). "
            + "Your divisions only on a running battle plan; 0 turns it off (mop-up too)."));
        _fireBrigade = Config.Bind("FireBrigade", "FireBrigade", true,
            "Keeps a few armored or motorized divisions per front off its battle plan, a province behind the line, to counterattack break-ins. "
            + "AI countries without spare mobile divisions use infantry.");
        _depth = Config.Bind("DepthDefense", "DepthDefense", true,
            "Puts a spare division a province behind key line provinces (ports, victory points, river crossings, cities, mountains) while the line holds there.");
        _shuffle = Config.Bind("Shuffle", "Shuffle", true,
            "Sends spare fresh divisions of quiet fronts to a losing front until a day after the crisis, and pins idle enemy stacks next to it. "
            + "A front facing only a pocket keeps twice the pocket's divisions; the rest join their army's main front.");
        _playerRepositions = Config.Bind("Player", "PlayerRepositions", false,
            "Takes your divisions off their battle plans to stand elsewhere: depth defence, fire brigade posts, shuffles, cordons. "
            + "Off: only actions where they stand and the operation keys; posted divisions go back onto their plan.");
        if (Config.Bind("Keys", "BattlePlanKeys", true, "Ctrl+Alt+E activates every battle plan at once, Ctrl+Alt+H halts them."))
            MainThread.Every(TimeSpan.FromMilliseconds(30), PollKeys);
        _ai = Config.Bind("AI", "ApplyToAI", false,
            "Single player: AI countries get the same micro (not rotation) and the lure, with the settings above. "
            + "AI Assist's matching features leave these divisions alone; without AI Assist the AI may take them back.");
        var operations = BindOperations();
        var micro = _rotation || _plugging || _escape || _attack || _fireBrigade || _depth || _shuffle;
        _ai &= micro && !Multiplayer;
        Jobs.AiMicro = _ai ? Info.Id : null;
        if (!micro && !operations) return;

        // Resolve the core's patterns now: a broken one fails the load, not a turn.
        _ = Sig.Find(Commands.AssignSig) + Sig.Find(Commands.UnassignSig) + Sig.Find(Game.IsAtWarWithSig) + Sig.Find(Game.SameOrAlliedSig)
            + Sig.Find(Province.FortLevelSig) + Sig.Find(Buildings.FindSig) + Sig.Find(Buildings.DbCallSig) + Sig.VTable(Signatures.LandCombat);
        _game = new PerGame(() => // one reset for the operations too: they share _held
        {
            _sides.Clear(); // Begin swaps a new side in
            _breakouts.Clear();
            _aiTurnHour.Clear();
            _lured.Clear();
            _orgSeen.Clear();
            _orders.Clear();
            _operations.Clear();
            foreach (var arrow in _blitzArrows.Concat(_pincerArrows)) MapArrows.Hide(arrow); // a no-op for an old map's
            _blitzArrows.Clear();
            _pincerArrows.Clear();
            _opOrdered.Clear();
            _opLastHour = -1;
            _restore = true; // a save loaded or the plugin reloaded: put back what the last game took off its plans
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
    /// The index of the key in <paramref name="keys"/> just pressed with Ctrl+Alt while the game has focus, or -1. Fires once
    /// per press; <paramref name="down"/> holds each key's last state.
    /// </summary>
    private static int Pressed(int[] keys, bool[] down)
    {
        var hit = -1;
        for (var i = 0; i < keys.Length; i++)
            if (Gui.HotkeyPressed(keys[i], ref down[i])) hit = i;
        return hit;
    }

    /// <summary>Presses play (or stop) for every army and army group of <paramref name="tag"/> whose plan isn't in that state yet.</summary>
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
    /// Starts every timer's turn: resets per-game state after a load or a country change, swaps in <paramref name="tag"/>'s
    /// bookkeeping and gathers its land divisions (<paramref name="units"/>, <see cref="_alive"/>, <see cref="_at"/>).
    /// Divisions gone since (died, merged) are forgotten before anything dereferences them. Returns the country, or 0
    /// outside a running game.
    /// </summary>
    private nint Begin(int tag, out nint[] units)
    {
        _game.Check();
        var side = SideOf(tag);
        (_pending, _held, _sent, _lastSeen, _crisis, _recent) = (side.Pending, side.Held, side.Sent, side.LastSeen, side.Crisis, side.Recent);
        foreach (var (unit, hour) in _recent.ToArray()) if (Game.Hour - hour is < 0 or >= RepeatHours) _recent.Remove(unit);
        foreach (var (id, hour) in _vacated.ToArray()) if (Game.Hour - hour is < 0 or >= RepeatHours) _vacated.Remove(id);
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
        if (player != 0) // 0: observer
        {
            if (_restore) Restore(player);
            Turn(player);
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
                // Once per game hour: the game changes hourly, so a second turn would redo the same work.
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
        foreach (var (id, sent) in _sent.ToArray()) // done once none is on its way (or, for an attack, fighting)
            if (!sent.Units.Any(u => _alive.Contains(u) && (_pending.ContainsKey(u) || Division.Walking(u) || sent.Attack && Division.Fighting(u))))
                _sent.Remove(id);

        // Orders of each army and its army group (a field marshal's front holds his armies' divisions).
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
        var all = fronts.Values.ToList();
        _eta.Clear();
        _losingNow = _plugging || _shuffle ? LosingDefences(tag) : [];
        if (_fireBrigade || _depth || _shuffle || _plugging) Survey(country, tag, all); // the lines, lost ground (cordon's breaches)

        Return(country, tag);
        // Not for the AI: its front orders take resting divisions straight back, so they walk to and fro.
        if (_rotation && !_force) foreach (var group in groups) RotateArmy(country, tag, group);
        if (_plugging) Plug(country, tag, units);
        if (_fireBrigade) Counterattack(country, tag, all);
        var reposition = _force || _playerRepositions; // the player's divisions stay put unless PlayerRepositions
        if (!reposition) ReleasePosts(tag);
        if (_plugging && reposition) Cordon(country, tag, units);
        if (_escape)
        {
            Escape(country, tag, units);
            Breakout(country, tag, units);
        }
        if (_shuffle && reposition)
        {
            Shuffle(country, tag, all);
            TrimPocketFronts(country, tag, all);
        }
        if (_depth && reposition) DepthDefense(country, tag, all);
        if (_fireBrigade && reposition) PostReserves(country, tag, all);
        if (_attack) Attack(country, tag);
    }

    /// <summary>
    /// Our defences being lost (attacker past LosingPercent, or the defenders' organisation gone within EarlyPlugHours) with
    /// the attacker's share (%), skipping ones a reserve already waits to join. Fills _eta for every defence.
    /// </summary>
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
    private readonly Dictionary<(int, nint), (int Hour, double Org)> _orgSeen = [];    // (tag, battle province) -> defenders' organisation at the last hour seen

    /// <summary>Hours until the defenders' organisation runs out at the rate it fell since the last hour seen; infinite while it holds.</summary>
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
    /// Takes the pool's division closest to <paramref name="target"/> that can walk there within <paramref name="hours"/>
    /// (MotorizedHoursPerProvince per province if mobile, else InfantryHoursPerProvince); 0 if none.
    /// </summary>
    private static nint TakeInTime(int tag, nint target, int radius, Dictionary<nint, List<nint>> pool, double hours)
    {
        if (pool.Count == 0) return 0;
        nint chosen = 0;
        var at = Search(tag, target, radius, (p, depth) =>
        {
            if (!pool.TryGetValue(p, out var here)) return false;
            chosen = Freshest(here.Where(u => depth * (Division.Motorized(u) ? MotorizedHoursPerProvince : InfantryHoursPerProvince) <= hours));
            return chosen != 0;
        });
        if (at == 0) return 0;
        if (pool[at].Remove(chosen) && pool[at].Count == 0) pool.Remove(at);
        return chosen;
    }

    /// <summary>
    /// Puts held divisions back onto their front when their job is done: resting ones once recovered (or after MaxRestHours),
    /// escaped ones once their salient's neck no longer closes, guards once their post is quiet or lost (or after GuardHours).
    /// Ones the player reordered or moved to another army are forgotten. The operations' timer runs it too, with possibly
    /// stale _orders; units with a command in flight (PendingMs) are skipped.
    /// </summary>
    private void Return(nint country, int tag)
    {
        foreach (var (unit, held) in _held.ToArray())
        {
            if (_pending.ContainsKey(unit)) continue; // our command is still on its way
            if (held.Job == Job.Operation) continue; // its operation's
            if (held.Job != Job.Pin && (_orders.ContainsKey(unit + MemberOfUnit) || Division.Group(unit) != held.Group))
            {
                _held.Remove(unit); // back on an order, or in another army: the player's now
                continue;
            }
            if (held.Job == Job.Done)
            {
                Release(unit, tag);
                continue;
            }
            if (Division.Walking(unit)) continue;
            var post = Game.Province(held.Post);
            if (held.Job == Job.Guard)
            {
                var holding = post != 0 && Province.Controller(post) == tag && EnemiesNext(country, tag, post) > 0 && Game.Hour - held.Since < GuardHours;
                if (!holding && !Division.Fighting(unit)) Release(unit, tag);
                continue;
            }
            if (held.Job != Job.Rest) continue;
            var overdue = Game.Hour - held.Since >= MaxRestHours;
            if (!overdue && Game.Hour - held.Since < MinRestHours) continue; // back sooner, it was sent off again at once
            if (!overdue && post != 0 && Province.Controller(post) == tag && Neck(country, tag, post) >= 0) continue; // escaped, and its salient still closes
            if (!overdue && (Org(unit) < _returnOrg || Str(unit) < _returnStrength)) continue;
            if (Release(unit, tag)) Log.Debug($"a division ({Org(unit):0}% organisation, {Str(unit):0}% strength) is back on its front after {Game.Hour - held.Since} hours off it");
        }
    }

    private void RotateArmy(nint country, int tag, nint group)
    {
        var parent = *(nint*)(group + OrdersGroup.ParentOffset);
        nint[] owners = parent == 0 ? [group] : [group, parent];
        if (!owners.SelectMany(o => Native.PdxArray(o + OrdersGroup.RootOrdersOffset).ToArray()).Any(o => o != 0 && IsFront(o))) return;

        // Worn divisions in contact go to rest, the most worn first.
        var swaps = 0;
        foreach (var unit in Native.PdxArray(group + OrdersGroup.MembersOffset).ToArray()
                     .Where(m => _orders.TryGetValue(m, out var o) && IsFront(o)).Select(m => m - MemberOfUnit)
                     .Where(u => Free(u, tag) && (Org(u) < _wornOrg || Str(u) < _wornStrength) && InContact(country, tag, u)).OrderBy(Org).ToList())
        {
            if (swaps == MaxSwapsPerArmy) break;
            var front = _orders[unit + MemberOfUnit];
            if (_holding.GetValueOrDefault((front, Division.Location(unit))) < 2) continue; // alone there: it holds on
            var id = Order.Id(front);
            var resting = _held.Values.Count(h => h.Job == Job.Rest && h.Group == group && h.Order == id);
            if ((resting + 1) * 100 > (Order.Members(front).Length + resting) * _maxResting) continue;
            var rest = RestProvince(country, tag, unit);
            if (rest < 0) continue; // nowhere safe to rest: it stays
            if (!Send(unit, rest, Job.Rest)) continue;
            swaps++;
            Log.Debug($"rotation: a division at {Org(unit):0}% organisation, {Str(unit):0}% strength goes to rest");
        }
    }

    /// <summary>
    /// Sends fresh divisions that arrive in time to defences being lost, soonest to fall first: two if it falls within
    /// UrgentPlugHours, else one. Then <see cref="Thicken"/>.
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
                if (Send(helper, id, Job.Guard, id)) sent.Add(helper); // it joins the defence as a reserve
            }
            if (sent.Count == 0) continue;
            _sent[id] = ([.. sent], false);
            plugs += sent.Count;
            Log.Debug($"plugging: {sent.Count} fresh division(s) sent into a defence being lost ({progress:0}% for the attacker, its organisation gone in {(double.IsInfinity(eta) ? "-" : $"{eta:0}h")})");
        }
        Thicken(country, tag, units, helpers);
    }

    private const int MinThinThreat = 3, ThinRatio = 2, MaxThickensPerTurn = 4;

    /// <summary>
    /// Before the attack: a line province facing more than ThinRatio times its divisions (MinThinThreat at least) gets the
    /// nearest fresh division within MaxRestSearch, most outnumbered first. Plugging only reacts once a defence is being lost.
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
            Log.Debug($"plugging: a fresh division thickens a thin spot ({ours} of ours against {enemy} enemy division(s) next to it)");
        }
    }

    private const int LureRadius = 3, MinLureReserves = 2, LureHours = 48;
    private bool _lure;
    private readonly Dictionary<int, int> _lured = []; // province id -> game hour it was let go (logged once)

    /// <summary>
    /// An AI defence being lost is let go, not plugged, when it can be cut off: both shoulders (our line provinces next to it)
    /// held and not being lost, and MinLureReserves+ fresh mobile divisions within LureRadius against at most twice as many
    /// attackers. AI Assist's encirclement then cuts the salient and the fire brigade counterattacks.
    /// </summary>
    private bool Lure(nint country, int tag, nint province)
    {
        var shoulders = Game.Neighbours(province).Select(Game.Province)
            .Where(p => p != 0 && Province.Controller(p) == tag && NextToEnemy(country, tag, p)).ToList();
        if (shoulders.Count < 2 || shoulders.Any(p => _losingNow.ContainsKey(p) || !Province.Units(p).ToArray().Any(u => u != 0 && Division.Commander(u) == tag))) return false;
        var attackers = EnemiesNext(country, tag, province);
        var reserves = 0;
        Search(tag, province, LureRadius, (p, _) =>
        {
            foreach (var u in Province.Units(p))
                if (u != 0 && Division.IsLand(u) && Division.Commander(u) == tag && Division.Motorized(u) && Org(u) >= AttackOrg && !Division.Fighting(u)) reserves++;
            return false; // count them all
        });
        if (reserves < MinLureReserves || attackers > 2 * reserves) return false;
        var id = Game.Id(province);
        foreach (var (lured, hour) in _lured.ToArray()) if (Game.Hour - hour is < 0 or >= LureHours) _lured.Remove(lured);
        if (_lured.TryAdd(id, Game.Hour))
            Log.Debug($"  #{tag} lure: lets {attackers} enemy division(s) into province {id} (both shoulders held, {reserves} mobile division(s) within {LureRadius})");
        return true;
    }

    /// <summary>
    /// Divisions that can leave where they stand, by province: ready, free or resting with us, <paramref name="wanted"/>, and
    /// spare on their front (or on no order, with <paramref name="unassigned"/>). Spare: behind the line, any; on it, one of
    /// the front stays; facing enemy troops, out of combat, with at least two (and as many as the enemy next to it) staying.
    /// </summary>
    private Dictionary<nint, List<nint>> Pool(nint country, int tag, IEnumerable<nint> units, bool unassigned, Func<nint, bool> wanted)
    {
        var left = new Dictionary<(nint Front, nint Province), int>(_holding);
        var pool = new Dictionary<nint, List<nint>>();
        foreach (var unit in units)
        {
            var resting = _held.TryGetValue(unit, out var held);
            if (resting && held.Job != Job.Rest || !Ready(unit, tag) || Division.Walking(unit)) continue;
            // An AI division between armies: the AI is moving it to its new army, so taking it starts a tug of war.
            if (_force && Division.Group(unit) == 0) continue;
            var location = Division.Location(unit);
            if (location == 0) continue;
            if (_orders.TryGetValue(unit + MemberOfUnit, out var order))
            {
                if (!IsFront(order)) continue;
                var stay = left.GetValueOrDefault((order, location)) - 1;
                var enemies = EnemiesNext(country, tag, location);
                if (enemies > 0 ? stay < 2 || Division.Fighting(unit) || stay < enemies
                    : stay < 1 && NextToEnemy(country, tag, location)) continue; // needed where it is
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
    /// Moves the unit for <paramref name="job"/> and books it, off its order first unless pinning. False, with nothing booked,
    /// if the move was refused: its way runs through enemy divisions (<see cref="Commands.ClearWay"/>) or the game rejected it.
    /// </summary>
    private bool Send(nint unit, int province, Job job, int post = 0, int target = 0, (nint, int) front = default)
    {
        var location = Division.Location(unit);
        if (!Move(unit, province)) return false;
        _orders.TryGetValue(unit + MemberOfUnit, out var order);
        if (_holding.TryGetValue((order, location), out var n)) _holding[(order, location)] = n - 1; // one fewer holds it
        Book(unit, job, job == Job.Pin ? 0 : order, post, target, front);
        Arrow(unit, province, job.ToString(), job switch
        {
            Job.Rest => (80, 120, 255),
            Job.Counter or Job.Pin => (220, 60, 220),
            _ => (60, 220, 60), // a post or a reinforcement
        });
        if (_force) _recent[unit] = Game.Hour;
        return true;
    }

    /// <summary>A plain move, posted the AI's way on an AI turn; the unit is then left alone for PendingMs. False: refused.</summary>
    private bool Move(nint unit, int province)
    {
        if (!Commands.Move(unit, province, _force)) return false;
        _pending[unit] = _now + PendingMs;
        return true;
    }

    /// <summary>Logs the move; with [Debug] AiActionArrows, also flashes an arrow on the map.</summary>
    private void Arrow(nint unit, int to, string what, (byte R, byte G, byte B) colour)
    {
        var from = Division.Location(unit);
        var owner = Division.Owner(unit);
        Log.Debug($"  {(owner == Game.PlayerTag ? "player" : $"#{owner}")} {what}: province {(from != 0 ? Game.Id(from) : 0)} -> {to}");
        if (from != 0) MapArrows.Flash(Game.Id(from), to, colour.R, colour.G, colour.B);
    }

    /// <summary>
    /// Books the unit for <paramref name="job"/>, taking it off <paramref name="order"/> (0: none) to return to later. One
    /// already held keeps its original order.
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

    /// <summary>Lets the division go (<see cref="PutBack"/>). False: it can't take orders now.</summary>
    private bool Release(nint unit, int tag)
    {
        if (!Ready(unit, tag)) return false;
        _held.Remove(unit, out var held);
        PutBack(unit, held);
        return true;
    }

    /// <summary>An AI division the AI walked off our post is let go (<see cref="PutBack"/>); moved back, it went to and fro.</summary>
    private void GiveUp(nint unit, string job)
    {
        if (!_held.Remove(unit, out var held)) return;
        PutBack(unit, held);
        Log.Debug($"{job}: the AI moved a division off its post; it goes back onto its battle plan");
    }

    /// <summary>
    /// Lets a division go: back onto its order if it had one and is still in that army, and not taken again for RepeatHours.
    /// The post it left (and a second line's key province) isn't refilled for RepeatHours either, or the next division there
    /// would go the same way.
    /// </summary>
    private void PutBack(nint unit, Held held)
    {
        _recent[unit] = Game.Hour;
        if (held.Job is Job.Reserve or Job.Counter or Job.Depth or Job.Cordon)
        {
            if (held.Post != 0) _vacated[held.Post] = Game.Hour;
            if (held.Job == Job.Depth && held.Target != 0) _vacated[held.Target] = Game.Hour;
        }
        if (held.Order != 0 && Division.Group(unit) == held.Group && Commands.Assign(unit, held.Group, held.Order, _force)) _pending[unit] = _now + PendingMs;
    }

    private readonly Dictionary<int, int> _vacated = []; // province id -> hour a post (or key) there was left

    private bool Vacated(int id) => _vacated.TryGetValue(id, out var at) && Game.Hour - at is >= 0 and < RepeatHours;

    private bool _playerRepositions;

    /// <summary>The player's divisions on posts elsewhere (reserve, second line, cordon, shuffle) go back onto their battle plan once out of combat.</summary>
    private void ReleasePosts(int tag)
    {
        foreach (var (unit, held) in _held.ToArray())
            if (held.Job is Job.Reserve or Job.Counter or Job.Depth or Job.Cordon or Job.Shuffle && !Division.Fighting(unit)) Release(unit, tag);
    }

    private static bool IsFront(nint order) => Order.Type(order) == Order.Front;

    /// <summary><see cref="Division.Ready"/>, no command in flight, not another plugin's job, and not let go (or, for the AI, sent) lately unless still ours.</summary>
    private bool Ready(nint unit, int tag) =>
        Division.Ready(unit, tag) && !(_pending.TryGetValue(unit, out var until) && _now < until) && !Jobs.Taken(unit, Info.Id) // AI Assist's posts stay
        && (_held.ContainsKey(unit) || !_recent.ContainsKey(unit));

    // RepeatHours: a division we let go (or an AI one we sent) isn't taken again this long, or AI front orders pulling it
    // back make it walk to and fro. MinRestHours: the shortest rest.
    private const int RepeatHours = 72, MinRestHours = 72;
    private Dictionary<nint, int> _recent = [];

    /// <summary>Ready, and doing no job for us.</summary>
    private bool Free(nint unit, int tag) => Ready(unit, tag) && !_held.ContainsKey(unit);

    /// <summary>Organisation and strength in % of their maximum (the ratios has_unit_organization reads).</summary>
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
    private int EnemiesNext(nint country, int tag, nint province) =>
        Game.Neighbours(province).Sum(n => Game.Province(n) is var p && p != 0 && Enemy(country, tag, p) ? Province.Divisions(p) : 0);

    /// <summary>Enemy divisions stand next to the unit. Out of contact, a worn division recovers as well on the line.</summary>
    private bool InContact(nint country, int tag, nint unit)
    {
        var location = Division.Location(unit);
        return location != 0 && EnemiesNext(country, tag, location) > 0;
    }

    private static readonly string HeldFile = Path.Combine(Loader.ModsDir, "cache", "frontline_held.txt");

    /// <summary>Saves the player's held divisions (unit and army refs, order id) when they change, so a reload can put them back.</summary>
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
    /// After a load: listed divisions still the player's, in the same army and on no order go back onto their order. Refs from
    /// another campaign fail these checks.
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
                || !Division.Ready(unit, tag, headquarters: true) || !Commands.Assign(unit, group, order)) continue; // HQs too: older versions took them
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
