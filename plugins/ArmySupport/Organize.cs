using HOI4ModLoader;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>
    /// COrderGroupCommand ctor (mem[0x60], CPdxArray&lt;CArmy*&gt;* units, provider, CTheaterGroup* theaterOrNull): the army
    /// list's new army from the selected divisions (they leave their armies; empty ones are deleted).
    /// </summary>
    [Signature] public const string NewArmyCommand = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 49 8B F9 49 8B F0 48 8B EA 48 8B D9 C6 41 08 00";

    /// <summary>
    /// The army list's new army group from the selected armies: `mov ecx,80h; call malloc; ...; mov r8,[rbx+70h]; lea rdx,[armies];
    /// mov rcx,rax; call CArmyGroupCommand ctor (mem[0x80], CPdxArray&lt;COrdersGroup*&gt;* armies, provider)`.
    /// </summary>
    [Signature] public const string NewArmyGroupCall = "B9 80 00 00 00 E8 ? ? ? ? 48 89 44 24 ? 48 85 C0 74 ? 4C 8B 43 70 48 8D 54 24 ? 48 8B C8 E8";

    /// <summary>
    /// CAssignToArmyGroupCommand ctor (mem[0x50], CPdxArray&lt;COrdersGroup*&gt;* armies, COrdersGroup* armyGroup, int index, -1:
    /// last): a field marshal's card right-clicked with armies selected.
    /// </summary>
    [Signature] public const string JoinArmyGroupCommand = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 40 0F 29 74 24 ? 41 8B F9 49 8B D8 4C 8B E2 4C 8B F1 C6 41 08 00";

    /// <summary>A leader's over-command penalty: `test r8b,r8b; jz; mov edi,[rip+NMilitary::CORPS_COMMANDER_DIVISIONS_CAP]`.</summary>
    [Signature] public const string ArmySizeCap = "45 84 C0 74 08 8B 3D ? ? ? ? EB 08 48 8B 01 FF 50 60 8B F8";

    /// <summary>The leader list's assign (mem[0x40]): leader CRef @0x28, group CRef @0x30, bool @0x38 (the UI passes 0).</summary>
    [VTable] public const string SetLeaderCommand = "CSetArmyLeaderCommand";
}

/// <summary>
/// Ctrl+Alt+G with land divisions selected: they are split into armies of at most the game's army size for a general
/// (NMilitary.CORPS_COMMANDER_DIVISIONS_CAP), neighbours together; a whole army of at least half that size stays as it is.
/// Each new army gets the best idle general, and all of them join the army group most of the divisions came from, or a new
/// one under the best idle field marshal. The army list's own commands, one step a tick (multiplayer works).
/// </summary>
public unsafe partial class ArmySupport
{
    private static class Org
    {
        // CArmy (a land division), COrdersGroup, CCountry, provider (an orders group manager), CUnitLeader. Layouts for HOI4 1.19.3.
        public const int Kind = 0x08, Group = 0xC0, Commander = 0x1D8, Location = 0x1F0, Provider = 0x2A0;
        public const int SelfRef = 0x08, ProviderOfGroup = 0x40, MemberCount = 0x5C, Leader = 0x88;
        public const int Units = 0x290, Providers = 0x168, LeaderHolder = 0xFF0, Leaders = 0x70, GroupsOfProvider = 0xB0;
        public const int LeaderTag = 0x120, SkillLevel = 0xE60, LevelOfSkill = 0x1B8, LeaderType = 0xE7C, Attack = 0xF58, Defense = 0xF68, Planning = 0xF78, Logistics = 0xF88;
        public const int FieldMarshal = 0, General = 1;
        public const int FirstNode = 0x30, NextOfNode = 0x10;
        public const int NewArmySize = 0x60, NewArmyGroupSize = 0x80, JoinSize = 0x50, SetLeaderSize = 0x40;
        public const long TimeoutMs = 30_000; // multiplayer: a round trip
    }

    /// <summary>A reorganisation under way: pointers are looked up again every tick (units in the country's list, groups by CRef).</summary>
    private sealed class Organizing(List<List<nint>> chunks, HashSet<long> oldGroups, List<long> kept, long armyGroup)
    {
        public readonly List<List<nint>> Chunks = chunks; // divisions of each new army
        public readonly HashSet<long> OldGroups = oldGroups;
        public readonly HashSet<long> OldParents = [];     // the armies' army groups when the new army groups were sent
        public readonly List<long> Armies = kept;          // whole armies kept, then the new ones
        public long ArmyGroup = armyGroup;                 // 0: a new one
        public bool Joining;
        public long Deadline = Environment.TickCount64 + Org.TimeoutMs;
    }

    private delegate* unmanaged<nint, nint, nint, nint, nint> _newArmy;
    private delegate* unmanaged<nint, nint, nint, nint> _newArmyGroup;
    private delegate* unmanaged<nint, nint, nint, int, nint> _joinArmyGroup;
    private int* _armyCap;
    private nint _selectionUi;
    private int _selectionOffset;
    private bool _organizeDown;
    private Organizing? _organizing;
    private PerGame _organizeGame = null!;

    private void LoadOrganize()
    {
        var at = Sig.Find(Signatures.AirWingsSelected); // `mov rcx,[rip+ui]; ...; add rcx,538h`: the selection list
        _selectionUi = at + 11 + *(int*)(at + 7);
        _selectionOffset = *(int*)(at + 19);
        _newArmy = (delegate* unmanaged<nint, nint, nint, nint, nint>)Sig.Find(Signatures.NewArmyCommand);
        var call = Sig.Find(Signatures.NewArmyGroupCall) + 32;
        _newArmyGroup = (delegate* unmanaged<nint, nint, nint, nint>)(call + 5 + *(int*)(call + 1));
        _joinArmyGroup = (delegate* unmanaged<nint, nint, nint, int, nint>)Sig.Find(Signatures.JoinArmyGroupCommand);
        var cap = Sig.Find(Signatures.ArmySizeCap);
        _armyCap = (int*)(cap + 11 + *(int*)(cap + 7));
        _ = Sig.VTable(Signatures.SetLeaderCommand);
        _organizeGame = new PerGame(() => _organizing = null);
        MainThread.Every(TimeSpan.FromMilliseconds(30), PollOrganizeKey);
        MainThread.Every(TimeSpan.FromMilliseconds(500), Organize);
    }

    private void PollOrganizeKey()
    {
        var down = Gui.KeyHeld('G');
        var hit = down && !_organizeDown && Gui.CtrlHeld && Gui.AltHeld && Gui.GameFocused;
        _organizeDown = down;
        if (!hit || !Game.HasStarted) return;
        _organizeGame.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        if (_organizing != null)
        {
            Log.Info("organize: still busy with the last one");
            return;
        }
        var selected = SelectedDivisions(country, tag);
        if (selected.Count == 0)
        {
            Log.Info("organize: select divisions first");
            return;
        }
        StartOrganize(selected);
    }

    private void StartOrganize(List<nint> selected)
    {
        var cap = Math.Max(1, *_armyCap);
        var byGroup = selected.GroupBy(u => *(nint*)(u + Org.Group)).ToList();
        // A whole army of a sensible size stays as it is: its general, plans and battle plans are kept.
        var keep = byGroup.Where(g => g.Key != 0 && g.Count() == *(int*)(g.Key + Org.MemberCount) && g.Count() * 2 >= cap && g.Count() <= cap)
            .Select(g => g.Key).ToList();
        var pool = selected.Where(u => !keep.Contains(*(nint*)(u + Org.Group))).ToList();
        // Divisions of different providers (theatres) can't share an army.
        var chunks = pool.GroupBy(u => *(nint*)(u + Org.Provider)).SelectMany(p => Split([.. p], cap)).ToList();
        var parent = byGroup.Where(g => g.Key != 0 && *(nint*)(g.Key + OrdersGroup.ParentOffset) != 0)
            .GroupBy(g => *(nint*)(g.Key + OrdersGroup.ParentOffset)).MaxBy(p => p.Sum(g => g.Count()))?.Key ?? 0;
        var posted = new List<List<nint>>();
        foreach (var chunk in chunks)
        {
            var provider = *(nint*)(chunk[0] + Org.Provider);
            if (provider != 0 && Native.WithPdxArray([.. chunk], units => Post(Org.NewArmySize, cmd => _newArmy(cmd, units, provider, 0)))) posted.Add(chunk);
            else Log.Info($"organize: the game refused an army of {chunk.Count} division(s)");
        }
        Log.Info($"organize: {selected.Count} division(s) into {posted.Count} new army(ies) of at most {cap}, {keep.Count} whole army(ies) kept, "
            + (parent != 0 ? "joining their army group" : "under a new army group"));
        if (posted.Count == 0 && keep.Count == 0) return;
        _organizing = new Organizing(posted, [.. byGroup.Where(g => g.Key != 0).Select(g => Ref(g.Key))], [.. keep.Select(Ref)],
            parent == 0 ? 0 : Ref(parent));
    }

    /// <summary>The next step once the last one's commands have run.</summary>
    private void Organize()
    {
        if (_organizing == null || !Game.HasStarted) return;
        _organizeGame.Check();
        var job = _organizing;
        if (job == null) return;
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        if (Environment.TickCount64 > job.Deadline)
        {
            Log.Info($"organize: gave up waiting for the game ({(job.Joining ? "army group" : "new armies")})");
            _organizing = null;
            return;
        }
        if (!job.Joining) Armies(job, country, tag);
        else Marshal(job, country, tag);
    }

    /// <summary>Once every new army exists: generals for them, then into the army group (or a new one).</summary>
    private void Armies(Organizing job, nint country, int tag)
    {
        var ours = Native.PdxArray(country + Org.Units).ToArray().ToHashSet();
        var armies = new List<nint>();
        foreach (var chunk in job.Chunks)
        {
            var groups = chunk.Where(ours.Contains).Select(u => *(nint*)(u + Org.Group)).Distinct().ToList();
            if (groups.Count == 0) continue; // all gone
            if (groups.Count > 1 || groups[0] == 0 || job.OldGroups.Contains(Ref(groups[0]))) return; // not run yet
            armies.Add(groups[0]);
        }
        var generals = IdleLeaders(country, tag, Org.General);
        var missing = 0;
        foreach (var army in armies.Where(a => *(nint*)(a + Org.Leader) == 0))
            if (generals.Count == 0 || !SetLeader(generals[0], army)) missing++;
            else generals.RemoveAt(0);
        if (missing > 0) Log.Info($"organize: no idle general for {missing} new army(ies)");

        armies.AddRange(job.Armies.Select(r => Group(country, r)).Where(g => g != 0));
        var target = job.ArmyGroup == 0 ? 0 : Group(country, job.ArmyGroup);
        if (target != 0 && new OrdersGroup(target).IsArmyGroup)
        {
            var join = armies.Where(a => *(nint*)(a + OrdersGroup.ParentOffset) != target).ToArray();
            if (join.Length > 0 && !Native.WithPdxArray(join, a => Post(Org.JoinSize, cmd => _joinArmyGroup(cmd, a, target, -1))))
                Log.Info("organize: the game refused to move the armies into the army group");
            else Log.Info($"organize: {armies.Count} army(ies) in the army group");
            _organizing = null;
            return;
        }
        if (job.ArmyGroup != 0) Log.Info("organize: their army group is gone (its armies were emptied), making a new one");
        job.ArmyGroup = 0;
        // Done once every army is in an army group it isn't in now: a new army may already sit in one.
        job.OldParents.UnionWith(armies.Select(a => *(nint*)(a + OrdersGroup.ParentOffset)).Where(p => p != 0).Select(Ref));
        job.Armies.Clear();
        foreach (var byProvider in armies.GroupBy(a => *(nint*)(a + Org.ProviderOfGroup)))
            if (Native.WithPdxArray([.. byProvider], a => Post(Org.NewArmyGroupSize, cmd => _newArmyGroup(cmd, a, byProvider.Key))))
                job.Armies.AddRange(byProvider.Select(Ref)); // a refused group's armies aren't waited for
            else Log.Info("organize: the game refused a new army group");
        if (job.Armies.Count == 0)
        {
            _organizing = null;
            return;
        }
        job.Joining = true;
        job.Deadline = Environment.TickCount64 + Org.TimeoutMs;
    }

    /// <summary>Once the new army group exists: the best idle field marshal for it.</summary>
    private void Marshal(Organizing job, nint country, int tag)
    {
        var parents = new List<nint>();
        foreach (var army in job.Armies.Select(r => Group(country, r)).Where(g => g != 0))
        {
            var parent = *(nint*)(army + OrdersGroup.ParentOffset);
            if (parent == 0 || job.OldParents.Contains(Ref(parent))) return; // not run yet
            parents.Add(parent);
        }
        var marshals = IdleLeaders(country, tag, Org.FieldMarshal);
        foreach (var group in parents.Distinct().Where(g => *(nint*)(g + Org.Leader) == 0))
            if (marshals.Count == 0 || !SetLeader(marshals[0], group)) Log.Info("organize: no idle field marshal for the new army group");
            else marshals.RemoveAt(0);
        Log.Info($"organize: {parents.Count} army(ies) in a new army group");
        _organizing = null;
    }

    /// <summary>Our land divisions in the player's selection (checked against the country's own list before anything else is read).</summary>
    private List<nint> SelectedDivisions(nint country, int tag)
    {
        var ui = *(nint*)_selectionUi;
        var result = new List<nint>();
        if (ui == 0) return result;
        var ours = Native.PdxArray(country + Org.Units).ToArray().ToHashSet();
        var node = *(nint*)(ui + _selectionOffset + Org.FirstNode);
        for (var n = 0; node != 0 && n < 5000; n++, node = *(nint*)(node + Org.NextOfNode))
        {
            var item = *(nint*)node;
            if (ours.Remove(item) && *(int*)(item + Org.Kind) == 0 && *(int*)(item + Org.Commander) == tag && *(nint*)(item + Org.Location) != 0)
                result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="units"/> in armies of at most <paramref name="cap"/>, as even as possible: starting from one end of
    /// them, each army takes the divisions nearest (in provinces) to the first one left.
    /// </summary>
    // ponytail: hop distance over the province graph (sea included), no real geometry.
    private static List<List<nint>> Split(List<nint> units, int cap)
    {
        var count = (units.Count + cap - 1) / cap;
        if (count == 0) return [];
        var size = (units.Count + count - 1) / count;
        var fromAny = Hops(units, ProvinceOf(units[0]));
        var fromEnd = Hops(units, ProvinceOf(units.MaxBy(u => Hop(fromAny, u))));
        var pool = units.OrderBy(u => Hop(fromEnd, u)).ToList();
        var chunks = new List<List<nint>>();
        while (pool.Count > 0)
        {
            var near = Hops(pool, ProvinceOf(pool[0]));
            var chunk = pool.OrderBy(u => Hop(near, u)).Take(size).ToHashSet();
            pool.RemoveAll(chunk.Contains);
            chunks.Add([.. chunk]);
        }
        return chunks;
    }

    private static int ProvinceOf(nint unit) => Game.Id(*(nint*)(unit + Org.Location));

    private static int Hop(Dictionary<int, int> hops, nint unit) => hops.GetValueOrDefault(ProvinceOf(unit), int.MaxValue);

    /// <summary>Provinces from <paramref name="from"/>, until every one of <paramref name="units"/>' provinces is reached.</summary>
    private static Dictionary<int, int> Hops(List<nint> units, int from)
    {
        var left = units.Select(ProvinceOf).ToHashSet();
        left.Remove(from);
        var hops = new Dictionary<int, int> { [from] = 0 };
        var queue = new Queue<int>([from]);
        while (left.Count > 0 && queue.TryDequeue(out var id))
        {
            var province = Game.Province(id);
            if (province == 0) continue;
            foreach (var next in Game.Neighbours(province))
                if (hops.TryAdd(next, hops[id] + 1))
                {
                    queue.Enqueue(next);
                    left.Remove(next);
                }
        }
        return hops;
    }

    /// <summary>The country's leaders of <paramref name="type"/> leading nothing, best first (skill, then attack + defense + planning + logistics).</summary>
    private static List<nint> IdleLeaders(nint country, int tag, int type)
    {
        var holder = *(nint*)(country + Org.LeaderHolder);
        if (holder == 0) return [];
        var busy = Groups(country).Select(g => *(nint*)(g + Org.Leader)).ToHashSet();
        return [.. Native.PdxArray(holder + Org.Leaders).ToArray()
            .Where(l => l != 0 && *(int*)(l + Org.LeaderTag) == tag && *(int*)(l + Org.LeaderType) == type && !busy.Contains(l))
            .OrderByDescending(Skill)
            .ThenByDescending(l => *(int*)(l + Org.Attack) + *(int*)(l + Org.Defense) + *(int*)(l + Org.Planning) + *(int*)(l + Org.Logistics))];
    }

    private static int Skill(nint leader)
    {
        var skill = *(nint*)(leader + Org.SkillLevel);
        return skill == 0 ? 0 : *(int*)(skill + Org.LevelOfSkill);
    }

    private static bool SetLeader(nint leader, nint group) => Post(Org.SetLeaderSize, cmd =>
    {
        *(long*)(cmd + 0x28) = *(long*)(leader + Org.SelfRef);
        *(long*)(cmd + 0x30) = *(long*)(group + Org.SelfRef);
        return cmd;
    }, Signatures.SetLeaderCommand);

    /// <summary>A command of <paramref name="size"/> made by <paramref name="make"/> (a game ctor, or filling a <paramref name="vtable"/> one), posted if the game takes it.</summary>
    private static bool Post(int size, Func<nint, nint> make, string? vtable = null)
    {
        var cmd = vtable != null ? Commands.New(vtable, size, 0, 0) : Allocator.AllocZeroed(size);
        return Commands.TryPost(make(cmd));
    }

    private static IEnumerable<nint> Groups(nint country)
    {
        foreach (var provider in Native.PdxArray(country + Org.Providers).ToArray())
        {
            if (provider == 0) continue;
            foreach (var group in Native.PdxArray(provider + Org.GroupsOfProvider).ToArray())
                if (group != 0) yield return group;
        }
    }

    /// <summary>The country's army or army group with CRef <paramref name="cref"/>, or 0.</summary>
    private static nint Group(nint country, long cref) => Groups(country).FirstOrDefault(g => Ref(g) == cref);

    private static long Ref(nint group) => *(long*)(group + Org.SelfRef);
}
