using HOI4ModLoader;

namespace AiAssist;

public partial class AiAssist
{
    // ---- Beach defence: landings are walled in and attacked at once ----

    private const int BeachheadMax = 8, BeachheadRadius = 6, MaxLandingSends = 6, BeachDefenseHours = 3, LandedHours = 10 * 24;
    private const long LandingHours = 72; // game hours a beachhead or wall province gets no second division

    private static int _provinceCount = -1; // last province id, from ProbeProvinces
    // By province id, from the last controller scan: controller, hour it last changed hands, hour it was taken from the sea.
    // Written on the main thread (replaced whole on resize or load), read on AI threads: readers take one reference.
    private static int[] _controllers = [], _changed = [], _landed = [];
    private static int _scanned = Never;
    private int _beachesAt = Never;
    private int _defended;

    /// <summary>
    /// Every three game hours: for each landing on AI ground, the invaded country puts a division in each empty landward
    /// neighbour and attacks into the beachhead. Uses free divisions nearby only.
    /// </summary>
    private void DefendBeachheads()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _beachesAt, BeachDefenseHours)) return; // menus, lobby, loading: no AI commands
        _game.Check(); // a save loaded since the last reset
        var hour = Game.Hour;
        TrackControllers(hour);
        var beachheads = Landings(hour);
        if (beachheads.Count == 0) return;
        Expire(hour);
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player) continue;
            // An island may have no land line yet; a grad's holders stay on the grad.
            var line = (Grads.TryGetValue(tag, out var grad) ? Held(grad, tag) : LineOf(tag, hour)) ?? [];
            var sent = 0;
            // With HoldTheLine they are posted, so the front AI can't walk them back; the post ends when no enemy is next to the
            // wall, or after LandingHours for an attacker. Without it they are only sent.
            bool Wall(nint unit, int province) =>
                _holdLine ? Station(unit, province, tag, hour, LandingHours, PostKind.Helper) : Send(unit, province, hour, LandingHours);
            bool Strike(nint unit, int province) =>
                _holdLine ? Station(unit, province, tag, hour, LandingHours, PostKind.Drive, hour + (int)LandingHours) : Send(unit, province, hour, LandingHours);
            foreach (var beachhead in beachheads)
            {
                var first = Game.Province(beachhead.First());
                var holder = first == 0 ? 0 : Province.Controller(first);
                if (holder == tag || !Game.AtWar(country, holder)) continue; // not an enemy landing
                foreach (var id in beachhead)
                {
                    var p = Game.Province(id);
                    if (p == 0 || Province.Controller(p) != holder) continue;
                    // Wall it in: each empty land neighbour of ours gets a defender.
                    foreach (var n in Game.Neighbours(p))
                    {
                        var q = Game.Province(n);
                        if (q == 0 || beachhead.Contains(n) || Gap.ContainsKey(n) || Province.Controller(q) != tag || Standing(country, tag, q) > 0) continue;
                        var helper = Helper(tag, q, line, ReserveRadius);
                        if (helper == 0 || !Wall(helper, n)) continue;
                        if (++sent >= MaxLandingSends) goto nextCountry;
                    }
                    // Attack into the beachhead: the move turns into combat on arrival.
                    if (!Gap.ContainsKey(id) && Helper(tag, p, line, BeachheadRadius) is var attacker && attacker != 0 && Strike(attacker, id)
                        && ++sent >= MaxLandingSends) goto nextCountry;
                }
            }
        nextCountry: ;
            Interlocked.Add(ref _defended, sent);
        }
    }

    /// <summary>
    /// Beachheads, by holder: provinces taken from the sea in the last ten days plus neighbours taken since (flood-filled), and
    /// small coastal pockets with divisions next to a country at war with their holder. Capped at BeachheadMax provinces.
    /// </summary>
    // ponytail: same-holder components only — a mixed allied landing becomes several small beachheads, each handled fine.
    private static List<HashSet<int>> Landings(int hour)
    {
        var landings = new List<HashSet<int>>();
        var seen = new HashSet<int>();
        for (var id = 1; id <= _provinceCount; id++)
        {
            if (seen.Contains(id)) continue;
            var p = Game.Province(id);
            if (p == 0) continue;
            var holder = Province.Controller(p);
            if (holder <= 0) continue;
            var landed = Landed(id, hour);
            if (!landed)
            {
                if (Province.Divisions(p) == 0 || !Coastal(p)) continue;
                var holderCountry = Game.Country(holder);
                if (holderCountry == 0) continue;
                var contested = Game.Neighbours(p).Any(n => Game.Province(n) is var q && q != 0
                    && Province.Controller(q) is var controller && controller != holder && Game.AtWar(holderCountry, controller));
                if (!contested) continue;
            }
            var beachhead = new HashSet<int> { id };
            var frontier = new List<int> { id };
            while (frontier.Count > 0 && beachhead.Count < BeachheadMax)
            {
                var next = new List<int>();
                foreach (var b in frontier)
                foreach (var n in Game.Province(b) is var bp && bp != 0 ? Game.Neighbours(bp) : [])
                    if (Game.Province(n) is var q && q != 0 && Province.Controller(q) == holder && (!landed || Landed(n, hour)) && beachhead.Add(n)) next.Add(n);
                frontier = next;
            }
            foreach (var b in beachhead) seen.Add(b); // also its home coast, so it isn't rescanned
            if (landed || beachhead.Count < BeachheadMax) landings.Add(beachhead);
        }
        return landings;
    }

    /// <summary>
    /// Scans every province's controller, at most once a game hour: stamps changes of hands and landings (<see cref="FromSea"/>),
    /// and records who holds naval bases. Main thread.
    /// </summary>
    private static void TrackControllers(int hour)
    {
        if (hour == _scanned) return;
        _scanned = hour;
        ProbeProvinces();
        var first = _controllers.Length == 0;
        if (_controllers.Length <= _provinceCount)
        {
            var size = _provinceCount + 1;
            var old = _controllers.Length;
            Array.Resize(ref _controllers, size);
            Array.Resize(ref _changed, size);
            Array.Resize(ref _landed, size);
            Array.Fill(_changed, Never, old, size - old);
            Array.Fill(_landed, Never, old, size - old);
        }
        var before = (int[])_controllers.Clone();
        var ports = new HashSet<int>();
        var homePorts = new Dictionary<int, int>();
        for (var id = 1; id <= _provinceCount; id++)
        {
            var p = Game.Province(id);
            var taker = p == 0 ? 0 : Province.Controller(p);
            if (taker > 0 && Province.BuildingLevel(p, Buildings.NavalBaseSlot) > 0)
            {
                ports.Add(taker);
                if (Province.State(p) is var state && state != 0 && Native.PdxArray<int>(state + CoresOfState).Contains(taker))
                    homePorts[taker] = homePorts.GetValueOrDefault(taker) + 1;
            }
            if (taker == before[id]) continue;
            _controllers[id] = taker;
            if (first || taker <= 0 || before[id] <= 0) continue; // the first scan, sea or wasteland
            _changed[id] = hour;
            _landed[id] = FromSea(p, taker, before, hour) ? hour : Never;
        }
        _portHolders = ports;
        _homePorts = homePorts;
    }

    /// <summary>Finds the last province id once (the game has no province enumeration): stops after 512 missing ids in a row.</summary>
    private static void ProbeProvinces()
    {
        if (_provinceCount >= 0) return;
        for (int id = 1, misses = 0; misses < 512; id++)
            if (Game.Province(id) == 0) misses++;
            else (_provinceCount, misses) = (id, 0);
    }

    /// <summary>Naval bases each country holds on its own core ground, from the last controller scan. Read by AI worker threads.</summary>
    private static volatile Dictionary<int, int> _homePorts = [];

    /// <summary>Countries holding a naval base (can land troops), from the last controller scan; null before it. Read by AI worker threads.</summary>
    private static volatile HashSet<int>? _portHolders;

    /// <summary>
    /// Whether coastal <paramref name="p"/>, just taken by <paramref name="taker"/>, came from the sea: at the last scan at most one
    /// neighbour (not counting its fresh landings) was held by it or its allies. A push over land comes on a broader front.
    /// </summary>
    private static bool FromSea(nint p, int taker, int[] before, int hour)
    {
        var country = Game.Country(taker);
        if (country == 0 || !Coastal(p)) return false;
        var linked = 0;
        foreach (var n in Game.Neighbours(p))
            if ((uint)n < (uint)before.Length && before[n] > 0 && (before[n] == taker || Game.Allied(country, before[n])) && !Landed(n, hour) && ++linked > 1)
                return false;
        return true;
    }

    /// <summary>Taken from the sea in the last LandedHours, and held by the same country since (a change of hands resets it).</summary>
    private static bool Landed(int id, int hour) => _landed is var landed && (uint)id < (uint)landed.Length && hour - landed[id] is >= 0 and < LandedHours;

    /// <summary>Changed hands in the last <paramref name="hours"/> game hours.</summary>
    private static bool Taken(int id, int hour, int hours) => _changed is var changed && (uint)id < (uint)changed.Length && (uint)(hour - changed[id]) < (uint)hours;

    /// <summary>Whether the province borders the sea (a neighbour with no controller).</summary>
    private static bool Coastal(nint p) =>
        Game.Neighbours(p).Any(id => Game.Province(id) is var q && q != 0 && Province.Controller(q) == 0);
}
