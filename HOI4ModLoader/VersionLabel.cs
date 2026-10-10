using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// The main menu panel 20 px taller (it grows from its centre: 10 px each way) and its version label two lines tall, 10 px
// lower so the first line stays where it was, under the social buttons: room for the loader's line.
[assembly: HOI4ModLoader.GuiPatch("interface/frontendmainview.gui", "gui/main_menu_version.gui",
    Replace = @"(?s)(name =""mainmenu_panel_bottom""\s*position = \{ x=0 y =0\}\s*size = \{ width = 320 height = )450(.*?name =""version_label""\s*position = \{ x = 0 y = )411(.*?maxHeight = )20")]

namespace HOI4ModLoader;

/// <summary>
/// Adds the loader to the main menu's version label: a second line "HOI4ModLoader 1.0.0" when the
/// GuiPatch above made the panel and label two lines tall, else " +" on the version's line (a second line would run
/// out of the menu frame); and a tooltip with the loader's version and the loaded plugins. Only that label changes: the game's version
/// string (saves, multiplayer) is untouched.
/// </summary>
public static unsafe class VersionLabel
{
    /// <summary>Main menu view: writes the "version_label" text into a std::string (view, out), adding " (D)" in debug mode etc.</summary>
    [Signature] public const string TextSig = "48 89 54 24 ? 53 48 83 EC 40 48 8B DA C7 44 24";

    private const int WindowOfView = 18144;    // main menu view: its window
    private const int MaxHeight = 0xC4;        // CInstantTextBox: the .gui maxHeight

    [Function(CallingConventions.Microsoft)] public delegate nint TextFn(nint view, nint text);

    private static IHook<TextFn>? _hook;
    private static readonly Logger Log = new("VersionLabel");

    private static string Tooltip() => $"HOI4ModLoader {Loader.Version}\n"
        + (Updater.Installed is { } update ? Loc.Format("HOIMOD_UPDATE_INSTALLED", "{0} installed: restart the game to use it", update) + "\n" : "")
        + (Loader.LoadedCount == 0
        ? Loc.Get("HOIMOD_NO_PLUGINS", "No plugins loaded")
        : string.Join("\n", Loader.LoadedPlugins.Select(p => $"- {Loader.Name(p)} {p.Version}")));

    internal static void Install()
    {
        _hook = Patches.Hook<TextFn>(Sig.Find(TextSig), (view, text) =>
        {
            var result = _hook!.OriginalFunction(view, text);
            Log.Guard("version label", () =>
            {
                var box = Gui.FindTextBox(*(nint*)(view + WindowOfView), "version_label");
                var added = box != 0 && *(int*)(box + MaxHeight) >= 40 ? $"\nHOI4ModLoader {Loader.Version}" : " +";
                Native.SetMsvcString(text, [.. Native.MsvcString(text), .. Encoding.UTF8.GetBytes(added)]); // appended as bytes: the label is in the game's encoding
                Gui.SetTooltip(box, Tooltip);
            });
            return result;
        });
    }
}
