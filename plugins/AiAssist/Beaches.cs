using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Beach defence: landings are walled in and attacked at once, not after the front collapses ----

    private const int BeachheadMax = 8, BeachheadRadius = 6, MaxLandingSends = 6;
    private const long LandingMs = 300_000;

    private static int _provinceCount = -1; // probe result: the game has no province enumeration
    private int _defended;

    /// <summary>
    /// Every half minute: for each landing on AI ground, the invaded country walls the beachhead in (a division into each of
    /// its empty landward neighbours) and sends attackers into it. Free divisions near the beachhead only; the front is left to the AI.
    /// </summary>
    private void DefendBeachheads()
    {
        if (!Game.HasStarted || !SheepMod.Active) return; // menus, lobby, loading: no AI commands (and no mods mounted yet)
        _game.Check(); // a save loaded since the last reset: its caches point into the old world
        var beachheads = Landings();
        if (beachheads.Count == 0) return;
        var now = Environment.TickCount64;
        Expire(now);
        var hour = Game.Hour;
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
                        if (q == 0 || beachhead.Contains(n) || _gap.ContainsKey(n) || Province.Controller(q) != tag || Standing(country, tag, q) > 0) continue;
                        var helper = Helper(tag, q, line, ReserveRadius);
                        if (helper == 0 || !Send(helper, n, now, LandingMs)) continue;
                        if (++sent >= MaxLandingSends) goto nextCountry;
                    }
                    // Crush it: attack into the beachhead itself; the move turns into combat on arrival.
                    if (!_gap.ContainsKey(id) && Helper(tag, p, line, BeachheadRadius) is var attacker && attacker != 0 && Send(attacker, id, now, LandingMs)
                        && ++sent >= MaxLandingSends) goto nextCountry;
                }
            }
        nextCountry: ;
            Interlocked.Add(ref _defended, sent);
        }
    }

    /// <summary>
    /// Enemy coastal provinces with divisions on them, whose land side touches a country at war with their holder: flood-filled
    /// into beachheads by holder (one landing may spread over several provinces), capped — bigger is a home front, not a landing.
    /// </summary>
    // ponytail: same-holder components only — a mixed allied landing answers as several small beachheads, each handled fine.
    private static List<HashSet<int>> Landings()
    {
        var landings = new List<HashSet<int>>();
        var seen = new HashSet<int>();
        for (int id = 1, misses = 0; misses < 512 && (_provinceCount < 0 || id <= _provinceCount); id++)
        {
            var p = Game.Province(id);
            if (p == 0) { misses++; continue; }
            misses = 0;
            _provinceCount = id; // the run of valid ids up to here
            var holder = Province.Controller(p);
            if (holder <= 0 || seen.Contains(id) || Province.Divisions(p) == 0 || !Coastal(p)) continue;
            var holderCountry = Game.Country(holder);
            if (holderCountry == 0) continue;
            // A land neighbour held by a country at war with the holder.
            var contested = Game.Neighbours(p).Any(n => Game.Province(n) is var q && q != 0
                && Province.Controller(q) is var controller && controller != holder && Game.AtWar(holderCountry, controller));
            if (!contested) continue;
            var beachhead = new HashSet<int> { id };
            var frontier = new List<int> { id };
            while (frontier.Count > 0 && beachhead.Count < BeachheadMax)
            {
                var next = new List<int>();
                foreach (var b in frontier)
                foreach (var n in Game.Province(b) is var bp && bp != 0 ? Game.Neighbours(bp) : [])
                    if (Game.Province(n) is var q && q != 0 && Province.Controller(q) == holder && beachhead.Add(n)) next.Add(n);
                frontier = next;
            }
            foreach (var b in beachhead) seen.Add(b); // its home coast too: don't rescan it every half minute
            if (beachhead.Count < BeachheadMax) landings.Add(beachhead);
        }
        return landings;
    }

    /// <summary>Whether the province borders the sea (a neighbour with no controller).</summary>
    private static bool Coastal(nint p) =>
        Game.Neighbours(p).Any(id => Game.Province(id) is var q && q != 0 && Province.Controller(q) == 0);
}
