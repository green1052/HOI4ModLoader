using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Intelligence;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>COperativeBottomBar update (bar): fills the bar from the agency's operatives, in their order.</summary>
    [Signature] public const string BarUpdate = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 40 48 8B D9 48 83 B9 E0 0B 00 00 00";

    /// <summary>COperativeBottomBar full refresh (bar): refills and lays out the entries.</summary>
    [Signature] public const string BarRefresh = "40 55 48 83 EC 20 48 8B E9 48 8B 49 30 48 8B 01";

    /// <summary>Grid box cell position (box, int2* out, int2* cell) -> int2*: where the box puts a child in that cell.</summary>
    [Signature] public const string CellPosition = "40 53 55 56 57 41 54 41 56 41 57 48 83 EC 30 48 8B 81";

    /// <summary>The army card reading the mouse: `mov rax, [rip+Gui]` at +5; the GUI's input at +0x500, its vcall +0xC0 the position.</summary>
    [Signature] public const string ReadMouse = "48 8D 54 24 ? 48 8B 05 ? ? ? ? 48 8B 88 00 05 00 00 48 8B 01 FF 90 C0 00 00 00 48 8B 08";

    /// <summary>Button glue of an operative entry; vtable slot 1 handles mouse events.</summary>
    [VTable] public const string ItemGlue = "?$CLegacyButtonObserverGlue@VCOperativeBottomBarItem@@";

    [VTable] public const string Item = "COperativeBottomBarItem";

    /// <summary>
    /// Clears a finished operation from the operations list, as closing its overview does: frees operatives still tied to
    /// it and removes it. Its outcome was applied when it finished. +0x28 operation CRef, +0x30 tag, +0x34 refund.
    /// </summary>
    [VTable] public const string DeleteOperation = "CDeleteOperationCommand";
}

/// <summary>
/// The intelligence screen. Drag an operative in the bottom bar to move it, as army cards are dragged: it follows the mouse
/// and the others make room. The game has no command for this, so the agency's own list is reordered: single player only.
/// Finished operations clear themselves from the operations list a few days after they finished (the game's own command:
/// multiplayer too).
/// </summary>
[Plugin("com.green1052.intelligence", "Intelligence", "1.0.0", GameVersion = "1.19.4.1")]
public unsafe class Intelligence : Plugin
{
    // Layouts re-verified on HOI4 1.19.4.1: 1.19.4 moved ButtonGlue +0x80, OperativesOfAgency +0x18, BoxOfBar +0x100.
    private const int AgencyOfCountry = 0xFC0, OperativesOfAgency = 0xF0;
    private const int PortraitGlue = 0x50, ButtonGlue = 0x5D8, OperativeOfItem = 0x20; // entry: its two glues, operative CRef
    private const int SetPositionSlot = 8;                                               // entry vtable: SetPosition(packed x, y)
    private const int BoxOfBar = 0xC00, CellsOfBox = 0x188, ChildCountOfBox = 0x194, ChildrenOfBox = 0x1A0; // grid box
    private const int InputOfGui = 0x500, MousePositionSlot = 0xC0 / 8;
    private const int ButtonOfEvent = 0x20, TypeOfEvent = 0x24;                          // mouse event
    private const int MouseUp = 1, MouseDown = 2, Click = 5;
    private const int DragPixels = 4;
    private const float Ease = 0.35f; // share of the way to its place an entry moves per frame
    // Country: operations manager; its CPdxArray<COperationInstance*> at +0x10. Operation: CRef, finished (awaiting
    // dismissal), start hour, duration in days. CDeleteOperationCommand: operation CRef, tag, refund.
    private const int OperationsOfCountry = 0x15A8, InstancesOfOperations = 0x10;
    private const int RefOfOperation = 0x08, FinishedOfOperation = 0x42, StartOfOperation = 0x70, DaysOfOperation = 0x80;
    private const int OperationOfCommand = 0x28, TagOfCommand = 0x30, RefundOfCommand = 0x34, DeleteCommandSize = 0x38;

    [Function(CallingConventions.Microsoft)] public delegate nint DispatchFn(nint glue, nint ev);

    [Function(CallingConventions.Microsoft)] public delegate void BarFn(nint bar);

    private IHook<DispatchFn>? _dispatch;
    private IHook<BarFn>? _barUpdate;
    private delegate* unmanaged<nint, void> _barRefresh;
    private delegate* unmanaged<nint, long*, long*, long*> _cellPosition;
    private nint* _gui;
    private nint _glueVTable, _itemVTable;
    private nint _pressed, _target; // entries: the one dragged, the one whose place it takes
    private (int X, int Y) _downAt;
    private bool _dragging;
    private readonly Dictionary<nint, (float X, float Y)> _shown = [];
    private readonly HashSet<long> _deleted = []; // operations already sent off, until the command runs
    private PerGame _game = null!;
    private int _dismissDays, _checkedDay = -1;

    public override void Load()
    {
        _dismissDays = Config.Bind("Intelligence", "DismissDays", 7,
            "Game days after an operation finished before it clears itself from the operations list (0: never).");
        var drag = Config.Bind("Intelligence", "EnableDrag", true,
            "Reorder operatives by dragging their portraits in the bottom bar, as army cards (single player only).");
        _game = new PerGame(() =>
        {
            _deleted.Clear();
            _checkedDay = -1;
        });
        if (_dismissDays > 0) MainThread.Every(TimeSpan.FromSeconds(1), DismissFinished);
        if (Multiplayer || !drag) return; // the drag changes the game
        _glueVTable = Sig.VTable(Signatures.ItemGlue);
        _itemVTable = Sig.VTable(Signatures.Item);
        _barRefresh = (delegate* unmanaged<nint, void>)Sig.Find(Signatures.BarRefresh);
        _cellPosition = (delegate* unmanaged<nint, long*, long*, long*>)Sig.Find(Signatures.CellPosition);
        var read = Sig.Find(Signatures.ReadMouse);
        _gui = (nint*)(read + 12 + *(int*)(read + 8));
        // Every button glue shares this handler: other buttons pass straight through.
        _dispatch = Patches.Hook<DispatchFn>(*(nint*)(_glueVTable + 8), (glue, ev) =>
        {
            if (*(nint*)glue != _glueVTable || Item(glue) is var item && item == 0) return _dispatch!.OriginalFunction(glue, ev);
            switch (*(int*)(ev + TypeOfEvent))
            {
                case MouseDown when *(int*)(ev + ButtonOfEvent) <= 1:
                    (_pressed, _target, _dragging, _downAt) = (item, item, false, Mouse());
                    break;
                case Click or MouseUp when _dragging:
                    return 0; // a drop: don't select the operative
            }
            return _dispatch!.OriginalFunction(glue, ev);
        });
        _barUpdate = Patches.Hook<BarFn>(Sig.Find(Signatures.BarUpdate), bar =>
        {
            var dropped = _pressed != 0 && GuardedDrop(); // most frames: no press, nothing allocated
            _barUpdate!.OriginalFunction(bar);
            if (dropped) _barRefresh(bar); // back to the normal layout, in the new order
            else if (_dragging) Log.Guard("operative drag", (this, bar), static s => s.Item1.Animate(s.bar));
        });
    }

    /// <summary>Once a game day: the player's operations finished at least DismissDays ago are cleared, as the overview's close does.</summary>
    private void DismissFinished()
    {
        if (!Game.HasStarted) return;
        _game.Check();
        if (Game.Hour / 24 == _checkedDay) return;
        _checkedDay = Game.Hour / 24;
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        var operations = country == 0 ? 0 : *(nint*)(country + OperationsOfCountry);
        if (operations == 0) return;
        foreach (var operation in Native.PdxArray(operations + InstancesOfOperations))
        {
            if (operation == 0 || *(byte*)(operation + FinishedOfOperation) == 0) continue;
            var finished = *(int*)(operation + StartOfOperation) + 24 * *(int*)(operation + DaysOfOperation);
            var reference = *(long*)(operation + RefOfOperation);
            if (Game.Hour < finished + 24 * _dismissDays || !_deleted.Add(reference)) continue;
            var cmd = Commands.New(Signatures.DeleteOperation, DeleteCommandSize, 0, 0);
            *(long*)(cmd + OperationOfCommand) = reference;
            *(int*)(cmd + TagOfCommand) = tag;
            *(byte*)(cmd + RefundOfCommand) = 0;
            if (Commands.TryPost(cmd)) Log.Info("a finished operation cleared from the operations list");
        }
    }

    /// <summary>The entry owning the glue, or 0.</summary>
    private nint Item(nint glue)
    {
        if (*(nint*)(glue - PortraitGlue) == _itemVTable) return glue - PortraitGlue;
        return *(nint*)(glue - ButtonGlue) == _itemVTable ? glue - ButtonGlue : 0;
    }

    /// <summary>The mouse in GUI coordinates.</summary>
    private (int X, int Y) Mouse()
    {
        var input = *(nint*)(*_gui + InputOfGui);
        long at;
        var p = ((delegate* unmanaged<nint, long*, long*>)(*(nint**)input)[MousePositionSlot])(input, &at);
        return ((int)*p, (int)(*p >> 32));
    }

    /// <summary><see cref="Drop"/> guarded; its closure lives here, so the bar's hook allocates nothing without a press.</summary>
    private bool GuardedDrop()
    {
        var dropped = false;
        Log.Guard("operative drag", () => dropped = Drop());
        return dropped;
    }

    /// <summary>Follows a press: past a few pixels it is a drag. True once a drag is released (the bar is then refreshed).</summary>
    private bool Drop()
    {
        if (_pressed == 0) return false;
        if (Gui.KeyHeld(0x01)) // VK_LBUTTON
        {
            var now = Mouse();
            if (Math.Abs(now.X - _downAt.X) + Math.Abs(now.Y - _downAt.Y) > DragPixels) _dragging = true;
            return false;
        }
        var (from, to, dragged) = (_pressed, _target, _dragging);
        (_pressed, _dragging) = (0, false);
        _shown.Clear();
        if (dragged && to != from) Move(Game.Resolve(*(long*)(from + OperativeOfItem)), Game.Resolve(*(long*)(to + OperativeOfItem)));
        return dragged;
    }

    /// <summary>
    /// One frame of a drag: the dragged entry at the mouse, the ones between it and its new place one cell over, easing
    /// there. The grid box only lays its entries out on a full refresh, so these positions hold until the drop.
    /// </summary>
    private void Animate(nint bar)
    {
        var box = *(nint*)(bar + BoxOfBar);
        if (box == 0) return;
        var children = new ReadOnlySpan<nint>(*(void**)(box + ChildrenOfBox), *(int*)(box + ChildCountOfBox));
        var n = 0;
        while (n < children.Length && *(nint*)children[n] == _itemVTable) n++; // entries first, then empty slots
        var from = children[..n].IndexOf(_pressed);
        if (from < 0 || n < 2) return;
        var cells = *(nint*)(box + CellsOfBox);
        var home = new (int X, int Y)[n];
        for (var k = 0; k < n; k++)
        {
            long out_;
            var p = *_cellPosition(box, &out_, (long*)(cells + 8 * k));
            home[k] = ((int)p, (int)(p >> 32));
        }
        var spacing = home[1].X - home[0].X;
        if (spacing == 0) return;
        var dx = Mouse().X - _downAt.X;
        var to = Math.Clamp(from + (int)Math.Round(dx / (double)spacing), 0, n - 1);
        _target = children[to];
        for (var k = 0; k < n; k++)
        {
            var (x, y) = k == from ? (home[k].X + dx, home[k].Y)
                : k > from && k <= to ? home[k - 1]
                : k < from && k >= to ? home[k + 1]
                : home[k];
            (float X, float Y) at = (x, y); // the dragged one sits at the mouse, the others ease toward their cell
            if (k != from)
            {
                (float X, float Y) was = _shown.TryGetValue(children[k], out var seen) ? seen : (home[k].X, home[k].Y);
                at = (was.X + (x - was.X) * Ease, was.Y + (y - was.Y) * Ease);
            }
            _shown[children[k]] = at;
            var packed = (long)(int)MathF.Round(at.X) | (long)(int)MathF.Round(at.Y) << 32;
            ((delegate* unmanaged<nint, long, void>)(*(nint**)children[k])[SetPositionSlot])(children[k], packed);
        }
    }

    /// <summary>Puts <paramref name="operative"/> at <paramref name="target"/>'s place in the player's agency; those between shift by one.</summary>
    private void Move(nint operative, nint target)
    {
        var country = Game.Country(Game.ControlledTag);
        var agency = country == 0 ? 0 : *(nint*)(country + AgencyOfCountry);
        if (agency == 0 || operative == 0 || target == 0) return;
        var list = Native.PdxArray<nint>(agency + OperativesOfAgency);
        int from = list.IndexOf(operative), to = list.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        if (from < to) list[(from + 1)..(to + 1)].CopyTo(list[from..]);
        else list[to..from].CopyTo(list[(to + 1)..]);
        list[to] = operative;
        Log.Info($"operative moved from place {from + 1} to {to + 1}");
    }
}
