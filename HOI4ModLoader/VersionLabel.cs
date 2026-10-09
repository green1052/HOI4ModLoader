using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// Adds the loader to the main menu's version label, a second line under "Operation Postern v1.19.3.0.c01a (5632) (S)".
/// Only that label's text: the game's version string (saves, multiplayer) is untouched.
/// </summary>
public static class VersionLabel
{
    /// <summary>Main menu view: writes the "version_label" text into a std::string (view, out), adding " (D)" in debug mode etc.</summary>
    [Signature] public const string TextSig = "48 89 54 24 ? 53 48 83 EC 40 48 8B DA C7 44 24";

    [Function(CallingConventions.Microsoft)] public delegate nint TextFn(nint view, nint text);

    static IHook<TextFn>? _hook;

    internal static void Install(Func<string> suffix)
    {
        _hook = Patches.Hook<TextFn>(Sig.Find(TextSig), (view, text) =>
        {
            var result = _hook!.OriginalFunction(view, text);
            Native.SetMsvcString(text, [.. Native.MsvcString(text), .. Encoding.ASCII.GetBytes(suffix())]); // bytes: the label is in the game's encoding
            return result;
        });
    }
}
