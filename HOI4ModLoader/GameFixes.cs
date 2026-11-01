using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>Vanilla bugs the game crashes on, fixed where they start.</summary>
internal static unsafe class GameFixes
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

    /// <summary>Air selection UI: appends an airbase's rows (view, base, showEmpty, filter, count*, context*).</summary>
    [Signature] public const string AirBaseRows = "48 8B C4 4C 89 48 20 44 88 40 18 48 89 50 10 48 89 48 08 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 68 B1 48 81 EC 98 00 00 00 0F 29 70 A8 49 8B D9 41 0F B6 F0 4C 8B FA 4C 8B F1";

    /// <summary>CAirBase's safe HasWings check: country data at +30h, then its pools at +B8h, only if nonnull.</summary>
    [Signature] public const string AirBaseCountryData = "48 63 C8 48 8B 46 30 48 89 7C 24 30 48 8B 14 C8 48 85 D2 74 ? 48 8B 8A B8 00 00 00";

    [Function(CallingConventions.Microsoft)] public delegate byte RemoveOperativeFn(nint agency, nint operative);

    [Function(CallingConventions.Microsoft)] public delegate byte AdjusterTooltipFn(nint tip, nint widget, nint text, nint a4);

    [Function(CallingConventions.Microsoft)] public delegate void AirBaseRowsFn(nint view, nint airbase, byte showEmpty, nint filter, nint count, nint context);

    private const int AgencyOfCountry = 0xFC0, OperativesOfAgency = 0xD8, TemplateOfTip = 0x190, TipOfTemplate = 0x18;

    private static IHook<RemoveOperativeFn>? _removeOperative;
    private static IHook<AdjusterTooltipFn>? _adjusterTooltip;
    private static IHook<AirBaseRowsFn>? _airBaseRows;
    private static int _missingAirBaseDataLogged;
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

        // The air selection refresh can retain a base with no data for the viewing country. Its row builder calls
        // GetPools before checking access; GetPools returns null+0xB8 and the next instruction crashes. Skip this
        // base for the refresh, leaving row counts alone, as the builder's own empty/unavailable exit would do.
        _ = Sig.Find(AirBaseCountryData);
        _airBaseRows = Patches.Hook<AirBaseRowsFn>(Sig.Find(AirBaseRows), (view, airbase, showEmpty, filter, count, context) =>
        {
            var tag = Game.PlayerTag;
            if (!AirBaseHasCountryData(airbase, tag > 0 ? Game.TagIndex(tag) : 0))
            {
                if (Interlocked.Exchange(ref _missingAirBaseDataLogged, 1) == 0)
                    Log.Info("skipped an unavailable airbase in the air selection refresh (no data for the viewing country)");
                return;
            }
            _airBaseRows!.OriginalFunction(view, airbase, showEmpty, filter, count, context);
        });
    }

    /// <summary>The country record must exist before the UI asks for its wing pools at record+0xB8.</summary>
    internal static bool AirBaseHasCountryData(nint airbase, int index)
    {
        if (airbase == 0) return false;
        var data = *(nint*)(airbase + 0x30);
        var size = *(int*)(airbase + 0x3C);
        return data != 0 && index >= 0 && index < size && ((nint*)data)[index] != 0;
    }

    private static bool Holds(nint agency, nint operative) => agency != 0 && Native.PdxArray(agency + OperativesOfAgency).Contains(operative);

}
