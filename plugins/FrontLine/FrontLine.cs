using HOI4ModLoader;

namespace FrontLine;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>COrderAssignCommand ctor (mem[0x50], CPdxArray&lt;CArmy*&gt;* units, COrdersGroup* group, int orderId, bool): onto one of the group's (or its parent's) orders.</summary>
    [Signature] public const string AssignCommand = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 4C 24 ? 56 57 41 54 41 56 41 57 48 83 EC 30 41 8B D9 49 8B E8 4C 8B FA 4C 8B F1 0F B6 BC 24 80 00 00 00 C6 41 08 00";

    /// <summary>COrderUnassignCommand ctor (mem[0x50], CArmy* unit, COrdersGroup* group, bool ungroup, bool alsoParent): off every order, into the army's reserve.</summary>
    [Signature] public const string UnassignCommand = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 41 0F B6 F1 49 8B F8 48 8B EA 48 8B D9 C6 41 08 00";

    /// <summary>CCountry::IsAtWarWith(country, int* {tag, tag index}) -> bool.</summary>
    [Signature] public const string IsAtWarWith = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 8B 41 08 48 8B FA 8B 12 48 8B F1 3B D0 74 ? 85 D2 74 ? 85 C0 74 ? 48 83 C1 08 E8 ? ? ? ? 39 47 04 74 ? 83 7E 08 00 7E ? 48 8B 86 ? ? ? ?";

    /// <summary>FortLevel(CProvince*) -> int: land forts, as the AI's micro attacks weigh them.</summary>
    [Signature] public const string FortLevel = "4C 63 91 EC 01 00 00 33 C0 4D 85 D2 7E ? 4C 8B 99 E0 01 00 00 44 8B C8 0F 1F 84 00 00 00 00 00 4B 8B 0C CB 49 FF C1 48 8B 91 E0 01 00 00 44 0F BF 41 42 44 0F AF 82 08 03 00 00";

    [VTable] public const string LandCombat = "CLandCombat";

    /// <summary>The battle plan's play button: (group CRefObj*, int orderId 0 = the whole group, char execute, char alsoArmies).</summary>
    [VTable] public const string ExecutePlan = "COrderExecuteCommand";
}

/// <summary>
/// Front line micro a player does by hand, with the same commands the battle plan UI sends (multiplayer works):
/// - Rotation: a worn-out division on a front line (low organisation or strength, not fighting, enemy next to it) comes off
///   the battle plan and rests a few provinces back, when another of our divisions on that front holds the same province
///   (so no province is left empty) and few enough of that front are resting.
/// - Plugging: a defence of ours that is being lost gets the nearest fresh division (resting, unassigned, or from a quiet
///   part of a front where another of ours stays) sent into the battle, where it joins the defence.
/// - Escape: a front division in a salient whose way back narrows to one province next to the enemy pulls back before
///   the pocket closes.
/// - Attack: an empty enemy province next to the line is taken by a fresh division; enemy divisions already surrounded are
///   attacked by enough fresh ones to outnumber them, forts counted (a province of ours always keeps one).
/// - Ctrl+Alt+E activates every battle plan, Ctrl+Alt+H halts them (the plan window's own play button, for every group).
/// Divisions taken off a front go back onto it once rested and free; one short of equipment or manpower goes back after
/// MaxRestHours once its organisation is back. A reload forgets them: they stay where they are.
/// </summary>
[Plugin("com.green1052.frontline", "Front Line", "1.0.0", GameVersion = "1.19.3")]
public unsafe class FrontLine : Plugin
{
    // Layouts for HOI4 1.19.3. CArmy (a land division):
    private const int KindOfUnit = 0x08, MemberOfUnit = 0xB8, GroupOfUnit = 0xC0, Combats = 0x1A8;
    private const int CommanderTag = 0x1D8, OwnerTag = 0x1E0, LocationOfUnit = 0x1F0, Path = 0x200, Retreating = 0x24C, Busy = 0x2F8;
    private const int StrengthRatioSlot = 34, OrgRatioSlot = 39; // CArmy vtable: (unit, long* out) -> out, 1e5 = 100%
    private const int UnitsOfCountry = 0x290, ProvidersOfCountry = 0x168; // CCountry: providers, one per theatre
    // COrdersGroup and its orders (COrderInstance):
    private const int BorderWar = 0x1B0, SelfRefOfGroup = 0x8, GroupsOfProvider = 0xB0; // the group's CRef {id, idx}; a provider's armies and army groups
    private const int RootOrders = 0x98;
    private static readonly int[] OrderLists = [RootOrders, 0xB0, 0xC8]; // root (fronts, offensive lines), fallback lines, invasions/paradrops
    private const int TypeOfOrder = 0x30, MembersOfOrder = 0x210, IdOfOrder = 0x244, ExecutingOfOrder = 0x248, FrontOrder = 2;
    // COrderExecuteCommand: group CRef at +0x28, order id at +0x30 (0: the whole group), execute, also its armies.
    private const int ExecuteCommandSize = 0x38, GroupOfCommand = 0x28, ExecuteOfCommand = 0x34, AlsoArmiesOfCommand = 0x35;
    // Land combat: every combat at gamestate +0x268; CLandCombat sides and their divisions.
    private const int CombatsOfGameState = 0x268, DefenderOfCombat = 0x30, ProvinceOfCombat = 0x38, ProgressSlot = 25;
    private const int DivisionsOfSide = 0x20, ReservesOfSide = 0x100;
    private const int HourOfGameState = 0x468;
    // Provinces:
    private const int IdOfProvince = 164, DivisionsInProvince = 0x11C, ControllerOfProvince = 392;
    private const int MaxRestSearch = 6, MaxNeckSearch = 8, MaxPlugsPerTurn = 2, MaxEscapesPerTurn = 2, MaxAttacksPerTurn = 2;
    private const int RestDepth = 2;          // provinces behind the line a division rests (none of its neighbours enemy)
    private const int MaxSwapsPerArmy = 2;    // sent to rest at a time, so a front never changes all at once
    private const int MaxRestHours = 14 * 24; // short of equipment or manpower, strength may never come back: organisation is enough then
    private const long PendingMs = 30_000;    // a unit we gave orders to is left alone meanwhile (multiplayer: a round trip)

    private static readonly int[] PlanKeys = ['E', 'H']; // with Ctrl+Alt held: activate, halt
    private readonly bool[] _down = new bool[PlanKeys.Length];

    private delegate* unmanaged<nint, nint, nint, int, byte, nint> _assign;
    private delegate* unmanaged<nint, nint, nint, byte, byte, nint> _unassign;
    private delegate* unmanaged<nint, int*, byte> _atWar;
    private delegate* unmanaged<nint, int> _fortLevel;

    private readonly Dictionary<nint, long> _pending = [];                                // unit -> until
    private readonly Dictionary<nint, (nint Group, int Order, int Since)> _resting = []; // unit we took off a front -> where it goes back, since (game hour)
    private readonly Dictionary<int, (nint[] Units, bool Attack)> _sent = [];          // province we sent divisions into (a battle to help, an attack) -> them
    private PerGame _game = null!;
    private bool _rotation, _plugging, _escape, _attack;
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
            "Pull a front division back out of a salient whose way back narrows to a single province next to the enemy, before the pocket closes.");
        _attack = Config.Bind("Attack", "Attack", true,
            "Take empty enemy provinces next to the line with a fresh division, and attack enemy divisions already surrounded with enough fresh ones to outnumber them (forts counted); a province of ours always keeps one.");
        if (Config.Bind("Keys", "BattlePlanKeys", true, "Ctrl+Alt+E activates every battle plan at once, Ctrl+Alt+H halts them."))
            MainThread.Every(TimeSpan.FromMilliseconds(30), PollKeys);

        if (!_rotation && !_plugging && !_escape && !_attack) return;
        _assign = (delegate* unmanaged<nint, nint, nint, int, byte, nint>)Sig.Find(Signatures.AssignCommand);
        _unassign = (delegate* unmanaged<nint, nint, nint, byte, byte, nint>)Sig.Find(Signatures.UnassignCommand);
        _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        _fortLevel = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FortLevel);
        _ = Sig.VTable(Signatures.LandCombat);
        _game = new PerGame(() =>
        {
            _pending.Clear();
            _resting.Clear();
            _sent.Clear();
        });
        MainThread.Every(TimeSpan.FromSeconds(2), Manage);
    }

    /// <summary>Fires once per key press while Ctrl+Alt is held (a key held across a modifier change does not re-fire).</summary>
    private void PollKeys()
    {
        var hot = Gui.CtrlHeld && Gui.AltHeld && Gui.GameFocused;
        var hit = -1;
        for (var i = 0; i < PlanKeys.Length; i++)
        {
            var down = Gui.KeyHeld(PlanKeys[i]);
            if (hot && down && !_down[i]) hit = i;
            _down[i] = down;
        }
        if (hit < 0) return;
        var tag = Game.ControlledTag;
        if (tag != 0) Execute(tag, hit == 0); // 0: main menu, loading, observer
    }

    /// <summary>Every army and army group of <paramref name="tag"/> whose plan runs counter to <paramref name="activate"/> gets the play (or stop) button's command.</summary>
    private void Execute(int tag, bool activate)
    {
        var country = Game.Country(tag);
        if (country == 0) return;
        var changed = 0;
        foreach (var provider in Native.PdxArray(country + ProvidersOfCountry).ToArray())
        foreach (var group in Native.PdxArray(provider + GroupsOfProvider).ToArray())
        {
            if (!OrderLists.Any(list => Native.PdxArray(group + list).ToArray().Any(o => o != 0 && (*(byte*)(o + ExecutingOfOrder) != 0) != activate))) continue;
            var cmd = Commands.New(Signatures.ExecutePlan, ExecuteCommandSize, tag, 0); // order id 0: the whole group
            *(nint*)(cmd + GroupOfCommand) = *(nint*)(group + SelfRefOfGroup);
            *(byte*)(cmd + ExecuteOfCommand) = (byte)(activate ? 1 : 0);
            *(byte*)(cmd + AlsoArmiesOfCommand) = 1; // an army group's command also reaches its armies
            if (Commands.TryPost(cmd)) changed++;
        }
        Log.Info($"{(activate ? "activated" : "halted")} {changed} battle plan(s)");
    }

    private void Manage()
    {
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var now = Environment.TickCount64;
        foreach (var (unit, until) in _pending.ToArray())
            if (now >= until) _pending.Remove(unit);
        var units = Native.PdxArray(country + UnitsOfCountry).ToArray();
        foreach (var unit in _resting.Keys.Except(units).ToList()) _resting.Remove(unit); // gone
        var alive = units.ToHashSet();
        foreach (var (id, sent) in _sent.ToArray()) // done once none of them is on the way (or, attacking, fighting) any more
            if (!sent.Units.Any(u => alive.Contains(u) && (_pending.ContainsKey(u) || Walking(u) || sent.Attack && *(int*)(u + Combats + 0xC) != 0)))
                _sent.Remove(id);

        // Every order a division is on, of its army and of its army group (a field marshal's front holds his armies' divisions).
        var orders = new Dictionary<nint, nint>(); // member -> order
        var groups = units.Select(u => *(nint*)(u + GroupOfUnit)).Where(g => g != 0 && !new OrdersGroup(g).IsArmyGroup && *(nint*)(g + BorderWar) == 0).Distinct().ToList();
        foreach (var owner in groups.Concat(groups.Select(g => *(nint*)(g + OrdersGroup.ParentOffset)).Where(p => p != 0)).Distinct())
        foreach (var list in OrderLists)
        foreach (var order in Native.PdxArray(owner + list))
        foreach (var member in Native.PdxArray(order + MembersOfOrder))
            orders[member] = order;

        Return(tag, orders, now);
        if (_rotation) foreach (var group in groups) RotateArmy(country, tag, group, orders, now);
        if (_plugging) Plug(country, tag, units, orders, now);
        if (_escape) Escape(country, tag, units, orders, now);
        if (_attack) Attack(country, tag, units, now);
    }

    /// <summary>Divisions we took off a front go back onto it once rested; ones the player gave other orders are forgotten.</summary>
    private void Return(int tag, Dictionary<nint, nint> orders, long now)
    {
        foreach (var (unit, rest) in _resting.ToArray())
        {
            if (_pending.ContainsKey(unit)) continue; // our command is still on its way
            if (orders.ContainsKey(unit + MemberOfUnit) || *(nint*)(unit + GroupOfUnit) != rest.Group)
            {
                _resting.Remove(unit); // back on an order, or into another army: the player's now
                continue;
            }
            if (!Free(unit, tag, now) || Walking(unit) || Org(unit) < _returnOrg) continue;
            if (Str(unit) < _returnStrength && Hour - rest.Since < MaxRestHours) continue;
            if (!Assign(unit, rest.Group, rest.Order)) continue;
            _pending[unit] = now + PendingMs;
            _resting.Remove(unit);
            Log.Info($"a rested division ({Org(unit):0}% organisation, {Str(unit):0}% strength) is back on its front");
        }
    }

    private void RotateArmy(nint country, int tag, nint group, Dictionary<nint, nint> orders, long now)
    {
        var parent = *(nint*)(group + OrdersGroup.ParentOffset);
        nint[] owners = parent == 0 ? [group] : [group, parent];
        if (!owners.SelectMany(o => Native.PdxArray(o + RootOrders).ToArray()).Any(IsFront)) return;
        var members = Native.PdxArray(group + OrdersGroup.MembersOffset).ToArray();

        // Worn and in contact: off the front to rest, where another of ours on that front holds the province.
        var frontOf = members.Where(m => orders.TryGetValue(m, out var o) && IsFront(o)).ToDictionary(m => m, m => orders[m]);
        var holding = Holding(frontOf);
        var swaps = 0;
        foreach (var unit in frontOf.Keys.Select(m => m - MemberOfUnit)
                     .Where(u => Free(u, tag, now) && (Org(u) < _wornOrg || Str(u) < _wornStrength) && InContact(country, tag, u)).OrderBy(Org))
        {
            if (swaps == MaxSwapsPerArmy) break;
            var front = frontOf[unit + MemberOfUnit];
            var place = (front, *(nint*)(unit + LocationOfUnit));
            if (holding[place] < 2) continue; // alone there: it holds on
            var id = *(int*)(front + IdOfOrder);
            var resting = _resting.Values.Count(r => r.Group == group && r.Order == id);
            if ((resting + 1) * 100 > (Native.PdxArray(front + MembersOfOrder).Length + resting) * _maxResting) continue;
            var rest = RestProvince(country, tag, unit);
            if (rest < 0) continue; // nowhere safe to rest: it stays
            TakeOff(unit, front);
            Commands.Move(unit, rest);
            _pending[unit] = now + PendingMs;
            holding[place]--;
            swaps++;
            Log.Info($"rotation: a division at {Org(unit):0}% organisation, {Str(unit):0}% strength goes to rest");
        }
    }

    /// <summary>Our defences being lost get the nearest fresh division: resting, unassigned, or spare in a quiet part of a front.</summary>
    private void Plug(nint country, int tag, nint[] units, Dictionary<nint, nint> orders, long now)
    {
        var landCombat = Sig.VTable(Signatures.LandCombat);
        var plugs = 0;
        Dictionary<nint, List<nint>>? helpersAt = null;
        foreach (var combat in Native.PdxArray(Game.State + CombatsOfGameState).ToArray())
        {
            if (plugs == MaxPlugsPerTurn) break;
            if (*(nint*)combat != landCombat) continue;
            var defender = *(nint*)(combat + DefenderOfCombat);
            var defending = Native.PdxArray(defender + DivisionsOfSide);
            if (defending.Length == 0 || *(int*)(defending[0] + CommanderTag) != tag) continue;
            if (*(int*)(defender + ReservesOfSide + 0xC) > 0) continue; // a reserve already waits to step in
            long progress;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)combat)[ProgressSlot])(combat, &progress); // attacker's share
            if (progress * 100.0 / Native.FixedPoint < _losing) continue;
            var province = *(nint*)(combat + ProvinceOfCombat);
            var id = *(int*)(province + IdOfProvince);
            if (_sent.ContainsKey(id)) continue; // one helper per battle on its way at a time

            helpersAt ??= Helpers(country, tag, units, orders, now);
            var helper = Nearest(tag, province, _plugRadius, helpersAt);
            if (helper == 0) continue;
            if (orders.TryGetValue(helper + MemberOfUnit, out var order)) TakeOff(helper, order);
            Commands.Move(helper, id); // arriving, it joins the defence as a reserve
            _pending[helper] = now + PendingMs;
            _sent[id] = ([helper], false);
            helpersAt[*(nint*)(helper + LocationOfUnit)].Remove(helper);
            plugs++;
            Log.Info($"plugging: a fresh division sent into a defence being lost ({progress * 100.0 / Native.FixedPoint:0}% for the attacker)");
        }
    }

    /// <summary>Fresh divisions that can leave where they are, by province: resting or unassigned, or spare on a quiet front.</summary>
    private Dictionary<nint, List<nint>> Helpers(nint country, int tag, nint[] units, Dictionary<nint, nint> orders, long now)
    {
        var frontOf = units.Select(u => u + MemberOfUnit).Where(m => orders.TryGetValue(m, out var o) && IsFront(o)).ToDictionary(m => m, m => orders[m]);
        var holding = Holding(frontOf);
        var helpers = new Dictionary<nint, List<nint>>();
        foreach (var unit in units)
        {
            if (!Free(unit, tag, now) || Walking(unit) || Org(unit) < 80 || Str(unit) < 70) continue;
            var member = unit + MemberOfUnit;
            var location = *(nint*)(unit + LocationOfUnit);
            if (location == 0) continue;
            if (orders.TryGetValue(member, out var order))
            {
                if (!IsFront(order) || holding[(order, location)] < 2 || InContact(country, tag, unit)) continue; // needed where it is
                holding[(order, location)]--;
            }
            if (!helpers.TryGetValue(location, out var here)) helpers[location] = here = [];
            here.Add(unit);
        }
        return helpers;
    }

    /// <summary>The helper closest to <paramref name="target"/> (breadth first over our provinces), or 0.</summary>
    private static nint Nearest(int tag, nint target, int radius, Dictionary<nint, List<nint>> helpersAt)
    {
        var seen = new HashSet<int> { *(int*)(target + IdOfProvince) };
        var frontier = new List<nint> { target };
        for (var depth = 1; depth <= radius && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var province in frontier)
            foreach (var id in Game.Neighbours(province))
            {
                if (!seen.Add(id)) continue;
                var p = Game.Province(id);
                if (p == 0 || *(int*)(p + ControllerOfProvince) != tag) continue;
                if (helpersAt.TryGetValue(p, out var here) && here.Count > 0) return here.MaxBy(Org);
                next.Add(p);
            }
            frontier = next;
        }
        return 0;
    }

    /// <summary>
    /// Empty enemy provinces next to the line get one fresh division; surrounded enemy divisions as many as outnumber them
    /// with forts counted (as vanilla's local attacks weigh them), or none. A province of ours always keeps one. Nothing
    /// more goes where ours are still on their way or fighting. Plain moves: front divisions go back to their front by themselves afterwards.
    /// </summary>
    private void Attack(nint country, int tag, nint[] units, long now)
    {
        var attacks = 0;
        foreach (var here in units.Where(u => *(int*)(u + KindOfUnit) == 0 && *(int*)(u + CommanderTag) == tag && *(nint*)(u + LocationOfUnit) != 0)
                     .GroupBy(u => *(nint*)(u + LocationOfUnit)))
        {
            if (attacks == MaxAttacksPerTurn) break;
            var spare = here.Count() - 1; // one stays
            var fresh = here.Where(u => Free(u, tag, now) && !Walking(u) && Org(u) >= 70 && Str(u) >= 70).OrderByDescending(Org).ToList();
            foreach (var id in Game.Neighbours(here.Key))
            {
                var free = Math.Min(spare, fresh.Count);
                if (free == 0 || attacks == MaxAttacksPerTurn) break;
                if (_sent.ContainsKey(id)) continue;
                var target = Game.Province(id);
                if (target == 0 || !Enemy(country, tag, target)) continue;
                var defenders = *(int*)(target + DivisionsInProvince);
                var need = defenders == 0 ? 1 : Surrounded(country, tag, target) ? defenders * (_fortLevel(target) / 2 + 1) : 0;
                if (need == 0 || need > free) continue; // not a pocket, or too strong for what can leave here
                var sent = fresh.GetRange(0, need);
                fresh.RemoveRange(0, need);
                spare -= need;
                foreach (var unit in sent)
                {
                    Commands.Move(unit, id);
                    _pending[unit] = now + PendingMs;
                }
                _sent[id] = ([.. sent], true);
                attacks++;
                Log.Info(defenders == 0 ? "opportunity: a fresh division takes an empty enemy province" : $"mop-up: {need} fresh division(s) attack {defenders} surrounded enemy division(s)");
            }
        }
    }

    /// <summary>No land neighbour of the enemy province is held by a country at war with us: its divisions can't get out.</summary>
    private bool Surrounded(nint country, int tag, nint province) =>
        Game.Neighbours(province).All(n => Game.Province(n) is var p && (p == 0 || *(int*)(p + ControllerOfProvince) == 0 || !Enemy(country, tag, p)));

    /// <summary>Front divisions in a salient whose way back narrows to one province next to the enemy pull back.</summary>
    private void Escape(nint country, int tag, nint[] units, Dictionary<nint, nint> orders, long now)
    {
        var escapes = 0;
        var checkedPlaces = new Dictionary<nint, bool>(); // province -> narrow
        var holding = Holding(units.Select(u => u + MemberOfUnit).Where(m => orders.TryGetValue(m, out var o) && IsFront(o)).ToDictionary(m => m, m => orders[m]));
        foreach (var unit in units)
        {
            if (escapes == MaxEscapesPerTurn) break;
            if (!orders.TryGetValue(unit + MemberOfUnit, out var order) || !IsFront(order) || !Free(unit, tag, now)) continue;
            var location = *(nint*)(unit + LocationOfUnit);
            if (location == 0) continue;
            if (holding[(order, location)] < 2) continue; // alone there: pulling it out opens the line
            if (!checkedPlaces.TryGetValue(location, out var narrow)) checkedPlaces[location] = narrow = Narrow(country, tag, location);
            if (!narrow) continue;
            var rest = RestProvince(country, tag, unit);
            if (rest < 0) continue;
            TakeOff(unit, order);
            Commands.Move(unit, rest);
            _pending[unit] = now + PendingMs;
            holding[(order, location)]--;
            escapes++;
            Log.Info("escape: a division pulls back from a salient about to be cut off");
        }
    }

    /// <summary>
    /// Going back from <paramref name="from"/> over our provinces, one ring (provinces at the same distance) is a single
    /// province next to the enemy before reaching provinces away from the enemy: the neck of a closing pocket.
    /// </summary>
    // ponytail: rings, not a max-flow of disjoint paths; a wide salient with a one-province gap elsewhere is missed.
    private bool Narrow(nint country, int tag, nint from)
    {
        if (!NextToEnemy(country, tag, from)) return false;
        var seen = new HashSet<int> { *(int*)(from + IdOfProvince) };
        var ring = new List<nint> { from };
        for (var depth = 1; depth <= MaxNeckSearch; depth++)
        {
            var next = new List<nint>();
            foreach (var province in ring)
            foreach (var id in Game.Neighbours(province))
            {
                if (!seen.Add(id)) continue;
                var p = Game.Province(id);
                if (p != 0 && *(int*)(p + ControllerOfProvince) == tag) next.Add(p);
            }
            if (next.Count == 0) return false;                                    // cut off already, or an island: nowhere to go
            if (next.Any(p => !NextToEnemy(country, tag, p))) return false;       // safe ground within reach
            if (next.Count == 1) return EnemyDivisionsNext(country, tag, next[0]); // the only way back, and enemy troops stand next to it
                                                                                   // (a thin coastal strip borders enemy land everywhere: not a pocket)
            ring = next;
        }
        return false;
    }

    private bool EnemyDivisionsNext(nint country, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && *(int*)(p + DivisionsInProvince) > 0 && Enemy(country, tag, p));

    private bool NextToEnemy(nint country, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Enemy(country, tag, p));

    private static bool IsFront(nint order) => *(int*)(order + TypeOfOrder) == FrontOrder;

    private static bool Walking(nint unit) => *(int*)(unit + Path + 0xC) != 0;

    private static int Hour => *(int*)(Game.State + HourOfGameState);

    /// <summary>Divisions of each front per province.</summary>
    private static Dictionary<(nint Front, nint Province), int> Holding(Dictionary<nint, nint> frontOf)
    {
        var holding = new Dictionary<(nint Front, nint Province), int>();
        foreach (var (member, front) in frontOf)
        {
            var place = (front, *(nint*)(member - MemberOfUnit + LocationOfUnit));
            holding[place] = holding.GetValueOrDefault(place) + 1;
        }
        return holding;
    }

    /// <summary>Off its front, remembering where it goes back to.</summary>
    private void TakeOff(nint unit, nint front)
    {
        var group = *(nint*)(unit + GroupOfUnit);
        Unassign(unit, group);
        _resting[unit] = (group, *(int*)(front + IdOfOrder), Hour);
    }

    /// <summary>A land division of <paramref name="tag"/> (not lent) that can take orders now: not fighting, retreating, embarked or just ordered.</summary>
    private bool Free(nint unit, int tag, long now) =>
        *(int*)(unit + KindOfUnit) == 0 && *(int*)(unit + CommanderTag) == tag && *(int*)(unit + OwnerTag) == tag
        && *(int*)(unit + Combats + 0xC) == 0 && *(byte*)(unit + Retreating) == 0 && *(nint*)(unit + Busy) == 0
        && !_pending.ContainsKey(unit);

    /// <summary>Organisation and strength in % of their maximum, from the division's own ratio getters (what has_unit_organization reads).</summary>
    private static double Org(nint unit) => Ratio(unit, OrgRatioSlot);
    private static double Str(nint unit) => Ratio(unit, StrengthRatioSlot);

    private static double Ratio(nint unit, int slot)
    {
        long ratio;
        ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[slot])(unit, &ratio);
        return ratio * 100.0 / Native.FixedPoint;
    }

    /// <summary>
    /// A province of ours at least RestDepth steps behind the unit, with no enemy-held neighbour, reached through our own
    /// provinces; -1 if none nearby.
    /// </summary>
    // ponytail: looks at who holds provinces, not at enemy units inside ours (paratroopers, a breakthrough in progress).
    private int RestProvince(nint country, int tag, nint unit)
    {
        var location = *(nint*)(unit + LocationOfUnit);
        if (location == 0) return -1;
        var seen = new HashSet<int> { *(int*)(location + IdOfProvince) };
        var frontier = new List<nint> { location };
        for (var depth = 1; depth <= MaxRestSearch && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var province in frontier)
            foreach (var id in Game.Neighbours(province))
            {
                if (!seen.Add(id)) continue;
                var p = Game.Province(id);
                if (p == 0 || *(int*)(p + ControllerOfProvince) != tag) continue;
                if (depth >= RestDepth && !NextToEnemy(country, tag, p)) return id;
                next.Add(p);
            }
            frontier = next;
        }
        return -1;
    }

    /// <summary>Enemy divisions stand next to the unit: only then is a worn division at risk on the line (it recovers as fast there otherwise).</summary>
    private bool InContact(nint country, int tag, nint unit)
    {
        var location = *(nint*)(unit + LocationOfUnit);
        return location != 0 && Game.Neighbours(location).Any(n => Game.Province(n) is var p && p != 0 && *(int*)(p + DivisionsInProvince) > 0 && Enemy(country, tag, p));
    }

    /// <summary>The province is held by a country at war with us.</summary>
    private bool Enemy(nint country, int tag, nint province)
    {
        var controller = *(int*)(province + ControllerOfProvince);
        if (controller <= 0 || controller == tag) return false;
        var other = stackalloc int[] { controller, Game.TagIndex(controller) };
        return _atWar(country, other) != 0;
    }

    // Commands from the game's own constructors. ponytail: a refused one is freed without its inner arrays (a few bytes).
    private bool Assign(nint unit, nint group, int order)
    {
        var cmd = Allocator.AllocZeroed(0x50);
        Native.WithPdxArray([unit], units => _assign(cmd, units, group, order, 0));
        return Commands.TryPost(cmd);
    }

    private void Unassign(nint unit, nint group)
    {
        var cmd = Allocator.AllocZeroed(0x50);
        _unassign(cmd, unit, group, 0, 1); // stays in the army; off its army group's fronts too
        Commands.TryPost(cmd);
    }
}
