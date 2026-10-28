using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// Keys for the battle plan's naval invasion buttons (vanilla has none): D, and Ctrl+D with a floating harbour.
[assembly: GuiPatch("interface/battleplantools.gui", "gui/shortcut_naval_invasion.gui", After = @"name = ""btn_order_draw_naval_inv""")]
[assembly: GuiPatch("interface/battleplantools.gui", "gui/shortcut_naval_invasion_harbor.gui", After = @"name = ""btn_order_draw_naval_inv_with_harbor""")]

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>CNavalMissionSetTypeCommand ctor (mem[0x48], CTaskForce*, int mission); the word at +0x44 (+0x45 stop training at max) is hardcoded to 1.</summary>
    [Signature] public const string SetNavalMission = "48 89 5C 24 20 48 89 4C 24 08 55 56 57 48 83 EC 30 41 8B D8 48 8B F2 48 8B F9 C6 41 08 00 C7 41 0C FF FF FF FF 33 C0 48 89 41 10 33 C9 48 89 4F 10 C7 47 18 FF FF 00 00 48 89 4F 1C 48 8D 05 ? ? ? ? 48 89 07 48 8D 4F 28 E8 ? ? ? ? 90 89 5F 40 66 C7 47 44 00 01";

    /// <summary>Adds a strategic region (+0x30: its id) to a task force's mission (+0x28: its CRef, the task force's +0x08).</summary>
    [VTable] public const string AddNavalRegion = "CNavalMissionAddRegionCommand";

    /// <summary>Battle plan tool click in naval invasion mode (tool, ...) -> bool consumed. tool+552: the invasion being edited.</summary>
    [Signature] public const string InvasionToolClick = "48 8B C4 48 89 58 ? 4C 89 48 ? 48 89 48 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D 68 ? 48 81 EC B0 00 00 00";

    /// <summary>A naval invasion may land on the province (order, int province id, ...) -> bool.</summary>
    [Signature] public const string InvasionTargetValid = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 30 8B FA";

    /// <summary>
    /// COrderSetInvasionSourceCommand ctor (mem[0x60], group, int sourceProvince, bool bestConvoys, CPdxArray&lt;CArmy*&gt;* units,
    /// int orderId, int): order id 0 makes a new invasion from the port with those divisions (and the tool edits that one).
    /// </summary>
    [Signature] public const string InvasionSourceCommand = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 4C 24 ? 56 57 41 55 41 56 41 57 48 83 EC 30 41 0F B6 D9";

    [VTable] public const string AddInvasionTarget = "CAddNavalInvasionTargetCommand";
}

/// <summary>
/// Naval invasions. Several beaches: with several divisions on an invasion, each further beach click makes another
/// invasion from the same port, the divisions shared evenly over the beaches. Auto support: an invasion of the player's
/// with no task force on naval invasion support gets the idle ones (holding, not in combat) on that mission over the sea
/// regions next to its port and its beaches. Vanilla commands, so multiplayer works.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int InvasionOrder = 3;
    private const int FleetsOfCountry = 0x278, TaskForcesOfFleet = 0xB8, CombatOfTaskForce = 0x1B4, MissionOfTaskForce = 0x374;
    private const int ParentOfTaskForce = 0x4B0, ActivityOfTaskForce = 0x4B8;
    private const int HoldMission = 0, InvasionSupportMission = 9, NavalCommandSize = 0x48, AddRegionSize = 0x38, CRefOfTaskForce = 0x08;

    private const int InvasionMode = 3, InvasionOfTool = 552, InvasionSourceSize = 0x60;

    [Function(CallingConventions.Microsoft)] public delegate byte InvasionToolClickFn(nint tool, nint a2, nint a3, nint a4, nint a5);

    private delegate* unmanaged<nint, nint, int, nint> _setNavalMission;
    private IHook<InvasionToolClickFn>? _invasionToolClick;
    private delegate* unmanaged<nint, int, nint, byte> _invasionTargetValid;
    private delegate* unmanaged<nint, nint, int, byte, nint, int, int, nint> _invasionSourceCommand;

    // The invasion being split: its orders (the tool moves to each new one), group and port, divisions per beach.
    private readonly HashSet<int> _landingOrders = [];
    private nint _landingGroup;
    private int _landingSource;
    private readonly List<(int Beach, List<nint> Units)> _landings = [];
    private readonly HashSet<int> _supported = []; // invasion order ids given task forces
    private PerGame _invasionGame = null!;

    private void LoadInvasionSplit()
    {
        _invasionTargetValid = (delegate* unmanaged<nint, int, nint, byte>)Sig.Find(Signatures.InvasionTargetValid);
        _invasionSourceCommand = (delegate* unmanaged<nint, nint, int, byte, nint, int, int, nint>)Sig.Find(Signatures.InvasionSourceCommand);
        _splitGame ??= new PerGame(ResetSplits);
        // Every click of the battle plan tool lands here: the other modes go straight on, with nothing allocated.
        _invasionToolClick = Patches.Hook<InvasionToolClickFn>(Sig.Find(Signatures.InvasionToolClick), (tool, a2, a3, a4, a5) =>
            *(int*)(tool + ModeOfTool) == InvasionMode && GuardedSplit("invasion split", SplitInvasion, tool)
                ? (byte)1 : _invasionToolClick!.OriginalFunction(tool, a2, a3, a4, a5));
    }

    /// <summary>
    /// A beach click on an invasion that already has one: the new beach gets its share of the divisions, taken from the beaches
    /// holding the most, as a new invasion from the same port. True: the click is handled.
    /// </summary>
    private bool SplitInvasion(nint tool)
    {
        if (*(int*)(tool + ModeOfTool) != InvasionMode) return false;
        _splitGame!.Check();
        var order = *(nint*)(tool + InvasionOfTool);
        var group = *(nint*)(tool + GroupOfTool) is var g && g != 0 ? g : *(nint*)(tool + ArmyGroupOfTool);
        var province = *(nint*)(tool + ProvinceOfTool);
        if (order == 0 || group == 0 || province == 0 || *(int*)(order + SourceOfOrder) is var source && source <= 0) return false;
        var beaches = Native.PdxArray<int>(order + TargetsOfOrder);
        if (beaches.Length == 0) return false; // the first beach: vanilla sets it
        var beach = Game.Id(province);
        if (beach == source) return false;     // vanilla: clicking the port again deletes the invasion

        if (group != _landingGroup || !_landingOrders.Contains(Order.Id(order))) // a new invasion: start from the order as it is
        {
            var units = new List<nint>();
            foreach (var member in Order.Members(order)) if (member != 0) units.Add(member - MemberOfUnit);
            (_landingGroup, _landingSource) = (group, source);
            _landingOrders.Clear();
            _landingOrders.Add(Order.Id(order));
            _landings.Clear();
            _landings.Add((beaches[0], units));
        }
        if (_landings.Any(l => l.Beach == beach)) return true;
        if (_invasionTargetValid(order, beach, 0) == 0) return false; // vanilla refuses it as usual
        var divisions = _landings.Sum(l => l.Units.Count);
        if (divisions <= _landings.Count)
        {
            Log.Info($"invasion: {divisions} division(s) already on {_landings.Count} beaches, none left for province {beach}");
            return true;
        }

        var share = new List<nint>();
        for (var want = divisions / (_landings.Count + 1); share.Count < want;)
        {
            var fullest = _landings.MaxBy(l => l.Units.Count).Units;
            share.Add(fullest[^1]);
            fullest.RemoveAt(fullest.Count - 1);
        }
        foreach (var unit in share) Commands.Unassign(unit, group);
        var country = Game.Country(Game.ControlledTag);
        if (country == 0) return false;
        var cmd = Allocator.AllocZeroed(InvasionSourceSize);
        Native.WithPdxArray(share.ToArray(), units => _invasionSourceCommand(cmd, group, _landingSource, 0, units, 0, 0));
        if (!Commands.TryPost(cmd)) return false;
        _nextOrderId = Math.Max(_nextOrderId, *(int*)(country + OrderCounterOfCountry)) + 1; // the new invasion's id when it runs
        Commands.TryPost(Commands.NewOrderCommand(Signatures.AddInvasionTarget, *(long*)(group + OrdersGroup.RefOffset), _nextOrderId, beach));
        _landingOrders.Add(_nextOrderId);
        _landings.Add((beach, share));
        Log.Info($"invasion: {share.Count} division(s) to province {beach}; {_landings.Count} beaches for {divisions}");
        return true;
    }

    private void LoadInvasionSupport()
    {
        _setNavalMission = (delegate* unmanaged<nint, nint, int, nint>)Sig.Find(Signatures.SetNavalMission);
        _invasionGame = new PerGame(_supported.Clear);
        MainThread.Every(TimeSpan.FromSeconds(10), SupportInvasions);
    }

    private void SupportInvasions()
    {
        var tag = Game.ControlledTag;
        var country = Game.HasStarted ? Game.Country(tag) : 0;
        if (country == 0) return;
        _invasionGame.Check();
        var taskForces = new List<nint>();
        foreach (var fleet in Native.PdxArray(country + FleetsOfCountry))
            if (fleet != 0)
                foreach (var tf in Native.PdxArray(fleet + TaskForcesOfFleet))
                    if (tf != 0) taskForces.Add(tf);
        if (taskForces.Any(tf => *(int*)(tf + MissionOfTaskForce) == InvasionSupportMission)) return; // already covered by the player

        var groups = new HashSet<nint>();
        foreach (var unit in Game.Units(country))
            if (Division.Group(unit) is var group && group != 0) groups.Add(group);
        foreach (var group in groups)
        foreach (var order in Native.PdxArray(group + InvasionOrders))
        {
            if (order == 0 || Order.Type(order) != InvasionOrder || *(int*)(order + SourceOfOrder) is var source && source <= 0) continue;
            var beaches = Native.PdxArray<int>(order + TargetsOfOrder);
            if (beaches.Length == 0 || _supported.Contains(Order.Id(order))) continue;
            // ponytail: the seas next to the port and the beaches, not the whole route; the route is sub_14103CA80's path.
            var regions = new HashSet<int>();
            foreach (var id in beaches.ToArray().Append(source)) SeaRegionsNext(id, regions);
            var idle = taskForces.Where(tf => *(int*)(tf + MissionOfTaskForce) == HoldMission && *(long*)(tf + ParentOfTaskForce) == 0
                                               && *(int*)(tf + CombatOfTaskForce) <= 0 && *(int*)(tf + ActivityOfTaskForce) == 0).ToList();
            if (regions.Count == 0 || idle.Count == 0) continue;
            foreach (var tf in idle)
            {
                var cmd = Allocator.AllocZeroed(NavalCommandSize);
                _setNavalMission(cmd, tf, InvasionSupportMission);
                if (!Commands.TryPost(cmd)) continue;
                foreach (var region in regions)
                {
                    var add = Commands.New(Signatures.AddNavalRegion, AddRegionSize, 0, 0); // as its ctor fills it
                    *(long*)(add + 0x28) = *(long*)(tf + CRefOfTaskForce);
                    *(int*)(add + 0x30) = region;
                    Commands.TryPost(add);
                }
            }
            _supported.Add(Order.Id(order));
            Log.Info($"invasion support: {idle.Count} task force(s) over {regions.Count} sea region(s) for the landing from province {source}");
            return; // the next invasion once these are busy
        }
    }

    /// <summary>Adds the strategic region ids of the sea provinces next to the province.</summary>
    private static void SeaRegionsNext(int province, HashSet<int> regions)
    {
        var p = Game.Province(province);
        var all = AirRegions.All;
        if (p == 0) return;
        foreach (var n in Game.Neighbours(p))
            if (Game.Province(n) is var q && q != 0 && !Province.Enterable(q) && Province.StrategicRegion(q) is var region && region != 0)
                if (all.IndexOf(region) is var id and >= 0) regions.Add(id);
    }
}
