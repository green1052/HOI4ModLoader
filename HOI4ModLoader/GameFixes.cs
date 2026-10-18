using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>Vanilla bugs, fixed where they start: crashes and a broken drag scroll. Interface only or as vanilla's own, so multiplayer safe.</summary>
internal static unsafe partial class GameFixes
{
    /// <summary>
    /// CIntelligenceAgency::RemoveOperative(agency, operative) -> bool: takes the operative off the agency's list. Callers
    /// free the operative whether or not it was removed.
    /// </summary>
    [Signature] public const string RemoveOperative = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 41 56 48 83 EC 30 48 63 81 E4 00 00 00 45 33 F6 48 8B DA 48 8B F1";

    /// <summary>
    /// CDesignerAdjusterItem tooltip (tip = item+0x18, widget, text, ...) -> bool: a division stat's breakdown. Reads the
    /// item's division template (tip+0x190 = template+0x18).
    /// </summary>
    [Signature] public const string AdjusterTooltip = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 00 01 00 00 49 8B F8 48 8B D9 45 33 FF 45 8B F7 44 89 7D ? 48 3B 91 78 01 00 00";

    [VTable] public const string DivisionTemplate = "CReferencedDivisionTemplate";

    /// <summary>CStandardGridBox reorder update (box, input): while a row is dragged, moves it and scrolls near the list's edges.</summary>
    [Signature] public const string ReorderUpdate = "48 8B C4 48 89 50 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 68";

    /// <summary>The reorder update reading the UI scale: `mov rax, [rip+Interface]`, the scale a float at +0x325D8.</summary>
    [Signature] public const string UiScale = "48 8B 05 ? ? ? ? F3 0F 5E 80 D8 25 03 00";

    /// <summary>The production screen's line list; its reorder observer (view+0x580) has this vtable + 0x100.</summary>
    [VTable] public const string ProductionLineView = "CCountryProductionLineView";

    [Function(CallingConventions.Microsoft)] public delegate byte RemoveOperativeFn(nint agency, nint operative);

    [Function(CallingConventions.Microsoft)] public delegate byte AdjusterTooltipFn(nint tip, nint widget, nint text, nint a4);

    [Function(CallingConventions.Microsoft)] public delegate void ReorderUpdateFn(nint box, nint input);

    private const int AgencyOfCountry = 0xFC0, OperativesOfAgency = 0xD8, TemplateOfTip = 0x190, TipOfTemplate = 0x18;
    // CStandardGridBox: scrollbar, reorder observer, scroll sensitivity (fixed point), a row being dragged. Scrollbar: its scroll
    // model, the step (fixed point). Vtable slots: input/scrollbar position and scrollbar size (int2* f(obj, int2* out)),
    // model value (float f(model, 0)) and set value (f(model, long fixed, 1, 1)).
    private const int ScrollbarOfBox = 0x200, ObserverOfBox = 0x208, SensitivityOfBox = 0x210, DraggingOfBox = 0x225;
    private const int ModelOfScrollbar = 0x80, StepOfScrollbar = 0x1568, ScaleOfInterface = 0x325D8;
    private const int PositionSlot = 0x30 / 8, SizeSlot = 0xE0 / 8, MouseSlot = 0xC0 / 8, ValueSlot = 0x20 / 8, SetValueSlot = 0x38 / 8;
    private const int ObserverOfViewVTable = 0x100, DragScrollZone = 40; // px inside the list's top and bottom

    private static IHook<RemoveOperativeFn>? _removeOperative;
    private static IHook<AdjusterTooltipFn>? _adjusterTooltip;
    private static IHook<ReorderUpdateFn>? _reorderUpdate;
    private static readonly Logger Log = new("Game Fixes");

    internal static void Install()
    {
        // A caller may name the wrong agency ("not found in agency=---"). The freed operative then stays on its real agency's
        // list and crashes its teardown on the next load. Remove it from the agency that holds it.
        _removeOperative = Patches.Hook<RemoveOperativeFn>(Sig.Find(RemoveOperative), (agency, operative) =>
        {
            if (operative != 0 && !Holds(agency, operative))
                foreach (var country in Game.Countries)
                    if (*(nint*)(country + AgencyOfCountry) is var holder && holder != agency && Holds(holder, operative))
                    {
                        Log.Info($"an operative left its own agency (country {Game.Tag(country)}), not the one the game named");
                        agency = holder;
                        break;
                    }
            return _removeOperative!.OriginalFunction(agency, operative);
        });

        // A template deleted while the view is open leaves a dangling pointer. Drop it if it no longer leads to a template;
        // the tooltip then shows the stat without one. ponytail: freed memory reused by another template still passes.
        var template = Sig.VTable(DivisionTemplate);
        _adjusterTooltip = Patches.Hook<AdjusterTooltipFn>(Sig.Find(AdjusterTooltip), (tip, widget, text, a4) =>
        {
            if (*(nint*)(tip + TemplateOfTip) is var p && p != 0 && !(Readable(p - TipOfTemplate) && *(nint*)(p - TipOfTemplate) == template))
                *(nint*)(tip + TemplateOfTip) = 0;
            return _adjusterTooltip!.OriginalFunction(tip, widget, text, a4);
        });

        // Vanilla scrolls a dragged row's list within half a row of its edge. The production lines' rows are 1 px slots, so
        // only past the edge, and the list ends at the screen's bottom: a line couldn't be dragged below what is shown.
        var observer = Sig.VTable(ProductionLineView) + ObserverOfViewVTable;
        var scaleAt = Sig.Find(UiScale);
        var ui = (nint*)(scaleAt + 7 + *(int*)(scaleAt + 3));
        _reorderUpdate = Patches.Hook<ReorderUpdateFn>(Sig.Find(ReorderUpdate), (box, input) =>
        {
            if (*(byte*)(box + DraggingOfBox) != 0 && *(nint*)(box + ObserverOfBox) is var obs && obs != 0 && *(nint*)obs == observer
                && *(nint*)(box + ScrollbarOfBox) is var bar && bar != 0 && *ui != 0)
                ScrollNearEdge(box, input, bar, *(float*)(*ui + ScaleOfInterface));
            _reorderUpdate!.OriginalFunction(box, input);
        });
    }

    /// <summary>Scrolls the list by vanilla's step, faster nearer the edge, while the mouse is within DragScrollZone of it.</summary>
    private static void ScrollNearEdge(nint box, nint input, nint bar, float scale)
    {
        long tmp;
        var y = Y(input, MouseSlot, &tmp) / scale - Y(bar, PositionSlot, &tmp);
        var height = Y(bar, SizeSlot, &tmp);
        var up = y >= 0 && y < DragScrollZone;
        var share = up ? (DragScrollZone - y) / DragScrollZone : y <= height && y > height - DragScrollZone ? (y - height + DragScrollZone) / DragScrollZone : 0;
        if (share <= 0) return;
        var delta = (long)(*(long*)(bar + StepOfScrollbar) * share) * *(long*)(box + SensitivityOfBox) / Native.FixedPoint;
        var model = bar + ModelOfScrollbar;
        var value = (long)MathF.Round(((delegate* unmanaged<nint, int, float>)(*(nint**)model)[ValueSlot])(model, 0) * Native.FixedPoint);
        ((delegate* unmanaged<nint, long, byte, byte, void>)(*(nint**)model)[SetValueSlot])(model, up ? value - delta : value + delta, 1, 1);
    }

    /// <summary>y of the int2 an `int2* f(obj, int2* out)` vtable call returns.</summary>
    private static float Y(nint obj, int slot, long* tmp)
    {
        var at = ((delegate* unmanaged<nint, long*, long*>)(*(nint**)obj)[slot])(obj, tmp);
        return ((int*)at)[1];
    }

    private static bool Holds(nint agency, nint operative) => agency != 0 && Native.PdxArray(agency + OperativesOfAgency).Contains(operative);

    /// <summary>An aligned qword at <paramref name="at"/> can be read: committed, not no-access or guard.</summary>
    private static bool Readable(nint at)
    {
        MemoryInfo info;
        return VirtualQuery(at, &info, sizeof(MemoryInfo)) != 0 && info.State == 0x1000 && (info.Protect & 0x101) == 0; // MEM_COMMIT; PAGE_NOACCESS, PAGE_GUARD
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo // MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint VirtualQuery(nint address, MemoryInfo* info, nint size);
}
