using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Hold the line: pinned defenders, holes refilled, reserves and second-line posts, new divisions; attacks wait on a thinly
    //      manned line (rotation, escape and the fire brigade: Micro.cs; beach defence: Beaches.cs) ----

    private const int ArrivalOfUnit = 0x160, FreshHours = 168, FreshWalkHours = 168;
    // TrackFronts (CAIGeneral::UpdateFrontUnitRequests) refreshes a line once a game day, plus a few extra hours: two days keep it between updates.
    private const int LineFreshHours = 48, MaxBackfills = 2, BackfillRadius = 8, MinBackfillOrg = 60;
    private const int KeyBackfillRadius = 20, KeyHole = 60;
    // Game hours: a division we sent walks without new orders; a hole or target isn't sent another meanwhile.
    private const long SentHours = 48, GapHours = 168, KeyGapHours = 72;
    private const int BackfillHours = 6; // the pass runs at most this often, in game hours (the game runs up to ~16 hours a real second)
    private const int ReserveMax = 6, ReservePerLine = 4, ReserveMoves = 2, ReserveRadius = 6;
    private const int DepthKey = 25, MaxDepthPosts = 2, DepthRadius = 4;
    private const int MemberOfUnit = 0xB8; // CArmy: its COrdersGroupMember (what COrder members point at)

    [Function(CallingConventions.Microsoft)] public delegate byte CollectFn(nint context, nint list);
    private IHook<CollectFn>? _collect;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<nint> Provinces)> _line = new(); // AI front -> its threatened provinces, refreshed daily
    private static readonly Dictionary<nint, long> _sent = []; // unit -> game hour until: a division we sent is left alone while it walks (main thread)
    private static readonly Dictionary<int, long> _gap = [];   // province id -> game hour until: a hole or target a division is walking to (main thread)

    private enum PostKind { Reserve, Garrison, Helper, Depth, Drive }

    /// <summary>A pinned division's job: its tag, the province it holds, the line province it backs (second line).</summary>
    private readonly record struct Posting(int Tag, PostKind Kind, int Province, int Guard = 0);

    // Reserves, grad garrisons, line helpers and second-line divisions: the theatre shuffle and army balancing leave them. AI threads
    // only ask whether a unit is in it; never dereferenced unless still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Posting> _posted = new();
    // Divisions we took off their orders, with the order they were on: put back on it when we let them go, else vanilla's regrouping
    // sends them to some far front and back (main thread; never dereferenced unless still one of the tag's units).
    private static readonly Dictionary<nint, (int Tag, long Group, int Order)> _home = [];
    // New divisions we sent where they were needed, with their tag and since when: the theatre shuffle and army balancing leave
    // them while they walk (FreshlyArrived keeps them a week once there). Never dereferenced.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> _fresh = new();
    // Tags that outgun the enemy on their line two to one, with the hour seen: their attacks go ahead however thin the line (AI threads read).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _dominant = new();
    private static readonly Dictionary<int, HashSet<nint>> Known = []; // tag -> its land divisions at its last backfill pass (main thread)
    private PerGame _game = null!;
    private static int _world = -1; // Game.Generation at the last reset: until it runs after a load, _line points into the old world
    private int _backfillAt = Never;
    private static int _backfilled, _reserved, _deployed, _returned, _stopped, _depth, _kept;

    /// <summary>
    /// A division that took up its post only days ago: the theatre shuffle would march it somewhere else and back
    /// (ping-pong). While it walks, its arrival field is a schedule, not a stamp; a stale schedule reads as long ago.
    /// </summary>
    private static bool FreshlyArrived(nint unit)
    {
        if (unit == 0 || Division.Walking(unit)) return false;
        var age = Game.Hour - *(int*)(unit + ArrivalOfUnit);
        if (age is < 0 or >= FreshHours) return false;
        Interlocked.Increment(ref _stationed);
        return true;
    }

    /// <summary>The AI's regrouping can't take this division: a reserve, a grad garrison, resting, a new division on its way,
    /// a paratrooper of ours, the last one of its country in a province on the line (next to enemy ground, troops there or not:
    /// vanilla strips quiet stretches of a front bare) or one of no more than the enemy divisions next to it, or one on the wall of a pocket (an enemy province with divisions whose
    /// neighbours are all ours). AI threads: read only.</summary>
    private static bool Pinned(nint unit)
    {
        if (unit == 0) return false;
        var tag = Division.Commander(unit);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        if (_posted.ContainsKey(unit) || _resting.ContainsKey(unit) || Paratrooper(unit) || Jobs.Taken(unit)
            || _fresh.TryGetValue(unit, out var fresh) && Game.Hour - fresh.Hour is >= 0 and < FreshWalkHours) return true;
        var country = Game.Country(tag);
        var location = Division.Location(unit);
        if (country == 0 || location == 0) return false;
        var ours = 0;
        foreach (var other in Province.Units(location))
            if (Division.Commander(other) == tag) ours++;
        var facing = 0;
        foreach (var id in Game.Neighbours(location))
        {
            var p = Game.Province(id);
            if (p == 0 || !Province.Enemy(country, tag, p)) continue;
            if (ours <= 1 || Province.Divisions(p) > 0 && Province.Surrounded(country, tag, p))
                return true; // nothing else holds the province, or a cut-off pocket sits against it
            facing += Province.Divisions(p);
        }
        return ours <= facing; // a line province keeps as many as the enemy divisions next to it
    }

    /// <summary>The country's tracked threatened line (refreshed within the last two days) that it still holds, or null. AI threads too.</summary>
    private static HashSet<nint>? LineOf(int tag, int hour)
    {
        if (Game.Generation != _world) return null; // a load the reset hasn't caught up with (AI threads): no line, not a stale one
        HashSet<nint>? line = null;
        foreach (var e in _line)
            if (e.Value.Tag == tag && hour - e.Value.Hour is >= 0 and < LineFreshHours)
                foreach (var province in e.Value.Provinces)
                    if (Province.Controller(province) == tag) (line ??= []).Add(province);
        return line;
    }

    /// <summary>
    /// The country may start attacks: its divisions standing on the threatened line outnumber the line's provinces by a
    /// margin, or no line is tracked. Attacking on a barely manned line widens it, the holes open, and the spearhead gets
    /// cut off.
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
        foreach (var unit in Province.Units(province))
        {
            var commander = Division.Commander(unit);
            if (commander == tag || Game.Allied(country, commander)) divisions++;
        }
        return divisions;
    }

    /// <summary>
    /// Our side's power (the tag's and its allies' divisions) within two provinces of <paramref name="from"/> is at least twice the
    /// enemy's there: no resting, escaping or holding back of attacks on that ground.
    /// </summary>
    private static bool Dominant(nint country, int tag, IEnumerable<nint> from)
    {
        long ours = 0, theirs = 0;
        foreach (var p in Within(from, 2, q => Province.Controller(q) != 0))
        foreach (var unit in Province.Units(p))
        {
            if (unit == 0 || !Division.IsLand(unit)) continue;
            var commander = Division.Commander(unit);
            if (commander == tag || Game.Allied(country, commander)) ours += Power(unit);
            else if (Game.AtWar(country, commander)) theirs += Power(unit);
        }
        return ours > 0 && ours >= 2 * theirs;
    }

    /// <summary>Provinces within <paramref name="radius"/> steps of <paramref name="starts"/> (included), nearest first, walking only into ones <paramref name="through"/> accepts.</summary>
    private static List<nint> Within(IEnumerable<nint> starts, int radius, Func<nint, bool> through)
    {
        var all = new List<nint>();
        var seen = new HashSet<int>();
        foreach (var s in starts)
            if (s != 0 && seen.Add(Game.Id(s))) all.Add(s);
        for (int depth = 1, from = 0; depth <= radius; depth++)
        {
            var to = all.Count;
            for (var i = from; i < to; i++)
            foreach (var id in Game.Neighbours(all[i]))
                if (seen.Add(id) && Game.Province(id) is var p && p != 0 && through(p)) all.Add(p);
            if (to == all.Count) break;
            from = to;
        }
        return all;
    }

    /// <summary>At most every <paramref name="hours"/> game hours (never while paused): true, and the time stamped.</summary>
    private static bool Due(ref int last, int hours)
    {
        var hour = Game.Hour;
        if ((uint)(hour - last) < (uint)hours) return false; // a load to an earlier date runs at once
        last = hour;
        return true;
    }

    private const int Never = -(1 << 30);

    /// <summary>
    /// Every six game hours, per AI country: posts no longer behind the line are let go, new divisions go where they are needed,
    /// free divisions walk back into the empty provinces of the threatened line (or the grad), the ones worth holding first,
    /// running offensives on a thin line stop, and a country with divisions to spare keeps reserves and second-line posts.
    /// </summary>
    private void Backfill()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _backfillAt, BackfillHours)) return; // menus, lobby, loading: no AI commands
        _game.Check(); // a save loaded since the last reset: its caches point into the old world
        var hour = Game.Hour;
        Expire(hour);
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player) continue;
            PrunePosted(country, tag);
            DropAreaDefence(country, tag);
            var fresh = NewDivisions(country, tag); // every pass: one deployed in peace isn't new once war comes
            var grad = _postures ? Posture(country, tag, hour) : null; // a much stronger enemy: its grad is the line
            var line = grad != null ? Held(grad, tag) : LineOf(tag, hour);
            if (line == null || line.Count == 0)
            {
                _dominant.TryRemove(tag, out _);
                continue; // no line seen lately: its posts stay while they still back one (PrunePosted)
            }
            var dominant = grad == null && Dominant(country, tag, line);
            if (dominant) _dominant[tag] = hour;
            else _dominant.TryRemove(tag, out _);
            if (fresh.Count > 0) Deploy(country, tag, fresh, grad, line, hour);
            var sent = 0;
            var fronts = new Dictionary<nint, HashSet<nint>>(); // AI front -> the armies standing on it
            foreach (var (province, key) in Holes(country, tag, grad, line).Select(p => (p, Key(country, tag, p))).OrderByDescending(h => h.Item2))
            {
                if (sent == (grad != null ? MaxGradMoves : MaxBackfills)) break;
                var important = grad != null || key >= KeyHole; // a port, a city on a river, the grad: worth walking further for
                var id = Game.Id(province);
                var armies = grad == null ? Armies(tag, province, hour, fronts) : null;
                var helper = Helper(tag, province, line, important ? KeyBackfillRadius : BackfillRadius, prefer: armies);
                if (helper == 0) continue; // nothing free nearby: the attack gate keeps the front still instead
                var done = grad != null ? Garrison(helper, id, tag, hour)
                    : armies != null && armies.Contains(Division.Group(helper)) ? Send(helper, id, hour, important ? KeyGapHours : GapHours) // its own front: it stays
                    : Station(helper, id, tag, hour, important ? KeyGapHours : GapHours, PostKind.Helper); // another front's would walk it straight back
                if (!done) continue;
                sent++;
                Interlocked.Increment(ref _backfilled);
            }
            var manned = Manned(tag, line);
            if (grad == null && !manned && !dominant) HoldOffensives(country, tag, hour);
            if (!manned) continue;
            if (MicroElsewhere) continue;
            Reserve(country, tag, line, hour);
            if (grad == null) Depth(country, tag, line, hour);
        }
        ShareJobs();
    }

    private const int AreaDefenceOrder = 5; // COrder type of an area defence (port and home garrison) battle plan

    /// <summary>
    /// Against enemies that hold no port (<see cref="CantBeInvaded"/>), the AI's area defence orders left over are deleted
    /// (UpdateAreaDefense makes no new ones then): their armies walk divisions to ports far away (Italy's East African ones to
    /// Kismayo, Libya, the Dodecanese), and divisions on an order are never collected for other fronts. Main thread.
    /// </summary>
    private static void DropAreaDefence(nint country, int tag)
    {
        if (!CantBeInvaded(country, tag)) return;
        foreach (var group in OrdersGroup.OfCountry(country))
        foreach (var order in Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray())
        {
            if (order == 0 || Order.Type(order) != AreaDefenceOrder) continue;
            var divisions = Order.Members(order).Length;
            if (!PostOrderCommand(Signatures.DeleteOrderCommand, new OrdersGroup(group).Ref, Order.Id(order), 0)) continue;
            _log?.Info($"#{tag}: deleted an area defence order of {divisions} division(s): its enemies hold no port");
        }
    }

    /// <summary>The line's provinces short of defenders (one each, or what the grad wants) that nobody is walking to yet.</summary>
    private static IEnumerable<nint> Holes(nint country, int tag, Grad? grad, HashSet<nint> line) =>
        line.Where(p => !_gap.ContainsKey(Game.Id(p)) && Standing(country, tag, p) < (grad?.Want.GetValueOrDefault(Game.Id(p), 1) ?? 1));

    /// <summary>
    /// The armies (COrdersGroup) of the tag's divisions standing on the AI fronts this province is on, cached per front; null if it
    /// is on none. Read through the provinces, never the front: a tracked front may have been freed since.
    /// </summary>
    private static HashSet<nint>? Armies(int tag, nint province, int hour, Dictionary<nint, HashSet<nint>> cache)
    {
        HashSet<nint>? armies = null;
        foreach (var (front, e) in _line)
        {
            if (e.Tag != tag || hour - e.Hour is < 0 or >= LineFreshHours || !e.Provinces.Contains(province)) continue;
            if (!cache.TryGetValue(front, out var groups)) cache[front] = groups = ArmiesIn(tag, e.Provinces);
            (armies ??= []).UnionWith(groups);
        }
        return armies;
    }

    /// <summary>The armies of the tag's divisions standing in the provinces.</summary>
    private static HashSet<nint> ArmiesIn(int tag, IEnumerable<nint> provinces)
    {
        var groups = new HashSet<nint>();
        foreach (var p in provinces)
        foreach (var unit in Province.Units(p))
            if (unit != 0 && Division.Commander(unit) == tag && Division.Group(unit) is var g and not 0) groups.Add(g);
        return groups;
    }

    /// <summary>
    /// Fronts of the tag whose threatened provinces aren't manned (as <see cref="Manned"/>): the armies standing on them call off their
    /// running front offensives (the attack gate then holds new ones until manned). The AI's own execute command, posted forced.
    /// </summary>
    private static void HoldOffensives(nint country, int tag, int hour)
    {
        var done = new HashSet<nint>();
        foreach (var e in _line.Values)
        {
            if (e.Tag != tag || hour - e.Hour is < 0 or >= LineFreshHours) continue;
            var held = e.Provinces.Where(p => Province.Controller(p) == tag).ToList();
            if (held.Count == 0 || held.Count * 6 <= held.Sum(p => Standing(country, tag, p)) * 5) continue; // manned
            foreach (var group in ArmiesIn(tag, held))
            {
                if (!done.Add(group)) continue;
                var reference = new OrdersGroup(group).Ref;
                foreach (var order in Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray())
                    if (order != 0 && Order.Type(order) == Order.Front && Order.Executing(order)
                        && Commands.TryPost(Commands.NewOrderCommand(Commands.ExecuteOrder, reference, Order.Id(order), 0), true))
                        Interlocked.Increment(ref _stopped);
            }
        }
    }

    /// <summary>The tag's land divisions that weren't there at its last pass (just deployed, or come from elsewhere); none on its first pass in a game.</summary>
    // ponytail: by pointer; a new division in a dead one's memory is missed (vanilla places it), read a creation date if that shows.
    private static List<nint> NewDivisions(nint country, int tag)
    {
        var units = new HashSet<nint>();
        foreach (var unit in Game.Units(country))
            if (unit != 0 && Division.IsLand(unit)) units.Add(unit);
        var fresh = Known.TryGetValue(tag, out var old) ? units.Where(u => !old.Contains(u)).ToList() : [];
        Known[tag] = units;
        foreach (var (unit, since) in _fresh)
            if (since.Tag == tag && Game.Hour - since.Hour is < 0 or >= FreshWalkHours) _fresh.TryRemove(unit, out _);
        return fresh;
    }

    /// <summary>
    /// New divisions go where the country needs them most, not where vanilla's theatre share-out points: its grad's holes (on one), else
    /// the empty provinces of its threatened line (key ground first), then the least held provinces of its most thinly held front until
    /// that front is manned. Off their orders and pinned while they walk; whatever is left over is vanilla's.
    /// </summary>
    // ponytail: first come, first sent, no distance; new divisions mostly stand together at the deployment province.
    private void Deploy(nint country, int tag, List<nint> fresh, Grad? grad, HashSet<nint> line, int hour)
    {
        var free = new Queue<nint>(fresh.Where(u => Movable(u, tag)));
        foreach (var hole in Holes(country, tag, grad, line).OrderByDescending(p => Key(country, tag, p)))
        {
            if (free.Count == 0) return;
            var unit = free.Dequeue();
            if (grad != null ? Garrison(unit, Game.Id(hole), tag, hour) : Dispatch(unit, Game.Id(hole), tag, hour)) Interlocked.Increment(ref _deployed);
        }
        if (grad != null || free.Count == 0) return; // on a grad, the old line's fronts are given up
        Dictionary<nint, int>? thinnest = null;
        foreach (var e in _line)
        {
            if (e.Value.Tag != tag || hour - e.Value.Hour is < 0 or >= LineFreshHours) continue;
            var held = e.Value.Provinces.Where(p => Province.Controller(p) == tag).ToDictionary(p => p, p => Standing(country, tag, p));
            if (held.Count > 0 && (thinnest == null || held.Values.Sum() * thinnest.Count < thinnest.Values.Sum() * held.Count)) thinnest = held;
        }
        if (thinnest == null) return;
        var divisions = thinnest.Values.Sum();
        while (free.Count > 0 && thinnest.Count * 6 > divisions * 5) // ~1.2 divisions per threatened province, as Manned
        {
            var target = thinnest.MinBy(p => p.Value).Key;
            if (!Dispatch(free.Dequeue(), Game.Id(target), tag, hour)) continue;
            thinnest[target]++;
            divisions++;
            Interlocked.Increment(ref _deployed);
        }
    }

    /// <summary>A new division off its orders and to the province, pinned while it walks there (<see cref="Pinned"/>). Main thread.</summary>
    private static bool Dispatch(nint unit, int province, int tag, int hour)
    {
        if (!Relocate(unit, province, hour, GapHours, remember: false)) return false; // new: its deployment front isn't home
        _fresh[unit] = (tag, hour);
        return true;
    }

    /// <summary>Drops the bookings whose time is up (game hours): a sent division is free again, a hole may be refilled. Main thread.</summary>
    private static void Expire(long hour)
    {
        foreach (var (unit, until) in _sent.ToArray()) if (hour >= until) _sent.Remove(unit);
        foreach (var (province, until) in _gap.ToArray()) if (hour >= until) _gap.Remove(province);
        foreach (var (unit, at) in _combat.ToArray()) if (hour - at is < 0 or >= CombatHours) _combat.Remove(unit);
    }

    /// <summary>
    /// Moves a division to the province and books it (game hours): the division is left alone while it walks, the province isn't
    /// sent another meanwhile. A post sent elsewhere stops being one unless <paramref name="keep"/> (the callers that post it
    /// re-post it). Main thread.
    /// </summary>
    private static bool Send(nint unit, int province, long hour, long gapHours, bool keep = false, bool attack = false)
    {
        var from = Division.Location(unit);
        if (!Commands.Move(unit, province, true)) return false; // posted the way the AI posts its own moves
        if (from != 0) MapArrows.Flash(Game.Id(from), province, attack ? (byte)255 : (byte)60, attack ? (byte)40 : (byte)220, attack ? (byte)40 : (byte)60); // [Debug] AiActionArrows
        _sent[unit] = hour + SentHours;
        _gap[province] = hour + gapHours;
        if (!keep) _posted.TryRemove(unit, out _);
        return true;
    }

    /// <summary><see cref="Send"/>, off its orders first: on a front, the AI would walk it straight back. Main thread.</summary>
    private static bool Relocate(nint unit, int province, long hour, long gapHours, bool remember = true)
    {
        TakeOff(unit, remember);
        return Send(unit, province, hour, gapHours);
    }

    /// <summary><see cref="Relocate"/>, then pinned there as a post of <paramref name="kind"/>. Main thread.</summary>
    private static bool Station(nint unit, int province, int tag, long hour, long gapHours, PostKind kind, int guard = 0)
    {
        if (!Relocate(unit, province, hour, gapHours)) return false;
        _posted[unit] = new Posting(tag, kind, province, guard);
        return true;
    }

    /// <summary>Off every order of its army, the order it was on remembered (unless already) to put it back (<see cref="Return"/>). Main thread.</summary>
    private static void TakeOff(nint unit, bool remember = true)
    {
        var group = Division.Group(unit);
        if (group == 0) return;
        if (remember && !_home.ContainsKey(unit) && OrderOf(unit, group) is var order and not 0)
            _home[unit] = (Division.Commander(unit), new OrdersGroup(group).Ref, order);
        Commands.Unassign(unit, group, true);
    }

    /// <summary>Id of the order (not an invasion or paradrop) of the army or its army group the division is on, 0 if none.</summary>
    private static int OrderOf(nint unit, nint group)
    {
        var member = unit + MemberOfUnit;
        for (var g = group; g != 0; g = *(nint*)(g + OrdersGroup.ParentOffset))
        {
            foreach (var order in Native.PdxArray(g + OrdersGroup.RootOrdersOffset))
                if (order != 0 && Order.Type(order) is not (Order.Invasion or Order.Paradrop) && Order.Members(order).Contains(member)) return Order.Id(order);
            if (g != group) break; // the army, then its army group
        }
        return 0;
    }

    /// <summary>
    /// A division we let go goes back on the order we took it off (the game's own assign command), if it is still in that army
    /// and the order still exists; otherwise vanilla's regrouping has it. Callers pass a live division of the tag. Main thread.
    /// </summary>
    private static void Return(nint unit, int tag)
    {
        if (!_home.Remove(unit, out var home) || home.Tag != tag) return;
        var group = GroupOf(home.Group, tag);
        if (group == 0 || Division.Group(unit) != group || _findOrder(group, home.Order) == 0) return;
        if (Commands.Assign(unit, group, home.Order, true)) Interlocked.Increment(ref _returned);
    }

    /// <summary>
    /// A country with divisions to spare posts reserves a province behind its line (never on a grad's given-up side): the posts
    /// covering the most line provinces (key ground first) each get a division, off its orders, which stays until the front moves.
    /// </summary>
    private void Reserve(nint country, int tag, HashSet<nint> line, int hour)
    {
        if (line.Count < ReservePerLine) return; // a short line is its own reserve
        var posts = new Dictionary<nint, int>();
        foreach (var p in line)
        foreach (var id in Game.Neighbours(p))
        {
            var q = Game.Province(id);
            if (q == 0 || posts.ContainsKey(q) || line.Contains(q) || Province.Controller(q) != tag || GivenUp(tag, id)) continue;
            if (Province.NextToEnemy(country, tag, q)) continue; // on the front itself
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
            if (helper == 0 || !Station(helper, id, tag, hour, GapHours, PostKind.Reserve)) continue;
            sent++;
            Interlocked.Increment(ref _reserved);
        }
    }

    /// <summary>
    /// Depth defence: line provinces worth holding (Key 25+: rivers, cities, mountains, victory points, ports) get a second division
    /// on a safe province right behind them, up to as many as the country keeps reserves, most important first.
    /// </summary>
    private void Depth(nint country, int tag, HashSet<nint> line, int hour)
    {
        var backed = _posted.Values.Where(p => p.Tag == tag && p.Kind == PostKind.Depth).Select(p => p.Guard).ToHashSet();
        if (backed.Count >= Math.Min(ReserveMax, line.Count / ReservePerLine)) return;
        var posted = 0;
        foreach (var (province, _) in line.Where(p => !backed.Contains(Game.Id(p))).Select(p => (p, Key(country, tag, p)))
                     .Where(k => k.Item2 >= DepthKey).OrderByDescending(k => k.Item2).ToList())
        {
            if (posted == MaxDepthPosts) return;
            var post = 0;
            foreach (var id in Game.Neighbours(province))
                if (Game.Province(id) is var q && q != 0 && !line.Contains(q) && Province.Controller(q) == tag && !GivenUp(tag, id) && !_gap.ContainsKey(id)
                    && !Province.NextToEnemy(country, tag, q) && Standing(country, tag, q) == 0)
                {
                    post = id;
                    break;
                }
            if (post == 0) continue;
            var helper = Helper(tag, Game.Province(post), line, DepthRadius, reserves: false);
            if (helper == 0 || !Station(helper, post, tag, hour, GapHours, PostKind.Depth, Game.Id(province))) continue;
            posted++;
            Interlocked.Increment(ref _depth);
        }
    }

    /// <summary>Whether the post still does its job: a reserve or second line stays behind the line, a helper on it; a garrison while the grad stands.</summary>
    private static bool PostHolds(nint country, int tag, Posting post)
    {
        if (post.Kind == PostKind.Garrison) return true; // the grad's: let go when it is (Unpin)
        if (post.Kind == PostKind.Drive) return Game.Hour < post.Guard; // a drive through a gap (Guard: until when), then back on its order
        var p = Game.Province(post.Province);
        if (p == 0 || Province.Controller(p) != tag) return false;
        var front = Province.NextToEnemy(country, tag, p);
        if (post.Kind == PostKind.Helper) return front;
        if (front || !Game.Neighbours(p).Any(id => Game.Province(id) is var q && q != 0 && Province.NextToEnemy(country, tag, q))) return false; // the line moved onto it, or away (or peace)
        if (post.Kind != PostKind.Depth) return true;
        var guard = Game.Province(post.Guard);
        return guard != 0 && Province.Controller(guard) == tag && Province.NextToEnemy(country, tag, guard);
    }

    /// <summary>The tag's grad is gone: its posts stop being ones and go back on their orders.</summary>
    private static void Unpin(nint country, int tag)
    {
        HashSet<nint>? units = null;
        foreach (var (unit, post) in _posted)
        {
            if (post.Tag != tag || !_posted.TryRemove(unit, out _)) continue;
            if ((units ??= [.. Game.Units(country)]).Contains(unit)) Return(unit, tag);
        }
    }

    /// <summary>
    /// The tag's posts that are no longer its divisions (died, merged: their memory may be another unit's now) are dropped; ones
    /// whose post no longer backs the line (<see cref="PostHolds"/>) go back on their orders.
    /// </summary>
    private static void PrunePosted(nint country, int tag)
    {
        HashSet<nint>? units = null;
        foreach (var (unit, post) in _posted)
        {
            if (post.Tag != tag) continue;
            units ??= [.. Game.Units(country)];
            if (!units.Contains(unit)) _posted.TryRemove(unit, out _);
            else if (!PostHolds(country, tag, post) && _posted.TryRemove(unit, out _)) Return(unit, tag);
        }
        foreach (var (unit, home) in _home.ToArray())
            if (home.Tag == tag && !(units ??= [.. Game.Units(country)]).Contains(unit)) _home.Remove(unit);
    }

    /// <summary>What holding this province of ours is worth: a port (the fleet and supply), victory points, a river in front of it, city or mountain ground.</summary>
    private static int Key(nint country, int tag, nint province)
    {
        var port = Province.BuildingLevel(province, Buildings.NavalBaseSlot);
        if (port > 0) return 1000 + port;
        var key = 3 * Math.Min(Province.VictoryPoints(province), 30);
        if ((Province.TerrainCategory(province) & (Province.Mountain | Province.Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || Game.AdjacencyType(province, id) is not (Province.River or Province.LargeRiver) || !Province.Enemy(country, tag, p)) continue; // impassable: no attack at all
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }

    /// <summary>
    /// The nearest free, rested division of the tag within radius provinces of ours, not standing on the line itself, or 0; one
    /// of <paramref name="prefer"/>'s armies (the same front's) first, wherever it is in range; a post of ours only with <paramref name="reserves"/>.
    /// </summary>
    private static nint Helper(int tag, nint target, HashSet<nint> line, int radius, bool reserves = true, HashSet<nint>? prefer = null)
    {
        nint fallback = 0;
        foreach (var p in Within([target], radius, q => Province.Controller(q) == tag))
        {
            if (p == target || line.Contains(p) && !Crowded(tag, p)) continue;
            foreach (var unit in Province.Units(p))
            {
                if (!Spare(unit, tag) || !reserves && _posted.ContainsKey(unit)) continue;
                if (prefer == null || prefer.Contains(Division.Group(unit))) return unit;
                if (fallback == 0) fallback = unit;
            }
        }
        return fallback;
    }

    /// <summary>
    /// A line province with a division to spare for a hole: three of ours or more (two stay), or two with no enemy divisions
    /// next to it (vanilla stacks its attacks there while other stretches stand empty).
    /// </summary>
    private static bool Crowded(int tag, nint province)
    {
        var ours = 0;
        foreach (var unit in Province.Units(province))
            if (unit != 0 && Division.IsLand(unit) && Division.Commander(unit) == tag && !Division.IsHeadquarters(unit)) ours++; // an HQ holds nothing
        var country = Game.Country(tag);
        return ours >= 3 || ours == 2 && country != 0 && !Province.EnemyDivisionsNext(country, tag, province);
    }

    /// <summary>A land division of the tag (not lent) that can take orders now: not fighting, retreating or embarked, not one we sent or rest.</summary>
    private static bool Movable(nint unit, int tag)
    {
        return Division.Ready(unit, tag) && !_sent.ContainsKey(unit) && !_resting.ContainsKey(unit) && !Paratrooper(unit) && !Jobs.Taken(unit, Id);
    }

    /// <summary>Our divisions with a job (posted, resting, walking somewhere for us), for the other plugins to leave alone.</summary>
    private static void ShareJobs() => Jobs.Set(Id, _posted.Keys.Concat(_resting.Keys).Concat(_sent.Keys));

    /// <summary>Another plugin (FrontLine) runs the AI's rotation, escape, reserves, second line and local attacks: ours stand aside.</summary>
    private static bool MicroElsewhere => Jobs.AiMicro is { } owner && owner != Id;

    /// <summary>A movable division that stands idle, rested enough to go somewhere.</summary>
    private static bool Spare(nint unit, int tag) => Movable(unit, tag) && !Division.Walking(unit) && Percent(Division.Organisation(unit)) >= MinBackfillOrg;

    // ---- The PostAICommand gate: an "attack on" is dropped while the country's threatened line is thinly manned (or it holds
    //      a grad), a landing while its beach is crowded; the AI asks again every hour. Also our paradrops' guard ----

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
        _executeVTable = Sig.VTable(Commands.ExecuteOrder);
        _post = Patches.Hook<PostFn>(Sig.Find(Signatures.PostAICommand), (cmd, tag) =>
        {
            if (*(int*)tag == Game.ControlledTag || !SheepMod.Active) return _post!.OriginalFunction(cmd, tag);
            bool keep;
            try
            {
                keep = !_paradrops || KeepsParadrops(cmd);
                if (keep && _holdLine && Redeploys(cmd))
                {
                    keep = false;
                    Interlocked.Increment(ref _kept);
                }
                if (keep && *(nint*)cmd == _executeVTable && *(byte*)(cmd + Commands.ArgOfOrderCommand) != 0 && !Ready(cmd, tag))
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

    private const int UnitOfRedeployment = 0x38; // CStrategicRedeploymentCommand: the division's CRef (1.19.3, seen in game)
    private static nint _redeployVTable;

    private const int RedeployEnemyRadius = 2; // enemy divisions this close (provinces) make the line a live one

    /// <summary>
    /// A strategic redeployment (to another front: walking to a port, shipping out) of a division on the line or right behind
    /// it (a province next to enemy ground, or next to one) while enemy divisions stand within RedeployEnemyRadius: vanilla
    /// re-tasks whole armies so and empties a live front (Italy's Somali line, Ethiopia across it). Quiet fronts may still
    /// give theirs. AI threads: read only.
    /// </summary>
    private static bool Redeploys(nint cmd)
    {
        if (_redeployVTable == 0) _redeployVTable = Sig.VTable("CStrategicRedeploymentCommand");
        if (*(nint*)cmd != _redeployVTable) return false;
        var unit = Game.Resolve(*(long*)(cmd + UnitOfRedeployment));
        if (unit == 0 || !Division.IsLand(unit)) return false;
        var tag = Division.Commander(unit);
        var country = tag <= 0 || tag == Game.ControlledTag ? 0 : Game.Country(tag);
        var location = Division.Location(unit);
        if (country == 0 || location == 0) return false;
        var onLine = Province.NextToEnemy(country, tag, location)
                     || Game.Neighbours(location).Any(id => Game.Province(id) is var p && p != 0 && Province.Controller(p) == tag && Province.NextToEnemy(country, tag, p));
        if (!onLine || !EnemyDivisionsNear(country, tag, location, RedeployEnemyRadius)) return false;
        _log?.Info($"#{tag}: kept a division in province {Game.Id(location)} on a live line from a strategic redeployment");
        return true;
    }

    /// <summary>Enemy divisions stand within <paramref name="radius"/> provinces of <paramref name="from"/>.</summary>
    private static bool EnemyDivisionsNear(nint country, int tag, nint from, int radius)
    {
        var seen = new HashSet<nint> { from };
        var frontier = new List<nint> { from };
        for (var depth = 0; depth < radius && frontier.Count > 0; depth++)
        {
            frontier = frontier.SelectMany(p => Game.Neighbours(p)).Select(Game.Province).Where(p => p != 0 && seen.Add(p)).ToList();
            if (frontier.Any(p => Province.Enemy(country, tag, p) && Province.Divisions(p) > 0)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether an "attack on" may go ahead: it runs already, a paradrop, an invasion whose beach isn't crowded (StrategicInvasions),
    /// or the country holds no grad (DefensivePostures) and its threatened line is manned or outguns the enemy two to one
    /// (HoldTheLine). Read only: AI threads.
    /// </summary>
    private static bool Ready(nint cmd, nint tag)
    {
        var reference = (int*)(cmd + Commands.GroupOfOrderCommand);
        if (reference[0] == 0 && reference[1] == 0) return true;
        var group = _resolve((nint)reference);
        if (group == 0) return true;
        var id = *(int*)(cmd + Commands.OrderOfOrderCommand);
        // CAIOperation::UpdateExecution launches an invasion for the whole group: order id 0 (`xor r8d,r8d` at 0x141A4D9C2).
        var order = id != 0 ? _findOrder(group, id) : Unlaunched(group);
        if (id != 0 && (order == 0 || Order.Executing(order))) return true; // running
        if (order != 0 && Order.Type(order) is Order.Invasion or Order.Paradrop) // at sea: the home line has no say
            return Order.Type(order) != Order.Invasion || !_strategicInvasions || BeachClear(group, order, *(int*)tag);
        if (Grads.ContainsKey(*(int*)tag)) return false; // on its grad (DefensivePostures): no offensives until strong enough again
        if (!_holdLine) return true;
        var hour = Game.Hour;
        if (_dominant.TryGetValue(*(int*)tag, out var seen) && hour - seen is >= 0 and < 2 * BackfillHours) return true; // twice the enemy's power on its line
        return Manned(*(int*)tag, LineOf(*(int*)tag, hour)); // a thin line doesn't advance: attacking widens it, the holes get the spearhead cut off
    }
}
