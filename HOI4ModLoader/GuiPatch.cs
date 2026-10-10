using System.Reflection;
using System.Text.RegularExpressions;

namespace HOI4ModLoader;

/// <summary>
/// A file of the assembly's UI mod (its mod\ folder, installed as Documents\Paradox Interactive\Hearts of Iron IV\mod\&lt;assembly
/// name&gt;), built at startup from the installed game's vanilla <see cref="File"/> plus <see cref="Snippet"/> (an embedded
/// gui\*.gui file): inserted after the <see cref="After"/> match, or replacing the <see cref="Replace"/> match (the snippet can
/// put back groups with ${1} etc.). Built from the game the player has, it follows game updates without a new release.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GuiPatchAttribute(string file, string snippet) : Attribute
{
    /// <summary>Path in the game folder and in the mod, e.g. "interface/settings.gui".</summary>
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

/// <summary>Writes every <see cref="GuiPatchAttribute"/> file into its installed UI mod, before the game reads its interface files.</summary>
internal static class UiMods
{
    private static readonly Logger Log = new("UI mods");
    private static readonly string UserDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paradox Interactive", "Hearts of Iron IV");
    private static readonly string GameDir = Path.GetDirectoryName(Loader.ModsDir)!;

    internal static void Generate(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var patches = assembly.GetCustomAttributes<GuiPatchAttribute>().ToArray();
            if (patches.Length == 0) continue;
            var name = assembly.GetName().Name!;
            if (ModDir(name) is not { } dir)
            {
                Log.Info($"{name}: its UI mod isn't installed (no mod\\{name}.mod)");
                continue;
            }
            foreach (var patch in patches)
            {
                var target = Path.Combine(dir, patch.File);
                try
                {
                    var vanilla = Path.Combine(GameDir, patch.File);
                    var patched = patch.Apply(File.ReadAllText(vanilla), patch.ReadSnippet(assembly));
                    if (patched == null)
                    {
                        File.Delete(target); // an old one would hide the updated vanilla file: the game's own is better
                        Log.Error($"{name}: {patch.File} changed in this game version, left vanilla");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target) || File.ReadAllText(target) != patched) File.WriteAllText(target, patched);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Error($"{name}: {patch.File}: {e.Message}");
                }
            }
        }
    }

    /// <summary>The installed mod's folder, from the path= line of Documents\...\mod\&lt;name&gt;.mod; null if it isn't installed.</summary>
    private static string? ModDir(string name)
    {
        var descriptor = Path.Combine(UserDir, "mod", name + ".mod");
        if (!File.Exists(descriptor)) return null;
        var path = File.ReadLines(descriptor).Select(l => Regex.Match(l, @"^\s*path\s*=\s*""(.*)""")).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        return path == null ? null : Path.GetFullPath(Path.Combine(UserDir, path)); // relative paths are from the user folder
    }
}
