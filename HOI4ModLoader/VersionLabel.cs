using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// Adds the loader to the main menu's version label: " +N" (plugins) on its line (the label has room for little more,
/// and a second line would run out of the menu frame), and a tooltip with the loader's version and the loaded plugins. Only that label changes: the game's version
/// string (saves, multiplayer) is untouched.
/// </summary>
public static unsafe class VersionLabel
{
    /// <summary>Main menu view: writes the "version_label" text into a std::string (view, out), adding " (D)" in debug mode etc.</summary>
    [Signature] public const string TextSig = "48 89 54 24 ? 53 48 83 EC 40 48 8B DA C7 44 24";

    private const int WindowOfView = 18144;    // main menu view: its window
    private const int SetTooltipHandlerSlot = 69; // GUI object: SetTooltipHandler(CTooltipHandler*), used when the .gui has no tooltip

    [Function(CallingConventions.Microsoft)] public delegate nint TextFn(nint view, nint text);

    private static IHook<TextFn>? _hook;
    private static Func<string>? _tooltip;
    private static nint _handler; // a CTooltipHandler: { vtable } -> { BuildTooltip, destructor }

    internal static void Install(Func<string> line, Func<string> tooltip)
    {
        _tooltip = tooltip;
        var vtable = (nint*)NativeMemory.Alloc(2, (nuint)sizeof(nint)); // the GUI never frees a handler
        vtable[0] = (nint)(delegate* unmanaged<nint, nint, nint, void>)&BuildTooltip;
        vtable[1] = (nint)(delegate* unmanaged<nint, int, void>)&Destroy;
        _handler = (nint)NativeMemory.Alloc((nuint)sizeof(nint));
        *(nint*)_handler = (nint)vtable;

        _hook = Patches.Hook<TextFn>(Sig.Find(TextSig), (view, text) =>
        {
            var result = _hook!.OriginalFunction(view, text);
            Native.SetMsvcString(text, [.. Native.MsvcString(text), .. Encoding.ASCII.GetBytes(line())]); // bytes: the label is in the game's encoding
            var box = Gui.FindTextBox(*(nint*)(view + WindowOfView), "version_label");
            if (box != 0) ((delegate* unmanaged<nint, nint, void>)(*(nint**)box)[SetTooltipHandlerSlot])(box, _handler);
            return result;
        });
    }

    /// <summary>Called every frame while the label is hovered: the tooltip text goes into out's first std::string.</summary>
    [UnmanagedCallersOnly]
    private static void BuildTooltip(nint handler, nint guiObject, nint tooltip)
    {
        try
        {
            // $KEY$, [..] and § codes are expanded in tooltips: keep plugin names literal.
            var text = new string(_tooltip!().Where(c => c is not ('$' or '[' or ']' or '§')).ToArray());
            Native.SetMsvcString(tooltip, Encoding.ASCII.GetBytes(text));
        }
        catch { /* never throw into the game */ }
    }

    [UnmanagedCallersOnly]
    private static void Destroy(nint handler, int flags) { }
}
