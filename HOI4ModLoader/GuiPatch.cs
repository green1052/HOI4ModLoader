using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using CallingConventions = Reloaded.Hooks.Definitions.X64.CallingConventions;

namespace HOI4ModLoader;

/// <summary>
/// A game file the assembly changes, built at startup from the installed game's vanilla <see cref="File"/> plus
/// <see cref="Snippet"/> (an embedded gui\*.gui file): inserted after the <see cref="After"/> match, or replacing the
/// <see cref="Replace"/> match (the snippet can put back groups with ${1} etc.). Built from the game the player has, it
/// follows game updates without a new release; <see cref="GameFiles"/> shows it to the game.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GuiPatchAttribute(string file, string snippet) : Attribute
{
    /// <summary>Path in the game folder, e.g. "interface/settings.gui".</summary>
    public string File { get; } = file;

    /// <summary>Embedded resource name, e.g. "gui/settings_mods.gui" (every project embeds its gui\*.gui so).</summary>
    public string Snippet { get; } = snippet;

    /// <summary>Regex: the snippet goes in after each match.</summary>
    public string? After { get; init; }

    /// <summary>Regex: each match is replaced by the snippet.</summary>
    public string? Replace { get; init; }

    /// <summary>The snippet text from <paramref name="assembly"/>'s resources.</summary>
    public string ReadSnippet(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(Snippet) ?? throw new FileNotFoundException($"no embedded {Snippet}");
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary><paramref name="vanilla"/> with the snippet in, or null when the pattern isn't there (the game's GUI changed).</summary>
    public string? Apply(string vanilla, string snippet)
    {
        var patched = Replace != null ? Regex.Replace(vanilla, Replace, snippet) : Regex.Replace(vanilla, After!, m => m.Value + snippet);
        return patched == vanilla ? null : patched;
    }
}

/// <summary>
/// The <see cref="GuiPatchAttribute"/> files, written to HOI4ModLoader\ui and put in front of the game's virtual file system
/// (its PhysFS search path, which hoi4.exe exports): the game reads them instead of its own and mods' copies, with no mod
/// to install or enable. Only interface files: the checksum (common, events, history, map) stays vanilla.
/// </summary>
internal static class GameFiles
{
    [Function(CallingConventions.Microsoft)] public delegate int MountFn(nint dir, nint mountPoint, int appendToPath);
    [Function(CallingConventions.Microsoft)] public delegate int UnmountFn(nint dir);

    private const string MountExport = "?PHYSFS_mount@@YAHPEBD0H@Z", UnmountExport = "?PHYSFS_unmount@@YAHPEBD@Z";

    private static readonly Logger Log = new("GameFiles");
    private static readonly string Dir = Path.Combine(Loader.ModsDir, "ui");
    private static readonly string GameDir = Path.GetDirectoryName(Loader.ModsDir)!;
    private static IHook<MountFn>? _mount;
    private static UnmountFn? _unmount;
    private static nint _dir; // Dir in UTF-8, for PhysFS; never freed

    /// <summary>Before the game starts: every directory it mounts goes in behind ours (PhysFS mounts in front by default).</summary>
    internal static void Install()
    {
        var exe = NativeLibrary.GetMainProgramHandle();
        _unmount = Marshal.GetDelegateForFunctionPointer<UnmountFn>(NativeLibrary.GetExport(exe, UnmountExport));
        _dir = Marshal.StringToCoTaskMemUTF8(Dir);
        _mount = Patches.Hook<MountFn>(NativeLibrary.GetExport(exe, MountExport), (dir, mountPoint, appendToPath) =>
        {
            var result = _mount!.OriginalFunction(dir, mountPoint, appendToPath);
            if (result != 0 && dir != _dir && Directory.Exists(Dir))
            {
                _unmount(_dir);
                _mount.OriginalFunction(_dir, 0, 0);
            }
            return result;
        });
    }

    /// <summary>Writes the files of <paramref name="assemblies"/> anew (left out: vanilla). Before the game reads its interface.</summary>
    internal static void Build(IEnumerable<Assembly> assemblies)
    {
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true); // a disabled plugin's or a game update's leftovers
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"can't clear {Dir}: {e.Message}");
        }
        var built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // file -> text so far: patches of one file stack
        foreach (var assembly in assemblies)
        foreach (var patch in assembly.GetCustomAttributes<GuiPatchAttribute>())
        {
            try
            {
                var text = built.TryGetValue(patch.File, out var earlier) ? earlier : File.ReadAllText(Path.Combine(GameDir, patch.File));
                var patched = patch.Apply(text, patch.ReadSnippet(assembly));
                if (patched == null)
                {
                    Log.Error($"{assembly.GetName().Name}: {patch.File} changed in this game version, left vanilla");
                    continue;
                }
                var target = Path.Combine(Dir, patch.File);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, built[patch.File] = patched);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"{assembly.GetName().Name}: {patch.File}: {e.Message}");
            }
        }
    }
}
