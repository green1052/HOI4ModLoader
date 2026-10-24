using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using CallingConventions = Reloaded.Hooks.Definitions.X64.CallingConventions;

namespace HOI4ModLoader;

/// <summary>
/// A game file the assembly changes. At startup, <see cref="Snippet"/> (an embedded gui\*.gui) goes into the installed
/// game's <see cref="File"/>, after each <see cref="After"/> match or in place of each <see cref="Replace"/> match (${1}
/// etc. work). Built from the installed game, so it follows game updates. <see cref="GameFiles"/> serves it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GuiPatchAttribute(string file, string snippet) : Attribute
{
    /// <summary>Path in the game folder, e.g. "interface/settings.gui".</summary>
    public string File { get; } = file;

    /// <summary>Embedded resource name, e.g. "gui/settings_mods.gui".</summary>
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
/// Files written to HOI4ModLoader\ui and mounted first in the game's PhysFS search path, so the game reads them over its
/// own and mods' copies. Interface files only, so the checksum stays vanilla.
/// </summary>
public static class GameFiles
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string target, nint security);

    [Function(CallingConventions.Microsoft)] public delegate int MountFn(nint dir, nint mountPoint, int appendToPath);
    [Function(CallingConventions.Microsoft)] public delegate int UnmountFn(nint dir);

    private const string MountExport = "?PHYSFS_mount@@YAHPEBD0H@Z", UnmountExport = "?PHYSFS_unmount@@YAHPEBD@Z";

    private static readonly Logger Log = new("GameFiles");
    private static readonly string Dir = Path.Combine(Loader.ModsDir, "ui");
    private static IHook<MountFn>? _mount;
    private static UnmountFn? _unmount;
    private static nint _dir; // Dir in UTF-8, for PhysFS; never freed

    /// <summary>Before the game starts: keeps our directory in front of every directory the game mounts.</summary>
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

    /// <summary><see cref="Add(string, string)"/> with the file's contents (e.g. a plugin's embedded resource).</summary>
    public static void Add(string path, byte[] contents) => File.WriteAllBytes(Target(path), contents);

    /// <summary>
    /// Serves <paramref name="source"/> as game file <paramref name="path"/> (e.g. "gfx/fonts/korean/hoi_18mbs.dds"), over its
    /// own and mods' copies. Call from Load: the game reads most files once at startup. Hard link if possible, else a copy.
    /// </summary>
    public static void Add(string path, string source)
    {
        var target = Target(path);
        if (!CreateHardLinkW(target, source, 0)) File.Copy(source, target);
    }

    /// <summary>
    /// Where <paramref name="path"/> goes, with its folder made and any old file deleted (writing into an old hard link
    /// would change its source).
    /// </summary>
    private static string Target(string path)
    {
        var target = Path.Combine(Dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Delete(target);
        return target;
    }

    /// <summary>Rewrites the patched files of <paramref name="assemblies"/>, before the game reads its interface.</summary>
    internal static void Build(IEnumerable<Assembly> assemblies)
    {
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true); // stale files
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"can't clear {Dir}: {e.Message}");
        }
        try { Gui.WriteNumberIcons(Dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"number icons: {e.Message}"); }
        var built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // file -> text so far, so patches stack
        foreach (var assembly in assemblies)
        foreach (var patch in assembly.GetCustomAttributes<GuiPatchAttribute>())
        {
            try
            {
                var text = built.TryGetValue(patch.File, out var earlier) ? earlier : File.ReadAllText(Path.Combine(Loader.GameDir, patch.File));
                var patched = patch.Apply(text, patch.ReadSnippet(assembly));
                if (patched == null)
                {
                    Log.Error($"{assembly.GetName().Name}: {patch.File} changed in this game version, left vanilla");
                    continue;
                }
                // Localisation needs its BOM, which ReadAllText dropped.
                File.WriteAllText(Target(patch.File), built[patch.File] = patched, new UTF8Encoding(patch.File.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception e) // a bad pattern skips only this patch
            {
                Log.Error($"{assembly.GetName().Name}: {patch.File}: {e.Message}");
            }
        }
    }
}
