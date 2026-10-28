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

    private const int WindowOfView = 0x4DE0;    // main menu view: the window holding "version_label"
    private const int MaxHeight = 0xC4;        // CInstantTextBox: the .gui maxHeight
    private const long VtablesBegin = 0x142785000, VtablesEnd = 0x1430FD000;  // exe .rdata: where GUI vtables live (PE section bounds)
    private const long TextBegin = 0x140001000, TextEnd = 0x142785000;        // exe .text

    [Function(CallingConventions.Microsoft)] public delegate nint TextFn(nint view, nint text);

    private static IHook<TextFn>? _hook;
    private static readonly Logger Log = new("VersionLabel");

    private static string Tooltip() => $"HOI4ModLoader {Loader.Version}\n"
        + (Updater.Installed is { } update ? Loc.Format("HOIMOD_UPDATE_INSTALLED", "{0} installed: restart the game to use it", update) + "\n" : "")
        + (Loader.LoadedCount == 0
        ? Loc.Get("HOIMOD_NO_PLUGINS", "No plugins loaded")
        : string.Join("\n", Loader.LoadedPlugins.Select(p => $"- {Loader.Name(p)} {p.Version}")));

    // shortcut: 1.19.4 also calls this builder through a function table with other views, whose +0x4DE0 is not a
    // window; gate on a native-looking vtable so those calls are passed through untouched instead of faulting.
    private static bool LooksLikeGuiWindow(nint window)
    {
        if (window == 0) return false;
        var vtable = *(nint*)window;
        if (vtable < VtablesBegin || vtable >= VtablesEnd) return false;
        var find = *(nint*)(vtable + 15 * 8);
        return find >= TextBegin && find < TextEnd;
    }

    internal static void Install()
    {
        _hook = Patches.Hook<TextFn>(Sig.Find(TextSig), (view, text) =>
        {
            var result = _hook!.OriginalFunction(view, text);
            Log.Guard("version label", () =>
            {
                var window = *(nint*)(view + WindowOfView);
                if (!LooksLikeGuiWindow(window)) return;
                var box = Gui.Find(window, 15, "version_label");
                var added = box != 0 && *(int*)(box + MaxHeight) >= 40 ? $"\nHOI4ModLoader {Loader.Version}" : " +";
                Native.SetMsvcString(text, [.. Native.MsvcString(text), .. Encoding.UTF8.GetBytes(added)]); // as bytes: the label is in the game's encoding
                Gui.SetTooltip(box, Tooltip);
            });
            return result;
        });
    }
}
