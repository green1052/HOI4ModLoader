using HOI4ModLoader;

namespace AutoConstruction;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class SupplySignatures
{
    /// <summary>
    /// The supply hub popup's "upgrade railway to capital" (BottleneckData* out[0x40], int* tag, CProvince* node, bool naval,
    /// int maxLevel, void* ignore, bool skipIfQueued, bool allowForeign): +1 railway level on the weakest link from the
    /// hub to the capital, or a naval base level where a sea link is it. The AI's own bottleneck fixer calls it too.
    /// </summary>
    [Signature] public const string UpgradeRailToCapital = "48 89 5C 24 ? 48 89 54 24 ? 48 89 4C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8B EC 48 83 EC 70 0F 29 74 24 ? 45 0F B6 F9";

    /// <summary>BottleneckData destructor (data): frees its arrays.</summary>
    [Signature] public const string BottleneckDataDtor = "48 89 5C 24 ? 57 48 83 EC 20 48 8B 51 ? 33 FF 48 8B D9 48 85 D2 74 ? 89 79 ? 48 8B 49 ? 48 8B 01 FF 50 ? 48 89 7B ? 89 7B ? 48 39 7B ? 74 ? 44 8B 43";

    /// <summary>FindRailPath(unused, CPdxArray&lt;int&gt;* out, int from, int to, bool checkCanBuild): the railway tool's path, for the player.</summary>
    [Signature] public const string FindRailPath = "48 8B C4 48 89 58 ? 48 89 70 ? 48 89 78 ? 48 89 50 ? 55 41 54 41 55 41 56 41 57 48 8D 68 ? 48 81 EC 00 01 00 00";

    /// <summary>QueueRailway(CPdxArray&lt;int&gt;* path, int* tag, int addLevels, byte position, bool skipIfQueued): what the game's helpers queue railways with.</summary>
    [Signature] public const string QueueRailway = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 41 54 41 55 41 56 41 57 48 83 EC 30 45 0F B6 E9";

    /// <summary>A call of the building database getter, `call Get; mov rcx,[rax+3B8h]` (naval base type); the getter reads [rip+db].</summary>
    [Signature] public const string BuildingDbCall = "E8 ? ? ? ? 48 8B 88 B8 03 00 00 E8 ? ? ? ? 8B D8";
}

/// <summary>
/// Auto supply: where the player's divisions are low on supply, queue what fixes it with the construction tab's own
/// commands, as the supply map's buttons do:
/// - the hub feeding them is saturated: +1 railway on its weakest link to the capital (or a naval base on a sea link);
/// - the hub isn't connected: a railway from it to the nearest connected hub;
/// - no hub near them: a supply hub in their state (railway-connected provinces first), then a railway to it.
/// </summary>
public unsafe partial class AutoConstruction
{
    // Layouts for HOI4 1.19.3.
    private const int SupplyOfGameState = 0x3D8, RailwaysOfGameState = 0x3E0;
    private const int CountriesOfSupply = 0x108, FlowOfSupply = 0x138, FlowSize = 0xD8, NetworkOfCountry = 0x80;
    private const int Nodes = 0xB8, NodeSize = 0xE8, LandNodes = 0x2C0;                                  // supply network
    private const int ProvinceOfNode = 4, KindOfNode = 8, Capital = 12, SupplyIn = 56, SupplyLeft = 64, Connected = 152, NavalNode = 3;
    private const int FlowEntries = 48, FlowEntrySize = 16, CountryOfEntry = 2, AmountOfEntry = 8;      // flow: {u16 node, u16 country, pad, i64 amount}
    private const int RailwayPairs = 0x38;                                                                // railway info: {int neighbour, int level}
    private const int SupplyNodeOfDb = 0x3A0, InfrastructureOfDb = 0x3B0;
    private const int StateOfProvince = 0xC0, ControllerOfProvince = 0x188; // (+0xC8 is the strategic region)
    private const int UnitsOfCountry = 0x290, KindOfUnit = 8, LocationOfUnit = 0x1F0, IsLowSupplySlot = 43;
    private const long SupplyCooldownMs = 60_000;  // supply only changes once something is built
    private const int MaxSupplyActions = 2, MaxRailSearch = 15;

    private delegate* unmanaged<nint, int*, nint, byte, int, nint, byte, byte, nint> _upgradeRail;
    private delegate* unmanaged<nint, void> _bottleneckDtor;
    private delegate* unmanaged<nint, nint, int, int, byte, nint> _findRailPath;
    private delegate* unmanaged<nint, int*, int, byte, byte, void> _queueRailway;
    private nint _supplyNode, _buildingDb;
    private readonly Dictionary<int, long> _supplySent = []; // province id -> until
    private int _saturation, _hubDistance;

    private void LoadSupply()
    {
        _saturation = Math.Clamp(Config.Bind("Supply", "SaturationPercent", 80, "A hub using this much (%) of the supply it gets is upgraded (railway to the capital)."), 1, 100);
        _hubDistance = Math.Clamp(Config.Bind("Supply", "HubDistance", 3, "Build a new supply hub for starving divisions with no hub this close (provinces)."), 1, 10);
        _upgradeRail = (delegate* unmanaged<nint, int*, nint, byte, int, nint, byte, byte, nint>)Sig.Find(SupplySignatures.UpgradeRailToCapital);
        _bottleneckDtor = (delegate* unmanaged<nint, void>)Sig.Find(SupplySignatures.BottleneckDataDtor);
        _findRailPath = (delegate* unmanaged<nint, nint, int, int, byte, nint>)Sig.Find(SupplySignatures.FindRailPath);
        _queueRailway = (delegate* unmanaged<nint, int*, int, byte, byte, void>)Sig.Find(SupplySignatures.QueueRailway);
        var at = Sig.Find(SupplySignatures.BuildingDbCall);
        var getter = at + 5 + *(int*)(at + 1);
        _buildingDb = getter + 18 + *(int*)(getter + 14); // the global; the database is created after the plugins load
        MainThread.Every(TimeSpan.FromSeconds(5), SupplyPump);
    }

    private void SupplyPump()
    {
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        var supply = Game.State == 0 ? 0 : *(nint*)(Game.State + SupplyOfGameState);
        if (country == 0 || supply == 0) return;
        var db = *(nint*)_buildingDb;
        if (db == 0) return;
        _supplyNode = *(nint*)(db + SupplyNodeOfDb);
        if (_infrastructure == 0) _infrastructure = *(nint*)(db + InfrastructureOfDb);
        var countrySupply = *(nint*)(*(nint*)(supply + CountriesOfSupply) + 8 * Game.TagIndex(tag));
        var network = countrySupply == 0 ? 0 : *(nint*)(countrySupply + NetworkOfCountry);
        if (network == 0 || _supplyNode == 0) return;

        var starving = new Dictionary<nint, int>(); // province -> divisions low on supply there
        foreach (var unit in Native.PdxArray(country + UnitsOfCountry))
        {
            if (*(int*)(unit + KindOfUnit) != 0 || ((delegate* unmanaged<nint, byte>)(*(nint**)unit)[IsLowSupplySlot])(unit) == 0) continue;
            var province = *(nint*)(unit + LocationOfUnit);
            if (province != 0) starving[province] = starving.GetValueOrDefault(province) + 1;
        }

        var now = Environment.TickCount64;
        var actions = 0;
        foreach (var (province, _) in starving.OrderByDescending(s => s.Value))
        {
            if (actions == MaxSupplyActions) break;
            var id = *(int*)(province + IdOfProvince);
            if (_supplySent.TryGetValue(id, out var until) && now < until) continue;
            var acted = FixSupply(country, tag, supply, network, province);
            // Whether or not there was something to do, give it time: what was queued or is missing changes slowly.
            _supplySent[id] = now + SupplyCooldownMs;
            if (!acted) continue;
            actions++;
            foreach (var n in Game.Neighbours(province)) _supplySent[n] = now + SupplyCooldownMs; // one fix covers the area
        }
    }

    private bool FixSupply(nint country, int tag, nint supply, nint network, nint province)
    {
        var node = FeedingNode(supply, network, *(int*)(province + IdOfProvince), Game.TagIndex(tag));
        if (node != 0)
        {
            var nodeProvince = *(int*)(node + ProvinceOfNode);
            if (*(long*)(node + Connected) <= 0 && *(byte*)(node + Capital) == 0) return ConnectRailway(network, tag, nodeProvince);
            var supplied = *(long*)(node + SupplyIn);
            if (supplied > 0 && (supplied - *(long*)(node + SupplyLeft)) * 100 / supplied >= _saturation)
                return UpgradeRail(tag, Game.Province(nodeProvince), *(int*)(node + KindOfNode) == NavalNode, nodeProvince);
            if (Nearest(province, _hubDistance, id => id == nodeProvince) >= 0) return false; // near and not saturated: building won't help (disruption)
        }
        return BuildHub(country, tag, network, province);
    }

    /// <summary>Our node sending the most supply into the province (its supply flow), or 0.</summary>
    private static nint FeedingNode(nint supply, nint network, int province, int tagIndex)
    {
        var flow = *(nint*)(supply + FlowOfSupply) + (nint)FlowSize * province;
        var entries = *(nint*)(flow + FlowEntries);
        (int Node, long Amount) best = (-1, 0);
        for (int i = 0, n = *(int*)(flow + FlowEntries + 0xC); i < n; i++)
        {
            var e = entries + i * FlowEntrySize;
            if (*(ushort*)(e + CountryOfEntry) != tagIndex || *(long*)(e + AmountOfEntry) <= best.Amount) continue;
            best = (*(ushort*)e, *(long*)(e + AmountOfEntry));
        }
        return best.Node < 0 || best.Node >= *(int*)(network + Nodes + 0xC) ? 0 : *(nint*)(network + Nodes) + (nint)NodeSize * best.Node;
    }

    private bool UpgradeRail(int tag, nint node, bool naval, int id)
    {
        if (node == 0) return false;
        var data = stackalloc byte[0x40];
        new Span<byte>(data, 0x40).Clear();
        _upgradeRail((nint)data, &tag, node, naval ? (byte)1 : (byte)0, -1, 0, 1, 0);
        var queued = *(int*)(data + 0x14) > 0 || *(int*)(data + 0x2C) > 0; // railway segments, naval base provinces
        _bottleneckDtor((nint)data);
        if (queued) Log.Info($"supply: hub in province {id} is saturated, upgrading its link to the capital");
        return queued;
    }

    /// <summary>A railway (+1 level) from <paramref name="from"/> to the nearest hub connected to the capital.</summary>
    private bool ConnectRailway(nint network, int tag, int from)
    {
        var start = Game.Province(from);
        if (start == 0) return false;
        var connected = new HashSet<int>();
        var pairs = *(nint*)(network + LandNodes);
        for (int i = 0, n = *(int*)(network + LandNodes + 0xC); i < n; i++)
        {
            var node = *(nint*)(network + Nodes) + (nint)NodeSize * *(int*)(pairs + i * 8 + 4);
            if (*(long*)(node + Connected) > 0 || *(byte*)(node + Capital) != 0) connected.Add(*(int*)(pairs + i * 8));
        }
        var target = Nearest(start, MaxRailSearch, connected.Contains);
        if (target < 0) return false;

        var path = stackalloc byte[24]; // CPdxArray<int> with its allocator, filled by FindRailPath
        new Span<byte>(path, 24).Clear();
        _findRailPath(0, (nint)path, from, target, 1);
        var length = *(int*)(path + 0xC);
        if (length > 0) _queueRailway((nint)path, &tag, 1, 1, 1);
        var data = *(nint*)path;
        var allocator = *(nint*)(path + 0x10);
        if (data != 0 && allocator != 0) ((delegate* unmanaged<nint, nint, void>)(*(nint**)allocator)[2])(allocator, data);
        if (length > 0) Log.Info($"supply: railway from province {from} to the connected hub in {target} ({length} provinces)");
        return length > 0;
    }

    /// <summary>A supply hub for the starving province's area, in its state or next to it, then a railway to it if it has none.</summary>
    private bool BuildHub(nint country, int tag, nint network, nint province)
    {
        var state = *(nint*)(province + StateOfProvince);
        if (state == 0 || QueuedHubIn(country, state)) return false;
        var candidates = new List<nint>();
        foreach (var p in Native.PdxArray(state + ProvincesOfState)) candidates.Add(p);
        foreach (var id in Game.Neighbours(province))
            if (Game.Province(id) is var p && p != 0 && !candidates.Contains(p)) candidates.Add(p);
        var best = candidates
            .Where(p => *(int*)(p + ControllerOfProvince) == tag && _canBuildInProvince(country, p, _supplyNode, 1) != 0 && _freeLevels(p + BuildingsOfProvince, _supplyNode) > 0)
            .OrderByDescending(HasRailway).ThenByDescending(p => p == province).ThenBy(p => *(int*)(p + IdOfProvince))
            .FirstOrDefault();
        if (best == 0) return false;
        var hub = *(int*)(best + IdOfProvince);
        if (!PostConstruction(tag, hub, _supplyNode)) return false;
        Log.Info($"supply: no hub near province {*(int*)(province + IdOfProvince)}, building one in {hub}");
        if (!HasRailway(best)) ConnectRailway(network, tag, hub);
        return true;
    }

    private bool QueuedHubIn(nint country, nint state)
    {
        var production = *(nint*)(country + ProductionOfCountry);
        if (production == 0) return false;
        foreach (var line in Native.PdxArray(production + Lines))
        {
            if (*(nint*)line != Sig.VTable(Signatures.BuildingLine)) continue;
            var building = *(nint*)(line + BuildingOfLine);
            if (building == 0 || *(nint*)(building + TypeOfBuilding) != _supplyNode) continue;
            var container = *(nint*)(building + ContainerOfBuilding); // province + BuildingsOfProvince
            if (container != 0 && *(nint*)(container - BuildingsOfProvince + StateOfProvince) == state) return true;
        }
        return false;
    }

    /// <summary>Any railway out of the province (the railway map's level per neighbour).</summary>
    private static bool HasRailway(nint province)
    {
        var railways = *(nint*)(Game.State + RailwaysOfGameState);
        var info = railways == 0 ? 0 : *(nint*)(*(nint*)(railways + 8) + 8 * *(int*)(province + IdOfProvince));
        if (info == 0) return false;
        var pairs = *(nint*)(info + RailwayPairs);
        for (int i = 0, n = *(int*)(info + RailwayPairs + 0xC); i < n; i++)
            if (*(int*)(pairs + i * 8 + 4) > 0) return true;
        return false;
    }


    /// <summary>The nearest province (breadth first over adjacency, up to <paramref name="max"/> steps) matching <paramref name="wanted"/>, or -1.</summary>
    private static int Nearest(nint from, int max, Func<int, bool> wanted)
    {
        var seen = new HashSet<int> { *(int*)(from + IdOfProvince) };
        if (wanted(*(int*)(from + IdOfProvince))) return *(int*)(from + IdOfProvince);
        var frontier = new List<nint> { from };
        for (var depth = 1; depth <= max && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var p in frontier)
            foreach (var id in Game.Neighbours(p))
            {
                if (!seen.Add(id)) continue;
                if (wanted(id)) return id;
                if (Game.Province(id) is var q && q != 0 && *(int*)(q + ControllerOfProvince) != 0) next.Add(q); // land only
            }
            frontier = next;
        }
        return -1;
    }
}
