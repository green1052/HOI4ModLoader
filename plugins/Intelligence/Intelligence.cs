using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Intelligence;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>COperativeBottomBar update (bar): fills the bar from the agency's operatives, in their order.</summary>
    [Signature] public const string BarUpdate = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 40 48 8B D9 48 83 B9 E0 0A 00 00 00";

    /// <summary>COperativeBottomBar full refresh (bar).</summary>
    [Signature] public const string BarRefresh = "40 55 48 83 EC 20 48 8B E9 48 8B 49 30 48 8B 01";

    /// <summary>Button glue of an operative entry; vtable slot 1 handles mouse events.</summary>
    [VTable] public const string ItemGlue = "?$CLegacyButtonObserverGlue@VCOperativeBottomBarItem@@";

    [VTable] public const string Item = "COperativeBottomBarItem";
}

/// <summary>
/// Drag an operative in the intelligence screen's bottom bar onto another to move it there. The game has no command for
/// this, so the agency's own list is reordered: single player only.
/// </summary>
[Plugin("com.green1052.intelligence", "Intelligence", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe partial class Intelligence : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int AgencyOfCountry = 0xFC0, OperativesOfAgency = 0xD8;
    private const int PortraitGlue = 0x50, ButtonGlue = 0x558, OperativeOfItem = 0x20; // entry: its two glues, operative CRef
    private const int ButtonOfEvent = 0x20, TypeOfEvent = 0x24;                          // mouse event
    private const int MouseUp = 1, MouseDown = 2, MouseEnter = 3, MouseLeave = 4, Click = 5;
    private const int DragPixels = 4;

    [Function(CallingConventions.Microsoft)] public delegate nint DispatchFn(nint glue, nint ev);

    [Function(CallingConventions.Microsoft)] public delegate void BarFn(nint bar);

    private IHook<DispatchFn>? _dispatch;
    private IHook<BarFn>? _barUpdate;
    private delegate* unmanaged<nint, void> _barRefresh;
    private nint _glueVTable, _itemVTable;
    private nint _pressed, _hover; // entries
    private Point _downAt;
    private bool _dragging;
    private readonly HashSet<int> _seen = [];

    public override void Load()
    {
        _glueVTable = Sig.VTable(Signatures.ItemGlue);
        _itemVTable = Sig.VTable(Signatures.Item);
        _barRefresh = (delegate* unmanaged<nint, void>)Sig.Find(Signatures.BarRefresh);
        // Every button glue shares this handler: other buttons pass straight through.
        _dispatch = Patches.Hook<DispatchFn>(*(nint*)(_glueVTable + 8), (glue, ev) =>
        {
            if (*(nint*)glue != _glueVTable || Item(glue) is var item && item == 0) return _dispatch!.OriginalFunction(glue, ev);
            var (type, button) = (*(int*)(ev + TypeOfEvent), *(int*)(ev + ButtonOfEvent));
            // ponytail: event numbers are from IDA, not yet seen in game; remove once dragging works.
            if (_seen.Add(type << 8 | button)) Log.Info($"operative bar: mouse event {type}, button {button}");
            switch (type)
            {
                case MouseDown when button <= 1:
                    (_pressed, _dragging) = (item, false);
                    GetCursorPos(out _downAt);
                    break;
                case MouseEnter:
                    _hover = item;
                    break;
                case MouseLeave when _hover == item:
                    _hover = 0;
                    break;
                case Click or MouseUp when _dragging:
                    return 0; // a drop: don't select the operative
            }
            return _dispatch!.OriginalFunction(glue, ev);
        });
        _barUpdate = Patches.Hook<BarFn>(Sig.Find(Signatures.BarUpdate), bar =>
        {
            var moved = false;
            Log.Guard("operative drag", () => moved = Drag());
            _barUpdate!.OriginalFunction(bar);
            if (moved) _barRefresh(bar);
        });
    }

    /// <summary>The entry owning the glue, or 0.</summary>
    private nint Item(nint glue)
    {
        if (*(nint*)(glue - PortraitGlue) == _itemVTable) return glue - PortraitGlue;
        return *(nint*)(glue - ButtonGlue) == _itemVTable ? glue - ButtonGlue : 0;
    }

    /// <summary>A press that moved a few pixels is a drag; released over another entry, it moves the operative. True if moved.</summary>
    private bool Drag()
    {
        if (_pressed == 0) return false;
        if (Gui.KeyHeld(0x01)) // VK_LBUTTON
        {
            GetCursorPos(out var now);
            if (Math.Abs(now.X - _downAt.X) + Math.Abs(now.Y - _downAt.Y) > DragPixels) _dragging = true;
            return false;
        }
        var (from, to, dragged) = (_pressed, _hover, _dragging);
        (_pressed, _dragging) = (0, false);
        return dragged && to != 0 && to != from && Move(Game.Resolve(*(long*)(from + OperativeOfItem)), Game.Resolve(*(long*)(to + OperativeOfItem)));
    }

    /// <summary>Puts <paramref name="operative"/> at <paramref name="target"/>'s place in the player's agency; those between shift by one.</summary>
    private bool Move(nint operative, nint target)
    {
        var country = Game.Country(Game.ControlledTag);
        var agency = country == 0 ? 0 : *(nint*)(country + AgencyOfCountry);
        if (agency == 0 || operative == 0 || target == 0) return false;
        var list = Native.PdxArray<nint>(agency + OperativesOfAgency);
        int from = list.IndexOf(operative), to = list.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return false;
        if (from < to) list[(from + 1)..(to + 1)].CopyTo(list[from..]);
        else list[to..from].CopyTo(list[(to + 1)..]);
        list[to] = operative;
        Log.Info($"operative moved from place {from + 1} to {to + 1}");
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);
}
