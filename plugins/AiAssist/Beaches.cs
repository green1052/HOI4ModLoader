using HOI4ModLoader;

namespace AiAssist;

public partial class AiAssist
{
    // ---- Beach defence: landings are walled in and attacked at once, not after the front collapses ----

    private const int BeachheadMax = 8, BeachheadRadius = 6, MaxLandingSends = 6, BeachDefenseHours = 3, LandedHours = 10 * 24;
    private const long LandingHours = 72; // game hours a beachhead province or its wall isn't sent another

    private static int _provinceCount = -1; // probe result: the game has no province enumeration
    // By province id, from the last controller scan: its controller then, the game hour it last changed hands, the hour it was taken
    // from the sea (no neighbour held by the taker or its allies before, or next to its fresh landing). Main thread.
    private static int[] _controllers = [], _changed = [], _landed = [];
    private static int _scanned = Never;
    private int _beachesAt = Never;
    private int _defended;

    /// <summary>
    /// Every three game hours: for each landing on AI ground, the invaded country walls the beachhead in (a division into each of
    /// its empty landward neighbours) and sends attackers into it. Free divisions near the beachhead only; the front is left to the AI.
    /// </summary>
    private void DefendBeachheads()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _beachesAt, BeachDefenseHours)) return; // menus, lobby, loading: no AI commands
        _game.Check(); // a save loaded since the last reset: its caches point into the old world
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
            // An island or a quiet country being invaded has no land line yet; a grad's holders stay on the grad.
            var line = (Grads.TryGetValue(tag, out var grad) ? Held(grad, tag) : LineOf(tag, hour)) ?? [];
            var sent = 0;
            foreach (var beachhead in beachheads)
            {
                var first = Game.Province(beachhead.First());
                var holder = first == 0 ? 0 : Province.Controller(first);
                if (holder == tag || !Game.AtWar(country, holder)) continue; // a friendly or neutral landing, not ours to answer
                foreach (var id in beachhead)
                {
                    var p = Game.Province(id);
                    if (p == 0 || Province.Controller(p) != holder) continue;
                    // Wall it in: an empty land neighbour of ours the landing could pour through gets a defender.
                    foreach (var n in Game.Neighbours(p))
                    {
                        var q = Game.Province(n);
                        if (q == 0 || beachhead.Contains(n) || Gap.ContainsKey(n) || Province.Controller(q) != tag || Standing(country, tag, q) > 0) continue;
                        var helper = Helper(tag, q, line, ReserveRadius);
                        if (helper == 0 || !Send(helper, n, hour, LandingHours)) continue;
                        if (++sent >= MaxLandingSends) goto nextCountry;
                    }
                    // Crush it: attack into the beachhead itself; the move turns into combat on arrival.
                    if (!Gap.ContainsKey(id) && Helper(tag, p, line, BeachheadRadius) is var attacker && attacker != 0 && Send(attacker, id, hour, LandingHours)
                        && ++sent >= MaxLandingSends) goto nextCountry;
                }
            }
        nextCountry: ;
            Interlocked.Add(ref _defended, sent);
        }
    }

    /// <summary>
    /// Beachheads, by holder: provinces taken from the sea in the last ten days and the ones next to them taken since (flood-filled),
    /// and small enemy pockets on the coast with divisions in them whose land side touches a country at war with their holder (one
    /// landing may spread over several provinces; capped: bigger is a home front, not a landing).
    /// </summary>
    // ponytail: same-holder components only — a mixed allied landing answers as several small beachheads, each handled fine.
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
                // A land neighbour held by a country at war with the holder.
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
            foreach (var b in beachhead) seen.Add(b); // its home coast too: don't rescan it every pass
            if (landed || beachhead.Count < BeachheadMax) landings.Add(beachhead);
        }
        return landings;
    }

    /// <summary>
    /// Scans every province's controller (once a game hour at most): a change of hands is stamped, and a coastal one the taker had
    /// barely any land link to (<see cref="FromSea"/>) counts as taken from the sea. Main thread.
    /// </summary>
    private static void TrackControllers(int hour)
    {
        if (hour == _scanned) return;
        _scanned = hour;
        if (_provinceCount < 0)
            for (int id = 1, misses = 0; misses < 512; id++)
                if (Game.Province(id) == 0) misses++;
                else (_provinceCount, misses) = (id, 0); // the run of valid ids up to here
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
        for (var id = 1; id <= _provinceCount; id++)
        {
            var p = Game.Province(id);
            var taker = p == 0 ? 0 : Province.Controller(p);
            if (taker > 0 && Province.BuildingLevel(p, Buildings.NavalBaseSlot) > 0) ports.Add(taker);
            if (taker == before[id]) continue;
            _controllers[id] = taker;
            if (first || taker <= 0 || before[id] <= 0) continue; // the first scan, sea or wasteland
            _changed[id] = hour;
            _landed[id] = FromSea(p, taker, before, hour) ? hour : Never;
        }
        _portHolders = ports;
    }

    /// <summary>Countries holding a naval base, from the last controller scan (null before it): the ones that can land troops. Read by AI worker threads.</summary>
    private static volatile HashSet<int>? _portHolders;

    /// <summary>
    /// Whether <paramref name="p"/>, a coastal province just taken by <paramref name="taker"/>, came from the sea: at the last scan at most
    /// one neighbour was held by it or its allies, not counting its own fresh landing (a landing next to its holdings has a short land
    /// link to them; a push over land comes on a broader front).
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
    private static bool Landed(int id, int hour) => (uint)id < (uint)_landed.Length && hour - _landed[id] is >= 0 and < LandedHours;

    /// <summary>Changed hands in the last <paramref name="hours"/> game hours.</summary>
    private static bool Taken(int id, int hour, int hours) => (uint)id < (uint)_changed.Length && (uint)(hour - _changed[id]) < (uint)hours;

    /// <summary>Whether the province borders the sea (a neighbour with no controller).</summary>
    private static bool Coastal(nint p) =>
        Game.Neighbours(p).Any(id => Game.Province(id) is var q && q != 0 && Province.Controller(q) == 0);
}
