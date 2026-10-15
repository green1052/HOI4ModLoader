using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>Vanilla bugs the game crashes on, fixed where they start.</summary>
internal static unsafe class GameFixes
{
    /// <summary>
    /// CIntelligenceAgency::RemoveOperative(agency, operative) -> bool (intelligenceagency.cpp): takes the operative off the
    /// agency's list. Its callers (dismissing, retiring or moving a character) pass the agency of the country they resolve the
    /// operative to, and free the operative whether or not it was removed.
    /// </summary>
    [Signature] public const string RemoveOperative = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 41 56 48 83 EC 30 48 63 81 E4 00 00 00 45 33 F6 48 8B DA 48 8B F1";

    [Function(CallingConventions.Microsoft)] public delegate byte RemoveOperativeFn(nint agency, nint operative);

    private const int AgencyOfCountry = 0xFC0, OperativesOfAgency = 0xD8;

    private static IHook<RemoveOperativeFn>? _removeOperative;
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
    }

    private static bool Holds(nint agency, nint operative) => agency != 0 && Native.PdxArray(agency + OperativesOfAgency).Contains(operative);
}
