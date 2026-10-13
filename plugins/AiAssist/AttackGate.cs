using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Running offensives called off (a thin line, a trapped salient, a beaten attack) ----

    /// <summary>
    /// Fronts of the tag whose threatened provinces aren't manned (as <see cref="Manned"/>): the armies standing on them call off their
    /// running front offensives (the attack gate then holds new ones until manned). The AI's own execute command, posted forced.
    /// </summary>
    private static void HoldOffensives(nint country, int tag, int hour)
    {
        var done = new HashSet<nint>();
        foreach (var e in Line.Values)
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

    /// <summary>
    /// Running front offensives with a division in a salient whose way back is closing (<see cref="Closing"/>) are called off:
    /// vanilla kept pushing single divisions deeper until the enemy cut the salient's neck (Japan around Guangzhou). Their
    /// divisions then hold, and those inside pull out as worn ones do. The AI's own execute command, posted forced.
    /// </summary>
    private static void StopTrappedOffensives(nint country, int tag)
    {
        var closing = new Dictionary<nint, bool>();
        foreach (var group in OrdersGroup.OfCountry(country))
        {
            if (group == 0 || new OrdersGroup(group).IsArmyGroup) continue;
            foreach (var order in Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray())
            {
                if (order == 0 || Order.Type(order) != Order.Front || !Order.Executing(order)) continue;
                var trapped = false;
                foreach (var member in Order.Members(order))
                {
                    var at = Division.Location(member - MemberOfUnit);
                    if (at == 0) continue;
                    if (!closing.TryGetValue(at, out var c)) closing[at] = c = Closing(country, tag, at);
                    if (trapped = c) break;
                }
                if (trapped && Commands.TryPost(Commands.NewOrderCommand(Commands.ExecuteOrder, new OrdersGroup(group).Ref, Order.Id(order), 0), true))
                {
                    Interlocked.Increment(ref _trappedStops);
                    _log?.Info($"#{tag}: calls off an offensive: a division of it stands in a salient whose way back is closing");
                }
            }
        }
    }

    private static int _trappedStops;

    private const int DefenderOfCombat = 0x30, DivisionsOfSide = 0x20, ReservesOfSide = 0x100, ProgressSlot = 25;
    private const int LosingProgressPercent = 40, HopelessProgressPercent = 25, OutnumberedPercent = 150, BeatenHours = 72, GroupOfOrder = 0x38;
    // army -> hour its offensive was called off: by army, not order, as the general redraws its front order (a new one) at once
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> Beaten = new();
    private static int _beatenStops;

    /// <summary>
    /// Running front offensives losing more of their attacks than they win are called off, and don't start again for three
    /// days (the army's: the general redraws the order at once): an attack is losing while the attacker holds under 25 % of the
    /// battle, or under 40 % with the defenders, reserves counted, half again as many as the attackers next to it. Vanilla kept
    /// feeding three divisions into five with seven in reserve.
    /// </summary>
    private static void StopBeatenOffensives(nint country, int tag, int hour)
    {
        foreach (var (army, at) in Beaten) if (hour - at is < 0 or >= BeatenHours) Beaten.TryRemove(army, out _);
        var landCombat = Sig.VTable(LandCombat);
        var losing = new HashSet<nint>();  // our attacks being lost: the battles' provinces
        var winning = new HashSet<nint>();
        foreach (var combat in Native.PdxArray(Game.State + CombatsOfGameState).ToArray())
        {
            if (combat == 0 || *(nint*)combat != landCombat || *(nint*)(combat + ProvinceOfCombat) is not (var p and not 0) || !Province.Enemy(country, tag, p)) continue;
            var defender = *(nint*)(combat + DefenderOfCombat);
            if (defender == 0) continue;
            var attackers = 0;
            foreach (var id in Game.Neighbours(p))
                if (Game.Province(id) is var q && q != 0)
                    foreach (var unit in Province.Units(q))
                        if (unit != 0 && Division.Commander(unit) == tag && Division.IsLand(unit) && Division.Fighting(unit)) attackers++;
            if (attackers == 0) continue; // not our attack
            var defenders = *(int*)(defender + DivisionsOfSide + 0xC) + *(int*)(defender + ReservesOfSide + 0xC);
            long progress;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)combat)[ProgressSlot])(combat, &progress); // attacker's share
            if (progress * 100 < HopelessProgressPercent * Native.FixedPoint
                || progress * 100 < LosingProgressPercent * Native.FixedPoint && defenders * 100 >= OutnumberedPercent * attackers) losing.Add(p);
            else winning.Add(p);
        }
        if (losing.Count == 0) return;
        foreach (var group in OrdersGroup.OfCountry(country))
        {
            if (group == 0 || new OrdersGroup(group).IsArmyGroup) continue;
            foreach (var order in Native.PdxArray(group + OrdersGroup.RootOrdersOffset).ToArray())
            {
                if (order == 0 || Order.Type(order) != Order.Front || !Order.Executing(order)) continue;
                var (lost, won) = (new HashSet<nint>(), new HashSet<nint>());
                foreach (var member in Order.Members(order))
                    if (Division.Location(member - MemberOfUnit) is var at && at != 0)
                        foreach (var id in Game.Neighbours(at))
                            if (Game.Province(id) is var q && q != 0)
                            {
                                if (losing.Contains(q)) lost.Add(q);
                                else if (winning.Contains(q)) won.Add(q);
                            }
                if (lost.Count == 0 || lost.Count <= won.Count) continue;
                if (!Commands.TryPost(Commands.NewOrderCommand(Commands.ExecuteOrder, new OrdersGroup(group).Ref, Order.Id(order), 0), true)) continue;
                Beaten[group] = hour;
                Interlocked.Increment(ref _beatenStops);
                _log?.Info($"#{tag}: calls off an offensive losing {lost.Count} of its {lost.Count + won.Count} attack(s)");
            }
        }
    }

    /// <summary>A member stands in a salient whose way back is closing. AI threads.</summary>
    private static bool Trapped(ReadOnlySpan<nint> members, int tag)
    {
        var country = Game.Country(tag);
        if (country == 0) return false;
        foreach (var member in members)
            if (Division.Location(member - MemberOfUnit) is var at && at != 0 && Closing(country, tag, at)) return true;
        return false;
    }

    // ---- The PostAICommand gate: an "attack on" is dropped while the country's threatened line is thinly manned (or it holds
    //      a grad), a landing while its beach is crowded; the AI asks again every hour ----

    [Function(CallingConventions.Microsoft)] public delegate byte PostFn(nint cmd, nint tag);

    private IHook<PostFn>? _post;
    private static delegate* unmanaged<nint, int, nint> _findOrder;
    private static nint _executeVTable, _assignVTable;
    private static int _held;

    /// <summary>
    /// The one PostAICommand hook (every AI command): the line's divisions kept on it (HoldTheLine), AI front orders on
    /// bystanders' borders dropped, then the launch gate: invasions by their beach (StrategicInvasions), other attacks by the grad and the line (HoldTheLine).
    /// </summary>
    private void LoadPostAiCommand()
    {
        _findOrder = (delegate* unmanaged<nint, int, nint>)Sig.Find(Signatures.FindOrder);
        _replaceRootVTable = Sig.VTable("COrderReplaceRootCommands");
        _editRootVTable = Sig.VTable("COrderEditRootCommand");
        _armyGroupVTable = Sig.VTable("CArmyGroupCommand");
        _executeVTable = Sig.VTable(Commands.ExecuteOrder);
        if (_holdLine) _redeployVTable = Sig.VTable("CStrategicRedeploymentCommand"); // here, not on an AI thread
        _assignVTable = Sig.VTable(Signatures.AssignOrderCommand);
        _post = Patches.Hook<PostFn>(Sig.Find(Signatures.PostAiCommand), (cmd, tag) =>
        {
            if (*(int*)tag == Game.ControlledTag || !SheepMod.Active) return _post!.OriginalFunction(cmd, tag);
            bool keep;
            try
            {
                keep = !_holdLine || KeepsLine(cmd, *(int*)tag);
                if (keep && _holdLine && !KeepsFrontOrder(cmd, *(int*)tag))
                {
                    keep = false;
                    Interlocked.Increment(ref _frontOrdersDropped);
                }
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
        if (_redeployVTable == 0 || *(nint*)cmd != _redeployVTable) return false;
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
        var group = Game.Resolve(*(long*)(cmd + Commands.GroupOfOrderCommand));
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
        if (DominantSince.TryGetValue(*(int*)tag, out var seen) && hour - seen is >= 0 and < 2 * BackfillHours) return true; // twice the enemy's power on its line
        if (!Manned(*(int*)tag, LineOf(*(int*)tag, hour))) return false; // a thin line doesn't advance: attacking widens it, the holes get the spearhead cut off
        return !_timing || order == 0 || TimeToAttack(order, *(int*)tag, hour);
    }

    private const int MinAttackOrg = 60, MaxGatheringPercent = 30, MaxEnemyAirPercent = 70;
    private const int RegionOfProvince = 0xC8; // CProvince -> its strategic region
    // The weather manager (gamestate+0x688): province records of 0x180 bytes by province id at +0x10, the mud flag at +0x130
    // (the set_mud console command 0x140284FC0 sets it).
    private const int WeatherOfGameState = 0x688, ProvincesOfWeather = 0x10, WeatherRecordSize = 0x180, MudOfWeather = 0x130;

    /// <summary>Most of the order's divisions stand in muddy provinces. AI threads, read only.</summary>
    private static bool Muddy(ReadOnlySpan<nint> members)
    {
        var weather = Game.State == 0 ? 0 : *(nint*)(Game.State + WeatherOfGameState);
        var records = weather == 0 ? 0 : *(nint*)(weather + ProvincesOfWeather);
        if (records == 0) return false;
        var muddy = 0;
        foreach (var member in members)
            if (Division.Location(member - MemberOfUnit) is var at && at != 0 && Game.Id(at) is var id && id > 0 && (_provinceCount <= 0 || id <= _provinceCount)
                && *(byte*)(records + (nint)id * WeatherRecordSize + MudOfWeather) != 0) muddy++; // ids past the map's last: no record
        return muddy * 2 > members.Length;
    }

    /// <summary>
    /// The enemy's share (%) of air superiority over most of the order's divisions, when over MaxEnemyAirPercent; 0 otherwise
    /// (or with no air war there). From the country's strategic air records (as AirRetreat reads them). AI threads.
    /// </summary>
    private static int EnemyAirOver(ReadOnlySpan<nint> members, int tag)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return 0;
        var records = Native.PdxArray<byte>(air + SuperiorityRecords);
        int over = 0, counted = 0;
        long shares = 0;
        foreach (var member in members)
        {
            var at = Division.Location(member - MemberOfUnit);
            var region = at == 0 ? 0 : *(nint*)(at + RegionOfProvince);
            var id = region == 0 ? 0 : *(int*)(region + SuperiorityIdOfRegion);
            if (id <= 0 || id >= records.Length) continue;
            var record = *(nint*)(air + SuperiorityRecords) + (nint)id * SuperiorityRecordSize;
            long ours = *(long*)(record + OurSuperiority), enemy = *(long*)(record + EnemySuperiority);
            if (enemy <= 0) continue;
            counted++;
            var share = enemy * 100 / (ours + enemy);
            shares += share;
            if (share > MaxEnemyAirPercent) over++;
        }
        return over * 2 > members.Length && counted > 0 ? (int)(shares / counted) : 0;
    }
    private const int MinFortLevel = 3, FortAttackRatioPercent = 200;

    /// <summary>
    /// Why the order's divisions shouldn't attack yet: half or more of the enemy provinces next to them have forts of level
    /// MinFortLevel or more, and their power is under twice the enemy divisions' there; null otherwise. Vanilla attacked the
    /// Maginot line with Germany's western armies, wore them out on its forts, and France then walked into Germany. Power
    /// counts strength and organisation, not quality, so open ground is left to vanilla (Japan beats China's numbers). AI threads.
    /// </summary>
    private static string? Outgunned(ReadOnlySpan<nint> members, int tag)
    {
        var country = Game.Country(tag);
        if (country == 0) return null;
        var ahead = new HashSet<nint>();
        long ours = 0;
        foreach (var member in members)
        {
            var unit = member - MemberOfUnit;
            ours += Division.Power(unit);
            var at = Division.Location(unit);
            if (at == 0) continue;
            foreach (var id in Game.Neighbours(at))
                if (Game.Province(id) is var p && p != 0 && Province.Enemy(country, tag, p)) ahead.Add(p);
        }
        if (ahead.Count == 0) return null;
        long theirs = 0;
        var forts = 0;
        foreach (var p in ahead)
        {
            if (Province.FortLevel(p) >= MinFortLevel) forts++;
            foreach (var unit in Province.Units(p))
                if (unit != 0 && Division.IsLand(unit) && Game.AtWar(country, Division.Commander(unit))) theirs += Division.Power(unit);
        }
        if (forts * 2 < ahead.Count || ours * 100 >= theirs * FortAttackRatioPercent) return null;
        return $"{forts} of the {ahead.Count} enemy provinces ahead are fortified and it has {ours * 100 / Math.Max(1, theirs)}% of the defenders' power, under {FortAttackRatioPercent}%";
    }

    private static bool _timing;
    private static int _waited;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> WaitedAt = new(); // order -> hour last logged

    /// <summary>
    /// Offensive timing: a battle plan starts once its divisions are ready, not while they still recover their organisation
    /// (under MinAttackOrg % on average), walk up to the line (over MaxGatheringPercent % of them on the move), fight under an
    /// enemy sky, stand in mud, face a fortified line they don't outgun two to one, stand in a salient whose way back closes,
    /// or had their attacks beaten back in the last three days. AI threads.
    /// </summary>
    private static bool TimeToAttack(nint order, int tag, int hour)
    {
        var members = Order.Members(order);
        if (members.Length == 0) return true;
        long org = 0;
        var walking = 0;
        foreach (var member in members)
        {
            var unit = member - MemberOfUnit;
            org += Percent(Division.Organisation(unit));
            if (Division.Walking(unit)) walking++;
        }
        var why = org < (long)MinAttackOrg * members.Length ? $"its divisions are at {org / members.Length}% organisation"
            : walking * 100 > MaxGatheringPercent * members.Length ? $"{walking} of its {members.Length} divisions are still on their way"
            : EnemyAirOver(members, tag) is var air and > 0 ? $"the enemy holds {air}% of the air over it"
            : Muddy(members) ? "most of its divisions stand in mud"
            : Outgunned(members, tag) is { } ahead ? ahead
            : PredictedLoss(members, tag) is { } odds ? odds
            : Trapped(members, tag) ? "a division of it stands in a salient whose way back is closing"
            : Beaten.TryGetValue(*(nint*)(order + GroupOfOrder), out var beaten) && hour - beaten is >= 0 and < BeatenHours ? "its attacks were beaten back in the last three days"
            : null;
        if (why == null) return true;
        Interlocked.Increment(ref _waited);
        if (!WaitedAt.TryGetValue(order, out var last) || hour - last is < 0 or >= 24)
        {
            foreach (var (o, at) in WaitedAt) if (hour - at is < 0 or >= 48) WaitedAt.TryRemove(o, out _); // orders come and go
            WaitedAt[order] = hour;
            _log?.Info($"#{tag}: holds an offensive of {members.Length} division(s): {why}");
        }
        return false;
    }
}
