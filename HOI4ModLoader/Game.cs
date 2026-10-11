using System.Text;
using System.Text.Json;

namespace HOI4ModLoader;

/// <summary>The running game: version, game state, the player's country. Offsets for HOI4 1.19.3.</summary>
public static unsafe class Game
{
    /// <summary>launcher-settings.json "rawVersion", e.g. "1.19.3.0"; "unknown" without one (plugins made for a version then skip).</summary>
    public static readonly string Version = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "launcher-settings.json")));
            return settings.RootElement.GetProperty("rawVersion").GetString() ?? "unknown";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "unknown"; // a type initializer that throws would take every Game member (and the loader's startup) down with it
        }
    }

    /// <summary>
    /// Country by tag: `mov rdi,[rip+GameState]; ...; call TagIndex; ...; mov rax,[rdi+Countries]; mov rax,[rax+rcx*8]`.
    /// Twice: the const and non-const getter, both identical.
    /// </summary>
    [Signature(2)] public const string CountryLookupSig = "83 3B 00 48 8B 3D ?? ?? ?? ?? 75 04 33 C0 EB 08 48 8B CB E8 ?? ?? ?? ?? 48 8B 5C 24 48 48 63 C8 48 8B 87 ?? ?? ?? ?? 48 8B 04 C8";

    private const int PlayerTagOffset = 0x520, ObserverTagOffset = 0x524; // gamestate: int country tags
    private const int ServerOfSession = 88;                                // CSession -> CServer*

    private static nint* _state;
    private static delegate* unmanaged<nint, int> _tagIndex;
    private static int _countries;

    private static void Resolve()
    {
        if (_state != null) return;
        var at = Sig.FindAll(CountryLookupSig, 2)[0];
        _tagIndex = (delegate* unmanaged<nint, int>)(at + 24 + *(int*)(at + 20));
        _countries = *(int*)(at + 35);
        _state = (nint*)(at + 10 + *(int*)(at + 6));
    }

    /// <summary>CGameState, or 0 before the game application is up. One object for the whole process: loading a save
    /// refills it, so it says nothing about which game runs (see <see cref="Generation"/>).</summary>
    public static nint State
    {
        get { Resolve(); return *_state; }
    }

    private const int StartedOfGameState = 0xA39; // byte: a game runs (SetGameStarted 0x1401EDFF0 clears it in menus and while loading)

    /// <summary>A game is running: false in the menus, the lobby and while a save loads.</summary>
    public static bool HasStarted => State is var gs && gs != 0 && *(byte*)(gs + StartedOfGameState) != 0;

    private static (bool Started, int Tag) _seen;

    /// <summary>
    /// Counts game changes: a start (a save loaded, a new game) or a change of the controlled country. State caches of
    /// game objects belong to one generation (<see cref="PerGame"/>). Advanced on the main thread every frame.
    /// </summary>
    public static int Generation { get; private set; }

    internal static void Track()
    {
        var now = (HasStarted, ControlledTag);
        if (now == _seen) return;
        _seen = now;
        Generation++;
    }

    /// <summary>The player's country tag, or 0 (main menu, no country).</summary>
    public static int PlayerTag
    {
        get
        {
            var gs = State;
            if (gs == 0) return 0;
            var tag = *(int*)(gs + PlayerTagOffset);
            return tag > 0 ? tag : *(int*)(gs + ObserverTagOffset);
        }
    }

    /// <summary>
    /// In a multiplayer lobby or game: the session's server is a network one (host) or a proxy of one (client). The game's
    /// own telemetry tells "multiplayer" from single player the same way.
    /// </summary>
    public static bool IsMultiplayer
    {
        get
        {
            var session = Commands.Session;
            var server = session == 0 ? 0 : *(nint*)(session + ServerOfSession);
            return server != 0 && (Image.Game.IsA(server, "CNetworkServer") || Image.Game.IsA(server, "CProxyServer"));
        }
    }

    /// <summary>
    /// The country the player controls, or 0 (main menu, observer): what commands must be sent for. <see cref="PlayerTag"/>
    /// also gives an observer's country, for showing things.
    /// </summary>
    public static int ControlledTag
    {
        get
        {
            var gs = State;
            return gs == 0 ? 0 : Math.Max(0, *(int*)(gs + PlayerTagOffset));
        }
    }

    /// <summary>CCountry for <paramref name="tag"/>, or 0.</summary>
    public static nint Country(int tag)
    {
        if (tag <= 0) return 0;
        var countries = Countries;
        var index = countries.IsEmpty ? -1 : TagIndex(tag); // a tag from another game (a stale cache) may index past the end
        return (uint)index < (uint)countries.Length ? countries[index] : 0;
    }

    /// <summary>Index of <paramref name="tag"/> into the game's per-country arrays (what the game pairs with a tag as a TagIdx).</summary>
    public static int TagIndex(int tag)
    {
        Resolve();
        return _tagIndex((nint)(&tag));
    }

    /// <summary>Every CCountry, empty before a game is loaded.</summary>
    public static ReadOnlySpan<nint> Countries => State == 0 ? default : Native.PdxArray(State + _countries);

    private const int ProvincesOfGameState = 8;                              // province container: vtable slot 0 = by id
    private const int AdjacencyOfProvince = 0xB8, Adjacencies = 0x70, AdjacencySize = 0x30, NeighbourId = 8, TypeOfAdjacency = 0x10;

    /// <summary>CProvince by id (+164 id, +0xC0 state, +0xC8 strategic region, +0x188 controller tag, +400 buildings), or 0.</summary>
    public static nint Province(int id)
    {
        var gs = State;
        if (gs == 0) return 0;
        var provinces = gs + ProvincesOfGameState;
        return ((delegate* unmanaged<nint, int, nint>)(**(nint**)provinces))(provinces, id);
    }

    public const int IdOfProvince = 0xA4;

    /// <summary>The province's id (what <see cref="Province"/> and <see cref="Neighbours"/> take and give).</summary>
    public static int Id(nint province) => *(int*)(province + IdOfProvince);

    /// <summary>The adjacency from <paramref name="from"/> to its neighbour <paramref name="to"/> (an id): 0 normal, 1 strait,
    /// 2 river, 3 large river, 4 impassable; 0 when they aren't neighbours.</summary>
    public static int AdjacencyType(nint from, int to)
    {
        var (data, count) = AdjacencyList(from);
        for (var i = 0; i < count; i++)
            if (*(int*)(data + i * AdjacencySize + NeighbourId) == to) return *(byte*)(data + i * AdjacencySize + TypeOfAdjacency);
        return 0;
    }

    /// <summary>Ids of the provinces next to <paramref name="province"/> (land and sea).</summary>
    public static List<int> Neighbours(nint province)
    {
        var (data, count) = AdjacencyList(province);
        var ids = new List<int>(count);
        for (var i = 0; i < count; i++) ids.Add(*(int*)(data + i * AdjacencySize + NeighbourId));
        return ids;
    }

    /// <summary>The province's adjacency entries (AdjacencySize bytes each), none when it has no adjacency data.</summary>
    private static (nint Data, int Count) AdjacencyList(nint province)
    {
        var adjacency = *(nint*)(province + AdjacencyOfProvince);
        return adjacency == 0 ? (0, 0) : (*(nint*)(adjacency + Adjacencies), *(int*)(adjacency + Adjacencies + 0xC));
    }
}

/// <summary>Readers for engine containers.</summary>
public static unsafe class Native
{
    /// <summary>The engine's fixed-point scale: 1.0 is 100000 (resources, scores, progress).</summary>
    public const long FixedPoint = 100_000;

    /// <summary>CPdxArray&lt;T*&gt; at <paramref name="at"/>: { T** data; int capacity; int size; }.</summary>
    public static ReadOnlySpan<nint> PdxArray(nint at) => new((void*)*(nint*)at, *(int*)(at + 0xC));

    /// <summary>CPdxArray&lt;T&gt; of values (ints, longs, ...) at <paramref name="at"/>, writable in place.</summary>
    public static Span<T> PdxArray<T>(nint at) where T : unmanaged => new((void*)*(nint*)at, *(int*)(at + 0xC));

    /// <summary>Shrinks the CPdxArray at <paramref name="at"/> to its first <paramref name="size"/> elements (no free, no move).</summary>
    public static void SetPdxArraySize(nint at, int size) => *(int*)(at + 0xC) = size;

    /// <summary>
    /// Calls <paramref name="use"/> with a CPdxArray header viewing <paramref name="items"/> (data +0, capacity +8, size +0xC),
    /// for game functions that only read the array passed to them.
    /// </summary>
    public static T WithPdxArray<T>(nint[] items, Func<nint, T> use)
    {
        var header = stackalloc nint[3];
        fixed (nint* data = items)
        {
            header[0] = (nint)data;
            ((int*)header)[2] = ((int*)header)[3] = items.Length;
            return use((nint)header);
        }
    }

    /// <summary>Characters of an MSVC std::string at <paramref name="at"/> (inline buffer when capacity &lt; 16).</summary>
    public static Span<byte> MsvcString(nint at)
    {
        var size = *(long*)(at + 0x10);
        var data = *(long*)(at + 0x18) < 16 ? (byte*)at : *(byte**)at;
        return new Span<byte>(data, (int)size);
    }

    /// <summary>
    /// Replaces the contents of the MSVC std::string at <paramref name="at"/>. Heap buffers come from and go back to the
    /// game's own heap (<see cref="Allocator"/>), like the game's strings.
    /// </summary>
    public static void SetMsvcString(nint at, string value) => SetMsvcString(at, Encoding.UTF8.GetBytes(value));

    /// <inheritdoc cref="SetMsvcString(nint, string)"/>
    public static void SetMsvcString(nint at, ReadOnlySpan<byte> value)
    {
        var bytes = value.ToArray(); // value may point into the buffer freed below
        if (bytes.Length >= BigBuffer - 1) throw new ArgumentException("too long: MSVC over-aligns buffers of 4 KB and more");
        FreeMsvcString(at);
        var data = bytes.Length < 16 ? (byte*)at : (byte*)Allocator.Alloc(bytes.Length + 1);
        if (data != (byte*)at) *(byte**)at = data;
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        data[bytes.Length] = 0;
        *(long*)(at + 0x10) = bytes.Length;
        *(long*)(at + 0x18) = Math.Max(15, bytes.Length);
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private const int BigBuffer = 0x1000; // MSVC allocates std::string buffers this big 32-byte aligned, the real block's pointer just before

    /// <summary>Frees the heap buffer of the MSVC std::string at <paramref name="at"/> (none when inline), leaving it dangling.</summary>
    internal static void FreeMsvcString(nint at)
    {
        var capacity = *(long*)(at + 0x18);
        if (capacity < 16) return;
        var buffer = *(nint*)at;
        Allocator.Free(capacity + 1 >= BigBuffer ? ((nint*)buffer)[-1] : buffer);
    }
}

/// <summary>COrdersGroup (hoi4/source/ordersgroup.cpp): an army or an army group. Offsets for HOI4 1.19.3.</summary>
public readonly unsafe struct OrdersGroup(nint ptr)
{
    public const int IsArmyGroupOffset = 0x39;
    public const int MembersOffset = 0x50;         // CPdxArray<COrdersGroupMember*>: its divisions (UngroupAllMembers walks it)
    public const int AttachedWingsOffset = 0x70;   // CPdxArray<CAirWing*>
    public const int ParentOffset = 0x1B8;         // army -> its army group
    public const int ArmiesOffset = 0x230;         // CPdxArray<COrdersGroup*>: armies of an army group

    public nint Ptr { get; } = ptr;
    public bool IsArmyGroup => *(byte*)(Ptr + IsArmyGroupOffset) != 0;
    public nint Members => Ptr + MembersOffset;
    public int AttachedWingCount => *(int*)(Ptr + AttachedWingsOffset + 0xC);
    public ReadOnlySpan<nint> Armies => Native.PdxArray(Ptr + ArmiesOffset);

    /// <summary>
    /// Runs <paramref name="action"/> while this army group's member list also holds its armies' divisions. Army groups own
    /// no divisions themselves, so game logic that looks at a group's members finds nothing for them; this lets it see the
    /// whole army group for one call. Keeps the game's order (multiplayer is lockstep: every client must see the same sequence).
    /// </summary>
    public void WithArmiesMembers(Action action)
    {
        List<nint> merged = [.. Native.PdxArray(Members)];
        foreach (var army in Armies) merged.AddRange(Native.PdxArray(new OrdersGroup(army).Members));
        var members = merged.Distinct().ToArray();

        var saved = (*(nint*)Members, *(long*)(Members + 8)); // data, capacity|size
        fixed (nint* data = members)
        {
            *(nint*)Members = (nint)data;
            *(int*)(Members + 8) = *(int*)(Members + 0xC) = members.Length;
            try { action(); }
            finally { (*(nint*)Members, *(long*)(Members + 8)) = saved; }
        }
    }
}

/// <summary>
/// A plugin's state that belongs to one game and country (queued items, targets, pointers into game data):
/// <see cref="Check"/> runs the reset when either changed since the last check. Call it before using that state.
/// </summary>
public sealed class PerGame(Action reset)
{
    private int _generation = -1;

    public void Check()
    {
        if (Game.Generation == _generation) return;
        _generation = Game.Generation;
        reset();
    }
}

/// <summary>An army card: the leader cards in the army list, one per army or army group. Offsets for HOI4 1.19.3.</summary>
public static unsafe class ArmyCard
{
    /// <summary>Right-click on a card (card): attaches selected air wings or assigns selected railway guns to its group, among others.</summary>
    [Signature] public const string RightClickSig = "48 8B C4 48 89 58 ? 48 89 70 ? 48 89 78 ? 55 41 54 41 55 41 56 41 57 48 8D 68 ? 48 81 EC B0 00 00 00 0F 29 70 ? 4C 8B E9 48 83 C1 30";

    /// <summary>Refreshes the card's attached-wing slots (card, group): `cmp byte [rcx+IsArmyGroup],0; ...; jnz skip; mov rcx,[rcx+WingGrid]`.</summary>
    [Signature] public const string RefreshWingSlotsSig = "48 89 5C 24 ? 57 48 83 EC 30 80 B9 ? ? ? ? 00 48 8B DA 48 8B F9 0F 85 ? ? ? ? 48 8B 89 ? ? ? ?";

    private static int _isArmyGroup;

    /// <summary>Offset of the card's "this card is an army group's" byte, as the slot refresh instruction names it.</summary>
    public static int IsArmyGroupOffset => _isArmyGroup != 0 ? _isArmyGroup : _isArmyGroup = *(int*)(Sig.Find(RefreshWingSlotsSig) + 12);

    /// <summary>
    /// Runs <paramref name="original"/> with the card treated as an army card when <paramref name="when"/>: the card UI skips
    /// army-group cards for attached wings and railway guns. UI thread only, so nothing else sees the flip.
    /// </summary>
    public static void AsArmyCard(nint card, bool when, Action original)
    {
        var flag = (byte*)(card + IsArmyGroupOffset);
        var was = *flag;
        if (when) *flag = 0;
        try { original(); }
        finally { *flag = was; }
    }

    /// <summary>
    /// <paramref name="items"/> dealt round-robin into <paramref name="groups"/> shares (fewer if there are fewer items), in
    /// order: share i gets items i, i + groups, ...
    /// </summary>
    // ponytail: equal shares, not weighted by army size or front; weigh them if that matters in play.
    public static nint[][] Spread(nint[] items, int groups) =>
        [.. Enumerable.Range(0, Math.Min(groups, items.Length)).Select(g => items.Where((_, i) => i % groups == g).ToArray())];
}
