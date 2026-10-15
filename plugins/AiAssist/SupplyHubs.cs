using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Supply hubs: the railway to the hub feeding starving AI divisions, and a new hub where none is near (late, rich only) ----
    //
    // A hub costs 20000 (a railway level 170 + 130 a province): early on, or with few civilian factories, that money is better
    // spent elsewhere. A railway level is cheap, so the feeding hub's railway to the capital is upgraded first, whatever the year.
    //
    // The interior minister already upgrades saturated hubs' railways and links cut-off hubs weekly; it never builds a new hub.
    // Ported from AutoConstruction's AutoSupply (BuildHub, FeedingNode), same layouts (HOI4 1.19.3); its railway rules are vanilla's here.

    private const int CountriesOfSupply = 0x108, NetworkOfCountry = 0x80, SupplyNodes = 0xB8, SupplyNodeSize = 0xE8, ProvinceOfNode = 4; // CSupplySystem
    private const int FlowEntrySize = 16, CountryOfFlowEntry = 2, AmountOfFlowEntry = 8; // Game.EntriesOfFlow: {u16 node, u16 country, pad, i64 amount}
    private const int RailwaysOfGameState = 0x3E0, RailwayPairs = 0x38;                  // railway info: {int neighbour, int level}
    private const int ProductionOfCountry = 0xF68, ConstructionLines = 112, BuildingOfLine = 112, ContainerOfBuilding = 472, TypeOfBuilding = 480;
    private const int TokenOfType = 8, LocationOfCommand = 56, BuildingOfCommand = 60, LevelsOfCommand = 72, InsertionOfCommand = 76, ConstructionCommandSize = 0x50;
    private const int IsLowSupplySlot = 43; // CArmy vtable
    private const int HubDistance = 3, HubCheckHours = 7 * 24, HubsPerMonth = 2, MonthHours = 30 * 24, StarvingTried = 5;
    private const int HubYear = 1941, HubCivilianFactories = 60;
    private const int MaxRailway = 5, RailwayCooldownHours = 60 * 24;

    private static delegate* unmanaged<nint, int*, int, byte, byte> _buildRailway;
    private static int _railways;
    private static readonly Dictionary<(int Tag, int Node), int> RailwayPlans = []; // (tag, feeding node province) -> hour its railway was upgraded (main thread)

    private static delegate* unmanaged<nint, nint, int> _freeLevels;
    private static delegate* unmanaged<nint, nint, nint, byte, byte> _canBuildInProvince;
    private static int _hubs, _hubWorld = -1, _yearLogged = -2;
    private static readonly Dictionary<int, (int Next, List<int> Queued)> HubPlans = []; // tag -> next check, hours hubs were queued (main thread)

    private void LoadSupplyHubs()
    {
        _freeLevels = (delegate* unmanaged<nint, nint, int>)Sig.Find(AiSignatures2.FreeLevels);
        _canBuildInProvince = (delegate* unmanaged<nint, nint, nint, byte, byte>)Sig.Find(AiSignatures2.CanBuildInProvince);
        _buildRailway = (delegate* unmanaged<nint, int*, int, byte, byte>)Sig.Find(AiSignatures2.BuildRailway);
        _ = Sig.VTable(AiSignatures2.ConstructionCommand) + Sig.VTable(AiSignatures2.BuildingReference) + Sig.VTable(AiSignatures2.BuildingLine);
        MainThread.Every(TimeSpan.FromSeconds(10), SupplyHubs);
    }

    /// <summary>
    /// Per AI country, every week of game time: a railway level toward the capital for the hub feeding its starving divisions,
    /// and up to <see cref="HubsPerMonth"/> new hubs a month where they starve with none near.
    /// </summary>
    private void SupplyHubs()
    {
        if (!Game.HasStarted || !SheepMod.Active) return;
        _game.Check();
        if (_hubWorld != Game.Generation)
        {
            HubPlans.Clear(); // tags only, but the old game's schedule
            RailwayPlans.Clear();
            _hubWorld = Game.Generation;
        }
        var supply = Game.Supply;
        var type = Buildings.Type(Buildings.SupplyNodeSlot);
        var networks = supply == 0 ? 0 : *(nint*)(supply + CountriesOfSupply);
        if (networks == 0 || type == 0) return;
        var (hour, player) = (Game.Hour, Game.ControlledTag);
        var year = Game.Year;
        if (_hubWorld != _yearLogged) Log.Info($"supply hubs: the game's year reads {year} (built from {HubYear}, by countries with {HubCivilianFactories}+ civilian factories)");
        _yearLogged = _hubWorld;
        var hubYear = year is <= 1900 or >= HubYear; // earlier: railways only (a date that makes no sense: the factories alone decide)
        Dictionary<int, int>? civilian = null;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player) continue;
            HubPlans.TryGetValue(tag, out var plan);
            if (plan.Queued != null && hour - plan.Next is < 0 and >= -HubCheckHours) continue; // not due (a load moving time back: due)
            var queued = plan.Queued ?? [];
            queued.RemoveAll(h => hour - h is < 0 or >= MonthHours);
            HubPlans[tag] = (hour + HubCheckHours, queued);
            var countrySupply = *(nint*)(networks + 8 * Game.TagIndex(tag));
            var network = countrySupply == 0 ? 0 : *(nint*)(countrySupply + NetworkOfCountry);
            var starving = network == 0 ? [] : Starving(country);
            if (starving.Count == 0) continue;
            UpgradeRailway(country, tag, network, starving, hour);
            if (!hubYear || queued.Count >= HubsPerMonth) continue;
            civilian ??= CivilianFactories();
            if (civilian.GetValueOrDefault(tag) < HubCivilianFactories) continue;
            if (NewHub(country, tag, network, type, starving)) queued.Add(hour);
        }
    }

    /// <summary>Civilian factories by the tag controlling each state (its main province's controller).</summary>
    private static Dictionary<int, int> CivilianFactories()
    {
        var civilian = new Dictionary<int, int>();
        var type = Buildings.Type(Buildings.CivilianFactorySlot);
        if (type == 0) return civilian;
        ProbeProvinces();
        var counted = new HashSet<nint>();
        for (var id = 1; id <= _provinceCount; id++)
        {
            var state = Game.Province(id) is var p && p != 0 ? Province.State(p) : 0;
            if (state == 0 || !counted.Add(state)) continue;
            var main = State.MainProvince(state);
            var tag = main == 0 ? 0 : Province.Controller(main);
            if (tag > 0) civilian[tag] = civilian.GetValueOrDefault(tag) + Buildings.Level(State.Buildings(state), type);
        }
        return civilian;
    }

    /// <summary>Province -> the country's land divisions low on supply there.</summary>
    private static Dictionary<nint, int> Starving(nint country)
    {
        var starving = new Dictionary<nint, int>();
        foreach (var unit in Game.Units(country))
        {
            if (unit == 0 || !Division.IsLand(unit) || ((delegate* unmanaged<nint, byte>)(*(nint**)unit)[IsLowSupplySlot])(unit) == 0) continue;
            var province = Division.Location(unit);
            if (province != 0) starving[province] = starving.GetValueOrDefault(province) + 1;
        }
        return starving;
    }

    /// <summary>
    /// One railway level more (the AI's own builder) on the railway from the hub feeding the worst-starving area to the capital
    /// state, where it isn't at the top level yet; one a check, and a hub's railway at most every <see cref="RailwayCooldownHours"/> hours.
    /// </summary>
    private static void UpgradeRailway(nint country, int tag, nint network, Dictionary<nint, int> starving, int hour)
    {
        var capital = *(int*)(country + CapitalOfCountry); // a state id
        foreach (var (province, _) in starving.OrderByDescending(s => s.Value).Take(StarvingTried))
        {
            var feeding = FeedingNodeProvince(network, Game.Id(province), Game.TagIndex(tag));
            if (feeding < 0 || RailwayPlans.TryGetValue((tag, feeding), out var last) && hour - last is >= 0 and < RailwayCooldownHours) continue;
            var path = RailwayToCapital(feeding, tag, capital, out var lowest);
            if (path == null || lowest >= MaxRailway) continue; // in the capital state, not linked by our railways, or at the top already
            // +1 level along the path, mode 2 as the interior minister calls it; it posts the command itself.
            if (Native.WithPdxArray(path, p => { var t = tag; return _buildRailway(p, &t, 1, 2); }) == 0) continue; // refused: the next starving area
            RailwayPlans[(tag, feeding)] = hour;
            Interlocked.Increment(ref _railways);
            return;
        }
    }

    /// <summary>
    /// The railway (fewest provinces, all controlled by the tag) from the province to the capital state, and its lowest level;
    /// null when the province is in the capital state or no railway of ours links them.
    /// </summary>
    private static int[]? RailwayToCapital(int from, int tag, int capital, out int lowest)
    {
        lowest = 0;
        var railways = *(nint*)(Game.State + RailwaysOfGameState);
        if (railways == 0 || InState(from, capital)) return null;
        var back = new Dictionary<int, (int From, int Level)> { [from] = (-1, 0) }; // province -> the one before it and the level between
        var queue = new Queue<int>([from]);
        while (queue.TryDequeue(out var id))
        {
            var info = *(nint*)(*(nint*)(railways + 8) + 8 * id);
            if (info == 0) continue;
            var pairs = *(nint*)(info + RailwayPairs);
            for (int i = 0, n = *(int*)(info + RailwayPairs + 0xC); i < n; i++)
            {
                var (next, level) = (*(int*)(pairs + i * 8), *(int*)(pairs + i * 8 + 4));
                if (level <= 0 || back.ContainsKey(next) || Game.Province(next) is var p && (p == 0 || Province.Controller(p) != tag)) continue;
                back[next] = (id, level);
                if (!InState(next, capital))
                {
                    queue.Enqueue(next);
                    continue;
                }
                var path = new List<int>();
                lowest = MaxRailway;
                for (var at = next; at != from; at = back[at].From)
                {
                    path.Add(at);
                    lowest = Math.Min(lowest, back[at].Level);
                }
                path.Add(from);
                path.Reverse();
                return [.. path];
            }
        }
        return null;
    }

    private static bool InState(int province, int state) => Game.Province(province) is var p && p != 0 && Province.State(p) is var s && s != 0 && State.Id(s) == state;

    /// <summary>A hub for the worst-starving area of the country that has none within <see cref="HubDistance"/> provinces.</summary>
    private static bool NewHub(nint country, int tag, nint network, nint type, Dictionary<nint, int> starving)
    {
        var building = QueuedHubStates(country, type);
        foreach (var (province, _) in starving.OrderByDescending(s => s.Value).Take(StarvingTried))
        {
            var feeding = FeedingNodeProvince(network, Game.Id(province), Game.TagIndex(tag));
            if (Near(province, HubDistance, id => id == feeding || Game.Province(id) is var p && p != 0 && Province.Controller(p) == tag && Province.BuildingLevel(p, Buildings.SupplyNodeSlot) > 0))
                continue; // a hub is near: vanilla's railway rules are what helps
            if (BuildHub(country, tag, province, type, building)) return true;
        }
        return false;
    }

    /// <summary>The province of our node sending the most supply into the province (its supply flow), or -1.</summary>
    private static int FeedingNodeProvince(nint network, int province, int tagIndex)
    {
        var flow = Game.SupplyFlow(province);
        if (flow == 0) return -1;
        var entries = *(nint*)(flow + Game.EntriesOfFlow);
        (int Node, long Amount) best = (-1, 0);
        for (int i = 0, n = *(int*)(flow + Game.EntriesOfFlow + 0xC); i < n; i++)
        {
            var e = entries + i * FlowEntrySize;
            if (*(ushort*)(e + CountryOfFlowEntry) != tagIndex || *(long*)(e + AmountOfFlowEntry) <= best.Amount) continue;
            best = (*(ushort*)e, *(long*)(e + AmountOfFlowEntry));
        }
        var nodes = *(nint*)(network + SupplyNodes);
        return best.Node < 0 || nodes == 0 || best.Node >= *(int*)(network + SupplyNodes + 0xC) ? -1 : *(int*)(nodes + (nint)SupplyNodeSize * best.Node + ProvinceOfNode);
    }

    /// <summary>A hub in the starving province's state or next to it (railway-connected provinces first), where none is queued.</summary>
    private static bool BuildHub(nint country, int tag, nint province, nint type, HashSet<nint> building)
    {
        var state = Province.State(province);
        if (state == 0 || building.Contains(state)) return false;
        var candidates = new List<nint>();
        foreach (var p in State.Provinces(state)) candidates.Add(p);
        foreach (var id in Game.Neighbours(province))
            if (Game.Province(id) is var p && p != 0 && !candidates.Contains(p)) candidates.Add(p);
        var best = candidates
            .Where(p => Province.Controller(p) == tag && Province.State(p) is var s && s != 0 && !building.Contains(s)
                && _canBuildInProvince(country, p, type, 1) != 0 && _freeLevels(Province.Buildings(p), type) > 0)
            .OrderByDescending(HasRailway).ThenByDescending(p => p == province).ThenBy(Game.Id)
            .FirstOrDefault();
        if (best == 0) return false;
        var cmd = Commands.New(AiSignatures2.ConstructionCommand, ConstructionCommandSize, tag, Sig.VTable(AiSignatures2.BuildingReference));
        *(int*)(cmd + LocationOfCommand) = Game.Id(best);
        *(int*)(cmd + BuildingOfCommand) = *(int*)(type + TokenOfType);
        *(int*)(cmd + LevelsOfCommand) = 1;
        *(byte*)(cmd + InsertionOfCommand) = 1; // at the end of the queue
        if (!Commands.TryPost(cmd, true)) return false;
        building.Add(Province.State(best));
        Interlocked.Increment(ref _hubs);
        return true;
    }

    /// <summary>States with a supply hub under construction (the country's construction lines).</summary>
    private static HashSet<nint> QueuedHubStates(nint country, nint type)
    {
        var states = new HashSet<nint>();
        var production = *(nint*)(country + ProductionOfCountry);
        if (production == 0) return states;
        var lineVTable = Sig.VTable(AiSignatures2.BuildingLine);
        foreach (var line in Native.PdxArray(production + ConstructionLines))
        {
            var b = line != 0 && *(nint*)line == lineVTable ? *(nint*)(line + BuildingOfLine) : 0;
            var container = b == 0 || *(nint*)(b + TypeOfBuilding) != type ? 0 : *(nint*)(b + ContainerOfBuilding); // Province.Buildings(province)
            if (container != 0 && Province.State(container - Province.BuildingsOffset) is var s && s != 0) states.Add(s);
        }
        return states;
    }

    /// <summary>Any railway out of the province (the railway map's level per neighbour).</summary>
    private static bool HasRailway(nint province)
    {
        var railways = *(nint*)(Game.State + RailwaysOfGameState);
        var info = railways == 0 ? 0 : *(nint*)(*(nint*)(railways + 8) + 8 * Game.Id(province));
        if (info == 0) return false;
        var pairs = *(nint*)(info + RailwayPairs);
        for (int i = 0, n = *(int*)(info + RailwayPairs + 0xC); i < n; i++)
            if (*(int*)(pairs + i * 8 + 4) > 0) return true;
        return false;
    }

    /// <summary>A province within <paramref name="max"/> land steps of <paramref name="from"/> matches <paramref name="wanted"/>.</summary>
    private static bool Near(nint from, int max, Func<int, bool> wanted)
    {
        var start = Game.Id(from);
        if (wanted(start)) return true;
        var seen = new HashSet<int> { start };
        var frontier = new List<nint> { from };
        for (var depth = 1; depth <= max && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var p in frontier)
            foreach (var id in Game.Neighbours(p))
            {
                if (!seen.Add(id)) continue;
                if (wanted(id)) return true;
                if (Game.Province(id) is var q && q != 0 && Province.Controller(q) != 0) next.Add(q); // land only
            }
            frontier = next;
        }
        return false;
    }
}
