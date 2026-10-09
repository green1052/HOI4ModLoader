using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HOI4ModLoader;

/// <summary>The running game: version, game state, the player's country. Offsets for HOI4 1.19.3.</summary>
public static unsafe class Game
{
    /// <summary>launcher-settings.json "rawVersion", e.g. "1.19.3.0".</summary>
    public static readonly string Version = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "launcher-settings.json"))).RootElement.GetProperty("rawVersion").GetString()!;

    /// <summary>
    /// Country by tag: `mov rdi,[rip+GameState]; ...; call TagIndex; ...; mov rax,[rdi+Countries]; mov rax,[rax+rcx*8]`.
    /// Twice: the const and non-const getter, both identical.
    /// </summary>
    [Signature(2)] public const string CountryLookupSig = "83 3B 00 48 8B 3D ?? ?? ?? ?? 75 04 33 C0 EB 08 48 8B CB E8 ?? ?? ?? ?? 48 8B 5C 24 48 48 63 C8 48 8B 87 ?? ?? ?? ?? 48 8B 04 C8";

    const int PlayerTagOffset = 0x520, ObserverTagOffset = 0x524; // gamestate: int country tags
    const int ServerOfSession = 88;                                // CSession -> CServer*

    static nint* _state;
    static delegate* unmanaged<nint, int> _tagIndex;
    static int _countries;

    static void Resolve()
    {
        if (_state != null) return;
        var at = Sig.FindAll(CountryLookupSig, 2)[0];
        _tagIndex = (delegate* unmanaged<nint, int>)(at + 24 + *(int*)(at + 20));
        _countries = *(int*)(at + 35);
        _state = (nint*)(at + 10 + *(int*)(at + 6));
    }

    /// <summary>CGameState, or 0 before a game is loaded.</summary>
    public static nint State
    {
        get { Resolve(); return *_state; }
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

    /// <summary>CCountry for <paramref name="tag"/>, or 0.</summary>
    public static nint Country(int tag)
    {
        var gs = State;
        return gs == 0 || tag <= 0 ? 0 : *(nint*)(*(nint*)(gs + _countries) + 8L * _tagIndex((nint)(&tag)));
    }
}

/// <summary>Readers for engine containers.</summary>
public static unsafe class Native
{
    /// <summary>CPdxArray&lt;T*&gt; at <paramref name="at"/>: { T** data; int capacity; int size; }.</summary>
    public static ReadOnlySpan<nint> PdxArray(nint at) => new((void*)*(nint*)at, *(int*)(at + 0xC));

    /// <summary>Characters of an MSVC std::string at <paramref name="at"/> (inline buffer when capacity &lt; 16).</summary>
    public static Span<byte> MsvcString(nint at)
    {
        var size = *(long*)(at + 0x10);
        var data = *(long*)(at + 0x18) < 16 ? (byte*)at : *(byte**)at;
        return new Span<byte>(data, (int)size);
    }

    /// <summary>
    /// Replaces the contents of the MSVC std::string at <paramref name="at"/>. Heap buffers come from and go back to the
    /// CRT heap, the same process heap the game's statically linked CRT uses.
    /// </summary>
    public static void SetMsvcString(nint at, string value) => SetMsvcString(at, Encoding.UTF8.GetBytes(value));

    /// <inheritdoc cref="SetMsvcString(nint, string)"/>
    public static void SetMsvcString(nint at, ReadOnlySpan<byte> value)
    {
        var bytes = value.ToArray(); // value may point into the buffer freed below
        if (bytes.Length >= 0xFFF) throw new ArgumentException("too long: MSVC over-aligns buffers of 4 KB and more"); // and frees them differently
        if (*(long*)(at + 0x18) >= 16) NativeMemory.Free(*(void**)at);
        var data = bytes.Length < 16 ? (byte*)at : (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        if (data != (byte*)at) *(byte**)at = data;
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        data[bytes.Length] = 0;
        *(long*)(at + 0x10) = bytes.Length;
        *(long*)(at + 0x18) = Math.Max(15, bytes.Length);
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
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
        var merged = new List<nint>(Native.PdxArray(Members).ToArray());
        foreach (var army in Armies) merged.AddRange(Native.PdxArray(new OrdersGroup(army).Members).ToArray());
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
