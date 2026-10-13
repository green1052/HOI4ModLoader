using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Hold the line: pinned defenders, holes refilled, reserves and second-line posts, new divisions; attacks wait on a thinly
    //      manned line (sending and posting divisions: Posts.cs; offensives called off and the launch gate: AttackGate.cs;
    //      rotation, escape and the fire brigade: Micro.cs; beach defence: Beaches.cs) ----

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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<nint> Provinces)> Line = new(); // AI front -> its threatened provinces, refreshed daily
    private static readonly Dictionary<nint, long> Sent = []; // unit -> game hour until: a division we sent is left alone while it walks (main thread)
    private static readonly Dictionary<int, long> Gap = [];   // province id -> game hour until: a hole or target a division is walking to (main thread)

    private enum PostKind { Reserve, Garrison, Helper, Depth, Drive }

    /// <summary>A pinned division's job: its tag, the province it holds, the line province it backs (second line).</summary>
    private readonly record struct Posting(int Tag, PostKind Kind, int Province, int Guard = 0);

    // Reserves, grad garrisons, line helpers and second-line divisions: the theatre shuffle and army balancing leave them. AI threads
    // only ask whether a unit is in it; never dereferenced unless still one of the tag's units.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Posting> Posted = new();
    // Divisions we took off their orders, with the order they were on: put back on it when we let them go, else vanilla's regrouping
    // sends them to some far front and back (main thread; never dereferenced unless still one of the tag's units).
    private static readonly Dictionary<nint, (int Tag, long Group, int Order)> Home = [];
    // New divisions we sent where they were needed, with their tag and since when: the theatre shuffle and army balancing leave
    // them while they walk (FreshlyArrived keeps them a week once there). Never dereferenced.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Tag, int Hour)> FreshDivisions = new();
    // Tags that outgun the enemy on their line two to one, with the hour seen: their attacks go ahead however thin the line (AI threads read).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> DominantSince = new();
    private static readonly Dictionary<int, HashSet<nint>> Known = []; // tag -> its land divisions at its last backfill pass (main thread)
    private PerGame _game = null!;
    private static int _world = -1; // Game.Generation at the last reset: until it runs after a load, Line points into the old world
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
        if (Posted.ContainsKey(unit) || Resting.ContainsKey(unit) || Paratrooper(unit) || Jobs.Taken(unit)
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

    private const int UnitsOfAssignCommand = 0x28; // COrderAssign/UnassignCommand: CPdxArray<CRef id> (then the group's CRef at +0x40, the order id at +0x48)

    /// <summary>
    /// An AI assign command can't take a division its line needs (<see cref="Pinned"/>) onto another order: vanilla made a new
    /// front around a pocket and pulled its divisions off the main line, which stood open meanwhile. Divisions next to the
    /// pocket (or any encircled enemy ground) may go: they are the ones to wall it in. Strips them in place; false when none
    /// is left. AI threads.
    /// </summary>
    private static bool KeepsLine(nint cmd, int tag)
    {
        if (*(nint*)cmd != _assignVTable) return true;
        var country = Game.Country(tag);
        if (country == 0) return true;
        var byRef = new Dictionary<long, nint>(); // the country's own divisions by CRef: never resolve ids blind (a stale one read a garbage tag)
        foreach (var u in Game.Units(country)) if (u != 0 && Division.IsLand(u)) byRef[Division.Ref(u)] = u;
        var units = Native.PdxArray<long>(cmd + UnitsOfAssignCommand);
        var kept = 0;
        foreach (var reference in units)
        {
            var unit = byRef.GetValueOrDefault(reference);
            // A division in no army (the army balancing's new armies are filled through here) always gets its assignment:
            // kept out, it stayed between armies and the AI walked it back and forth.
            if (unit == 0 || !Division.IsHeadquarters(unit) && (!Pinned(unit) || NextToPocket(unit) || Division.Group(unit) == 0)) units[kept++] = reference;
            else Interlocked.Increment(ref _keptOnLine);
        }
        if (kept == units.Length) return true;
        Native.SetPdxArraySize(cmd + UnitsOfAssignCommand, kept);
        return kept > 0;
    }

    private static int _keptOnLine;

    // Root front order commands: COrderReplaceRootCommands (0x70: +0x48 front line CRef, +0x50 section id, +0x58/+0x60 start/end
    // 0..100000), CArmyGroupCommand (0x80, a new army with its front: the same at +0x60/+0x68/+0x70/+0x78), COrderEditRootCommand
    // (0x50: +0x28 group CRef, +0x30 the order's id). CFrontSection: +0x30 CProvince* of our side; COrder: +0x88 its pairs.
    private static nint _replaceRootVTable, _editRootVTable, _armyGroupVTable;
    private const int ProvincesOfSection = 0x30, PairsOfOrder = 0x88, FullStretch = 100_000;
    private static int _frontOrdersDropped;

    /// <summary>
    /// An AI front order drawn, redrawn or given a new army along a bystanders' border (<see cref="Bystanders"/>) or an ally's
    /// line overseas is dropped before it exists: deleting it afterwards made it flicker, the general drawing it again hourly.
    /// AI threads.
    /// </summary>
    private static bool KeepsFrontOrder(nint cmd, int tag)
    {
        var vtable = *(nint*)cmd;
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
        else if (vtable == _editRootVTable)
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
        else return true;
        if (ours.Count == 0) return true;
        var country = Game.Country(tag);
        if (country == 0) return true;
        var hour = Game.Hour;
        var overseas = Overseas(country, tag, ours.ToHashSet(), hour);
        return !overseas && !Bystanders(country, tag, theirs.Where(p => p != 0));
    }

    private const int ShortHours = 48;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> ShortOfMen = new(); // tag -> hour its line was last unmanned

    private const int HeadquartersHours = 2;
    private static int _headquartersAt, _headquartersPulled;

    /// <summary>
    /// AI army HQs stay off the fronts (taken off once, never assigned again: <see cref="KeepsLine"/>) and step back when they
    /// stand next to enemy ground: vanilla's battle plans sent them into the attack with the line, where they died. An HQ's
    /// bonuses reach its army from behind the line. Main thread, every two game hours.
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

    /// <summary>The country's tracked threatened line (refreshed within the last two days) that it still holds, or null. AI threads too.</summary>
    private static HashSet<nint>? LineOf(int tag, int hour)
    {
        if (Game.Generation != _world) return null; // a load the reset hasn't caught up with (AI threads): no line, not a stale one
        HashSet<nint>? line = null;
        foreach (var e in Line)
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
            DropBystanderFronts(country, tag);
            var fresh = NewDivisions(country, tag); // every pass: one deployed in peace isn't new once war comes
            var grad = _postures ? Posture(country, tag, hour) : null; // a much stronger enemy: its grad is the line
            var line = grad != null ? Held(grad, tag) : LineOf(tag, hour);
            if (line == null || line.Count == 0)
            {
                DominantSince.TryRemove(tag, out _);
                continue; // no line seen lately: its posts stay while they still back one (PrunePosted)
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
            if (!manned) ShortOfMen[tag] = hour;
            if (grad == null && !manned && !dominant) HoldOffensives(country, tag, hour);
            StopTrappedOffensives(country, tag);
            StopBeatenOffensives(country, tag, hour);
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
            _log?.Info($"#{tag}: deleted an area defence order of {divisions} division(s): its garrisons go to the land fronts");
        }
    }

    /// <summary>
    /// The tag's front orders along bystanders' borders (<see cref="Bystanders"/>: no country across that
    /// it expects a war with) are deleted: their divisions go where a war is or may come, instead of facing Luxembourg, Belgium
    /// or Switzerland. A target being prepared (focus, justification, war goal, war preparation) keeps its front.
    /// </summary>
    private static void DropBystanderFronts(nint country, int tag)
    {
        foreach (var group in OrdersGroup.OfCountry(country))
        {
            if (group == 0 || new OrdersGroup(group).IsArmyGroup) continue;
            foreach (var order in Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray())
            {
                if (order == 0 || Order.Type(order) != Order.Front) continue;
                var members = Order.Members(order);
                if (members.Length == 0) continue;
                // The order's own line (its province pairs), as KeepsFrontOrder judged it when drawn: by where its divisions
                // stood (still walking up, or past a neutral corner on the way) it was drawn and deleted again every day.
                var across = new HashSet<nint>();
                var standing = new List<nint>();
                var pairs = *(nint*)(order + PairsOfOrder);
                for (int i = 0, count = *(int*)(order + PairsOfOrder + 0xC); i < count; i++)
                {
                    nint ours = *(nint*)(pairs + 16 * i), theirs = *(nint*)(pairs + 16 * i + 8);
                    if (ours != 0) standing.Add(ours);
                    if (theirs != 0 && Province.Controller(theirs) is var c && c > 0 && c != tag) across.Add(theirs);
                }
                var hour = Game.Hour;
                var overseas = Overseas(country, tag, standing.ToHashSet(), hour);
                if (!overseas && (across.Count == 0 || !Bystanders(country, tag, across))) continue;
                var divisions = members.Length;
                if (!PostOrderCommand(Signatures.DeleteOrderCommand, new OrdersGroup(group).Ref, Order.Id(order), 0)) continue;
                Interlocked.Increment(ref overseas ? ref _overseas : ref _bystanders);
                _log?.Info($"#{tag}: deleted a front order of {divisions} division(s) {(overseas ? "on an ally's front overseas" : "on a neutral border")}: they go where they are needed");
            }
        }
    }

    /// <summary>The line's provinces short of defenders (one each, or what the grad wants) that nobody is walking to yet.</summary>
    private static IEnumerable<nint> Holes(nint country, int tag, Grad? grad, HashSet<nint> line) =>
        line.Where(p => !Gap.ContainsKey(Game.Id(p)) && Standing(country, tag, p) < (grad?.Want.GetValueOrDefault(Game.Id(p), 1) ?? 1));

    /// <summary>
    /// The armies (COrdersGroup) of the tag's divisions standing on the AI fronts this province is on, cached per front; null if it
    /// is on none. Read through the provinces, never the front: a tracked front may have been freed since.
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

    /// <summary>The tag's land divisions that weren't there at its last pass (just deployed, or come from elsewhere); none on its first pass in a game.</summary>
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
        foreach (var e in Line)
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
        FreshDivisions[unit] = (tag, hour);
        return true;
    }
}
