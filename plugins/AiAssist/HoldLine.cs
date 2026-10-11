using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Hold the line: pinned defenders, holes refilled, attacks wait on a thinly manned line ----

    private const int LocationOfUnit = 0x1F0, OwnerOfUnit = 0x1E0, Path = 0x200, Retreating = 0x24C, Busy = 0x2F8;
    private const int ArrivalOfUnit = 0x160, FreshHours = 168;
    private const int LineFreshHours = 48, MaxBackfills = 2, BackfillRadius = 10, MinBackfillOrg = 60;
    private const long SentMs = 120_000, GapMs = 600_000; // a helper walks without new orders; a hole isn't refilled at once
    private const int KeyBackfillRadius = 20, KeyHole = 60;
    private const long KeyGapMs = 120_000;
    private const int ReserveMax = 6, ReservePerLine = 4, ReserveMoves = 2, ReserveRadius = 6;
    private const int BeachheadMax = 8, BeachheadRadius = 6, MaxLandingSends = 6;
    private const long LandingMs = 300_000, LandingScanMs = 30_000;

    [Function(CallingConventions.Microsoft)] public delegate byte CollectFn(nint context, nint list);
    private IHook<CollectFn>? _collect;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<nint> Provinces)> _line = new(); // AI front -> its threatened provinces, refreshed hourly
    private readonly Dictionary<nint, long> _sent = []; // unit -> until: a division sent at a hole is left alone while it walks
    private readonly Dictionary<int, long> _gap = [];   // province id -> until: a hole a helper is walking to
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, byte> _posted = new(); // units standing on reserve posts: the theatre shuffle leaves them
    private List<HashSet<int>> _beachheads = [];
    private long _beachScanned;
    private static int _provinceCount = -1; // probe result: the game has no province enumeration
    private PerGame _game = null!;
    private delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint> _move;
    private int _backfilled, _reserved, _defended;

    /// <summary>
    /// A division that took up its post only days ago: the theatre shuffle would march it somewhere else and back
    /// (ping-pong). While it walks, its arrival field is a schedule, not a stamp; a stale schedule reads as long ago.
    /// </summary>
    private static bool FreshlyArrived(nint unit)
    {
        if (*(int*)(unit + Path + 0xC) != 0) return false;
        var age = *(int*)(Game.State + HourOfGameState) - *(int*)(unit + ArrivalOfUnit);
        if (age is < 0 or >= FreshHours) return false;
        Interlocked.Increment(ref _stationed);
        return true;
    }

    /// <summary>The AI's regrouping can't take this division: it is the last one of its country in a province enemy divisions
    /// can walk into, or it stands on the wall of a pocket (an enemy province with divisions whose neighbours are all ours).</summary>
    private static bool Pinned(nint unit)
    {
        if (unit == 0) return false;
        var tag = *(int*)(unit + CommanderOfUnit);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        if (_posted.ContainsKey(unit)) return true; // standing on a reserve post: the theatre shuffle must leave it
        var country = Game.Country(tag);
        var location = *(nint*)(unit + LocationOfUnit);
        if (country == 0 || location == 0) return false;
        var alone = true;
        foreach (var other in Native.PdxArray(location + UnitsInProvince))
            if (other != unit && *(int*)(other + CommanderOfUnit) == tag) alone = false;
        foreach (var id in Game.Neighbours(location))
        {
            var p = Game.Province(id);
            if (p == 0 || *(int*)(p + DivisionsInProvince) == 0 || !Enemy(country, tag, p)) continue;
            if (alone || Game.Neighbours(p).All(n => Game.Province(n) is var q && (q == 0 || *(int*)(q + ControllerOfProvince) == 0 || !Enemy(country, tag, q))))
                return true; // nothing else holds the province, or a cut-off pocket sits against it
        }
        return false;
    }

    /// <summary>The country's tracked threatened line (fresh for two days), or null.</summary>
    private HashSet<nint>? LineOf(int tag, int hour)
    {
        HashSet<nint>? line = null;
        foreach (var e in _line)
            if (e.Value.Tag == tag && hour - e.Value.Hour is >= 0 and < LineFreshHours)
                (line ??= []).UnionWith(e.Value.Provinces);
        return line;
    }

    /// <summary>
    /// The country may start attacks: its divisions standing on the threatened line outnumber the line's provinces by a
    /// margin, or no line is tracked. Attacking on a barely manned line widens it, the holes open, and the spearhead gets
    /// cut off. Attacks already running are never touched.
    /// </summary>
    private bool Manned(int tag)
    {
        var line = LineOf(tag, *(int*)(Game.State + HourOfGameState));
        if (line == null || line.Count == 0) return true;
        var country = Game.Country(tag);
        var divisions = 0;
        foreach (var province in line)
            divisions += Standing(country, tag, province);
        return line.Count * 6 <= divisions * 5; // ~1.2 divisions per threatened province
    }

    /// <summary>Divisions of the tag or its allies standing in the province.</summary>
    private static int Standing(nint country, int tag, nint province)
    {
        var divisions = 0;
        var other = stackalloc int[2]; // {tag, tag index} of a division's commander
        foreach (var unit in Native.PdxArray(province + UnitsInProvince))
        {
            var commander = *(int*)(unit + CommanderOfUnit);
            if (commander == tag) { divisions++; continue; }
            (other[0], other[1]) = (commander, Game.TagIndex(commander));
            if (commander > 0 && _allied(country, other, 0, 0) != 0) divisions++;
        }
        return divisions;
    }

    /// <summary>
    /// Free divisions walk back into empty provinces of the threatened line, the ones worth holding first; a country with divisions to
    /// spare also keeps reserve posts a province behind its line.
    /// </summary>
    private void Backfill()
    {
        _game.Check();
        if (!SheepMod.Active || Game.State == 0) return; // 0 while the world is still loading: Game.Countries guards it too
        var now = Environment.TickCount64;
        foreach (var (unit, until) in _sent.ToArray()) if (now >= until) _sent.Remove(unit);
        foreach (var (province, until) in _gap.ToArray()) if (now >= until) _gap.Remove(province);
        var hour = *(int*)(Game.State + HourOfGameState);
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            var line = LineOf(tag, hour);
            if (line == null)
            {
                if (_reserve) Unpin(tag); // its front is gone: its reserve stops being one
                continue;
            }
            var sent = 0;
            foreach (var province in line.Where(p => !_gap.ContainsKey(Id(p)) && Standing(country, tag, p) == 0)
                                         .OrderByDescending(p => _defensePriority ? Key(country, tag, p) : 0))
            {
                if (sent == MaxBackfills) break;
                var id = Id(province);
                var key = _defensePriority && Key(country, tag, province) >= KeyHole; // a port, a city on a river: worth walking further for
                var helper = Helper(tag, province, line, key ? KeyBackfillRadius : BackfillRadius);
                if (helper == 0) continue; // nothing free nearby: the attack gate keeps the front still instead
                Move(helper, id);
                _sent[helper] = now + SentMs;
                _gap[id] = now + (key ? KeyGapMs : GapMs);
                sent++;
                Interlocked.Increment(ref _backfilled);
            }
            if (_reserve && Manned(tag)) Reserve(country, tag, line, now);
        }
    }

    /// <summary>
    /// A country with divisions to spare posts reserves a province behind its line: the posts covering the most line provinces
    /// (key ground first) each get a division, which stays until the front moves.
    /// </summary>
    private void Reserve(nint country, int tag, HashSet<nint> line, long now)
    {
        if (line.Count < ReservePerLine) return; // a short line is its own reserve
        var posts = new Dictionary<nint, int>();
        foreach (var p in line)
        foreach (var id in Game.Neighbours(p))
        {
            var q = Game.Province(id);
            if (q == 0 || posts.ContainsKey(q) || line.Contains(q) || *(int*)(q + ControllerOfProvince) != tag) continue;
            if (Game.Neighbours(q).Any(n => Game.Province(n) is var r && r != 0 && Enemy(country, tag, r))) continue; // on the front itself
            posts[q] = 0;
        }
        foreach (var post in posts.Keys.ToList())
            posts[post] = Game.Neighbours(post).Count(n => Game.Province(n) is var q && q != 0 && line.Contains(q));
        var want = Math.Min(ReserveMax, line.Count / ReservePerLine);
        var sent = 0;
        foreach (var (post, covers) in posts.OrderByDescending(p => p.Value).ThenBy(p => Key(country, tag, p.Key)).Take(want))
        {
            if (sent == ReserveMoves || covers == 0) break;
            var id = Id(post);
            if (Standing(country, tag, post) > 0 || _gap.ContainsKey(id)) continue;
            var helper = Helper(tag, post, line, ReserveRadius);
            if (helper == 0) continue;
            Move(helper, id);
            _posted[helper] = 1;
            _sent[helper] = now + SentMs;
            _gap[id] = now + GapMs;
            sent++;
            Interlocked.Increment(ref _reserved);
        }
    }

    /// <summary>The tag's tracked line is gone: its reserve divisions stop being reserves.</summary>
    private static void Unpin(int tag)
    {
        foreach (var unit in _posted.Where(p => *(int*)(p.Key + CommanderOfUnit) == tag).Select(p => p.Key).ToList()) _posted.TryRemove(unit, out _);
    }

    /// <summary>What holding this province of ours is worth: a port (the fleet and supply), victory points, a river in front of it, city or mountain ground.</summary>
    private static int Key(nint country, int tag, nint province)
    {
        var db = *(nint*)_buildingDb;
        var type = db == 0 ? 0 : *(nint*)(db + NavalBaseType);
        var port = type == 0 ? 0 : _findBuilding(province + BuildingsOfProvince, type);
        if (port != 0 && *(short*)(port + 0x40) > 0) return 1000 + *(short*)(port + 0x40);
        var key = 3 * Math.Min(*(int*)(province + VictoryPoints), 30);
        var terrain = *(nint*)(*(nint*)(province + StaticOfProvince) + TerrainOfStatic);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || !Enemy(country, tag, p) || Adjacency(province, id) < River) continue;
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }

    /// <summary>The nearest free, rested division of the tag within radius provinces of ours, not standing on the line itself, or 0.</summary>
    private nint Helper(int tag, nint target, HashSet<nint> line, int radius = BackfillRadius)
    {
        var seen = new HashSet<int> { Id(target) };
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
                if (!line.Contains(p))
                    foreach (var unit in Native.PdxArray(p + UnitsInProvince))
                        if (Spare(unit, tag)) return unit;
                next.Add(p);
            }
            frontier = next;
        }
        return 0;
    }

    /// <summary>A land division of the tag that can move now: rested, idle, and not one we sent already.</summary>
    private bool Spare(nint unit, int tag)
    {
        if (*(int*)(unit + KindOfUnit) != 0 || *(int*)(unit + CommanderOfUnit) != tag || *(int*)(unit + OwnerOfUnit) != tag) return false;
        if (*(int*)(unit + CombatsOfUnit) != 0 || *(byte*)(unit + Retreating) != 0 || *(nint*)(unit + Busy) != 0) return false;
        if (*(int*)(unit + Path + 0xC) != 0 || _sent.ContainsKey(unit)) return false;
        long org;
        ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[OrgRatioSlot])(unit, &org);
        return org >= (long)MinBackfillOrg * Native.FixedPoint / 100;
    }

    /// <summary>A plain move of a free division into an empty line province, posted the way the AI posts its own moves.</summary>
    private void Move(nint unit, int province)
    {
        var cmd = Allocator.AllocZeroed(0x88);
        _move(cmd, unit, province, 1, 2, 0, 0, 0, 0, 0);
        Commands.TryPost(cmd, true);
    }

    // ---- Beach defence: landings are walled in and attacked at once, not after the front collapses ----

    /// <summary>
    /// Every half minute: for each tracked landing on AI ground, the invaded country walls the beachhead in (a division into each of
    /// its empty landward neighbours) and sends attackers into it. Free divisions next to the beachhead only; the front is left to the AI.
    /// </summary>
    private void DefendBeachheads()
    {
        _game.Check();
        if (!SheepMod.Active || Game.State == 0) return; // 0 while the world is still loading: Game.Countries guards it too
        var now = Environment.TickCount64;
        if (now - _beachScanned >= LandingScanMs)
        {
            _beachheads = Landings();
            _beachScanned = now;
        }
        if (_beachheads.Count == 0) return;
        var hour = *(int*)(Game.State + HourOfGameState);
        var player = Game.ControlledTag;
        int* other = stackalloc int[2]; // {tag, tag index} of a beachhead holder
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            var line = LineOf(tag, hour);
            if (line == null) continue; // no tracked line: the next Concentrate starts one
            var sent = 0;
            foreach (var beachhead in _beachheads)
            {
                var holder = 0;
                foreach (var id in beachhead) { holder = *(int*)(Game.Province(id) + ControllerOfProvince); break; }
                if (holder <= 0 || holder == tag) continue;
                (other[0], other[1]) = (holder, Game.TagIndex(holder));
                if (_atWar(country, other) == 0) continue; // a friendly or neutral landing, not ours to answer
                foreach (var id in beachhead)
                {
                    var p = Game.Province(id);
                    if (p == 0 || *(int*)(p + ControllerOfProvince) != holder) continue;
                    // Wall it in: an empty land neighbour of ours the landing could pour through gets a defender.
                    foreach (var n in Game.Neighbours(p))
                    {
                        var q = Game.Province(n);
                        if (q == 0 || _gap.ContainsKey(n) || *(int*)(q + ControllerOfProvince) != tag || Standing(country, tag, q) > 0) continue;
                        if (beachhead.Contains(n)) continue;
                        var helper = Helper(tag, q, line, ReserveRadius);
                        if (helper == 0) continue;
                        Move(helper, n);
                        _sent[helper] = now + SentMs;
                        _gap[n] = now + LandingMs;
                        if (++sent >= MaxLandingSends) goto nextCountry;
                    }
                    // Crush it: attack into the beachhead itself; the move turns into combat on arrival.
                    if (!_gap.ContainsKey(id))
                    {
                        var helper = Helper(tag, p, line, BeachheadRadius);
                        if (helper != 0)
                        {
                            Move(helper, id);
                            _sent[helper] = now + SentMs;
                            _gap[id] = now + LandingMs;
                            if (++sent >= MaxLandingSends) goto nextCountry;
                        }
                    }
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
        int* other = stackalloc int[2]; // {tag, tag index} of a province's controller
        for (int id = 1, misses = 0; misses < 512 && (_provinceCount < 0 || id <= _provinceCount); id++)
        {
            var p = Game.Province(id);
            if (p == 0) { misses++; continue; }
            misses = 0;
            _provinceCount = id; // the run of valid ids up to here
            var holder = *(int*)(p + ControllerOfProvince);
            if (holder <= 0 || seen.Contains(id) || *(int*)(p + DivisionsInProvince) == 0 || !Coastal(p)) continue;
            var holderCountry = Game.Country(holder);
            if (holderCountry == 0) continue;
            var contested = false; // a land neighbour held by a country at war with the holder
            foreach (var n in Game.Neighbours(p))
            {
                var controller = Game.Province(n) is var q && q != 0 ? *(int*)(q + ControllerOfProvince) : 0;
                if (controller <= 0 || controller == holder) continue;
                (other[0], other[1]) = (controller, Game.TagIndex(controller));
                if (_atWar(holderCountry, other) != 0) { contested = true; break; }
            }
            if (!contested) continue;
            var beachhead = new HashSet<int> { id };
            var frontier = new List<int> { id };
            while (frontier.Count > 0 && beachhead.Count < BeachheadMax)
            {
                var next = new List<int>();
                foreach (var b in frontier)
                foreach (var n in Game.Neighbours(Game.Province(b)))
                    if (Game.Province(n) is var q && q != 0 && *(int*)(q + ControllerOfProvince) == holder && beachhead.Add(n)) next.Add(n);
                frontier = next;
            }
            foreach (var b in beachhead) seen.Add(b); // its home coast too: don't rescan it every half minute
            if (beachhead.Count < BeachheadMax) landings.Add(beachhead);
        }
        return landings;
    }

    /// <summary>Whether the province borders the sea (an adjacency to a province with no controller).</summary>
    private static bool Coastal(nint p)
    {
        var info = *(nint*)(p + StaticOfProvince);
        var records = *(nint*)(info + AdjacenciesOfStatic);
        for (int i = 0, n = *(int*)(info + AdjacenciesOfStatic + 0xC); i < n; i++)
        {
            var q = Game.Province(*(int*)(records + i * 0x30 + 8));
            if (q != 0 && *(int*)(q + ControllerOfProvince) == 0) return true;
        }
        return false;
    }
}
