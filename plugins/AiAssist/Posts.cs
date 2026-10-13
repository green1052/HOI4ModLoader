using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Hold the line's bookings and posts: sending divisions, taking them off their orders and back, reserves and the second
    //      line, and which divisions are free to go ----

    /// <summary>Drops the bookings whose time is up (game hours): a sent division is free again, a hole may be refilled. Main thread.</summary>
    private static void Expire(long hour)
    {
        foreach (var (unit, until) in Sent.ToArray()) if (hour >= until) Sent.Remove(unit);
        foreach (var (province, until) in Gap.ToArray()) if (hour >= until) Gap.Remove(province);
        foreach (var (unit, at) in Combat.ToArray()) if (hour - at is < 0 or >= CombatHours) Combat.Remove(unit);
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
        Sent[unit] = hour + SentHours;
        Gap[province] = hour + gapHours;
        if (!keep) Posted.TryRemove(unit, out _);
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
        Posted[unit] = new Posting(tag, kind, province, guard);
        return true;
    }

    /// <summary>Off every order of its army, the order it was on remembered (unless already) to put it back (<see cref="Return"/>). Main thread.</summary>
    private static void TakeOff(nint unit, bool remember = true)
    {
        var group = Division.Group(unit);
        if (group == 0) return;
        if (remember && !Home.ContainsKey(unit) && OrderOf(unit, group) is var order and not 0)
            Home[unit] = (Division.Commander(unit), new OrdersGroup(group).Ref, order);
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
        if (!Home.Remove(unit, out var home) || home.Tag != tag) return;
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
            if (Standing(country, tag, post) > 0 || Gap.ContainsKey(id)) continue;
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
        var backed = Posted.Values.Where(p => p.Tag == tag && p.Kind == PostKind.Depth).Select(p => p.Guard).ToHashSet();
        if (backed.Count >= Math.Min(ReserveMax, line.Count / ReservePerLine)) return;
        var posted = 0;
        foreach (var (province, _) in line.Where(p => !backed.Contains(Game.Id(p))).Select(p => (p, Key(country, tag, p)))
                     .Where(k => k.Item2 >= DepthKey).OrderByDescending(k => k.Item2).ToList())
        {
            if (posted == MaxDepthPosts) return;
            var post = 0;
            foreach (var id in Game.Neighbours(province))
                if (Game.Province(id) is var q && q != 0 && !line.Contains(q) && Province.Controller(q) == tag && !GivenUp(tag, id) && !Gap.ContainsKey(id)
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
        foreach (var (unit, post) in Posted)
        {
            if (post.Tag != tag || !Posted.TryRemove(unit, out _)) continue;
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
        foreach (var (unit, post) in Posted)
        {
            if (post.Tag != tag) continue;
            units ??= [.. Game.Units(country)];
            if (!units.Contains(unit)) Posted.TryRemove(unit, out _);
            else if (!PostHolds(country, tag, post) && Posted.TryRemove(unit, out _)) Return(unit, tag);
        }
        foreach (var (unit, home) in Home.ToArray())
            if (home.Tag == tag && !(units ??= [.. Game.Units(country)]).Contains(unit)) Home.Remove(unit);
    }

    /// <summary>What holding this province of ours is worth: a port (the fleet and supply), victory points, a river in front of it, city or mountain ground.</summary>
    private static int Key(nint country, int tag, nint province)
    {
        var port = Province.BuildingLevel(province, Buildings.NavalBaseSlot);
        if (port > 0) return 1000 + port;
        var key = 3 * Math.Min(Province.VictoryPoints(province), 30);
        if ((Province.TerrainCategory(province) & (Province.Mountain | Province.Urban)) != 0) key += 25;
        // The wall around an enemy pocket (Yantai's six divisions in one province): a hole there lets it break out.
        if (Game.Neighbours(province).Any(id => Game.Province(id) is var e && e != 0 && Province.Divisions(e) > 0 && Province.Encircled(country, tag, e))) key += KeyHole;
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
                if (!Spare(unit, tag) || !reserves && Posted.ContainsKey(unit)) continue;
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
        return Division.Ready(unit, tag) && !Sent.ContainsKey(unit) && !Resting.ContainsKey(unit) && !Jobs.Taken(unit, Id);
    }

    /// <summary>Our divisions with a job (posted, resting, walking somewhere for us), for the other plugins to leave alone.</summary>
    private static void ShareJobs() => Jobs.Set(Id, Posted.Keys.Concat(Resting.Keys).Concat(Sent.Keys));

    /// <summary>Another plugin (FrontLine) runs the AI's rotation, escape, reserves, second line and local attacks: ours stand aside.</summary>
    private static bool MicroElsewhere => Jobs.AiMicro is { } owner && owner != Id;

    /// <summary>A movable division that stands idle, rested enough to go somewhere.</summary>
    private static bool Spare(nint unit, int tag) => Movable(unit, tag) && !Division.Walking(unit) && Percent(Division.Organisation(unit)) >= MinBackfillOrg;

    private const int TagOfGroup = 0x3C; // COrdersGroup: owner tag

    /// <summary>A live army or army group of the tag by its CRef id, or 0 (gone, or the id is another country's).</summary>
    private static nint GroupOf(long reference, int tag)
    {
        var group = Game.Resolve(reference);
        return group != 0 && *(int*)(group + TagOfGroup) == tag ? group : 0;
    }

    /// <summary>The division is a member of one of its army's (or army group's) orders.</summary>
    private static bool OnOrder(nint unit, nint group)
    {
        var member = unit + MemberOfUnit;
        foreach (var owner in (ReadOnlySpan<nint>)[group, *(nint*)(group + OrdersGroup.ParentOffset)])
            if (owner != 0)
                foreach (var order in Native.PdxArray(owner + OrdersGroup.RootOrdersOffset))
                    if (order != 0 && Order.Members(order).Contains(member)) return true;
        return false;
    }

    /// <summary>An order command posted as the AI posts: Execute (on), Delete.</summary>
    private static bool PostOrderCommand(string className, long group, int order, int argument) =>
        Commands.TryPost(Commands.NewOrderCommand(className, group, order, argument), true);
}

