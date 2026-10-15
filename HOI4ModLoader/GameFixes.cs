using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>Vanilla bugs the game crashes on, fixed where they start.</summary>
internal static unsafe partial class GameFixes
{
    /// <summary>
    /// CIntelligenceAgency::RemoveOperative(agency, operative) -> bool (intelligenceagency.cpp): takes the operative off the
    /// agency's list. Its callers (dismissing, retiring or moving a character) pass the agency of the country they resolve the
    /// operative to, and free the operative whether or not it was removed.
    /// </summary>
    [Signature] public const string RemoveOperative = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 41 56 48 83 EC 30 48 63 81 E4 00 00 00 45 33 F6 48 8B DA 48 8B F1";

    /// <summary>
    /// CDesignerAdjusterItem tooltip (tip = item+0x18, widget, text, ...) -> bool: a division stat's breakdown in the division
    /// view or designer. Reads the division template the view last pointed the item at (tip+0x190: the template+0x18).
    /// </summary>
    [Signature] public const string AdjusterTooltip = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 00 01 00 00 49 8B F8 48 8B D9 45 33 FF 45 8B F7 44 89 7D ? 48 3B 91 78 01 00 00";

    [VTable] public const string DivisionTemplate = "CReferencedDivisionTemplate";

    [Function(CallingConventions.Microsoft)] public delegate byte RemoveOperativeFn(nint agency, nint operative);

    [Function(CallingConventions.Microsoft)] public delegate byte AdjusterTooltipFn(nint tip, nint widget, nint text, nint a4);

    private const int AgencyOfCountry = 0xFC0, OperativesOfAgency = 0xD8, TemplateOfTip = 0x190, TipOfTemplate = 0x18;

    private static IHook<RemoveOperativeFn>? _removeOperative;
    private static IHook<AdjusterTooltipFn>? _adjusterTooltip;
    private static readonly Logger Log = new("Game Fixes");

    internal static void Install()
    {
        // A caller that resolved the operative to no country (error.log: "not found in agency=---") removed nothing and freed
        // it: it stayed on its real agency's list, and that agency's teardown (loading a save) crashed on the freed operative.
        // It comes off the agency that holds it instead.
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

        // The template can be deleted while the view stays open, unrefreshed: hovering a stat then read the freed template.
        // A pointer that no longer leads to a template is dropped, and the tooltip shows the stat without one (vanilla's
        // branch for no template) until the view refreshes. ponytail: freed memory reused by another template still passes.
        var template = Sig.VTable(DivisionTemplate);
        _adjusterTooltip = Patches.Hook<AdjusterTooltipFn>(Sig.Find(AdjusterTooltip), (tip, widget, text, a4) =>
        {
            if (*(nint*)(tip + TemplateOfTip) is var p && p != 0 && !(Readable(p - TipOfTemplate) && *(nint*)(p - TipOfTemplate) == template))
                *(nint*)(tip + TemplateOfTip) = 0;
            return _adjusterTooltip!.OriginalFunction(tip, widget, text, a4);
        });
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
