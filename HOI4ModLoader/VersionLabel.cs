using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// Makes the main menu panel 20 px taller and its version label two lines tall, 10 px lower, to fit the loader's line.
[assembly: HOI4ModLoader.GuiPatch("interface/frontendmainview.gui", "gui/main_menu_version.gui",
    Replace = @"(?s)(name =""mainmenu_panel_bottom""\s*position = \{ x=0 y =0\}\s*size = \{ width = 320 height = )450(.*?name =""version_label""\s*position = \{ x = 0 y = )411(.*?maxHeight = )20")]

namespace HOI4ModLoader;

/// <summary>
/// Adds the loader to the main menu's version label: a second line "HOI4ModLoader 1.0.0" if the GuiPatch above applied,
/// else " +". Its tooltip lists the loader version, a pending update and the loaded plugins. The game's own version
/// string is untouched.
/// </summary>
public static unsafe class VersionLabel
{
    /// <summary>Main menu view (view, std::string* out): writes the "version_label" text.</summary>
    [Signature] public const string TextSig = "48 89 54 24 ? 53 48 83 EC 40 48 8B DA C7 44 24";

    [Function(CallingConventions.Microsoft)] public delegate nint TextFn(nint view, nint text);

    private static IHook<TextFn>? _hook;
    private static readonly Logger Log = new("VersionLabel");

    internal static void Install()
    {
        _hook = Patches.Hook<TextFn>(Sig.Find(TextSig), (view, text) =>
        {
            var result = _hook!.OriginalFunction(view, text);
            // shortcut: 1.19.4 also calls this builder with views whose "version_label" window is not safely
            // walkable from a detour, so only the string is touched here — no window lookups, no tooltip.
            Log.Guard("version label", () =>
                Native.SetMsvcString(text, [.. Native.MsvcString(text), .. Encoding.UTF8.GetBytes($"\nHOI4ModLoader {Loader.Version}")]));
            return result;
        });
    }
}
