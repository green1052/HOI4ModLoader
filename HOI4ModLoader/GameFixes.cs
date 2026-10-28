using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>Vanilla bugs the game crashes on, fixed where they start.</summary>
internal static unsafe partial class GameFixes
{
    /// <summary>
    /// CIntelligenceAgency::RemoveOperative(agency, operative) -> bool: takes the operative off the agency's list. Callers
    /// free the operative whether or not it was removed.
    /// </summary>
    [Signature] public const string RemoveOperative = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 41 56 48 83 EC 30 48 63 81 FC 00 00 00 45 33 F6 48 8B DA 48 8B F1";

    /// <summary>
    /// CDesignerAdjusterItem tooltip (tip = item+0x18, widget, text, ...) -> bool: a division stat's breakdown. Reads the
    /// item's division template (tip+0x190 = template+0x18).
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
            if (*(nint*)(tip + TemplateOfTip) is var p && p != 0 && !(Native.Readable(p - TipOfTemplate) && *(nint*)(p - TipOfTemplate) == template))
                *(nint*)(tip + TemplateOfTip) = 0;
            return _adjusterTooltip!.OriginalFunction(tip, widget, text, a4);
        });
    }

    private static bool Holds(nint agency, nint operative) => agency != 0 && Native.PdxArray(agency + OperativesOfAgency).Contains(operative);

}
