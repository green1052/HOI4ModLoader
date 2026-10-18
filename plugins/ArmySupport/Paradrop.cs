using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>Battle plan tool click (tool, input, hits) -> bool consumed. tool+144 drawing mode (4 = paradrop).</summary>
    [Signature] public const string PlanToolClick = "48 89 5C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8B EC 48 81 EC 80 00 00 00 0F 29 74 24 ? 0F 29 7C 24 ? 4D 8B E0";

    /// <summary>A paradrop order may land on the province (order, CProvince*) -> bool: land, not its source, in its planes' range.</summary>
    [Signature] public const string ParadropTargetValid = "40 53 55 57 48 83 EC 30 48 8B 82";

    /// <summary>
    /// COrderSetParadropSourceCommand ctor (mem[0x50], group, int sourceProvince, int orderId, CPdxArray&lt;CArmy*&gt;* units):
    /// order id 0 makes a new paradrop order from the source with those divisions.
    /// </summary>
    [Signature] public const string ParadropSourceCommand = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 4C 24 ? 56 57 41 55 41 56 41 57 48 83 EC 30 41 8B D9";

    [VTable] public const string ParadropTargetCommand = "COrderSetParadropTargetCommand";
}

/// <summary>
/// Paradrops. Several targets: with several paratrooper divisions on one paradrop order, each further destination click
/// makes another order from the same airbase, the divisions shared evenly over the destinations. Auto transports: a paradrop
/// order with no transport planes on the paradrop mission at its airbase gets the idle transport wings that reach its target.
/// Both use the game's own commands, so multiplayer works.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int ModeOfTool = 144, GroupOfTool = 136, ArmyGroupOfTool = 128, OrderOfTool = 560, ProvinceOfTool = 0x268, ParadropMode = 4;
    private const int ParadropOrder = 4, SourceOfOrder = 184, TargetsOfOrder = 112, InvasionOrders = 0xC8, MemberOfUnit = 0xB8;
    private const int OrderCounterOfCountry = 0x1B0, RegionOfProvince = 0xC8, SourceCommandSize = 0x50;
    private const int UnitTransport = 0x9E8, UnitTransportCapacity = 0x9DC; // CAirWing: non-zero when it can carry divisions
    private const int ParadropMission = 0x40;

    [Function(CallingConventions.Microsoft)] public delegate byte PlanToolClickFn(nint tool, nint input, nint hits);

    private IHook<PlanToolClickFn>? _planToolClick;
    private delegate* unmanaged<nint, nint, byte> _targetValid;
    private delegate* unmanaged<nint, nint, int, int, nint, nint> _sourceCommand;

    // The drop being split: the order the tool edits, its group and airbase, and the divisions per destination.
    private int _dropOrder = -1, _dropSource, _nextOrderId;
    private nint _dropGroup;
    private readonly List<(int Target, List<nint> Units)> _drops = [];

    private void LoadParadrop(bool split, bool transports)
    {
        _targetValid = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.ParadropTargetValid);
        _sourceCommand = (delegate* unmanaged<nint, nint, int, int, nint, nint>)Sig.Find(Signatures.ParadropSourceCommand);
        if (split)
            _planToolClick = Patches.Hook<PlanToolClickFn>(Sig.Find(Signatures.PlanToolClick), (tool, input, hits) =>
            {
                var consumed = false;
                Log.Guard("paradrop split", () => consumed = SplitParadrop(tool));
                return consumed ? (byte)1 : _planToolClick!.OriginalFunction(tool, input, hits);
            });
        if (transports)
        {
            ResolveAir();
            MainThread.Every(TimeSpan.FromSeconds(10), () => Log.Guard("paradrop transports", SendTransports));
        }
    }

    /// <summary>
    /// A destination click on a paradrop order that already has one: the new destination gets its share of the divisions, taken
    /// from the destinations holding the most, as a new order from the same airbase. True: the click is handled.
    /// </summary>
    private bool SplitParadrop(nint tool)
    {
        if (*(int*)(tool + ModeOfTool) != ParadropMode) return false;
        var order = *(nint*)(tool + OrderOfTool);
        var group = *(nint*)(tool + GroupOfTool) is var g && g != 0 ? g : *(nint*)(tool + ArmyGroupOfTool);
        var province = *(nint*)(tool + ProvinceOfTool);
        if (order == 0 || group == 0 || province == 0 || *(int*)(order + SourceOfOrder) <= 0) return false;
        var targets = Native.PdxArray<int>(order + TargetsOfOrder);
        if (targets.Length == 0) return false; // the first destination: vanilla sets it

        if (Order.Id(order) != _dropOrder || group != _dropGroup) // a new drop: start from the order as it is
        {
            var units = new List<nint>();
            foreach (var member in Order.Members(order)) if (member != 0) units.Add(member - MemberOfUnit);
            (_dropOrder, _dropGroup, _dropSource) = (Order.Id(order), group, *(int*)(order + SourceOfOrder));
            _drops.Clear();
            _drops.Add((targets[0], units));
        }
        var target = Game.Id(province);
        if (_targetValid(order, province) == 0) return false; // vanilla refuses it as usual
        var divisions = _drops.Sum(d => d.Units.Count);
        if (_drops.Any(d => d.Target == target)) return true;
        if (divisions <= _drops.Count)
        {
            Log.Info($"paradrop: {divisions} division(s) already on {_drops.Count} destinations, none left for province {target}");
            return true;
        }

        // Its share: from the fullest destinations first, so they end up within one division of each other.
        var share = new List<nint>();
        for (var want = divisions / (_drops.Count + 1); share.Count < want;)
        {
            var fullest = _drops.MaxBy(d => d.Units.Count).Units;
            share.Add(fullest[^1]);
            fullest.RemoveAt(fullest.Count - 1);
        }
        foreach (var unit in share) Commands.Unassign(unit, group);
        var country = Game.Country(Game.ControlledTag);
        if (country == 0) return false;
        var cmd = Allocator.AllocZeroed(SourceCommandSize);
        Native.WithPdxArray(share.ToArray(), units => _sourceCommand(cmd, group, _dropSource, 0, units));
        if (!Commands.TryPost(cmd)) return false;
        // The new order takes the country's next order id when the command runs.
        _nextOrderId = Math.Max(_nextOrderId, *(int*)(country + OrderCounterOfCountry)) + 1;
        Commands.TryPost(Commands.NewOrderCommand(Signatures.ParadropTargetCommand, *(long*)(group + OrdersGroup.RefOffset), _nextOrderId, target));
        _drops.Add((target, share));
        Log.Info($"paradrop: {share.Count} division(s) to province {target}; {_drops.Count} destinations for {divisions}");
        return true;
    }

    /// <summary>The player's paradrop orders lacking transports on the mission at their airbase get the idle transport wings that reach.</summary>
    private void SendTransports()
    {
        var tag = Game.ControlledTag;
        var country = Game.HasStarted ? Game.Country(tag) : 0;
        var air = country == 0 ? 0 : Game.StrategicAir(tag);
        if (air == 0) return;
        var bases = Native.PdxArray(air + BasesOfAir);
        var groups = new HashSet<nint>();
        foreach (var unit in Game.Units(country))
            if (Division.Group(unit) is var group && group != 0) groups.Add(group);
        foreach (var group in groups)
        foreach (var order in Native.PdxArray(group + InvasionOrders))
        {
            if (order == 0 || Order.Type(order) != ParadropOrder || *(int*)(order + SourceOfOrder) is var source && source <= 0) continue;
            var targets = Native.PdxArray<int>(order + TargetsOfOrder);
            var from = Game.Province(source);
            var to = targets.Length == 0 ? 0 : Game.Province(targets[0]);
            var airbase = from == 0 ? 0 : BaseOf(bases, Province.State(from));
            var region = to == 0 ? 0 : *(nint*)(to + RegionOfProvince);
            if (airbase == 0 || region == 0 || TransportWings(air).Any(w => _baseOf(w) == airbase && (*(uint*)(w + MissionMask) & ParadropMission) != 0)) continue;
            if (SendTransportsTo(air, airbase, region) is var sent and > 0)
                Log.Info($"paradrop: {sent} transport wing(s) to the airbase in province {source} for the drop on {targets[0]}");
        }
    }

    /// <summary>The country's wings that can carry divisions.</summary>
    private static List<nint> TransportWings(nint air)
    {
        var wings = new List<nint>();
        foreach (var pool in Native.PdxArray(air + PoolsOfAir))
        foreach (var wing in Native.PdxArray(pool + WingsOfPool))
            if (wing != 0 && (*(int*)(wing + UnitTransport) != 0 || *(int*)(wing + UnitTransportCapacity) != 0)) wings.Add(wing);
        return wings;
    }

    /// <summary>Sends the idle transport wings that fit and reach <paramref name="region"/> to the airbase on the paradrop mission: how many went.</summary>
    private int SendTransportsTo(nint air, nint airbase, nint region)
    {
        var room = _freeCapacity(airbase);
        var sent = 0;
        // ponytail: every idle transport that reaches goes; count planes per division if they should be shared out.
        foreach (var wing in TransportWings(air))
        {
            if (*(uint*)(wing + MissionMask) != 0 || *(long*)(wing + Destination) != 0 || *(nint*)(wing + AttachedArmy) != 0) continue; // busy
            long rangeOut;
            if (_maxSize(wing) > room || _inRange(0, region, airbase, *_range(wing, &rangeOut), 0) == 0) continue;
            var cmd = Allocator.AllocZeroed(0x48);
            _transfer(cmd, wing, airbase, 0, region, ParadropMission);
            if (!Commands.TryPost(cmd)) continue;
            room -= _maxSize(wing);
            sent++;
        }
        return sent;
    }

    private static nint BaseOf(ReadOnlySpan<nint> bases, nint state)
    {
        foreach (var b in bases) if (b != 0 && *(nint*)(b + StateOfBase) == state) return b;
        return 0;
    }
}
