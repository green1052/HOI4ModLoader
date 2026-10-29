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

    /// <summary>CContainerWindow: the window type the label lives in.</summary>
    [VTable] public const string ContainerWindow = "CContainerWindow";

    private const int WindowOfView = 0x4DE0;    // main menu view: the window holding "version_label"
    private const int MaxHeight = 0xC4;        // CInstantTextBox: the .gui maxHeight

    [Function(CallingConventions.Microsoft)] public delegate nint TextFn(nint view, nint text);

    private static IHook<TextFn>? _hook;
    private static readonly Logger Log = new("VersionLabel");

    private static string Tooltip() => $"HOI4ModLoader {Loader.Version}\n"
        + (Updater.Installed is { } update ? Loc.Format("HOIMOD_UPDATE_INSTALLED", "{0} installed: restart the game to use it", update) + "\n" : "")
        + (Loader.LoadedCount == 0
        ? Loc.Get("HOIMOD_NO_PLUGINS", "No plugins loaded")
        : string.Join("\n", Loader.LoadedPlugins.Select(p => $"- {Loader.Name(p)} {p.Version}")));

    // shortcut: 1.19.4 also calls this builder through a function table with other views, whose +0x4DE0 can be some
    // other GUI object; require a CContainerWindow-exact vtable so only real container windows are walked (any
    // container window is safe: its name Find just returns 0 when the label isn't in it).
    private static bool LooksLikeGuiWindow(nint window)
    {
        if (window == 0) return false;
        return *(nint*)window == Sig.VTable(ContainerWindow);
    }

    private static readonly HashSet<string> Logged = []; // one line per distinct outcome, to keep the log clean
    private static void Say(string outcome) { if (Logged.Add(outcome)) Log.Info(outcome); }

    internal static void Install()
    {
        _hook = Patches.Hook<TextFn>(Sig.Find(TextSig), (view, text) =>
        {
            var result = _hook!.OriginalFunction(view, text);
            Log.Guard("version label", () =>
            {
                var window = *(nint*)(view + WindowOfView);
                var vtable = window != 0 ? *(nint*)window : 0;
                if (!LooksLikeGuiWindow(window)) { Say($"skip: view 0x{view:X} window 0x{window:X} vtable 0x{vtable:X} (container vtable 0x{Sig.VTable(ContainerWindow):X})"); return; }
                var box = Gui.Find(window, 15, "version_label");
                Say($"found: window 0x{window:X} box 0x{box:X} maxHeight {(box != 0 ? *(int*)(box + MaxHeight) : -1)}");
                var added = box != 0 && *(int*)(box + MaxHeight) >= 40 ? $"\nHOI4ModLoader {Loader.Version}" : " +";
                Native.SetMsvcString(text, [.. Native.MsvcString(text), .. Encoding.UTF8.GetBytes(added)]); // as bytes: the label is in the game's encoding
                Gui.SetTooltip(box, Tooltip);
            });
            return result;
        });
    }
}
