using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Supply hubs: upgrade the railway to the hub feeding starving AI divisions; late, rich countries also build new hubs ----
    //
    // A hub costs 20000, a railway level about 170 + 130 per province: railways any year, hubs only from HubYear with
    // HubCivilianFactories+. Vanilla's interior minister upgrades and links existing hubs but never builds one.
    // Like AutoConstruction's AutoSupply, on the core's supply network, railways and Construction.

    private const int HubDistance = 3, HubCheckHours = 7 * 24, HubsPerMonth = 2, MonthHours = 30 * 24, StarvingTried = 5;
    private const int HubYear = 1941, HubCivilianFactories = 60;
    private const int MaxRailway = 5, RailwayCooldownHours = 60 * 24;

    private static delegate* unmanaged<nint, int*, int, byte, byte> _buildRailway;
    private static int _railways;
    private static readonly Dictionary<(int Tag, int Node), int> RailwayPlans = []; // (tag, feeding node province) -> hour its railway was upgraded (main thread)

    private static int _hubs, _hubWorld = -1, _yearLogged = -2;
    private static readonly Dictionary<int, (int Next, List<int> Queued)> HubPlans = []; // tag -> next check, hours hubs were queued (main thread)

    private void LoadSupplyHubs()
    {
        _buildRailway = (delegate* unmanaged<nint, int*, int, byte, byte>)Sig.Find(AiSignatures2.BuildRailway);
        _ = Sig.Find(Construction.FreeLevelsSig) + Sig.Find(Construction.CanBuildInProvinceSig) // the core's, now: a broken one fails the load
            + Sig.VTable(Construction.CommandClass) + Sig.VTable(Construction.BuildingReference) + Sig.VTable(Construction.LineClass);
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
            HubPlans.Clear(); // the old game's schedule
            RailwayPlans.Clear();
            _hubWorld = Game.Generation;
        }
        var type = Buildings.Type(Buildings.SupplyNodeSlot);
        if (Game.Supply == 0 || type == 0) return;
        var (hour, player) = (Game.Hour, Game.ControlledTag);
        var year = Game.Year;
        if (_hubWorld != _yearLogged) Log.Info($"supply hubs: the game's year reads {year} (built from {HubYear}, by countries with {HubCivilianFactories}+ civilian factories)");
        _yearLogged = _hubWorld;
        var hubYear = year is <= 1900 or >= HubYear; // earlier: railways only; an implausible year leaves it to the factory count
        Dictionary<int, int>? civilian = null;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player) continue;
            HubPlans.TryGetValue(tag, out var plan);
            if (plan.Queued != null && hour - plan.Next is < 0 and >= -HubCheckHours) continue; // not due; a load moving time back makes it due
            var queued = plan.Queued ?? [];
            queued.RemoveAll(h => hour - h is < 0 or >= MonthHours);
            HubPlans[tag] = (hour + HubCheckHours, queued);
            var network = Game.SupplyNetwork(tag);
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
            if (unit == 0 || !Division.IsLand(unit) || !Division.LowSupply(unit)) continue;
            var province = Division.Location(unit);
            if (province != 0) starving[province] = starving.GetValueOrDefault(province) + 1;
        }
        return starving;
    }

    /// <summary>
    /// Raises the railway from the hub feeding the worst-starving area to the capital state by one level, unless it is at the top.
    /// One per check; the same hub at most every <see cref="RailwayCooldownHours"/> hours.
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
            // +1 level along the path in mode 2, as the interior minister calls it; posts the command itself.
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
        if (InState(from, capital)) return null;
        var back = new Dictionary<int, (int From, int Level)> { [from] = (-1, 0) }; // province -> the one before it and the level between
        var queue = new Queue<int>([from]);
        while (queue.TryDequeue(out var id))
        {
            foreach (var (next, level) in Province.Railways(id))
            {
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
                continue; // a hub is near: the railway is what helps
            if (BuildHub(country, tag, province, type, building)) return true;
        }
        return false;
    }

    /// <summary>The province of our node sending the most supply into the province, or -1.</summary>
    private static int FeedingNodeProvince(nint network, int province, int tagIndex) =>
        Game.FeedingNode(network, province, tagIndex) is var node && node != 0 ? *(int*)(node + Game.ProvinceOfNode) : -1;

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
                && Construction.CanBuildInProvince(country, p, type) && Construction.FreeLevels(Province.Buildings(p), type) > 0)
            .OrderByDescending(Province.HasRailway).ThenByDescending(p => p == province).ThenBy(Game.Id)
            .FirstOrDefault();
        if (best == 0 || !Construction.Queue(tag, Game.Id(best), type, true)) return false;
        building.Add(Province.State(best));
        Interlocked.Increment(ref _hubs);
        return true;
    }

    /// <summary>States with a supply hub under construction (the country's construction lines).</summary>
    private static HashSet<nint> QueuedHubStates(nint country, nint type)
    {
        var states = new HashSet<nint>();
        foreach (var (_, building) in Construction.Lines(country, type))
            if (Construction.Container(building) is var container && container != 0 // Province.Buildings(province)
                && Province.State(container - Province.BuildingsOffset) is var s && s != 0) states.Add(s);
        return states;
    }

    /// <summary>Whether a province within <paramref name="max"/> land steps of <paramref name="from"/> matches <paramref name="wanted"/>.</summary>
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
