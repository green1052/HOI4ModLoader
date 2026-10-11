using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AutoTrade;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class OverlaySignatures
{
    /// <summary>CMapArrowManager::CreateArrow(manager, style name std::string*, CColor* colour, CColor* colour, {CColor,
    /// std::string x3}*, uint layer): a CMapArrowObject, or 0 when the manager holds no such style. Hooked to catch the
    /// manager: the game hands it over with every arrow it draws. From the entry: a hook further in runs mid-prologue.</summary>
    [Signature] public const string CreateArrow =
        "48 8B C4 48 89 58 10 4C 89 48 20 4C 89 40 18 48 89 48 08 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 90 00 00 00 0F 29 70 B8 4C 8B F2 48 63 81 AC 00 00 00";

    /// <summary>CMapArrowObject::AppendPoint(arrow, {i64 x, y}* point, char glyph, char head, int unused): the point is
    /// the province position scaled by 100000.</summary>
    [Signature] public const string AppendPoint =
        "44 88 4C 24 20 44 88 44 24 18 48 89 54 24 10 55 56 41 54 41 55 48 81 EC A8 00 00 00 4C 63 51 14";

    /// <summary>CMapArrowObject::ClearPoints(arrow, char rebuild).</summary>
    [Signature] public const string ClearPoints =
        "40 53 48 83 EC 20 C7 41 14 00 00 00 00 48 8B D9 84 D2 74 1C E8 ? ? ? ? 48 8B CB E8";

    /// <summary>CMapArrowObject::RebuildGlyphs(arrow, char redraw).</summary>
    [Signature] public const string RebuildGlyphs =
        "48 8B C4 55 41 54 41 56 41 57 48 81 EC 88 00 00 00 4C 63 B1 8C 01 00 00 44 0F B6 FA 44 8B 61 14";

    /// <summary>CMapArrowObject::SetVisible(arrow, bool).</summary>
    [Signature] public const string SetVisible =
        "48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 48 63 A9 8C 01 00 00 0F B6 F2 88 91 98 01 00 00 48";
}

/// <summary>
/// Ctrl+Alt+C draws our trade routes (imports and exports, over land and sea) in gold, in any map mode, with the supply
/// map's arrow style. Read-only, so other players need nothing. One arrow per route: an arrow is one polyline with a head
/// at its last point, so routes sharing one would be joined into a zig-zag.
/// </summary>
public unsafe partial class AutoTrade
{
    // Layouts for HOI4 1.19.3 (the map arrow manager's CreateArrow calling convention).
    // CProvince+0xB8 -> its static data, whose {int x, y} at +0xB8 is the map arrow point's 1/100000 (the game's own arrows
    // read it so, 0x141253BC0).
    private const int StaticOfProvince = 0xB8, PositionOfStatic = 0xB8;
    private const int RoutesOfResources = 0x788;   // CCountryResources: CResourceDeliveryRoute* per partner tag index; empty slots are 0
    private const int KindOfRoute = 8, TagAOfRoute = 48, TagBOfRoute = 52; // route: kind (0, 3: no live route), the two trading tags
    private const int StatesOfRoute = 72, RegionsOfRoute = 96; // route: the states (land) / regions (sea) it passes
    private const int ProvincesOfHolder = 24;      // CState and CStrategicRegion: CPdxArray<CProvince*>
    private const int ColourSize = 32, LabelsSize = 96; // CreateArrow's colour block and its 3 label std::strings
    private const string Style = "supply_to_consumers";
    private const long RedrawMs = 5000;

    [Function(CallingConventions.Microsoft)]
    public delegate nint CreateFn(nint manager, nint name, nint colourA, nint colourB, nint labels, uint layer);

    private delegate* unmanaged<nint, nint, byte, byte, int, char> _append;
    private delegate* unmanaged<nint, byte, void> _clear, _rebuild, _visible;
    private IHook<CreateFn>? _create;
    private readonly Dictionary<string, nint> _names = []; // style -> its std::string, kept: CreateArrow only reads it
    private readonly byte[] _colourA = new byte[ColourSize], _colourB = new byte[ColourSize], _labels = new byte[LabelsSize];
    private readonly List<nint> _arrows = [];               // one per route drawn, made as needed
    private nint _manager;
    private uint _layer;
    private string _capturedName = "";
    private PerGame _map = null!;
    private long _drawn;
    private bool _on, _down, _reported;

    private void LoadOverlay()
    {
        _append = (delegate* unmanaged<nint, nint, byte, byte, int, char>)Sig.Find(OverlaySignatures.AppendPoint);
        _clear = (delegate* unmanaged<nint, byte, void>)Sig.Find(OverlaySignatures.ClearPoints);
        _rebuild = (delegate* unmanaged<nint, byte, void>)Sig.Find(OverlaySignatures.RebuildGlyphs);
        _visible = (delegate* unmanaged<nint, byte, void>)Sig.Find(OverlaySignatures.SetVisible);
        _create = Patches.Hook<CreateFn>(Sig.Find(OverlaySignatures.CreateArrow), (manager, name, colourA, colourB, labels, layer) =>
        {
            if (_manager == 0) Capture(manager, name, colourA, colourB, layer);
            return _create!.OriginalFunction(manager, name, colourA, colourB, labels, layer);
        });
        _map = new PerGame(() => // a new map: its manager and arrows are new objects
        {
            _manager = 0;
            _arrows.Clear();
            _capturedName = "";
        });
        MainThread.Every(TimeSpan.FromMilliseconds(30), Poll);
    }

    /// <summary>The game calls this with every arrow it draws: the first plain call hands us the manager and the colours.</summary>
    private void Capture(nint manager, nint name, nint colourA, nint colourB, uint layer)
    {
        _layer = layer;
        _capturedName = Native.Utf8(Native.MsvcString(name));
        Marshal.Copy(colourA, _colourA, 0, ColourSize); // the same value objects, retinted: trade arrows get gold
        Marshal.Copy(colourB, _colourB, 0, ColourSize);
        Tint(_colourA);
        Tint(_colourB);
        for (var i = 0; i < 3; i++) BitConverter.GetBytes(15L).CopyTo(_labels, 0x20 * i + 0x18); // three empty inline strings: no labels
        _manager = manager; // last: Poll takes a non-zero manager as "everything caught"
        Log.Info($"trade overlay: caught the map's arrow manager (style seen: \"{_capturedName}\")");
    }

    /// <summary>The CColor is a plain value object with float r, g, b at +0x10: overwriting them retints a copy.</summary>
    private static void Tint(byte[] colour)
    {
        BitConverter.GetBytes(1f).CopyTo(colour, 0x10);
        BitConverter.GetBytes(0.8f).CopyTo(colour, 0x14);
        BitConverter.GetBytes(0.15f).CopyTo(colour, 0x18);
    }

    private void Poll()
    {
        _map.Check();
        var down = Gui.KeyHeld('C');
        if (Gui.CtrlHeld && Gui.AltHeld && Gui.GameFocused && down && !_down)
        {
            _on = !_on;
            _drawn = 0; // on: draw now, not on the next refresh
            if (!_on) foreach (var arrow in _arrows) _visible(arrow, 0);
        }
        _down = down;
        if (!_on || !Game.HasStarted || Game.ControlledTag == 0) return;
        if (_manager == 0)
        {
            if (!_reported) Log.Info("trade overlay: the map's arrow manager is not caught yet; select a unit or open a map mode with arrows, then toggle again");
            _reported = true;
            return;
        }
        var now = Environment.TickCount64;
        if (now - _drawn < RedrawMs) return;
        _drawn = now;
        Draw();
    }

    /// <summary>An arrow along each of our country's trade routes: over the land states it crosses, then the sea regions.</summary>
    private void Draw()
    {
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        var resources = country == 0 ? 0 : *(nint*)(country + ResourcesOfCountry);
        if (resources == 0) return;
        var used = 0;
        foreach (var route in Native.PdxArray(resources + RoutesOfResources).ToArray())
        {
            if (route == 0 || *(byte*)(route + KindOfRoute) is 0 or 3) continue;
            if (*(int*)(route + TagAOfRoute) != tag && *(int*)(route + TagBOfRoute) != tag) continue; // only ours
            var points = new List<(int X, int Y)>();
            Waypoints(route + StatesOfRoute, points);   // over land
            Waypoints(route + RegionsOfRoute, points);  // over sea
            if (points.Count < 2) continue;
            if (used == _arrows.Count)
            {
                var made = MakeArrow();
                if (made == 0) break;
                _arrows.Add(made);
            }
            var arrow = _arrows[used++];
            _clear(arrow, 0);
            for (var i = 0; i < points.Count; i++) PointAt(arrow, points[i].X, points[i].Y, i == points.Count - 1);
            _rebuild(arrow, 1);
            _visible(arrow, 1);
        }
        foreach (var spare in _arrows.Skip(used)) _visible(spare, 0); // routes gone since the last draw
    }

    /// <summary>One arrow in the supply map's own style, or 0. The manager holds the styles of the current map mode's
    /// .gfx, so an unknown style can fall back to a captured name — but only a supply one: the game draws its fronts
    /// through CreateArrow too, and reusing e.g. "defence_line" painted a huge dark band over the map.</summary>
    private nint MakeArrow()
    {
        var arrow = Create(Style);
        if (arrow != 0 || !_capturedName.Contains("supply") || _capturedName == Style) return arrow;
        Log.Info($"trade overlay: style \"{Style}\" not found, trying \"{_capturedName}\"");
        return Create(_capturedName);
    }

    private nint Create(string style)
    {
        if (!_names.TryGetValue(style, out var name))
        {
            _names[style] = name = Allocator.AllocZeroed(32); // an empty std::string, then the name (heap buffer past 15 chars)
            Native.SetMsvcString(name, style);
        }
        fixed (byte* a = _colourA, b = _colourB, l = _labels)
            return _create!.OriginalFunction(_manager, name, (nint)a, (nint)b, (nint)l, _layer);
    }

    /// <summary>The centre of each state or strategic region the route passes through, in the route's own order.</summary>
    private static void Waypoints(nint holders, List<(int X, int Y)> points)
    {
        foreach (var holder in Native.PdxArray(holders).ToArray())
        {
            if (holder == 0) continue;
            long x = 0, y = 0;
            var n = 0;
            foreach (var province in Native.PdxArray(holder + ProvincesOfHolder))
            {
                var info = province == 0 ? 0 : *(nint*)(province + StaticOfProvince);
                if (info == 0) continue;
                x += *(int*)(info + PositionOfStatic);
                y += *(int*)(info + PositionOfStatic + 4);
                n++;
            }
            if (n > 0) points.Add(((int)(x / n), (int)(y / n)));
        }
    }

    private void PointAt(nint arrow, int x, int y, bool head)
    {
        var point = stackalloc long[2];
        point[0] = x * 100_000L;
        point[1] = y * 100_000L;
        _append(arrow, (nint)point, 0, (byte)(head ? 1 : 0), -1);
    }
}
