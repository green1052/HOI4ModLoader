using HOI4ModLoader;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>A paradrop may start from the province (CProvince*, group) -> bool: our airbase there, transports on the paradrop mission.</summary>
    [Signature] public const string ParadropSourceValid = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 8D 72";

    [VTable] public const string DeleteOrderCommand = "COrderDeleteCommand";
}

/// <summary>
/// Ctrl+Alt+D turns the selected army's area defence into paradrops: the painted states' provinces that match its ticked
/// settings (victory points, ports, airbases, borders, forts, coastline, railways: a supply hub) become destinations, empty
/// ones and bigger victory points first, one per paratrooper division (shared out when there are fewer). The area order is
/// deleted. They start from the army's paradrop airbase, or the nearest airbase with transports on the paradrop mission;
/// with none, transports are sent first and the key is pressed again once they are there.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int AreaOrder = 5, AreaOrders = 0x98, StatesOfArea = 0xE0, SettingsOfArea = 0xF8;
    private const uint AreaVictoryPoints = 0x2, AreaPorts = 0x4, AreaAirbases = 0x8, AreaBorders = 0x10, AreaForts = 0x20, AreaCoast = 0x40, AreaRailways = 0x80;
    private const int TerrainOfProvince = 0xB8, TerrainFlags = 0xD2, LandFlag = 1, CoastFlag = 8, CanParadrop = 1461; // stats+1461

    private delegate* unmanaged<nint, nint, byte> _sourceValid;
    private bool _areaDropDown;

    private void LoadAreaDrop()
    {
        _sourceValid = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.ParadropSourceValid);
        ResolveAir();
        MainThread.Every(TimeSpan.FromMilliseconds(30), () =>
        {
            if (Gui.HotkeyPressed('D', ref _areaDropDown) && Game.HasStarted) Log.Guard("area paradrop", AreaDrop);
        });
    }

    private void AreaDrop()
    {
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var groups = SelectedDivisions(country, tag).Select(Division.Group).Where(g => g != 0).ToHashSet();
        var done = false;
        foreach (var group in groups)
        foreach (var order in Native.PdxArray(group + AreaOrders))
            if (order != 0 && Order.Type(order) == AreaOrder)
            {
                DropOnArea(country, tag, group, order);
                done = true;
                break;
            }
        if (!done) Log.Info("area paradrop: select an army with an area defence painted over the target states");
    }

    private void DropOnArea(nint country, int tag, nint group, nint area)
    {
        var paratroopers = Game.Units(country).ToArray()
            .Where(u => Division.Group(u) == group && Division.Stats(u) is var s && s != 0 && *(byte*)(s + CanParadrop) != 0).ToList();
        if (paratroopers.Count == 0)
        {
            Log.Info("area paradrop: the army has no paratroopers");
            return;
        }
        var targets = Targets(country, tag, area);
        if (targets.Count == 0)
        {
            Log.Info("area paradrop: no province in the painted states matches its settings");
            return;
        }
        var source = Source(tag, group, targets[0]);
        if (source == 0)
        {
            Log.Info("area paradrop: no airbase with transports on the paradrop mission yet; transports sent, press again once they are there");
            return;
        }

        var groupRef = *(long*)(group + OrdersGroup.RefOffset);
        Commands.TryPost(Commands.NewOrderCommand(Signatures.DeleteOrderCommand, groupRef, Order.Id(area), 0));
        var drops = Math.Min(targets.Count, paratroopers.Count);
        _nextOrderId = Math.Max(_nextOrderId, *(int*)(country + OrderCounterOfCountry));
        for (var i = 0; i < drops; i++)
        {
            var share = paratroopers.Where((_, k) => k % drops == i).ToArray();
            var cmd = Allocator.AllocZeroed(SourceCommandSize);
            Native.WithPdxArray(share, units => _sourceCommand(cmd, group, Game.Id(source), 0, units));
            if (!Commands.TryPost(cmd)) continue;
            Commands.TryPost(Commands.NewOrderCommand(Signatures.ParadropTargetCommand, groupRef, ++_nextOrderId, Game.Id(targets[i])));
        }
        Log.Info($"area paradrop: {paratroopers.Count} division(s) on {drops} destination(s) from province {Game.Id(source)}");
    }

    /// <summary>Land provinces of the painted states matching any ticked setting (all with none), empty and high victory points first.</summary>
    private static List<nint> Targets(nint country, int tag, nint area)
    {
        var states = Native.PdxArray<int>(area + StatesOfArea).ToArray().ToHashSet();
        var settings = *(uint*)(area + SettingsOfArea);
        var found = new List<nint>();
        for (int id = 1, misses = 0; misses < 512; id++) // the game has no province list: ids run until a long gap
        {
            var p = Game.Province(id);
            if (p == 0) { misses++; continue; }
            misses = 0;
            var state = Province.State(p);
            if (state == 0 || !states.Contains(State.Id(state)) || (Flags(p) & LandFlag) == 0 || Province.Controller(p) == tag) continue;
            if (settings == 0 || Matches(country, tag, p, state, settings)) found.Add(p);
        }
        return found.OrderBy(p => Province.Divisions(p) > 0).ThenByDescending(Province.VictoryPoints).ToList();
    }

    private static bool Matches(nint country, int tag, nint p, nint state, uint settings) =>
        (settings & AreaVictoryPoints) != 0 && Province.VictoryPoints(p) > 0
        || (settings & AreaPorts) != 0 && Province.BuildingLevel(p, Buildings.NavalBaseSlot) > 0
        || (settings & AreaAirbases) != 0 && State.MainProvince(state) == p && HasAirbase(state)
        || (settings & AreaBorders) != 0 && Game.Neighbours(p).ToArray().Any(n => Game.Province(n) is var q && q != 0 && !Province.Friendly(country, tag, q) && Province.Controller(q) != Province.Controller(p))
        || (settings & AreaForts) != 0 && Province.FortLevel(p) > 0
        || (settings & AreaCoast) != 0 && (Flags(p) & CoastFlag) != 0
        || (settings & AreaRailways) != 0 && Province.BuildingLevel(p, Buildings.SupplyNodeSlot) > 0;

    private static int Flags(nint p) => *(byte*)(*(nint*)(p + TerrainOfProvince) + TerrainFlags);

    private static bool HasAirbase(nint state)
    {
        foreach (var type in Buildings.AirBaseTypes)
            if (type != 0 && Buildings.Level(State.Buildings(state), type) > 0) return true;
        return false;
    }

    /// <summary>
    /// Where the drops start: the army's paradrop airbase if it has one, else the airbase nearest the first target whose
    /// transports are on the paradrop mission. With none, idle transports are sent to the nearest one with room: 0.
    /// </summary>
    private nint Source(int tag, nint group, nint target)
    {
        foreach (var order in Native.PdxArray(group + InvasionOrders))
            if (order != 0 && Order.Type(order) == ParadropOrder && Game.Province(*(int*)(order + SourceOfOrder)) is var p && p != 0) return p;
        var air = Game.StrategicAir(tag);
        var region = *(nint*)(target + RegionOfProvince);
        if (air == 0 || region == 0) return 0;
        var bases = Native.PdxArray(air + BasesOfAir).ToArray()
            .Where(b => b != 0 && *(nint*)(b + StateOfBase) != 0)
            .OrderBy(b => _regionToState(region, *(nint*)(b + StateOfBase))).ToList();
        foreach (var b in bases)
        foreach (var p in State.Provinces(*(nint*)(b + StateOfBase)))
            if (p != 0 && _sourceValid(p, group) != 0) return p;
        foreach (var b in bases)
            if (SendTransportsTo(air, b, region) > 0) return 0;
        return 0;
    }
}
