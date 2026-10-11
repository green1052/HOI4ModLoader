using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Hold the line: pinned defenders, holes refilled, reserves, new divisions; attacks wait on a thinly manned line ----
    //      (rotation and escape: Micro.cs; beach defence: Beaches.cs)

    private const int KindOfUnit = 8, GroupOfUnit = 0xC0, CombatsOfUnit = 0x1B4, UnitsOfCountry = 0x290;
    private const int LocationOfUnit = 0x1F0, OwnerOfUnit = 0x1E0, Path = 0x200, Retreating = 0x24C, Busy = 0x2F8;
    private const int ArrivalOfUnit = 0x160, FreshHours = 168, FreshWalkHours = 168;
    private const int LineFreshHours = 3, MaxBackfills = 2, BackfillRadius = 10, MinBackfillOrg = 60; // TrackFronts refreshes the line hourly
    private const long SentMs = 120_000, GapMs = 600_000; // a helper walks without new orders; a hole isn't refilled at once
    private const int KeyBackfillRadius = 20, KeyHole = 60;
    private const long KeyGapMs = 120_000;
    private const int ReserveMax = 6, ReservePerLine = 4, ReserveMoves = 2, ReserveRadius = 6;

    [Function(CallingConventions.Microsoft)] public delegate byte CollectFn(nint context, nint list);
    private IHook<CollectFn>? _collect;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<nint> Provinces)> _line = new(); // AI front -> its threatened provinces, refreshed hourly
    private static readonly Dictionary<nint, long> _sent = []; // unit -> until: a division we sent is left alone while it walks (main thread)
    private static readonly Dictionary<int, long> _gap = [];   // province id -> until: a hole or target a division is walking to (main thread)
    // Reserves and grad garrisons, with their tag: the theatre shuffle and army balancing leave them. Never dereferenced unless
    // still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> _posted = new();
    // New divisions we sent where they were needed, with their tag and since when: the theatre shuffle and army balancing leave
    // them while they walk (FreshlyArrived keeps them a week once there). Never dereferenced.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> _fresh = new();
    private static readonly Dictionary<int, HashSet<nint>> Known = []; // tag -> its land divisions at its last backfill pass (main thread)
    private static delegate* unmanaged<nint, nint, nint, byte, byte, nint> _unassign;
    private PerGame _game = null!;
    private static int _world = -1; // Game.Generation at the last reset: until it runs after a load, _line points into the old world
    private int _backfilled, _reserved, _deployed;

    /// <summary>
    /// A division that took up its post only days ago: the theatre shuffle would march it somewhere else and back
    /// (ping-pong). While it walks, its arrival field is a schedule, not a stamp; a stale schedule reads as long ago.
    /// </summary>
    private static bool FreshlyArrived(nint unit)
    {
        if (unit == 0 || *(int*)(unit + Path + 0xC) != 0) return false;
        var age = Hour - *(int*)(unit + ArrivalOfUnit);
        if (age is < 0 or >= FreshHours) return false;
        Interlocked.Increment(ref _stationed);
        return true;
    }

    /// <summary>The AI's regrouping can't take this division: a reserve, a grad garrison, resting, a new division on its way,
    /// a paratrooper of ours, the last one of its country in a province enemy divisions can walk into, or one on the wall of
    /// a pocket (an enemy province with divisions whose neighbours are all ours). AI threads: read only.</summary>
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

    /// <summary>The country's tracked threatened line (refreshed within the last hours) that it still holds, or null. AI threads too.</summary>
    private static HashSet<nint>? LineOf(int tag, int hour)
    {
        if (Game.Generation != _world) return null; // a load the reset hasn't caught up with (AI threads): no line, not a stale one
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
    /// Every 20 seconds, per AI country: new divisions go where they are needed, free divisions walk back into the empty provinces
    /// of the threatened line (or the grad), the ones worth holding first, and a country with divisions to spare keeps reserve
    /// posts a province behind it.
    /// </summary>
    private void Backfill()
    {
        if (!Game.HasStarted || !SheepMod.Active) return; // menus, lobby, loading: no AI commands (and no mods mounted yet)
        _game.Check(); // a save loaded since the last reset: its caches point into the old world
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
            foreach (var (province, key) in Holes(country, tag, grad, line).Select(p => (p, Key(country, tag, p))).OrderByDescending(h => h.Item2))
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

    /// <summary>The line's provinces short of defenders (one each, or what the grad wants) that nobody is walking to yet.</summary>
    private static IEnumerable<nint> Holes(nint country, int tag, Grad? grad, HashSet<nint> line) =>
        line.Where(p => !_gap.ContainsKey(Game.Id(p)) && Standing(country, tag, p) < (grad?.Want.GetValueOrDefault(Game.Id(p), 1) ?? 1));

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
        foreach (var hole in Holes(country, tag, grad, line).OrderByDescending(p => Key(country, tag, p)))
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

    /// <summary>A new division off its orders and to the province, pinned while it walks there (<see cref="Pinned"/>). Main thread.</summary>
    private static bool Dispatch(nint unit, int province, int tag, int hour, long now)
    {
        if (!Relocate(unit, province, now, GapMs)) return false;
        _fresh[unit] = (tag, hour);
        return true;
    }

    /// <summary>Drops the bookings whose time is up: a sent division is free again, a hole may be refilled. Main thread.</summary>
    private static void Expire(long now)
    {
        foreach (var (unit, until) in _sent.ToArray()) if (now >= until) _sent.Remove(unit);
        foreach (var (province, until) in _gap.ToArray()) if (now >= until) _gap.Remove(province);
    }

    /// <summary>
    /// Moves a division to the province and books it: the division is left alone while it walks, the province isn't sent another
    /// meanwhile. A reserve or garrison sent elsewhere stops being one (the callers that post it re-post it). Main thread.
    /// </summary>
    private static bool Send(nint unit, int province, long now, long gapMs)
    {
        if (!Commands.Move(unit, province, true)) return false; // posted the way the AI posts its own moves
        _sent[unit] = now + SentMs;
        _gap[province] = now + gapMs;
        _posted.TryRemove(unit, out _);
        return true;
    }

    /// <summary><see cref="Send"/>, off its orders first: on a front, the AI would walk it straight back. Main thread.</summary>
    private static bool Relocate(nint unit, int province, long now, long gapMs)
    {
        var group = *(nint*)(unit + GroupOfUnit);
        if (group != 0) Unassign(unit, group);
        return Send(unit, province, now, gapMs);
    }

    /// <summary>
    /// A country with divisions to spare posts reserves a province behind its line (never on a grad's given-up side): the posts
    /// covering the most line provinces (key ground first) each get a division, off its orders, which stays until the front moves.
    /// </summary>
    private void Reserve(nint country, int tag, HashSet<nint> line, long now)
    {
        if (line.Count < ReservePerLine) return; // a short line is its own reserve
        var posts = new Dictionary<nint, int>();
        foreach (var p in line)
        foreach (var id in Game.Neighbours(p))
        {
            var q = Game.Province(id);
            if (q == 0 || posts.ContainsKey(q) || line.Contains(q) || *(int*)(q + ControllerOfProvince) != tag || GivenUp(tag, id)) continue;
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
            var helper = Helper(tag, post, line, ReserveRadius, reserves: false); // not another post's: that only moves the gap
            if (helper == 0 || !Relocate(helper, id, now, GapMs)) continue;
            _posted[helper] = tag;
            sent++;
            Interlocked.Increment(ref _reserved);
        }
    }

    /// <summary>The tag's line or grad is gone: its reserves and garrisons stop being ones (the AI's regrouping takes them again).</summary>
    private static void Unpin(int tag)
    {
        foreach (var (unit, owner) in _posted) if (owner == tag) _posted.TryRemove(unit, out _);
    }

    /// <summary>Drops the tag's posted divisions that are no longer its divisions (died, merged): their memory may be another unit's now.</summary>
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
        if (port != 0 && *(short*)(port + LevelOfBuilding) > 0) return 1000 + *(short*)(port + LevelOfBuilding);
        var key = 3 * Math.Min(*(int*)(province + VictoryPoints), 30);
        var terrain = Terrain(province);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || Game.AdjacencyType(province, id) is not (River or LargeRiver) || !Enemy(country, tag, p)) continue; // impassable: no attack at all
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }

    /// <summary>
    /// The nearest free, rested division of the tag within radius provinces of ours, not standing on the line itself, or 0;
    /// a reserve or garrison of ours only with <paramref name="reserves"/>.
    /// </summary>
    private static nint Helper(int tag, nint target, HashSet<nint> line, int radius, bool reserves = true)
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
                        if (Spare(unit, tag) && (reserves || !_posted.ContainsKey(unit))) return unit;
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

    /// <summary>Off every order, staying in its army: COrderUnassignCommand, as the battle plan UI sends it.</summary>
    private static void Unassign(nint unit, nint group)
    {
        var cmd = Allocator.AllocZeroed(0x50);
        _unassign(cmd, unit, group, 0, 1); // stays in the army; off its army group's fronts too
        Commands.TryPost(cmd, true);
    }

    // ---- The PostAICommand gate: an "attack on" is dropped while the country's threatened line is thinly manned (or it holds
    //      a grad), a landing while its beach is crowded; the AI asks again every hour. Also our paradrops' guard ----

    private const int GroupOfCommand = 0x28, OrderOfCommand = 0x30, ExecuteOn = 0x34;   // COrderExecuteCommand (ExecuteOn: the order command argument, Paradrops' ArgOfOrderCommand)
    private const int TypeOfOrder = 0x30, Executing = 0x248, Invasion = 3, Paradrop = 4; // COrder

    [Function(CallingConventions.Microsoft)] public delegate byte PostFn(nint cmd, nint tag);

    private IHook<PostFn>? _post;
    private static delegate* unmanaged<nint, int, nint> _findOrder;
    private static nint _executeVTable;
    private static int _held;

    /// <summary>
    /// The one PostAICommand hook (every AI command): AI order commands that would undo a paradrop of ours (StrategicParadrops),
    /// then the launch gate: invasions by their beach (StrategicInvasions), other attacks by the grad and the line (HoldTheLine).
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
            Commands.Free(cmd); // deleted, as the game does with an invalid command
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
        if (Grads.ContainsKey(*(int*)tag)) return false; // on its grad (DefensivePostures): no offensives until strong enough again
        return !_holdLine || Manned(*(int*)tag, LineOf(*(int*)tag, Hour)); // a thin line doesn't advance: attacking widens it, the holes get the spearhead cut off
    }
}
