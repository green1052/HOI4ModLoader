using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Hold the line: pinned defenders, refilled holes, reserves, second-line posts and new divisions; no attacks on a thin
    //      line. Related: Posts.cs (moving and posting), AttackGate.cs (calling off and gating offensives), Micro.cs (rest,
    //      escape, fire brigade), Beaches.cs. ----

    private const int ArrivalOfUnit = 0x160, FreshHours = 168, FreshWalkHours = 168;
    // TrackFronts (CAIGeneral::UpdateFrontUnitRequests) refreshes a line about daily; two days bridge the gap.
    private const int LineFreshHours = 48, MaxBackfills = 2, BackfillRadius = 8, MinBackfillOrg = 60;
    private const int KeyBackfillRadius = 20, KeyHole = 60;
    // Game hours a sent division is left alone, and its hole or target gets no other.
    private const long SentHours = 48, GapHours = 168, KeyGapHours = 72;
    private const int BackfillHours = 6; // game hours between passes
    private const int ReserveMax = 6, ReservePerLine = 4, ReserveMoves = 2, ReserveRadius = 6;
    private const int DepthKey = 25, MaxDepthPosts = 2, DepthRadius = 4;
    private const int MemberOfUnit = Division.MemberOffset; // CArmy -> its COrdersGroupMember (what COrder members point at)

    [Function(CallingConventions.Microsoft)] public delegate byte CollectFn(nint context, nint list);
    private IHook<CollectFn>? _collect;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<nint> Provinces)> Line = new(); // AI front -> its threatened provinces, refreshed daily
    private static readonly Dictionary<nint, long> Sent = []; // unit -> hour until it is left alone while walking (main thread)
    private static readonly Dictionary<int, long> Gap = [];   // province id -> hour until: a division is walking there (main thread)

    private enum PostKind { Reserve, Garrison, Helper, Depth, Drive }

    /// <summary>A pinned division's job: tag, kind, the province it holds, and the line province it backs (second line).</summary>
    private readonly record struct Posting(int Tag, PostKind Kind, int Province, int Guard = 0);

    // Posted divisions (reserves, grad garrisons, helpers, second line): the theatre shuffle and army balancing leave them alone.
    // AI threads only test membership; a key is dereferenced only while it is still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Posting> Posted = new();
    // Divisions we took off their orders -> that order, to put them back on it when released; else vanilla sends them to a far
    // front and back. Main thread; dereferenced only while still one of the tag's units.
    private static readonly Dictionary<nint, (int Tag, long Group, int Order)> Home = [];
    // New divisions we dispatched -> tag and hour: left alone while they walk (FreshlyArrived keeps them a week once there).
    // Never dereferenced.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> FreshDivisions = new();
    // Tag -> hour it last had twice the enemy's power on its line: its attacks go ahead however thin the line. AI threads read.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> DominantSince = new();
    private static readonly Dictionary<int, HashSet<nint>> Known = []; // tag -> its land divisions at its last backfill pass (main thread)
    private PerGame _game = null!;
    private static int _world = -1; // Game.Generation at the last reset; until it runs after a load, Line points into the old world
    private int _backfillAt = Never;
    private static int _backfilled, _reserved, _deployed, _returned, _stopped, _depth, _kept;

    /// <summary>
    /// True for a division that arrived within FreshHours, so the theatre shuffle doesn't march it away and back. While a
    /// division walks, its arrival field is a schedule, not a stamp, so walking ones never count.
    /// </summary>
    private static bool FreshlyArrived(nint unit)
    {
        if (unit == 0 || Division.Walking(unit)) return false;
        var age = Game.Hour - *(int*)(unit + ArrivalOfUnit);
        return age is >= 0 and < FreshHours;
    }

    /// <summary>
    /// The AI's regrouping can't take this division: posted, resting, newly dispatched, or run by another plugin; or on the
    /// line where it is its country's last there, one of no more than the enemy divisions next to it, or walls in a pocket.
    /// AI threads, read only.
    /// </summary>
    private static bool Pinned(nint unit)
    {
        if (unit == 0) return false;
        var tag = Division.Commander(unit);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        if (Posted.ContainsKey(unit) || Resting.ContainsKey(unit) || Jobs.Taken(unit)
            || FreshDivisions.TryGetValue(unit, out var fresh) && Game.Hour - fresh.Hour is >= 0 and < FreshWalkHours) return true;
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

    private const int UnitsOfAssignCommand = 0x28; // COrderAssign/UnassignCommand: CPdxArray<CRef>; group CRef +0x40, order id +0x48

    /// <summary>
    /// Strips pinned divisions (<see cref="Pinned"/>) and HQs from an AI assign command, in place, so a new order can't empty
    /// the line. Divisions next to a pocket and ones in no army still go. False when none is left. AI threads.
    /// </summary>
    private static bool KeepsLine(nint cmd, int tag)
    {
        if (*(nint*)cmd != _assignVTable) return true;
        var country = Game.Country(tag);
        if (country == 0) return true;
        var own = Game.Units(country); // matched by CRef, no map: a stale id resolved blind reads garbage
        var units = Native.PdxArray<long>(cmd + UnitsOfAssignCommand);
        var kept = 0;
        foreach (var reference in units)
        {
            nint unit = 0;
            foreach (var u in own) if (u != 0 && Division.Ref(u) == reference && Division.IsLand(u)) { unit = u; break; }
            // A division in no army always goes: army balancing fills new armies this way, and one kept out is walked back and forth.
            if (unit == 0 || !Division.IsHeadquarters(unit) && (!Pinned(unit) || NextToPocket(unit) || Division.Group(unit) == 0)) units[kept++] = reference;
            else Interlocked.Increment(ref _keptOnLine);
        }
        if (kept == units.Length) return true;
        Native.SetPdxArraySize(cmd + UnitsOfAssignCommand, kept);
        return kept > 0;
    }

    private static int _keptOnLine;

    // Root front order commands:
    //   COrderReplaceRootCommands (0x70): +0x48 front line CRef, +0x50 section id, +0x58/+0x60 start/end (0..100000)
    //   CArmyGroupCommand (0x80, a new army with its front): the same at +0x60/+0x68/+0x70/+0x78
    //   COrderEditRootCommand (0x50): +0x28 group CRef, +0x30 order id
    // CFrontSection +0x30: our side's CProvince*; COrder +0x88: its province pairs.
    private static nint _replaceRootVTable, _editRootVTable, _armyGroupVTable;
    private const int ProvincesOfSection = 0x30, PairsOfOrder = 0x88, FullStretch = 100_000;
    private static int _frontOrdersDropped;

    /// <summary>
    /// Drops an AI front order (drawn, redrawn or with a new army) along a bystanders' border (<see cref="Bystanders"/>) or an
    /// ally's line overseas before it exists; deleting it later makes the general redraw it hourly. AI threads.
    /// </summary>
    private static bool KeepsFrontOrder(nint cmd, int tag)
    {
        var vtable = *(nint*)cmd;
        if (vtable != _replaceRootVTable && vtable != _armyGroupVTable && vtable != _editRootVTable) return true; // every AI command passes here
        List<nint> ours = [], theirs = [];
        if (vtable == _replaceRootVTable || vtable == _armyGroupVTable)
        {
            var at = vtable == _replaceRootVTable ? 0x48 : 0x60;
            var front = Game.Resolve(*(long*)(cmd + at));
            if (front == 0) return true;
            var id = *(int*)(cmd + at + 8);
            long start = *(long*)(cmd + at + 0x10), end = *(long*)(cmd + at + 0x18);
            nint section = 0;
            foreach (var s in Native.PdxArray(front + SectionsOfFront))
                if (s != 0 && *(int*)(s + IdOfSection) == id) { section = s; break; }
            if (section == 0) return true;
            var provinces = Native.PdxArray(section + ProvincesOfSection);
            var n = provinces.Length;
            if (n == 0) return true;
            int lo = (int)Math.Clamp((Math.Min(start, end) * (n - 1) + FullStretch / 2) / FullStretch, 0, n - 1);
            int hi = (int)Math.Clamp((Math.Max(start, end) * (n - 1) + FullStretch / 2) / FullStretch, 0, n - 1);
            var covered = new HashSet<nint>();
            for (var i = lo; i <= hi; i++) covered.Add(provinces[i]);
            var pairs = *(nint*)(section + PairsOfSection);
            for (int i = 0, count = *(int*)(section + PairsOfSection + 0xC); i < count; i++)
                if (covered.Contains(*(nint*)(pairs + 16 * i)))
                {
                    ours.Add(*(nint*)(pairs + 16 * i));
                    theirs.Add(*(nint*)(pairs + 16 * i + 8));
                }
        }
        else
        {
            var group = Game.Resolve(*(long*)(cmd + 0x28));
            var order = group == 0 ? 0 : _findOrder(group, *(int*)(cmd + 0x30));
            if (order == 0) return true;
            var pairs = *(nint*)(order + PairsOfOrder);
            for (int i = 0, count = *(int*)(order + PairsOfOrder + 0xC); i < count; i++)
            {
                ours.Add(*(nint*)(pairs + 16 * i));
                theirs.Add(*(nint*)(pairs + 16 * i + 8));
            }
        }
        if (ours.Count == 0) return true;
        var country = Game.Country(tag);
        if (country == 0) return true;
        return !Overseas(country, tag, ours.ToHashSet(), Game.Hour) && !Bystanders(country, tag, theirs.Where(p => p != 0));
    }

    private const int ShortHours = 48;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> ShortOfMen = new(); // tag -> hour its line was last unmanned

    private const int HeadquartersHours = 2;
    private static int _headquartersAt, _headquartersPulled;

    /// <summary>
    /// Every HeadquartersHours: AI army HQs are taken off their orders (never assigned again: <see cref="KeepsLine"/>) and step
    /// back from enemy ground, so battle plans don't walk them into the attack. Main thread.
    /// </summary>
    private static void KeepHeadquarters()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _headquartersAt, HeadquartersHours)) return;
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player) continue;
            foreach (var unit in Game.Units(country).ToArray())
            {
                if (unit == 0 || !Division.IsLand(unit) || !Division.IsHeadquarters(unit) || Division.Fighting(unit) || Division.Group(unit) is not (var g and not 0)) continue;
                if (OnOrder(unit, g))
                {
                    if (Commands.Unassign(unit, g, true)) Interlocked.Increment(ref _headquartersPulled);
                }
                else if (!Division.Walking(unit) && Division.Location(unit) is var at && at != 0 && Province.NextToEnemy(country, tag, at)
                         && RestProvince(country, tag, at) is var rear and > 0) Commands.Move(unit, rear, true);
            }
        }
    }

    /// <summary>The division stands next to encircled enemy ground with enemy divisions in it.</summary>
    private static bool NextToPocket(nint unit)
    {
        var tag = Division.Commander(unit);
        var country = Game.Country(tag);
        var at = Division.Location(unit);
        if (country == 0 || at == 0) return false;
        foreach (var id in Game.Neighbours(at))
            if (Game.Province(id) is var p && p != 0 && Province.Divisions(p) > 0 && Province.Enemy(country, tag, p) && Province.Encircled(country, tag, p)) return true;
        return false;
    }

    /// <summary>The country's tracked threatened line (seen within LineFreshHours) that it still holds, or null. Any thread.</summary>
    private static HashSet<nint>? LineOf(int tag, int hour)
    {
        if (Game.Generation != _world) return null; // loaded, not reset yet: no line rather than a stale one
        HashSet<nint>? line = null;
        foreach (var e in Line)
            if (e.Value.Tag == tag && hour - e.Value.Hour is >= 0 and < LineFreshHours)
                foreach (var province in e.Value.Provinces)
                    if (Province.Controller(province) == tag) (line ??= []).Add(province);
        return line;
    }

    /// <summary>
    /// True if no line is tracked or the country (with allies) has ~1.2 divisions per threatened line province. Attacking from
    /// a thinner line opens holes.
    /// </summary>
    private static bool Manned(int tag, HashSet<nint>? line)
    {
        if (line == null || line.Count == 0) return true;
        var country = Game.Country(tag);
        var divisions = 0;
        foreach (var province in line)
            divisions += Standing(country, tag, province);
        return line.Count * 6 <= divisions * 5;
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
    /// Our side's land power within two provinces of <paramref name="from"/> is at least twice the enemy's. Such ground needs no
    /// resting, escaping or holding back of attacks.
    /// </summary>
    private static bool Dominant(nint country, int tag, IEnumerable<nint> from)
    {
        long ours = 0, theirs = 0;
        foreach (var p in Within(from, 2, q => Province.Controller(q) != 0))
        foreach (var unit in Province.Units(p))
        {
            if (unit == 0 || !Division.IsLand(unit)) continue;
            var commander = Division.Commander(unit);
            if (commander == tag || Game.Allied(country, commander)) ours += Division.Power(unit);
            else if (Game.AtWar(country, commander)) theirs += Division.Power(unit);
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

    /// <summary>True, and stamps the hour, once <paramref name="hours"/> game hours have passed since the last time.</summary>
    private static bool Due(ref int last, int hours)
    {
        var hour = Game.Hour;
        if ((uint)(hour - last) < (uint)hours) return false; // unsigned: after loading an earlier date it runs at once
        last = hour;
        return true;
    }

    private const int Never = -(1 << 30);

    /// <summary>
    /// Every BackfillHours, per AI country: releases stale posts, places new divisions, refills the holes of the threatened line
    /// (or grad) with key ground first, stops offensives on a thin line, and posts reserves and a second line when it can spare them.
    /// </summary>
    private void Backfill()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _backfillAt, BackfillHours)) return;
        _game.Check(); // caches from before a load point into the old world
        var hour = Game.Hour;
        Expire(hour);
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player) continue;
            PrunePosted(country, tag);
            DropAreaDefence(country, tag);
            DropBystanderFronts(country, tag);
            var fresh = NewDivisions(country, tag); // every pass: one deployed in peace isn't new once war comes
            var grad = _postures ? Posture(country, tag, hour) : null; // facing a much stronger enemy, the grad is the line
            var line = grad != null ? Held(grad, tag) : LineOf(tag, hour);
            if (line == null || line.Count == 0)
            {
                DominantSince.TryRemove(tag, out _);
                continue; // no line lately: posts stay while they still back one (PrunePosted)
            }
            var dominant = grad == null && Dominant(country, tag, line);
            if (dominant) DominantSince[tag] = hour;
            else DominantSince.TryRemove(tag, out _);
            if (fresh.Count > 0) Deploy(country, tag, fresh, grad, line, hour);
            var sent = 0;
            var fronts = new Dictionary<nint, HashSet<nint>>(); // AI front -> the armies standing on it
            foreach (var (province, key) in Holes(country, tag, grad, line).Select(p => (p, Key(country, tag, p))).OrderByDescending(h => h.Item2))
            {
                if (sent == (grad != null ? MaxGradMoves : MaxBackfills)) break;
                var important = grad != null || key >= KeyHole; // worth walking further for
                var id = Game.Id(province);
                var armies = grad == null ? Armies(tag, province, hour, fronts) : null;
                var helper = Helper(tag, province, line, important ? KeyBackfillRadius : BackfillRadius, prefer: armies);
                if (helper == 0) continue; // nothing free nearby: the attack gate holds the front instead
                var done = grad != null ? Garrison(helper, id, tag, hour)
                    : armies != null && armies.Contains(Division.Group(helper)) ? Send(helper, id, hour, important ? KeyGapHours : GapHours) // its own front: it stays
                    : Station(helper, id, tag, hour, important ? KeyGapHours : GapHours, PostKind.Helper); // posted, else its front walks it back
                if (!done) continue;
                sent++;
                Interlocked.Increment(ref _backfilled);
            }
            var manned = Manned(tag, line);
            if (!manned) ShortOfMen[tag] = hour;
            if (grad == null && !manned && !dominant) HoldOffensives(country, tag, hour);
            StopTrappedOffensives(country, tag);
            StopBeatenOffensives(country, tag, hour);
            if (!manned || MicroElsewhere) continue;
            Reserve(country, tag, line, hour);
            if (grad == null) Depth(country, tag, line, hour);
        }
        ShareJobs();
    }

    private const int AreaDefenceOrder = 5; // COrder type: area defence (port and home garrisons)

    /// <summary>
    /// Deletes leftover area defence orders when no enemy holds a port (<see cref="NoLandings"/>; UpdateAreaDefense makes no
    /// new ones then). Their divisions would walk to far ports and are never collected for other fronts. Main thread.
    /// </summary>
    private static void DropAreaDefence(nint country, int tag)
    {
        if (!NoLandings(country, tag)) return; // landings possible: garrisons are only cut back (UnguardAreaDefence)
        foreach (var group in OrdersGroup.OfCountry(country))
        foreach (var order in Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray())
        {
            if (order == 0 || Order.Type(order) != AreaDefenceOrder) continue;
            var divisions = Order.Members(order).Length;
            if (!PostOrderCommand(Signatures.DeleteOrderCommand, new OrdersGroup(group).Ref, Order.Id(order), 0)) continue;
            _log?.Debug($"#{tag}: deleted an area defence order of {divisions} division(s): its enemies hold no port");
        }
    }

    /// <summary>
    /// Deletes the tag's front orders along bystanders' borders (<see cref="Bystanders"/>) or allies' lines overseas, so their
    /// divisions go where a war is or may come. A target being prepared keeps its front.
    /// </summary>
    private static void DropBystanderFronts(nint country, int tag)
    {
        var hour = Game.Hour;
        foreach (var group in OrdersGroup.OfCountry(country))
        {
            if (group == 0 || new OrdersGroup(group).IsArmyGroup) continue;
            foreach (var order in Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray())
            {
                if (order == 0 || Order.Type(order) != Order.Front) continue;
                var members = Order.Members(order);
                if (members.Length == 0) continue;
                // Judge the order's own province pairs, as KeepsFrontOrder did; judging by where its divisions stand
                // (still walking up) would delete and redraw it daily.
                var across = new HashSet<nint>();
                var standing = new List<nint>();
                var pairs = *(nint*)(order + PairsOfOrder);
                for (int i = 0, count = *(int*)(order + PairsOfOrder + 0xC); i < count; i++)
                {
                    nint ours = *(nint*)(pairs + 16 * i), theirs = *(nint*)(pairs + 16 * i + 8);
                    if (ours != 0) standing.Add(ours);
                    if (theirs != 0 && Province.Controller(theirs) is var c && c > 0 && c != tag) across.Add(theirs);
                }
                var overseas = Overseas(country, tag, standing.ToHashSet(), hour);
                if (!overseas && (across.Count == 0 || !Bystanders(country, tag, across))) continue;
                var divisions = members.Length;
                if (!PostOrderCommand(Signatures.DeleteOrderCommand, new OrdersGroup(group).Ref, Order.Id(order), 0)) continue;
                Interlocked.Increment(ref overseas ? ref _overseas : ref _bystanders);
                _log?.Debug($"#{tag}: deleted a front order of {divisions} division(s) {(overseas ? "on an ally's front overseas" : "on a neutral border")}: they go where they are needed");
            }
        }
    }

    /// <summary>The line's provinces short of defenders (one each, or what the grad wants) that nobody is walking to yet.</summary>
    private static IEnumerable<nint> Holes(nint country, int tag, Grad? grad, HashSet<nint> line) =>
        line.Where(p => !Gap.ContainsKey(Game.Id(p)) && Standing(country, tag, p) < (grad?.Want.GetValueOrDefault(Game.Id(p), 1) ?? 1));

    /// <summary>
    /// The armies (COrdersGroup) of the tag's divisions on the AI fronts this province is on, cached per front; null if on none.
    /// Read through the provinces, never the front, which may have been freed since.
    /// </summary>
    private static HashSet<nint>? Armies(int tag, nint province, int hour, Dictionary<nint, HashSet<nint>> cache)
    {
        HashSet<nint>? armies = null;
        foreach (var (front, e) in Line)
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

    /// <summary>The tag's land divisions not seen at its last pass; none on the first pass.</summary>
    // ponytail: by pointer; a new division in a dead one's memory is missed (vanilla places it), read a creation date if that shows.
    private static List<nint> NewDivisions(nint country, int tag)
    {
        var units = new HashSet<nint>();
        foreach (var unit in Game.Units(country))
            if (unit != 0 && Division.IsLand(unit)) units.Add(unit);
        var fresh = Known.TryGetValue(tag, out var old) ? units.Where(u => !old.Contains(u)).ToList() : [];
        Known[tag] = units;
        foreach (var (unit, since) in FreshDivisions)
            if (since.Tag == tag && Game.Hour - since.Hour is < 0 or >= FreshWalkHours) FreshDivisions.TryRemove(unit, out _);
        return fresh;
    }

    /// <summary>
    /// Sends new divisions where they are needed most: the grad's holes, else the line's holes (key ground first), then the
    /// most thinly held front until it is manned. Pinned while they walk; the rest are left to vanilla.
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
        if (grad != null || free.Count == 0) return; // on a grad the old fronts are given up
        Dictionary<nint, int>? thinnest = null;
        foreach (var e in Line)
        {
            if (e.Value.Tag != tag || hour - e.Value.Hour is < 0 or >= LineFreshHours) continue;
            var held = e.Value.Provinces.Where(p => Province.Controller(p) == tag).ToDictionary(p => p, p => Standing(country, tag, p));
            if (held.Count > 0 && (thinnest == null || held.Values.Sum() * thinnest.Count < thinnest.Values.Sum() * held.Count)) thinnest = held;
        }
        if (thinnest == null) return;
        var divisions = thinnest.Values.Sum();
        while (free.Count > 0 && thinnest.Count * 6 > divisions * 5) // as Manned
        {
            var target = thinnest.MinBy(p => p.Value).Key;
            if (!Dispatch(free.Dequeue(), Game.Id(target), tag, hour)) continue;
            thinnest[target]++;
            divisions++;
            Interlocked.Increment(ref _deployed);
        }
    }

    /// <summary>Takes a new division off its orders and sends it to the province, pinned while it walks. Main thread.</summary>
    private static bool Dispatch(nint unit, int province, int tag, int hour)
    {
        if (!Relocate(unit, province, hour, GapHours, remember: false)) return false; // its deployment order isn't home
        FreshDivisions[unit] = (tag, hour);
        return true;
    }
}
