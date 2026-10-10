using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// Adds the loader to the main menu's version label: a second line "HOI4ModLoader 1.0.0 (N plugins)" when the
/// HOI4ModLoader UI mod made the panel and label two lines tall, else " +N" on the version's line (a second line would run
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

    internal static void Install(Func<string> line, Func<string> suffix, Func<string> tooltip)
    {
        _hook = Patches.Hook<TextFn>(Sig.Find(TextSig), (view, text) =>
        {
            var result = _hook!.OriginalFunction(view, text);
            var box = Gui.FindTextBox(*(nint*)(view + WindowOfView), "version_label");
            var twoLines = box != 0 && *(int*)(box + MaxHeight) >= 40;
            var added = twoLines ? "\n" + line() : suffix();
            Native.SetMsvcString(text, [.. Native.MsvcString(text), .. Encoding.ASCII.GetBytes(added)]); // bytes: the label is in the game's encoding
            Gui.SetTooltip(box, tooltip);
            return result;
        });
    }
}
