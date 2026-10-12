using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// Arrows on the map, in any map mode, made by the game's own map arrow manager in the supply map's style and tinted:
/// AutoTrade's trade routes, and with [Debug] AiActionArrows the AI plugins' front line actions (<see cref="Flash"/>).
/// The manager is caught from the game's own CreateArrow calls (select a unit or open a map mode with arrows), once per
/// game: a new map has a new manager and new arrows. Main thread only. Offsets for HOI4 1.19.3.
/// </summary>
public static unsafe class MapArrows
{
    /// <summary>CMapArrowManager::CreateArrow(manager, style name std::string*, CColor* colour, CColor* colour, {CColor,
    /// std::string x3}*, uint layer): a CMapArrowObject, or 0 when the manager holds no such style. Hooked to catch the
    /// manager: the game hands it over with every arrow it draws. From the entry: a hook further in runs mid-prologue.</summary>
    [Signature] public const string CreateArrowSig =
        "48 8B C4 48 89 58 10 4C 89 48 20 4C 89 40 18 48 89 48 08 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 90 00 00 00 0F 29 70 B8 4C 8B F2 48 63 81 AC 00 00 00";

    /// <summary>CMapArrowObject::AppendPoint(arrow, {i64 x, y}* point, char glyph, char head, int unused): the point is
    /// the province position scaled by 100000.</summary>
    [Signature] public const string AppendPointSig =
        "44 88 4C 24 20 44 88 44 24 18 48 89 54 24 10 55 56 41 54 41 55 48 81 EC A8 00 00 00 4C 63 51 14";

    /// <summary>CMapArrowObject::ClearPoints(arrow, char rebuild).</summary>
    [Signature] public const string ClearPointsSig =
        "40 53 48 83 EC 20 C7 41 14 00 00 00 00 48 8B D9 84 D2 74 1C E8 ? ? ? ? 48 8B CB E8";

    /// <summary>CMapArrowObject::RebuildGlyphs(arrow, char redraw).</summary>
    [Signature] public const string RebuildGlyphsSig =
        "48 8B C4 55 41 54 41 56 41 57 48 81 EC 88 00 00 00 4C 63 B1 8C 01 00 00 44 0F B6 FA 44 8B 61 14";

    /// <summary>CMapArrowObject::SetVisible(arrow, bool).</summary>
    [Signature] public const string SetVisibleSig =
        "48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 48 63 A9 8C 01 00 00 0F B6 F2 88 91 98 01 00 00 48";

    // A province's static data: {int x, y} at +0xB8 is the map arrow point's 1/100000 (the game's own arrows read it so, 0x141253BC0).
    private const int PositionOfStatic = 0xB8;
    private const int ColourSize = 32, LabelsSize = 96; // CreateArrow's colour block and its 3 label std::strings
    private const string Style = "supply_to_consumers";
    private const long FlashMs = 8000;
    private const int FlashArrows = 16;                 // per colour: past that the oldest one shown moves

    [Function(CallingConventions.Microsoft)]
    public delegate nint CreateFn(nint manager, nint name, nint colourA, nint colourB, nint labels, uint layer);

    private static delegate* unmanaged<nint, nint, byte, byte, int, char> _append;
    private static delegate* unmanaged<nint, byte, void> _clear, _rebuild, _visible;
    private static IHook<CreateFn>? _create;
    private static readonly Dictionary<string, nint> Names = [];   // style -> its std::string, kept: CreateArrow only reads it
    private static readonly byte[] ColourA = new byte[ColourSize], ColourB = new byte[ColourSize], Labels = new byte[LabelsSize];
    private static readonly HashSet<nint> Made = [];                // this map's arrows: others are gone with their map
    private static readonly Dictionary<int, List<(nint Arrow, long Until)>> Flashes = []; // by colour; Until 0: hidden
    private static readonly Logger Log = new("MapArrows");
    private static nint _manager;
    private static uint _layer;
    private static string _capturedName = "";
    private static int _start;

    /// <summary>[Debug] AiActionArrows: <see cref="Flash"/> draws.</summary>
    public static bool Enabled { get; internal set; }

    /// <summary>The map's arrow manager has been caught in this game: <see cref="Make"/> can make arrows.</summary>
    public static bool Ready => Current() && _manager != 0;

    internal static void Install()
    {
        _append = (delegate* unmanaged<nint, nint, byte, byte, int, char>)Sig.Find(AppendPointSig);
        _clear = (delegate* unmanaged<nint, byte, void>)Sig.Find(ClearPointsSig);
        _rebuild = (delegate* unmanaged<nint, byte, void>)Sig.Find(RebuildGlyphsSig);
        _visible = (delegate* unmanaged<nint, byte, void>)Sig.Find(SetVisibleSig);
        for (var i = 0; i < 3; i++) BitConverter.GetBytes(15L).CopyTo(Labels, 0x20 * i + 0x18); // three empty inline strings: no labels
        _create = Patches.Hook<CreateFn>(Sig.Find(CreateArrowSig), (manager, name, colourA, colourB, labels, layer) =>
        {
            // Not before this game's start is seen: the reset that follows would drop the catch.
            if (_manager == 0 && Game.HasStarted && _start == Game.Starts) Log.Guard("catch", () => Capture(manager, name, colourA, colourB, layer));
            return _create!.OriginalFunction(manager, name, colourA, colourB, labels, layer);
        });
        MainThread.Every(TimeSpan.FromMilliseconds(250), Expire);
    }

    /// <summary>The game calls this with every arrow it draws: the first call in a game hands us the manager and the colours.</summary>
    private static void Capture(nint manager, nint name, nint colourA, nint colourB, uint layer)
    {
        _layer = layer;
        _capturedName = Native.Utf8(Native.MsvcString(name));
        Marshal.Copy(colourA, ColourA, 0, ColourSize); // the same value objects, retinted per arrow
        Marshal.Copy(colourB, ColourB, 0, ColourSize);
        _manager = manager; // last: a non-zero manager means everything is caught
        Log.Info($"caught the map's arrow manager (style seen: \"{_capturedName}\")");
    }

    /// <summary>A game runs; a new one since the last call forgets the old map's manager and arrows.</summary>
    private static bool Current()
    {
        if (_start != Game.Starts)
        {
            _start = Game.Starts;
            _manager = 0;
            _capturedName = "";
            Made.Clear();
            Flashes.Clear();
        }
        return Game.HasStarted;
    }

    /// <summary>
    /// A new arrow in the supply map's style tinted (<paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/>),
    /// hidden until <see cref="Draw"/>; 0 before <see cref="Ready"/> or when the map has no such style. It lives as long as
    /// the game: keep and reuse it.
    /// </summary>
    public static nint Make(byte r, byte g, byte b)
    {
        if (!Ready) return 0;
        var arrow = Create(Style, r, g, b);
        // The manager holds the styles of the current map mode's .gfx, so an unknown style can fall back to a captured name,
        // but only a supply one: the game draws its fronts through CreateArrow too, and reusing e.g. "defence_line" painted
        // a huge dark band over the map.
        if (arrow == 0 && _capturedName.Contains("supply") && _capturedName != Style)
        {
            Log.Info($"style \"{Style}\" not found, trying \"{_capturedName}\"");
            arrow = Create(_capturedName, r, g, b);
        }
        if (arrow != 0) Made.Add(arrow);
        return arrow;
    }

    private static nint Create(string style, byte r, byte g, byte b)
    {
        if (!Names.TryGetValue(style, out var name))
        {
            Names[style] = name = Allocator.AllocZeroed(32); // an empty std::string, then the name (heap buffer past 15 chars)
            Native.SetMsvcString(name, style);
        }
        var colourA = Tint(ColourA, r, g, b);
        var colourB = Tint(ColourB, r, g, b);
        fixed (byte* a = colourA, c = colourB, l = Labels)
            return _create!.OriginalFunction(_manager, name, (nint)a, (nint)c, (nint)l, _layer);
    }

    /// <summary>A copy of the CColor, a plain value object with float r, g, b at +0x10, in the given colour.</summary>
    private static byte[] Tint(byte[] colour, byte r, byte g, byte b)
    {
        var tinted = (byte[])colour.Clone();
        BitConverter.GetBytes(r / 255f).CopyTo(tinted, 0x10);
        BitConverter.GetBytes(g / 255f).CopyTo(tinted, 0x14);
        BitConverter.GetBytes(b / 255f).CopyTo(tinted, 0x18);
        return tinted;
    }

    /// <summary>Shows <paramref name="arrow"/> along <paramref name="points"/> (map positions, as <see cref="Position"/>
    /// gives), its head on the last one. Ignored for an arrow of an earlier game.</summary>
    public static void Draw(nint arrow, IReadOnlyList<(int X, int Y)> points)
    {
        if (!Current() || !Made.Contains(arrow)) return;
        _clear(arrow, 0);
        var point = stackalloc long[2];
        for (var i = 0; i < points.Count; i++)
        {
            point[0] = points[i].X * 100_000L;
            point[1] = points[i].Y * 100_000L;
            _append(arrow, (nint)point, 0, (byte)(i == points.Count - 1 ? 1 : 0), -1);
        }
        _rebuild(arrow, 1);
        _visible(arrow, 1);
    }

    /// <summary>Hides <paramref name="arrow"/>. Ignored for an arrow of an earlier game.</summary>
    public static void Hide(nint arrow)
    {
        if (Current() && Made.Contains(arrow)) _visible(arrow, 0);
    }

    /// <summary>The province's position on the map (its static data's), (0, 0) without static data.</summary>
    public static (int X, int Y) Position(nint province)
    {
        var info = *(nint*)(province + Province.StaticOffset);
        return info == 0 ? (0, 0) : (*(int*)(info + PositionOfStatic), *(int*)(info + PositionOfStatic + 4));
    }

    /// <summary>
    /// With [Debug] AiActionArrows on: an arrow from one province (id) to another in the given colour for about 8 seconds.
    /// Arrows are reused, up to 16 per colour shown at once (more moves the oldest). A no-op before <see cref="Ready"/>.
    /// </summary>
    public static void Flash(int fromProvince, int toProvince, byte r, byte g, byte b)
    {
        if (!Enabled || !Ready) return;
        var from = Game.Province(fromProvince);
        var to = Game.Province(toProvince);
        if (from == 0 || to == 0) return;
        var colour = r << 16 | g << 8 | b;
        if (!Flashes.TryGetValue(colour, out var pool)) Flashes[colour] = pool = [];
        var slot = pool.FindIndex(f => f.Until == 0);
        if (slot < 0 && pool.Count < FlashArrows)
        {
            var made = Make(r, g, b);
            if (made == 0) return;
            pool.Add((made, 0));
            slot = pool.Count - 1;
        }
        if (slot < 0) slot = pool.IndexOf(pool.MinBy(f => f.Until));
        Draw(pool[slot].Arrow, [Position(from), Position(to)]);
        pool[slot] = (pool[slot].Arrow, Environment.TickCount64 + FlashMs);
    }

    private static void Expire()
    {
        if (!Current()) return;
        var now = Environment.TickCount64;
        foreach (var pool in Flashes.Values)
        {
            for (var i = 0; i < pool.Count; i++)
            {
                if (pool[i].Until == 0 || now < pool[i].Until) continue;
                _visible(pool[i].Arrow, 0);
                pool[i] = (pool[i].Arrow, 0);
            }
        }
    }
}
