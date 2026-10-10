using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using HOI4ModLoader;

namespace FrontLine;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CMoveCommand ctor (mem[0x88], CArmy* unit, int province, bool clearQueue, int moveType, bool setFinalDestination,
    /// bool, bool, bool, bool): the game finds the path to the province itself.
    /// </summary>
    [Signature] public const string MoveCommand = "48 89 4C 24 ? 53 48 83 EC 60 48 8B D9 C6 41 08 00 C7 41 0C FF FF FF FF 33 C9 89 4B 10 C7 43 14 00 00 FF FF 66 89 4B 18 48 89 4B 1C 48 8D 05 ? ? ? ? 48 89 03 48 8D 4B 28";

    /// <summary>COrderAssignCommand ctor (mem[0x50], CPdxArray&lt;CArmy*&gt;* units, COrdersGroup* group, int orderId, bool): onto one of the group's (or its parent's) orders.</summary>
    [Signature] public const string AssignCommand = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 4C 24 ? 56 57 41 54 41 56 41 57 48 83 EC 30 41 8B D9 49 8B E8 4C 8B FA 4C 8B F1 0F B6 BC 24 80 00 00 00 C6 41 08 00";

    /// <summary>COrderUnassignCommand ctor (mem[0x50], CArmy* unit, COrdersGroup* group, bool ungroup, bool alsoParent): off every order, into the army's reserve.</summary>
    [Signature] public const string UnassignCommand = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 41 0F B6 F1 49 8B F8 48 8B EA 48 8B D9 C6 41 08 00";

    /// <summary>CCountry::IsAtWarWith(country, int* {tag, tag index}) -> bool.</summary>
    [Signature] public const string IsAtWarWith = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 8B 41 08 48 8B FA 8B 12 48 8B F1 3B D0 74 ? 85 D2 74 ? 85 C0 74 ? 48 83 C1 08 E8 ? ? ? ? 39 47 04 74 ? 83 7E 08 00 7E ? 48 8B 86 ? ? ? ?";

    [VTable] public const string LandCombat = "CLandCombat";

    /// <summary>The AI theatre distribution: collect divisions it may move to other fronts (ctx, CPdxArray<{CArmy*, slot*}>* out): every army member on no order.</summary>
    [Signature] public const string CollectReassignableUnits = "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 98 00 00 00 4C 8B FA";

    /// <summary>CAIFront: put its divisions into armies (front, CPdxArray<CArmy*>* units, CPdxArray<CPdxArray<int>>* partitions by army, specs, log): assigns, or makes new armies.</summary>
    [Signature] public const string ApplyArmiesBalancing = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 41 56 48 83 EC 50 49 8B F9 49 8B F0";
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
///   attacked by up to two (a province of ours always keeps one).
/// Divisions taken off a front go back onto it once rested and free (after a reload that memory is gone: a rested division
/// on no order then joins its army's front). ApplyToAi does rotation and plugging for the AI countries too (single player), with their theatre distribution and army balancing kept off the divisions it handles (they'd regroup them into new small armies). AiEscape runs the escape for them as well: a salient about to be cut off is worth a hole in the AI's own placement.
/// </summary>
[Plugin("com.green1052.frontline", "Front Line", "1.0.0", GameVersion = "1.19.3")]
public unsafe class FrontLine : Plugin
{
    // Layouts for HOI4 1.19.3. CArmy (a land division):
    private const int KindOfUnit = 0x08, MemberOfUnit = 0xB8, GroupOfUnit = 0xC0, Combats = 0x1A8;
    private const int CommanderTag = 0x1D8, OwnerTag = 0x1E0, LocationOfUnit = 0x1F0, Path = 0x200, Retreating = 0x24C, Busy = 0x2F8;
    private const int StrengthRatioSlot = 34, OrgRatioSlot = 39; // CArmy vtable: (unit, long* out) -> out, 1e5 = 100%
    private const int UnitsOfCountry = 0x290;
    // COrdersGroup and its orders (COrderInstance):
    private const int BorderWar = 0x1B0;
    private const int RootOrders = 0x98;
    private static readonly int[] OrderLists = [RootOrders, 0xB0, 0xC8]; // root (fronts, offensive lines), fallback lines, invasions/paradrops
    private const int TypeOfOrder = 0x30, MembersOfOrder = 0x210, IdOfOrder = 0x244, FrontOrder = 2;
    // Land combat: every combat at gamestate +0x268; CLandCombat sides and their divisions.
    private const int CombatsOfGameState = 0x268, DefenderOfCombat = 0x30, ProvinceOfCombat = 0x38, ProgressSlot = 25;
    private const int DivisionsOfSide = 0x20, ReservesOfSide = 0x100;
    // Provinces:
    private const int IdOfProvince = 164, DivisionsInProvince = 0x11C, ControllerOfProvince = 392;
    private const int MaxRestSearch = 6, MaxNeckSearch = 8, MaxPlugsPerTurn = 2, MaxEscapesPerTurn = 2, MaxAttacksPerTurn = 2;
    private const long PendingMs = 30_000;   // a unit we gave orders to is left alone meanwhile (multiplayer: a round trip)
    private const long PlugCooldownMs = 20_000; // one helper per battle at a time: it needs time to get there

    private delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint> _move;
    private delegate* unmanaged<nint, nint, nint, int, byte, nint> _assign;
    private delegate* unmanaged<nint, nint, nint, byte, byte, nint> _unassign;
    private delegate* unmanaged<nint, int*, byte> _atWar;

    private readonly Dictionary<nint, long> _pending = [];                                // unit -> until
    private readonly Dictionary<nint, (int Tag, nint Group, int Order)> _resting = [];  // unit we took off a front -> where it goes back
    private readonly Dictionary<int, long> _plugged = [];                               // province of a battle helped -> until
    private PerGame _game = null!;
    private bool _rotation, _plugging, _escape, _opportunity, _mopUp;
    private double _wornOrg, _wornStrength, _returnOrg, _returnStrength, _losing;
    private int _maxSwaps, _maxResting, _restDepth, _plugRadius;

    public override void Load()
    {
        _rotation = Config.Bind("Rotation", "Rotation", true,
            "Worn-out divisions in contact with the enemy rest behind the line and come back rested, where another of ours holds their province.");
        _wornOrg = Math.Clamp(Config.Bind("Rotation", "WornOrganisation", 30, "A front line division below this organisation (%) goes to rest."), 0, 100);
        _wornStrength = Math.Clamp(Config.Bind("Rotation", "WornStrength", 50, "Or below this strength (%: equipment and manpower)."), 0, 100);
        _returnOrg = Math.Clamp(Config.Bind("Rotation", "ReturnOrganisation", 90, "A resting division goes back to its front at this organisation (%)."), 0, 100);
        _returnStrength = Math.Clamp(Config.Bind("Rotation", "ReturnStrength", 80, "And this strength (%)."), 0, 100);
        _maxSwaps = Math.Max(1, Config.Bind("Rotation", "MaxSwapsPerArmy", 2, "Divisions per army sent to rest at a time, so a front never changes all at once."));
        _maxResting = Math.Clamp(Config.Bind("Rotation", "MaxRestingPercent", 25, "At most this share (%) of a front's divisions rests at once."), 1, 100);
        _restDepth = Math.Clamp(Config.Bind("Rotation", "RestDepth", 2, "How many provinces behind the line a division rests (none of its neighbours enemy)."), 1, MaxRestSearch);
        _plugging = Config.Bind("Plugging", "Plugging", true,
            "Send the nearest fresh division into a defence of ours that is being lost; it joins the battle and goes back to its front afterwards.");
        _losing = Math.Clamp(Config.Bind("Plugging", "LosingPercent", 60, "A defence counts as being lost when the attacker holds this share (%) of the battle's organisation (the battle bar)."), 50, 100);
        _plugRadius = Math.Clamp(Config.Bind("Plugging", "PlugRadius", 4, "How far (provinces) a helper may come from."), 1, MaxRestSearch);
        _escape = Config.Bind("Escape", "Escape", true,
            "Pull a front division back out of a salient whose way back narrows to a single province next to the enemy, before the pocket closes.");

        _move = (delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint>)Sig.Find(Signatures.MoveCommand);
        _assign = (delegate* unmanaged<nint, nint, nint, int, byte, nint>)Sig.Find(Signatures.AssignCommand);
        _unassign = (delegate* unmanaged<nint, nint, nint, byte, byte, nint>)Sig.Find(Signatures.UnassignCommand);
        _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        _ = Sig.VTable(Signatures.LandCombat);
        _game = new PerGame(() =>
        {
            _pending.Clear();
            _resting.Clear();
            _plugged.Clear();
        });

        _opportunity = Config.Bind("Attack", "Opportunity", true,
            "Take empty enemy provinces next to the line with a fresh division, where another of ours stays behind.");
        _mopUp = Config.Bind("Attack", "MopUp", true,
            "Attack enemy divisions already surrounded (every land neighbour ours or not at war with us) with up to two fresh divisions from next to them.");

        var ai = Config.Bind("AI", "ApplyToAi", false,
            "Rotation and plugging for the AI countries' divisions too (single player); the AI's army management then leaves the divisions it handles alone.");

        if (!_rotation && !_plugging && !_escape && !_opportunity && !_mopUp) return;
        MainThread.Every(TimeSpan.FromSeconds(2), () => Manage(Game.ControlledTag, false));
        _aiOpportunity = Config.Bind("AI", "AiOpportunity", true,
            "AI countries take empty enemy provinces next to their line with a fresh division (single player); vanilla's local attacks often skip them.");
        _aiEscape = Config.Bind("AI", "AiEscape", true,
            "AI divisions pull back out of a salient whose way back is closing, like the player's; they go back to their front once away.");
        _aiFront = ai;
        if (!ai && !_aiOpportunity && !_aiEscape || Multiplayer) return; // AI decisions exist on one machine only; in multiplayer they'd be the host's to make
        if (ai || _aiEscape) LoadAiFilters(); // rotation, plugging and escape take divisions off fronts
        MainThread.Every(TimeSpan.FromMinutes(1), () =>
        {
            if (_aiRotations + _aiPlugs + _aiEscapes + _aiAttacks > 0)
                Log.Info($"AI, last minute: {_aiRotations} rotation(s), {_aiPlugs} plug(s), {_aiEscapes} escape(s), {_aiAttacks} empty province(s) taken");
            _aiRotations = _aiPlugs = _aiEscapes = _aiAttacks = 0;
        });
        MainThread.Every(TimeSpan.FromSeconds(5), () =>
        {
            var player = Game.ControlledTag;
            foreach (var country in Game.Countries)
            {
                var tag = country == 0 ? 0 : *(int*)(country + 8);
                if (tag > 0 && tag != player) Manage(tag, true);
            }
            Volatile.Write(ref _managed, [.. _resting.Where(r => r.Value.Tag != player).Select(r => r.Key)]);
        });
    }

    // ---- AI countries: their theatre distribution and army balancing must not regroup the divisions we took off a front ----

    [Function(CallingConventions.Microsoft)] public delegate byte CollectFn(nint context, nint list);
    [Function(CallingConventions.Microsoft)] public delegate void BalanceFn(nint front, nint units, nint partitions, nint specs, nint log);

    private IHook<CollectFn>? _collect;
    private IHook<BalanceFn>? _balance;
    private HashSet<nint> _managed = []; // AI divisions resting or sent by us; replaced whole, read on AI threads
    private int _filterFailed;

    private void LoadAiFilters()
    {
        _collect = Patches.Hook<CollectFn>(Sig.Find(Signatures.CollectReassignableUnits), (context, list) =>
        {
            var result = _collect!.OriginalFunction(context, list);
            var managed = Volatile.Read(ref _managed);
            if (managed.Count == 0) return result;
            try
            {
                var data = *(nint*)list;
                int n = *(int*)(list + 0xC), kept = 0;
                for (var i = 0; i < n; i++)
                {
                    var entry = data + 16 * i; // {CArmy*, front slot*}
                    if (managed.Contains(*(nint*)entry)) continue;
                    if (kept != i) Buffer.MemoryCopy((void*)entry, (void*)(data + 16 * kept), 16, 16);
                    kept++;
                }
                *(int*)(list + 0xC) = kept;
            }
            catch (Exception e) { FilterFailed(e); }
            return result;
        });
        _balance = Patches.Hook<BalanceFn>(Sig.Find(Signatures.ApplyArmiesBalancing), (front, units, partitions, specs, log) =>
        {
            var managed = Volatile.Read(ref _managed);
            if (managed.Count > 0)
                try
                {
                    var unit = (nint*)*(nint*)units;
                    for (int p = 0, parts = *(int*)(partitions + 0xC); p < parts; p++)
                    {
                        var part = *(nint*)partitions + 0x18 * p; // CPdxArray<int> indices into units, for army p
                        var indices = (int*)*(nint*)part;
                        int n = *(int*)(part + 0xC), kept = 0;
                        for (var k = 0; k < n; k++)
                            if (!managed.Contains(unit[indices[k]])) indices[kept++] = indices[k];
                        *(int*)(part + 0xC) = kept; // the game skips empty partitions
                    }
                }
                catch (Exception e) { FilterFailed(e); }
            _balance!.OriginalFunction(front, units, partitions, specs, log);
        });
    }

    private void FilterFailed(Exception e)
    {
        if (Interlocked.Exchange(ref _filterFailed, 1) == 0) Log.Error($"AI filter: {e}");
    }

    private bool _ai; // Manage is working for an AI country: post as the AI does, no log lines

    private void Manage(int tag, bool ai)
    {
        _ai = ai;
        _game.Check();
        var country = Game.Country(tag);
        if (country == 0) return;
        var now = Environment.TickCount64;
        foreach (var (unit, until) in _pending.ToArray())
            if (now >= until) _pending.Remove(unit);
        foreach (var (province, until) in _plugged.ToArray())
            if (now >= until) _plugged.Remove(province);
        var units = Native.PdxArray(country + UnitsOfCountry).ToArray();
        foreach (var unit in _resting.Where(r => r.Value.Tag == tag).Select(r => r.Key).Except(units).ToList()) _resting.Remove(unit); // gone

        // Every order a division is on, of its army and of its army group (a field marshal's front holds his armies' divisions).
        var orders = new Dictionary<nint, nint>(); // member -> order
        var groups = units.Select(u => *(nint*)(u + GroupOfUnit)).Where(g => g != 0 && !new OrdersGroup(g).IsArmyGroup && *(nint*)(g + BorderWar) == 0).Distinct().ToList();
        foreach (var owner in groups.Concat(groups.Select(g => *(nint*)(g + OrdersGroup.ParentOffset)).Where(p => p != 0)).Distinct())
        foreach (var list in OrderLists)
        foreach (var order in Native.PdxArray(owner + list))
        foreach (var member in Native.PdxArray(order + MembersOfOrder))
            orders[member] = order;

        // Rested AI divisions we took off (escape, or rotation when its own pass isn't running) go back to their front.
        if (ai && !(_rotation && _aiFront))
            foreach (var (unit, rest) in _resting.Where(r => r.Value.Tag == tag).ToList())
            {
                if (_pending.ContainsKey(unit) || orders.ContainsKey(unit + MemberOfUnit) || *(int*)(unit + Path + 0xC) != 0) continue;
                if (Org(unit) < _returnOrg || Str(unit) < _returnStrength) continue;
                if (Assign(unit, rest.Group, rest.Order)) _pending[unit] = now + PendingMs;
            }
        if (_rotation && (!_ai || _aiFront)) foreach (var group in groups) RotateArmy(country, tag, group, orders, now);
        if (_plugging && (!_ai || _aiFront)) Plug(country, tag, units, orders, now);
        if (_escape && (!_ai || _aiEscape)) Escape(country, tag, units, orders, now);
        if (_ai ? _aiOpportunity : _opportunity || _mopUp) Attack(country, tag, units, now); // AI: empty provinces only
    }

    private void RotateArmy(nint country, int tag, nint group, Dictionary<nint, nint> orders, long now)
    {
        var parent = *(nint*)(group + OrdersGroup.ParentOffset);
        nint[] owners = parent == 0 ? [group] : [group, parent];
        var fronts = owners.SelectMany(o => Native.PdxArray(o + RootOrders).ToArray()).Where(IsFront).ToList();
        if (fronts.Count == 0) return;
        var members = Native.PdxArray(group + OrdersGroup.MembersOffset).ToArray();

        // Rested: back onto the front it left (or, unknown after a reload, the army's first front).
        foreach (var member in members)
        {
            var unit = member - MemberOfUnit;
            if (orders.ContainsKey(member) || !Free(unit, tag, now) || *(int*)(unit + Path + 0xC) != 0) continue; // on an order, or still walking
            if (Org(unit) < _returnOrg || Str(unit) < _returnStrength) continue;
            var order = _resting.Remove(unit, out var rest) && rest.Group == group ? rest.Order : *(int*)(fronts[0] + IdOfOrder);
            if (!Assign(unit, group, order)) continue;
            _pending[unit] = now + PendingMs;
            if (!_ai) Log.Info($"a rested division ({Org(unit):0}% organisation) is back on its front");
        }

        // Worn and in contact: off the front to rest, where another of ours on that front holds the province.
        var frontOf = members.Where(m => orders.TryGetValue(m, out var o) && IsFront(o)).ToDictionary(m => m, m => orders[m]);
        var holding = Holding(frontOf);
        var swaps = 0;
        foreach (var unit in frontOf.Keys.Select(m => m - MemberOfUnit)
                     .Where(u => Free(u, tag, now) && (Org(u) < _wornOrg || Str(u) < _wornStrength) && InContact(country, tag, u)).OrderBy(Org))
        {
            if (swaps == _maxSwaps) break;
            var front = frontOf[unit + MemberOfUnit];
            var place = (front, *(nint*)(unit + LocationOfUnit));
            if (holding[place] < 2) continue; // alone there: it holds on
            var id = *(int*)(front + IdOfOrder);
            var resting = _resting.Values.Count(r => r.Group == group && r.Order == id);
            if ((resting + 1) * 100 > (Native.PdxArray(front + MembersOfOrder).Length + resting) * _maxResting) continue;
            var rest = RestProvince(country, tag, unit);
            if (rest < 0) continue; // nowhere safe to rest: it stays
            TakeOff(unit, front, tag);
            Move(unit, rest);
            _pending[unit] = now + PendingMs;
            holding[place]--;
            swaps++;
            Count(ref _aiRotations);
            if (!_ai) Log.Info($"rotation: a division at {Org(unit):0}% organisation, {Str(unit):0}% strength goes to rest");
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
            if (_plugged.ContainsKey(id)) continue;

            helpersAt ??= Helpers(country, tag, units, orders, now);
            var helper = Nearest(tag, province, _plugRadius, helpersAt);
            if (helper == 0) continue;
            if (orders.TryGetValue(helper + MemberOfUnit, out var order)) TakeOff(helper, order, tag);
            Move(helper, id); // arriving, it joins the defence as a reserve
            _pending[helper] = now + PendingMs;
            _plugged[id] = now + PlugCooldownMs;
            helpersAt[*(nint*)(helper + LocationOfUnit)].Remove(helper);
            plugs++;
            Count(ref _aiPlugs);
            if (!_ai) Log.Info($"plugging: a fresh division sent into a defence being lost ({progress * 100.0 / Native.FixedPoint:0}% for the attacker)");
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
            if (!Free(unit, tag, now) || *(int*)(unit + Path + 0xC) != 0 || Org(unit) < 80 || Str(unit) < 70) continue;
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
    /// Empty enemy provinces next to the line get one fresh division, surrounded enemy divisions up to two; a province of
    /// ours always keeps one. Plain moves: front divisions go back to their front by themselves afterwards.
    /// </summary>
    private void Attack(nint country, int tag, nint[] units, long now)
    {
        var attacks = 0;
        foreach (var here in units.Where(u => *(int*)(u + KindOfUnit) == 0 && *(int*)(u + CommanderTag) == tag && *(nint*)(u + LocationOfUnit) != 0)
                     .GroupBy(u => *(nint*)(u + LocationOfUnit)))
        {
            if (attacks == MaxAttacksPerTurn) break;
            var province = here.Key;
            var spare = here.Count() - 1; // one stays
            var fresh = here.Where(u => Free(u, tag, now) && *(int*)(u + Path + 0xC) == 0 && Org(u) >= 70 && Str(u) >= 70).OrderByDescending(Org).ToList();
            foreach (var id in Game.Neighbours(province))
            {
                if (spare == 0 || fresh.Count == 0 || attacks == MaxAttacksPerTurn) break;
                if (_plugged.ContainsKey(id)) continue; // already on its way there
                var target = Game.Province(id);
                if (target == 0 || !Enemy(country, tag, target)) continue;
                var empty = *(int*)(target + DivisionsInProvince) == 0;
                var send = empty ? (_ai ? _aiOpportunity : _opportunity) ? 1 : 0 : !_ai && _mopUp && Surrounded(country, tag, target) ? 2 : 0;
                for (var i = 0; i < send && spare > 0 && fresh.Count > 0; i++)
                {
                    var unit = fresh[0];
                    fresh.RemoveAt(0);
                    Move(unit, id);
                    _pending[unit] = now + PendingMs;
                    spare--;
                }
                if (send == 0) continue;
                _plugged[id] = now + PlugCooldownMs;
                attacks++;
                Count(ref _aiAttacks);
                if (!_ai) Log.Info(empty ? "opportunity: a fresh division takes an empty enemy province" : "mop-up: fresh divisions attack surrounded enemy divisions");
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
            TakeOff(unit, order, tag);
            Move(unit, rest);
            _pending[unit] = now + PendingMs;
            holding[(order, location)]--;
            escapes++;
            Count(ref _aiEscapes);
            if (!_ai) Log.Info("escape: a division pulls back from a salient about to be cut off");
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

    // AI actions per minute, for the log (AI ones aren't logged one by one).
    private int _aiRotations, _aiPlugs, _aiEscapes, _aiAttacks;
    private bool _aiFront, _aiOpportunity, _aiEscape;

    private void Count(ref int counter)
    {
        if (_ai) counter++;
    }

    private bool NextToEnemy(nint country, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Enemy(country, tag, p));

    private static bool IsFront(nint order) => *(int*)(order + TypeOfOrder) == FrontOrder;

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
    private void TakeOff(nint unit, nint front, int tag)
    {
        var group = *(nint*)(unit + GroupOfUnit);
        Unassign(unit, group);
        _resting[unit] = (tag, group, *(int*)(front + IdOfOrder));
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
                if (depth >= _restDepth && !NextToEnemy(country, tag, p)) return id;
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
        return Commands.TryPost(cmd, _ai);
    }

    private void Unassign(nint unit, nint group)
    {
        var cmd = Allocator.AllocZeroed(0x50);
        _unassign(cmd, unit, group, 0, 1); // stays in the army; off its army group's fronts too
        Commands.TryPost(cmd, _ai);
    }

    private void Move(nint unit, int province)
    {
        var cmd = Allocator.AllocZeroed(0x88);
        _move(cmd, unit, province, 1, 2, 0, 0, 0, 0, 0); // as the AI moves units
        Commands.TryPost(cmd, _ai);
    }
}
