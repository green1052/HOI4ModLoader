using System.IO.Compression;
using System.Text.Json;

namespace HOI4ModLoader;

/// <summary>
/// Auto-update from the GitHub releases: checks the latest release at startup (and every few hours) and installs a newer
/// one for the next start. Loaded files (version.dll, core\*.dll) can't be overwritten but can be renamed, so each file
/// is swapped in and the old one renamed to *.old, deleted at the next start. Plugins are staged in update\plugins and
/// moved in at the next start, before they load: a reload this session must not pair new plugins with the running core.
/// </summary>
internal static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/green1052/HOI4ModLoader/releases/latest";
    private const string PluginsPrefix = "HOI4ModLoader/plugins/";
    private static readonly string GameDir = Path.GetFullPath(Path.Combine(Loader.ModsDir, ".."));
    private static readonly string Staged = Path.Combine(Loader.ModsDir, "update", "plugins");
    private static readonly string Shipped = Path.Combine(Loader.ModsDir, "update", "plugins.txt"); // plugins the last update installed
    private static readonly Logger Log = new("Updater");

    /// <summary>The release installed for the next start (e.g. "v1.2.0"), if any.</summary>
    internal static string? Installed { get; private set; }

    /// <summary>
    /// Plugins later releases merged into others or dropped. Deleted at every start, before plugins load: a manual install over
    /// an old one (or a first update, with no plugins.txt yet) would otherwise run them next to their replacements.
    /// </summary>
    private static readonly string[] Retired =
        ["AirWingFieldMarshal", "AirbaseCapacity", "RailwayGuns", "AutoProduction", "NoTelemetry", "PlanTools", "SupplyOverlay", "WarStats"];

    /// <summary>Finishes an update installed last session: deletes the swapped-out files and moves the staged plugins in.</summary>
    internal static void Finish(string pluginsDir)
    {
        foreach (var name in Retired)
        foreach (var file in new[] { name + ".dll", name + ".pdb" }.Select(f => Path.Combine(pluginsDir, f)).Where(File.Exists))
        {
            try
            {
                File.Delete(file);
                Log.Info($"removed {Path.GetFileName(file)}: merged into another plugin or dropped");
            }
            catch (Exception e) { Log.Error($"remove {Path.GetFileName(file)}: {e.Message}"); }
        }

        var leftovers = Directory.EnumerateFiles(Loader.ModsDir, "*.old", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Loader.ModsDir, "*.new", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(GameDir, "version.dll.*"));
        foreach (var file in leftovers.Where(f => !f.EndsWith("version.dll", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try { File.Delete(file); }
            catch (Exception) { } // still loaded (e.g. version.dll by the launcher): next start
        }

        if (!Directory.Exists(Staged)) return;
        var names = Directory.GetFiles(Staged).Select(f => Path.GetFileName(f)).ToArray();
        if (names.Length > 0)
        {
            // Plugins get merged or renamed between versions: the previous release's go, ones the player added stay.
            if (File.Exists(Shipped))
                foreach (var name in File.ReadAllLines(Shipped)) File.Delete(Path.Combine(pluginsDir, Path.GetFileName(name)));
            Directory.CreateDirectory(pluginsDir);
            foreach (var name in names) File.Move(Path.Combine(Staged, name), Path.Combine(pluginsDir, name), true);
            File.WriteAllLines(Shipped, names);
            Log.Info($"plugins updated: {string.Join(", ", names)}");
        }
        Directory.Delete(Staged, true);
    }

    /// <summary>Checks now, then every <paramref name="every"/> (zero: only now) until an update is installed.</summary>
    internal static void Start(TimeSpan every) => Task.Run(async () =>
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"HOI4ModLoader/{Loader.Version}"); // GitHub's API requires one
        while (Installed == null)
        {
            try { await Check(http); }
            catch (Exception e) { Log.Error($"update check: {e.Message}"); }
            if (every <= TimeSpan.Zero) return;
            await Task.Delay(every);
        }
    });

    private static async Task Check(HttpClient http)
    {
        using var release = JsonDocument.Parse(await http.GetStringAsync(LatestRelease));
        var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var latest) || latest <= Version.Parse(Loader.Version)) return;
        var url = release.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => a.GetProperty("browser_download_url").GetString() ?? "")
            .FirstOrDefault(u => u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{tag} has no zip");
        Log.Info($"{tag} available: downloading {url}");
        using var zip = new ZipArchive(new MemoryStream(await http.GetByteArrayAsync(url)));
        Install(zip);
        Installed = tag;
        Log.Info($"{tag} installed: it takes effect at the next start");
    }

    private static void Install(ZipArchive zip)
    {
        var entries = zip.Entries.Where(e => e.Name != "").Select(e => (Entry: e, Path: e.FullName.Replace('\\', '/'))).ToList();
        if (!entries.Any(e => e.Path.Equals("HOI4ModLoader/core/HOI4ModLoader.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("zip is not a HOI4ModLoader release");

        var files = new List<string>();
        foreach (var (entry, path) in entries) // everything written first: a failed download or a full disk changes nothing
        {
            var target = Path.GetFullPath(path.StartsWith(PluginsPrefix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Staged, path[PluginsPrefix.Length..])
                : Path.Combine(GameDir, path));
            if (!target.StartsWith(GameDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"path outside the game folder: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target + ".new", true);
            files.Add(target);
        }
        var stamp = DateTime.UtcNow.Ticks; // a unique .old: an earlier one may still be loaded
        foreach (var file in files)
        {
            if (File.Exists(file)) File.Move(file, $"{file}.{stamp}.old");
            File.Move(file + ".new", file);
        }
    }
}
