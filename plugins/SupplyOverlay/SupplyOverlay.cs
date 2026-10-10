using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace SupplyOverlay;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>CMapArrowManager::CreateArrow(manager, style name std::string*, CColor* colour, CColor* colour, {CColor,
    /// std::string x3}*, uint layer): a CMapArrowObject, or 0 when the manager holds no such style. Hooked to catch the
    /// manager: the game hands it over with every arrow it draws.</summary>
    [Signature] public const string CreateArrow =
        "48 81 EC 90 00 00 00 0F 29 70 B8 4C 8B F2 48 63 81 AC 00 00 00 85 C0 7E 5D"; // reads the manager's style count (+0xAC)

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
/// A supply overlay for any map mode: Ctrl+Alt+S draws an arrow from the hub feeding each of the player's starving
/// divisions to the division's province, in the supply map's own arrow style. Read-only, so other players need nothing.
/// </summary>
[Plugin("com.green1052.supplyoverlay", "Supply Overlay", "1.0.0", GameVersion = "1.19.3")]
public unsafe partial class SupplyOverlay : Plugin
{
    // Layouts for HOI4 1.19.3 (AutoSupply's supply system, the map arrow manager's CreateArrow calling convention).
    private const int SupplyOfGameState = 0x3D8, CountriesOfSupply = 0x108, NetworkOfCountry = 0x80;
    private const int FlowOfSupply = 0x138, FlowSize = 0xD8, FlowEntries = 48, FlowEntrySize = 16;
    private const int CountryOfEntry = 2, AmountOfEntry = 8, Nodes = 0xB8, NodeSize = 0xE8, ProvinceOfNode = 4;
    private const int UnitsOfCountry = 0x290, KindOfUnit = 8, LocationOfUnit = 0x1F0, IsLowSupplySlot = 43;
    private const int PositionOfProvince = 0xB8; // CProvince: {int x, y}, the map arrow point's 1/100000
    private const int ColourSize = 32, LabelsSize = 128; // CreateArrow's colour and {colour, 3 label strings} blocks

    [Function(CallingConventions.Microsoft)]
    private delegate nint CreateFn(nint manager, nint name, nint colourA, nint colourB, nint labels, uint layer);

    private delegate* unmanaged<nint, nint, byte, byte, int, char> _append;
    private delegate* unmanaged<nint, byte, void> _clear, _rebuild, _visible;
    private IHook<CreateFn>? _create;
    private readonly byte[] _colourA = new byte[ColourSize], _colourB = new byte[ColourSize], _labels = new byte[LabelsSize];
    private nint _manager, _arrow;
    private uint _layer;
    private string _style = "supply_to_consumers", _capturedName = "";
    private long _drawn;
    private bool _on, _down, _reported;

    public override void Load()
    {
        var toggle = Config.Bind("Keys", "Toggle", true, "Ctrl+Alt+S shows where the supply of each starving division comes from.");
        _append = (delegate* unmanaged<nint, nint, byte, byte, int, char>)Sig.Find(Signatures.AppendPoint);
        _clear = (delegate* unmanaged<nint, byte, void>)Sig.Find(Signatures.ClearPoints);
        _rebuild = (delegate* unmanaged<nint, byte, void>)Sig.Find(Signatures.RebuildGlyphs);
        _visible = (delegate* unmanaged<nint, byte, void>)Sig.Find(Signatures.SetVisible);
        _create = Patches.Hook<CreateFn>(Sig.Find(Signatures.CreateArrow),
            (manager, name, colourA, colourB, labels, layer) =>
            {
                if (_manager == 0 && EmptyStrings(labels)) Capture(manager, name, colourA, colourB, labels, layer);
                return _create!.OriginalFunction(manager, name, colourA, colourB, labels, layer);
            });
        MainThread.Every(TimeSpan.FromMilliseconds(30), () => Poll(toggle));
    }

    /// <summary>The game calls this with every arrow it draws: the first plain call hands us the manager and the colours.</summary>
    private void Capture(nint manager, nint name, nint colourA, nint colourB, nint labels, uint layer)
    {
        _manager = manager;
        _layer = layer;
        _capturedName = Native.Utf8(Native.MsvcString(name));
        Marshal.Copy(colourA, _colourA, 0, ColourSize);
        Marshal.Copy(colourB, _colourB, 0, ColourSize);
        Marshal.Copy(labels, _labels, 0, LabelsSize);
        Log.Info($"supply overlay: caught the map's arrow manager (style seen: \"{_capturedName}\")");
    }

    /// <summary>Whether every label string in the block is empty, so copying the block keeps no dangling heap buffer.</summary>
    private static bool EmptyStrings(nint labels) =>
        labels == 0 || *(long*)(labels + 0x30) == 0 && *(long*)(labels + 0x50) == 0 && *(long*)(labels + 0x70) == 0;

    private void Poll(bool toggle)
    {
        var down = (GetAsyncKeyState('S') & 0x8000) != 0;
        var hit = down && !_down && Gui.CtrlHeld && Gui.AltHeld;
        _down = down;
        if (hit && toggle) _on = !_on;
        if (!_on)
        {
            if (_arrow != 0) _visible(_arrow, 0);
            return;
        }
        if (Game.ControlledTag == 0) { _arrow = 0; return; } // the menu: the map is rebuilt, the arrow with it
        if (_manager == 0) { Warn(); return; }
        if (_arrow == 0 && (_arrow = MakeArrow()) == 0) return;
        _visible(_arrow, 1);
        if (Environment.TickCount64 - _drawn >= 5000) { _drawn = Environment.TickCount64; Draw(); }
    }

    /// <summary>One arrow in the supply map's own style, or 0: the manager's style list comes from the game's .gfx files,
    /// so an unknown style falls back to the name the game itself last drew with.</summary>
    private nint MakeArrow()
    {
        string[] tries = [_style, _capturedName];
        foreach (var style in tries.Distinct())
        {
            var arrow = Create(style);
            if (arrow != 0) return arrow;
            if (style != _capturedName) Log.Info($"supply overlay: style \"{style}\" not found, trying \"{_capturedName}\"");
        }
        return 0;
    }

    private nint Create(string style)
    {
        fixed (byte* name = System.Text.Encoding.ASCII.GetBytes(style + '\0'))
        fixed (byte* a = _colourA, b = _colourB, l = _labels)
            return _create!.OriginalFunction(_manager, (nint)name, (nint)a, (nint)b, (nint)l, _layer);
    }

    /// <summary>An arrow from the hub feeding each starving division to the division's province.</summary>
    private void Draw()
    {
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        var supply = Game.State == 0 ? 0 : *(nint*)(Game.State + SupplyOfGameState);
        if (country == 0 || supply == 0) return;
        var countrySupply = *(nint*)(*(nint*)(supply + CountriesOfSupply) + 8 * Game.TagIndex(tag));
        var network = countrySupply == 0 ? 0 : *(nint*)(countrySupply + NetworkOfCountry);
        if (network == 0) return;

        var starving = new Dictionary<nint, nint>(); // province -> the node feeding it
        foreach (var unit in Native.PdxArray(country + UnitsOfCountry))
        {
            if (*(int*)(unit + KindOfUnit) != 0 || ((delegate* unmanaged<nint, byte>)(*(nint**)unit)[IsLowSupplySlot])(unit) == 0) continue;
            var province = *(nint*)(unit + LocationOfUnit);
            if (province != 0 && !starving.ContainsKey(province)) starving[province] = FeedingNode(supply, network, *(int*)(province + 0xA4), Game.TagIndex(tag));
        }

        _clear(_arrow, 0);
        foreach (var (province, node) in starving)
        {
            var hub = node == 0 ? 0 : Game.Province(*(int*)(node + ProvinceOfNode));
            if (hub == 0) continue;
            Append(hub);
            Append(province, head: true);
        }
        _rebuild(_arrow, 1);
    }

    /// <summary>Our node sending the most supply into the province (its supply flow), or 0 (AutoSupply's FeedingNode).</summary>
    private static nint FeedingNode(nint supply, nint network, int province, int tagIndex)
    {
        var flow = *(nint*)(supply + FlowOfSupply) + (nint)FlowSize * province;
        var entries = *(nint*)(flow + FlowEntries);
        (int Node, long Amount) best = (-1, 0);
        for (int i = 0, n = *(int*)(flow + FlowEntries + 0xC); i < n; i++)
        {
            var e = entries + i * FlowEntrySize;
            if (*(ushort*)(e + CountryOfEntry) != tagIndex || *(long*)(e + AmountOfEntry) <= best.Amount) continue;
            best = (*(ushort*)e, *(long*)(e + AmountOfEntry));
        }
        return best.Node < 0 || best.Node >= *(int*)(network + Nodes + 0xC) ? 0 : *(nint*)(network + Nodes) + (nint)NodeSize * best.Node;
    }

    private void Append(nint province, bool head = false)
    {
        var point = stackalloc long[2];
        point[0] = *(int*)(province + PositionOfProvince) * 100_000L;
        point[1] = *(int*)(province + PositionOfProvince + 4) * 100_000L;
        _append(_arrow, (nint)point, 0, (byte)(head ? 1 : 0), -1);
    }

    private void Warn()
    {
        if (_reported) return;
        _reported = true;
        Log.Info("supply overlay: the map's arrow manager is not caught yet; select a unit or open a map mode with arrows, then toggle again");
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
