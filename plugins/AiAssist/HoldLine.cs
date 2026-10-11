using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Hold the line: pinned defenders, holes refilled, reserves, rotation and escape; attacks wait on a thinly manned line ----

    private const int KindOfUnit = 8, GroupOfUnit = 0xC0, CombatsOfUnit = 0x1B4, UnitsOfCountry = 0x290;
    private const int LocationOfUnit = 0x1F0, OwnerOfUnit = 0x1E0, Path = 0x200, Retreating = 0x24C, Busy = 0x2F8;
    private const int ArrivalOfUnit = 0x160, FreshHours = 168;
    private const int LineFreshHours = 3, MaxBackfills = 2, BackfillRadius = 10, MinBackfillOrg = 60; // TrackFronts refreshes the line hourly
    private const long SentMs = 120_000, GapMs = 600_000; // a helper walks without new orders; a hole isn't refilled at once
    private const int KeyBackfillRadius = 20, KeyHole = 60;
    private const long KeyGapMs = 120_000;
    private const int ReserveMax = 6, ReservePerLine = 4, ReserveMoves = 2, ReserveRadius = 6;
    private const int BeachheadMax = 8, BeachheadRadius = 6, MaxLandingSends = 6;
    private const long LandingMs = 300_000, LandingScanMs = 30_000;
    private const int WornOrg = 30, WornStrength = 50, RestedOrg = 90, RestedStrength = 80, MaxRestingPercent = 25; // %
    private const int MaxMoves = 2, RestDepth = 2, MaxRestSearch = 6, MaxNeckSearch = 8, MaxRestHours = 168;

    [Function(CallingConventions.Microsoft)] public delegate byte CollectFn(nint context, nint list);
    private IHook<CollectFn>? _collect;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<nint> Provinces)> _line = new(); // AI front -> its threatened provinces, refreshed hourly
    private static readonly Dictionary<nint, long> _sent = []; // unit -> until: a division sent at a hole is left alone while it walks
    private static readonly Dictionary<int, long> _gap = [];   // province id -> until: a hole a helper is walking to
    // Units standing on reserve posts, with their tag: the theatre shuffle leaves them. Never dereferenced (a unit may be gone).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> _posted = new();
    private static List<HashSet<int>> _beachheads = [];
    private long _beachScanned;
    private static int _provinceCount = -1; // probe result: the game has no province enumeration
    private PerGame _game = null!;
    // Divisions we took off their front to rest or escape, with their tag and since when: the theatre shuffle and army balancing
    // leave them until rested, then the AI puts them on a front again. Never dereferenced unless still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> _resting = new();
    private static delegate* unmanaged<nint, nint, nint, byte, byte, nint> _unassign;
    private int _backfilled, _reserved, _defended, _rested, _escaped, _deployed;
    // New divisions we sent where they were needed, with their tag and since when: the theatre shuffle and army balancing leave
    // them while they walk (FreshlyArrived keeps them a week once there). Never dereferenced.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> _fresh = new();
    private static readonly Dictionary<int, HashSet<nint>> Known = []; // tag -> its land divisions at its last backfill pass (main thread)
    private const int FreshWalkHours = 168;

    /// <summary>
    /// A division that took up its post only days ago: the theatre shuffle would march it somewhere else and back
    /// (ping-pong). While it walks, its arrival field is a schedule, not a stamp; a stale schedule reads as long ago.
    /// </summary>
    private static bool FreshlyArrived(nint unit)
    {
        if (*(int*)(unit + Path + 0xC) != 0) return false;
        var age = Hour - *(int*)(unit + ArrivalOfUnit);
        if (age is < 0 or >= FreshHours) return false;
        Interlocked.Increment(ref _stationed);
        return true;
    }

    /// <summary>The AI's regrouping can't take this division: it stands on a reserve post or rests, or it is the last one
    /// of its country in a province enemy divisions can walk into, or it stands on the wall of a pocket (an enemy province
    /// with divisions whose neighbours are all ours).</summary>
    private static bool Pinned(nint unit)
    {
        if (unit == 0) return false;
        var tag = *(int*)(unit + CommanderOfUnit);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        if (_posted.ContainsKey(unit) || _resting.ContainsKey(unit) || Paratrooper(unit)
            || _fresh.TryGetValue(unit, out var fresh) && Hour - fresh.Hour is >= 0 and < FreshWalkHours) return true;
        var country = Game.Country(tag);
        var location = *(nint*)(unit + LocationOfUnit);
        if (country == 0 || location == 0) return false;
        var alone = true;
        foreach (var other in Native.PdxArray(location + UnitsInProvince))
            if (other != unit && *(int*)(other + CommanderOfUnit) == tag) alone = false;
        foreach (var id in Game.Neighbours(location))
        {
            var p = Game.Province(id);
            if (p == 0 || Divisions(p) == 0 || !Enemy(country, tag, p)) continue;
            if (alone || Game.Neighbours(p).All(n => Game.Province(n) is var q && (q == 0 || *(int*)(q + ControllerOfProvince) == 0 || !Enemy(country, tag, q))))
                return true; // nothing else holds the province, or a cut-off pocket sits against it
        }
        return false;
    }

    /// <summary>The country's tracked threatened line (refreshed within the last hours) that it still holds, or null.</summary>
    private static HashSet<nint>? LineOf(int tag, int hour)
    {
        HashSet<nint>? line = null;
        foreach (var e in _line)
            if (e.Value.Tag == tag && hour - e.Value.Hour is >= 0 and < LineFreshHours)
                foreach (var province in e.Value.Provinces)
                    if (*(int*)(province + ControllerOfProvince) == tag) (line ??= []).Add(province);
        return line;
    }

    /// <summary>
    /// The country may start attacks: its divisions standing on the threatened line outnumber the line's provinces by a
    /// margin, or no line is tracked. Attacking on a barely manned line widens it, the holes open, and the spearhead gets
    /// cut off. Attacks already running are never touched.
    /// </summary>
    private static bool Manned(int tag, HashSet<nint>? line)
    {
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
        foreach (var unit in Native.PdxArray(province + UnitsInProvince))
        {
            var commander = *(int*)(unit + CommanderOfUnit);
            if (commander == tag || Allied(country, commander)) divisions++;
        }
        return divisions;
    }

    /// <summary>
    /// Free divisions walk back into empty provinces of the threatened line, the ones worth holding first; a country with divisions to
    /// spare also keeps reserve posts a province behind its line.
    /// </summary>
    private void Backfill()
    {
        if (!SheepMod.Active || Game.State == 0) return; // 0 while the world is still loading: Game.Countries guards it too
        var now = Environment.TickCount64;
        Expire(now);
        var hour = Hour;
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            PrunePosted(country, tag);
            var fresh = NewDivisions(country, tag); // every pass: one deployed in peace isn't new once war comes
            var grad = _postures ? Posture(country, tag, hour) : null; // a much stronger enemy: its grad is the line
            var line = grad != null ? Held(grad, tag) : LineOf(tag, hour);
            if (line == null)
            {
                Unpin(tag); // its front is gone: its reserve stops being one
                continue;
            }
            if (fresh.Count > 0) Deploy(country, tag, fresh, grad, line, hour, now);
            var sent = 0;
            var holes = line.Where(p => !_gap.ContainsKey(Game.Id(p)) && Standing(country, tag, p) < (grad?.Want.GetValueOrDefault(Game.Id(p), 1) ?? 1))
                .Select(p => (Province: p, Key: Key(country, tag, p)))
                .OrderByDescending(h => h.Key);
            foreach (var (province, key) in holes)
            {
                if (sent == (grad != null ? MaxGradMoves : MaxBackfills)) break;
                var important = grad != null || key >= KeyHole; // a port, a city on a river, the grad: worth walking further for
                var helper = Helper(tag, province, line, important ? KeyBackfillRadius : BackfillRadius);
                if (helper == 0 || !(grad != null ? Garrison(helper, Game.Id(province), tag, now)
                        : Send(helper, Game.Id(province), now, important ? KeyGapMs : GapMs))) continue; // nothing free nearby: the attack gate keeps the front still instead
                sent++;
                Interlocked.Increment(ref _backfilled);
            }
            if (Manned(tag, line)) Reserve(country, tag, line, now);
        }
    }

    /// <summary>The tag's land divisions that weren't there at its last pass (just deployed, or come from elsewhere); none on its first pass in a game.</summary>
    // ponytail: by pointer; a new division in a dead one's memory is missed (vanilla places it), read a creation date if that shows.
    private static List<nint> NewDivisions(nint country, int tag)
    {
        var units = new HashSet<nint>();
        foreach (var unit in Native.PdxArray(country + UnitsOfCountry))
            if (unit != 0 && *(int*)(unit + KindOfUnit) == 0) units.Add(unit);
        var fresh = Known.TryGetValue(tag, out var old) ? units.Where(u => !old.Contains(u)).ToList() : [];
        Known[tag] = units;
        foreach (var (unit, since) in _fresh)
            if (since.Tag == tag && Hour - since.Hour is < 0 or >= FreshWalkHours) _fresh.TryRemove(unit, out _);
        return fresh;
    }

    /// <summary>
    /// New divisions go where the country needs them most, not where vanilla's theatre share-out points: its grad's holes (on one), else
    /// the empty provinces of its threatened line (key ground first), then the least held provinces of its most thinly held front until
    /// that front is manned. Off their orders and pinned while they walk; whatever is left over is vanilla's.
    /// </summary>
    // ponytail: first come, first sent, no distance; new divisions mostly stand together at the deployment province.
    private void Deploy(nint country, int tag, List<nint> fresh, Grad? grad, HashSet<nint> line, int hour, long now)
    {
        var free = new Queue<nint>(fresh.Where(u => Movable(u, tag)));
        var holes = line.Where(p => !_gap.ContainsKey(Game.Id(p)) && Standing(country, tag, p) < (grad?.Want.GetValueOrDefault(Game.Id(p), 1) ?? 1))
            .OrderByDescending(p => Key(country, tag, p));
        foreach (var hole in holes)
        {
            if (free.Count == 0) return;
            var unit = free.Dequeue();
            if (grad != null ? Garrison(unit, Game.Id(hole), tag, now) : Dispatch(unit, Game.Id(hole), tag, hour, now)) Interlocked.Increment(ref _deployed);
        }
        if (grad != null || free.Count == 0) return; // on a grad, the old line's fronts are given up
        Dictionary<nint, int>? thinnest = null;
        foreach (var e in _line)
        {
            if (e.Value.Tag != tag || hour - e.Value.Hour is < 0 or >= LineFreshHours) continue;
            var held = e.Value.Provinces.Where(p => *(int*)(p + ControllerOfProvince) == tag).ToDictionary(p => p, p => Standing(country, tag, p));
            if (held.Count > 0 && (thinnest == null || held.Values.Sum() * thinnest.Count < thinnest.Values.Sum() * held.Count)) thinnest = held;
        }
        if (thinnest == null) return;
        var divisions = thinnest.Values.Sum();
        while (free.Count > 0 && thinnest.Count * 6 > divisions * 5) // ~1.2 divisions per threatened province, as Manned
        {
            var target = thinnest.MinBy(p => p.Value).Key;
            if (!Dispatch(free.Dequeue(), Game.Id(target), tag, hour, now)) continue;
            thinnest[target]++;
            divisions++;
            Interlocked.Increment(ref _deployed);
        }
    }

    /// <summary>Off its orders (else its front walks it back) and to the province, pinned while it walks there (<see cref="Pinned"/>). Main thread.</summary>
    private static bool Dispatch(nint unit, int province, int tag, int hour, long now)
    {
        var group = *(nint*)(unit + GroupOfUnit);
        if (group != 0) Unassign(unit, group);
        if (!Send(unit, province, now, GapMs)) return false;
        _fresh[unit] = (tag, hour);
        return true;
    }

    /// <summary>Drops the bookings whose time is up: a sent division is free again, a hole may be refilled. Main thread.</summary>
    private static void Expire(long now)
    {
        foreach (var (unit, until) in _sent.ToArray()) if (now >= until) _sent.Remove(unit);
        foreach (var (province, until) in _gap.ToArray()) if (now >= until) _gap.Remove(province);
    }

    /// <summary>Moves a helper to the province and books it: the helper is left alone while it walks, the hole isn't refilled meanwhile.</summary>
    private static bool Send(nint helper, int province, long now, long gapMs)
    {
        if (!Commands.Move(helper, province, true)) return false; // posted the way the AI posts its own moves
        _sent[helper] = now + SentMs;
        _gap[province] = now + gapMs;
        return true;
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
        foreach (var (post, covers) in posts.OrderByDescending(p => p.Value).ThenByDescending(p => Key(country, tag, p.Key)).Take(want))
        {
            if (sent == ReserveMoves || covers == 0) break;
            var id = Game.Id(post);
            if (Standing(country, tag, post) > 0 || _gap.ContainsKey(id)) continue;
            var helper = Helper(tag, post, line, ReserveRadius);
            if (helper == 0 || !Send(helper, id, now, GapMs)) continue;
            _posted[helper] = tag;
            sent++;
            Interlocked.Increment(ref _reserved);
        }
    }

    /// <summary>The tag's tracked line is gone: its reserve divisions stop being reserves.</summary>
    private static void Unpin(int tag)
    {
        foreach (var (unit, owner) in _posted) if (owner == tag) _posted.TryRemove(unit, out _);
    }

    /// <summary>Drops the tag's posted reserves that are no longer its divisions (died, merged): their memory may be another unit's now.</summary>
    private static void PrunePosted(nint country, int tag)
    {
        HashSet<nint>? units = null;
        foreach (var (unit, owner) in _posted)
        {
            if (owner != tag) continue;
            units ??= [.. Native.PdxArray(country + UnitsOfCountry)];
            if (!units.Contains(unit)) _posted.TryRemove(unit, out _);
        }
    }

    /// <summary>What holding this province of ours is worth: a port (the fleet and supply), victory points, a river in front of it, city or mountain ground.</summary>
    private static int Key(nint country, int tag, nint province)
    {
        var db = *(nint*)_buildingDb;
        var type = db == 0 ? 0 : *(nint*)(db + _navalBaseSlot);
        var port = type == 0 ? 0 : _findBuilding(province + BuildingsOfProvince, type);
        if (port != 0 && *(short*)(port + 0x40) > 0) return 1000 + *(short*)(port + 0x40);
        var key = 3 * Math.Min(*(int*)(province + VictoryPoints), 30);
        var terrain = Terrain(province);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || !Enemy(country, tag, p) || Game.AdjacencyType(province, id) < River) continue;
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }

    /// <summary>The nearest free, rested division of the tag within radius provinces of ours, not standing on the line itself, or 0.</summary>
    private static nint Helper(int tag, nint target, HashSet<nint> line, int radius = BackfillRadius)
    {
        var seen = new HashSet<int> { Game.Id(target) };
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

    /// <summary>A land division of the tag (not lent) that can take orders now: not fighting, retreating or embarked, not one we sent or rest.</summary>
    private static bool Movable(nint unit, int tag)
    {
        if (*(int*)(unit + KindOfUnit) != 0 || *(int*)(unit + CommanderOfUnit) != tag || *(int*)(unit + OwnerOfUnit) != tag) return false;
        if (*(int*)(unit + CombatsOfUnit) != 0 || *(byte*)(unit + Retreating) != 0 || *(nint*)(unit + Busy) != 0) return false;
        return !_sent.ContainsKey(unit) && !_resting.ContainsKey(unit) && !Paratrooper(unit);
    }

    /// <summary>A movable division that stands idle, rested enough to go somewhere.</summary>
    private static bool Spare(nint unit, int tag) => Movable(unit, tag) && *(int*)(unit + Path + 0xC) == 0 && Percent(unit, OrgRatioSlot) >= MinBackfillOrg;

    /// <summary>A CArmy ratio slot in % of full.</summary>
    private static long Percent(nint unit, int slot) => Ratio(unit, slot) * 100 / Native.FixedPoint;

    // ---- Rotation and escape: AI divisions come off their front to rest, or before a salient is cut off ----

    /// <summary>
    /// Every five seconds, per AI country: rested divisions go back to the AI, worn-out ones and ones in a closing salient
    /// come off the line (hold the line), fresh ones take empty enemy provinces (attack targets).
    /// </summary>
    private void Micro()
    {
        if (!SheepMod.Active || Game.State == 0) return; // 0 while the world is still loading: Game.Countries guards it too
        var now = Environment.TickCount64;
        Expire(now);
        var hour = Hour;
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            var units = Native.PdxArray(country + UnitsOfCountry).ToArray();
            if (_holdLine)
            {
                Release(tag, units, hour);
                PullBack(country, tag, units, now, hour);
            }
            if (Grads.TryGetValue(tag, out var grad)) CounterAttack(country, tag, grad, units, now); // on its grad: no piecemeal taking back of given-up ground
            else if (_attackTargets) TakeEmpty(country, tag, units, now);
        }
    }

    /// <summary>
    /// The tag's resting divisions that rested (or rested a week: short of equipment, they won't get better) go back to the
    /// AI, whose theatre shuffle puts them on a front again; gone ones are dropped.
    /// </summary>
    private static void Release(int tag, nint[] units, int hour)
    {
        foreach (var (unit, rest) in _resting)
            if (rest.Tag == tag && (Array.IndexOf(units, unit) < 0 || *(int*)(unit + Path + 0xC) == 0
                    && (Percent(unit, OrgRatioSlot) >= RestedOrg && Percent(unit, StrengthRatioSlot) >= RestedStrength || hour - rest.Hour is < 0 or >= MaxRestHours)))
                _resting.TryRemove(unit, out _);
    }

    /// <summary>
    /// Divisions in a salient whose way back is closing, and worn-out ones next to enemy divisions (while few enough of the
    /// country rest), come off their front to a province a few steps back, where another of ours stays in theirs.
    /// </summary>
    private void PullBack(nint country, int tag, nint[] units, long now, int hour)
    {
        var land = 0;
        foreach (var unit in units) if (*(int*)(unit + KindOfUnit) == 0) land++;
        var resting = _resting.Values.Count(r => r.Tag == tag);
        var moves = 0;
        var narrow = new Dictionary<nint, bool>(); // province -> its way back is closing
        var left = new Dictionary<nint, int>();    // province -> divisions still holding it
        foreach (var unit in units)
        {
            if (moves == MaxMoves) return;
            if (!Movable(unit, tag)) continue;
            var location = *(nint*)(unit + LocationOfUnit);
            if (location == 0) continue;
            if (!left.TryGetValue(location, out var here)) here = left[location] = Standing(country, tag, location);
            if (here < 2) continue; // alone there: it holds on
            if (!narrow.TryGetValue(location, out var closing)) closing = narrow[location] = Narrow(country, tag, location);
            if (closing && Grads.TryGetValue(tag, out var grad) && grad.Fortresses.Contains(Game.Id(location))) closing = false; // a fortress city on the grad holds to the last
            var worn = Percent(unit, OrgRatioSlot) < WornOrg || Percent(unit, StrengthRatioSlot) < WornStrength;
            if (!closing && !(worn && (resting + 1) * 100 <= land * MaxRestingPercent && EnemyDivisionsNext(country, tag, location))) continue;
            var rest = RestProvince(country, tag, location);
            if (rest < 0) continue; // nowhere safe nearby: it stays
            var group = *(nint*)(unit + GroupOfUnit);
            if (group != 0) Unassign(unit, group); // on its front, the AI would walk it straight back
            if (!Commands.Move(unit, rest, true)) continue;
            _sent[unit] = now + SentMs;
            _resting[unit] = (tag, hour);
            left[location] = here - 1;
            resting++;
            moves++;
            if (closing) Interlocked.Increment(ref _escaped);
            else Interlocked.Increment(ref _rested);
        }
    }

    /// <summary>
    /// Going back from <paramref name="from"/> over our provinces, one ring (provinces at the same distance) is a single
    /// province next to enemy divisions before reaching provinces away from the enemy: the neck of a closing pocket.
    /// </summary>
    // ponytail: rings, not a max-flow of disjoint paths; a wide salient with a one-province gap elsewhere is missed.
    private static bool Narrow(nint me, int tag, nint from)
    {
        if (!NextToEnemy(me, tag, from)) return false;
        var seen = new HashSet<int> { Game.Id(from) };
        var ring = new List<nint> { from };
        for (var depth = 1; depth <= MaxNeckSearch; depth++)
        {
            var next = new List<nint>();
            foreach (var province in ring)
            foreach (var id in Game.Neighbours(province))
                if (seen.Add(id) && Game.Province(id) is var p && p != 0 && *(int*)(p + ControllerOfProvince) == tag) next.Add(p);
            if (next.Count == 0) return false;                               // cut off already, or an island: nowhere to go
            if (next.Any(p => !NextToEnemy(me, tag, p))) return false;       // safe ground within reach
            if (next.Count == 1) return EnemyDivisionsNext(me, tag, next[0]); // the only way back, enemy troops next to it (a coastal strip isn't a pocket)
            ring = next;
        }
        return false;
    }

    /// <summary>A province of ours at least RestDepth steps back through our provinces, with no enemy-held neighbour; -1 if none nearby.</summary>
    // ponytail: looks at who holds provinces, not at enemy units inside ours (paratroopers, a breakthrough in progress).
    private static int RestProvince(nint me, int tag, nint from)
    {
        var seen = new HashSet<int> { Game.Id(from) };
        var frontier = new List<nint> { from };
        for (var depth = 1; depth <= MaxRestSearch && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var province in frontier)
            foreach (var id in Game.Neighbours(province))
            {
                if (!seen.Add(id)) continue;
                var p = Game.Province(id);
                if (p == 0 || *(int*)(p + ControllerOfProvince) != tag) continue;
                if (depth >= RestDepth && !NextToEnemy(me, tag, p)) return id;
                next.Add(p);
            }
            frontier = next;
        }
        return -1;
    }

    private static bool NextToEnemy(nint me, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Enemy(me, tag, p));

    private static bool EnemyDivisionsNext(nint me, int tag, nint province) =>
        Game.Neighbours(province).Any(n => Game.Province(n) is var p && p != 0 && Divisions(p) > 0 && Enemy(me, tag, p));

    /// <summary>Off every order, staying in its army: COrderUnassignCommand, as the battle plan UI sends it.</summary>
    private static void Unassign(nint unit, nint group)
    {
        var cmd = Allocator.AllocZeroed(0x50);
        _unassign(cmd, unit, group, 0, 1); // stays in the army; off its army group's fronts too
        Commands.TryPost(cmd, true);
    }

    // ---- The attack gate: an "attack on" is dropped while the country's threatened line is thinly manned; the AI asks again every hour ----

    private const int GroupOfCommand = 0x28, OrderOfCommand = 0x30, ExecuteOn = 0x34;   // COrderExecuteCommand
    private const int TypeOfOrder = 0x30, Executing = 0x248, Invasion = 3, Paradrop = 4; // COrder

    [Function(CallingConventions.Microsoft)] public delegate byte PostFn(nint cmd, nint tag);

    private IHook<PostFn>? _post;
    private static delegate* unmanaged<nint, int, nint> _findOrder;
    private static nint _executeVTable;
    private static int _held;

    /// <summary>
    /// The one PostAICommand hook (every AI command): AI order commands that would undo a paradrop of ours (StrategicParadrops),
    /// then the launch gate: invasions by their beach (StrategicInvasions), other attacks by the line (HoldTheLine).
    /// </summary>
    private void LoadPostAICommand()
    {
        _findOrder = (delegate* unmanaged<nint, int, nint>)Sig.Find(Signatures.FindOrder);
        _executeVTable = Sig.VTable(Signatures.ExecuteCommand);
        _post = Patches.Hook<PostFn>(Sig.Find(Signatures.PostAICommand), (cmd, tag) =>
        {
            if (*(int*)tag == Game.ControlledTag || !SheepMod.Active) return _post!.OriginalFunction(cmd, tag);
            bool keep;
            try
            {
                keep = !_paradrops || KeepsParadrops(cmd);
                if (keep && *(nint*)cmd == _executeVTable && *(byte*)(cmd + ExecuteOn) != 0 && !Ready(cmd, tag))
                {
                    keep = false;
                    Interlocked.Increment(ref _held);
                }
            }
            catch (Exception e)
            {
                Failed(e);
                keep = true;
            }
            if (keep) return _post!.OriginalFunction(cmd, tag);
            ((delegate* unmanaged<nint, int, void>)(*(nint**)cmd)[0])(cmd, 1); // deleted, as the game does with an invalid command
            return 0;
        });
    }

    /// <summary>
    /// Whether an "attack on" may go ahead: it runs already, a paradrop, an invasion whose beach isn't crowded (StrategicInvasions),
    /// or the country holds no grad (DefensivePostures) and its threatened line is manned (HoldTheLine). Read only: AI threads.
    /// </summary>
    private static bool Ready(nint cmd, nint tag)
    {
        var reference = (int*)(cmd + GroupOfCommand);
        if (reference[0] == 0 && reference[1] == 0) return true;
        var group = _resolve((nint)reference);
        if (group == 0) return true;
        var id = *(int*)(cmd + OrderOfCommand);
        // CAIOperation::UpdateExecution launches an invasion for the whole group: order id 0 (`xor r8d,r8d` at 0x141A4D9C2).
        var order = id != 0 ? _findOrder(group, id) : Unlaunched(group);
        if (id != 0 && (order == 0 || *(byte*)(order + Executing) != 0)) return true; // running
        if (order != 0 && *(int*)(order + TypeOfOrder) is Invasion or Paradrop) // at sea: the home line has no say
            return *(int*)(order + TypeOfOrder) != Invasion || !_strategicInvasions || BeachClear(group, order, *(int*)tag);
        if (Grads.ContainsKey(*(int*)tag)) return false; // on its grad (DefensivePostures): no offensives until strong enough again; landings and paradrops are judged above
        return !_holdLine || Manned(*(int*)tag, LineOf(*(int*)tag, Hour)); // a thin line doesn't advance: attacking widens it, the holes get the spearhead cut off
    }

    // ---- Beach defence: landings are walled in and attacked at once, not after the front collapses ----

    /// <summary>
    /// Every half minute: for each tracked landing on AI ground, the invaded country walls the beachhead in (a division into each of
    /// its empty landward neighbours) and sends attackers into it. Free divisions next to the beachhead only; the front is left to the AI.
    /// </summary>
    private void DefendBeachheads()
    {
        if (!SheepMod.Active || Game.State == 0) return; // 0 while the world is still loading: Game.Countries guards it too
        var now = Environment.TickCount64;
        Expire(now);
        if (now - _beachScanned >= LandingScanMs)
        {
            _beachheads = Landings();
            _beachScanned = now;
        }
        if (_beachheads.Count == 0) return;
        var hour = Hour;
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            // An island or a quiet country being invaded has no land line yet; a grad's holders stay on the grad.
            var line = (Grads.TryGetValue(tag, out var grad) ? Held(grad, tag) : LineOf(tag, hour)) ?? [];
            var sent = 0;
            foreach (var beachhead in _beachheads)
            {
                var first = Game.Province(beachhead.First());
                var holder = first == 0 ? 0 : *(int*)(first + ControllerOfProvince);
                if (holder == tag || !AtWar(country, holder)) continue; // a friendly or neutral landing, not ours to answer
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
            var holder = *(int*)(p + ControllerOfProvince);
            if (holder <= 0 || seen.Contains(id) || Divisions(p) == 0 || !Coastal(p)) continue;
            var holderCountry = Game.Country(holder);
            if (holderCountry == 0) continue;
            // A land neighbour held by a country at war with the holder.
            var contested = Game.Neighbours(p).Any(n => Game.Province(n) is var q && q != 0
                && *(int*)(q + ControllerOfProvince) is var controller && controller != holder && AtWar(holderCountry, controller));
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

    /// <summary>Whether the province borders the sea (a neighbour with no controller).</summary>
    private static bool Coastal(nint p) =>
        Game.Neighbours(p).Any(id => Game.Province(id) is var q && q != 0 && *(int*)(q + ControllerOfProvince) == 0);
}
