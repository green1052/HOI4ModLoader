using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Interface;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>CProductionEquipmentWindow: sorts the design entries and adds them to the grid (view, CPdxArray* entries, int count, grid).</summary>
    [Signature] public const string FillDesigns = "48 89 5C 24 ? 4C 89 4C 24 ? 48 89 54 24 ? 55 56 57 41 54 41 55 41 56 41 57 B8 70 10 00 00";

    /// <summary>
    /// CStandardGridBox::EnableReordering (box, observer, scrollbar, long sensitivity, float interpolation, int first): rows can
    /// be dragged; on a drop the box re-adds its children in the new order and calls observer slot 0 (obs, from, to).
    /// </summary>
    [Signature] public const string EnableReordering = "48 83 EC 38 83 B9 70 01 00 00 01 74 ? 80 3D ? ? ? ? 00 74 ? 80 3D ? ? ? ? 00 75";

    /// <summary>A container window's vertical scrollbar (container) -> scrollbar.</summary>
    [Signature] public const string ScrollbarOf = "48 89 5C 24 ? 57 48 81 EC 30 01 00 00 48 8B F9 48 8B 81 E0 15 00 00";

    /// <summary>CStandardGridBox layout (box): places the children in their order.</summary>
    [Signature] public const string Layout = "40 53 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 38 45 33 F6 0F 29 74 24 ? 41 8B DE 48 8B E9";

    /// <summary>The production lines view enabling reordering: `movss xmm6, [rip+interpolation]` at +4, `mov rbx, [rip+sensitivity]` at +11.</summary>
    [Signature] public const string ReorderDefines = "F3 0F 10 35 ? ? ? ? 48 8B 1D ? ? ? ? 49 8B 8E A8 24 00 00 E8 ? ? ? ? 49 8D 96 80 05 00 00";

    [VTable] public const string DesignEntry = "CEquipmentViewItem";
}

/// <summary>
/// Interface conveniences. The production screen's design list (what a new line can produce) can be reordered by dragging,
/// with vanilla's own row drag (as the production lines have). The order is kept per country in cache\interface_designs.txt
/// and only changes what is shown, so multiplayer works.
/// </summary>
[Plugin("com.green1052.interface", "Interface", "1.0.0", GameVersion = "1.19.3")]
public unsafe class Interface : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int WindowOfView = 3968, ChildrenOfBox = 0x1A0, ChildCountOfBox = 0x1AC, ReorderingOfBox = 0x224;
    private const int VariantOfEntry = 4056, RefOfVariant = 0x08, WindowSlot = 55;

    [Function(CallingConventions.Microsoft)] public delegate void FillDesignsFn(nint view, nint entries, int count, nint grid);

    private static readonly string OrderFile = Path.Combine(Loader.ModsDir, "cache", "interface_designs.txt");
    private static readonly Dictionary<int, List<long>> Orders = []; // country tag -> design CRefs, first first
    private static nint _entryVTable;
    private static Logger _log = null!;

    private IHook<FillDesignsFn>? _fill;
    private delegate* unmanaged<nint, nint, nint, long, float, int, void> _enable;
    private delegate* unmanaged<nint, nint> _scrollbarOf;
    private static delegate* unmanaged<nint, void> _layout;
    private long _sensitivity;
    private float _interpolation;
    private readonly Dictionary<nint, nint> _observers = []; // grid -> our observer
    private static nint _observerVTable;

    public override void Load()
    {
        _log = Log;
        _entryVTable = Sig.VTable(Signatures.DesignEntry);
        _enable = (delegate* unmanaged<nint, nint, nint, long, float, int, void>)Sig.Find(Signatures.EnableReordering);
        _scrollbarOf = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.ScrollbarOf);
        _layout = (delegate* unmanaged<nint, void>)Sig.Find(Signatures.Layout);
        var defines = Sig.Find(Signatures.ReorderDefines);
        _interpolation = *(float*)(defines + 8 + *(int*)(defines + 4));
        _sensitivity = *(long*)(defines + 15 + *(int*)(defines + 11));
        if (_observerVTable == 0) // slot 0: dropped; the others (drag start, end, cancel): nothing to do
        {
            _observerVTable = (nint)NativeMemory.AllocZeroed(4 * 8);
            ((nint*)_observerVTable)[0] = (nint)(delegate* unmanaged<nint, int, int, void>)&Dropped;
            for (var i = 1; i < 4; i++) ((nint*)_observerVTable)[i] = (nint)(delegate* unmanaged<nint, void>)&Nothing;
        }
        LoadOrders();
        _fill = Patches.Hook<FillDesignsFn>(Sig.Find(Signatures.FillDesigns), (view, entries, count, grid) =>
        {
            _fill!.OriginalFunction(view, entries, count, grid);
            Log.Guard("design order", () => Arrange(view, grid));
        });
    }

    /// <summary>After vanilla filled the grid: the saved order applied, and the grid made draggable once.</summary>
    private void Arrange(nint view, nint grid)
    {
        if (grid == 0) return;
        if (Orders.TryGetValue(Game.ControlledTag, out var order) && order.Count > 0)
        {
            var rank = new Dictionary<long, int>();
            for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
            var children = Children(grid);
            var n = Entries(children);
            // Ranked designs in the saved order; new ones after them, in vanilla's order.
            var sorted = children[..n].ToArray().Select((item, i) => (item, key: rank.TryGetValue(Ref(item), out var r) ? r : order.Count + i))
                .OrderBy(e => e.key).Select(e => e.item).ToArray();
            if (!sorted.AsSpan().SequenceEqual(children[..n]))
            {
                sorted.CopyTo(children);
                _layout(grid);
            }
        }
        if (*(byte*)(grid + ReorderingOfBox) != 0) return;
        if (!_observers.TryGetValue(grid, out var observer))
        {
            observer = (nint)NativeMemory.AllocZeroed(16); // ponytail: one per grid for the process' life (two grids)
            *(nint*)observer = _observerVTable;
            *(nint*)(observer + 8) = grid;
            _observers[grid] = observer;
        }
        var container = Gui.Find(*(nint*)(view + WindowOfView), WindowSlot, "equipments");
        _enable(grid, observer, container == 0 ? 0 : _scrollbarOf(container), _sensitivity, _interpolation, 0);
    }

    private static Span<nint> Children(nint grid) => new((void*)*(nint*)(grid + ChildrenOfBox), *(int*)(grid + ChildCountOfBox));

    /// <summary>How many children from the start are design entries (empty slots may follow).</summary>
    private static int Entries(Span<nint> children)
    {
        var n = 0;
        while (n < children.Length && children[n] != 0 && *(nint*)children[n] == _entryVTable && *(nint*)(children[n] + VariantOfEntry) != 0) n++;
        return n;
    }

    private static long Ref(nint entry) => *(long*)(*(nint*)(entry + VariantOfEntry) + RefOfVariant);

    /// <summary>
    /// A row was dropped: the grid holds the new order of what is shown. Designs filtered out keep their places in the saved
    /// order; the shown ones fill the places shown ones held, in the new order.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void Dropped(nint observer, int from, int to)
    {
        try
        {
            var grid = *(nint*)(observer + 8);
            var children = Children(grid);
            var shown = children[..Entries(children)].ToArray().Select(Ref).ToList();
            var tag = Game.ControlledTag;
            var order = Orders.TryGetValue(tag, out var saved) ? saved : Orders[tag] = [];
            var visible = shown.ToHashSet();
            var places = Enumerable.Range(0, order.Count).Where(i => visible.Contains(order[i])).ToList();
            foreach (var design in shown.Where(d => !order.Contains(d)))
            {
                places.Add(order.Count);
                order.Add(design);
            }
            for (var k = 0; k < places.Count && k < shown.Count; k++) order[places[k]] = shown[k];
            SaveOrders();
            _log.Info($"design moved from row {from + 1} to {to + 1}");
        }
        catch (Exception e) { _log.Error($"design order: {e.Message}"); }
    }

    [UnmanagedCallersOnly]
    private static void Nothing(nint observer) { }

    private static void LoadOrders()
    {
        Orders.Clear();
        try
        {
            if (!File.Exists(OrderFile)) return;
            foreach (var line in File.ReadAllLines(OrderFile))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && int.TryParse(parts[0], out var tag))
                    Orders[tag] = parts.Skip(1).Select(p => long.TryParse(p, out var r) ? r : 0).Where(r => r != 0).ToList();
            }
        }
        catch (Exception e) { _log.Error($"design order file: {e.Message}"); }
    }

    private static void SaveOrders()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OrderFile)!);
            File.WriteAllLines(OrderFile, Orders.Select(o => $"{o.Key} {string.Join(' ', o.Value)}"));
        }
        catch (Exception e) { _log.Error($"design order file: {e.Message}"); }
    }
}
