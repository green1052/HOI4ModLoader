using System.IO.Compression;
using System.Text.Json;

namespace HOI4ModLoader;

/// <summary>
/// Auto-update from GitHub releases: checks at startup and every few hours, and installs a newer release for the next
/// start. Loaded files are renamed to *.old (deleted next start) and replaced. Plugins are staged in update\plugins and
/// moved in next start, so a reload can't pair new plugins with the old core.
/// </summary>
internal static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/green1052/HOI4ModLoader/releases/latest";
    private const string PluginsPrefix = "HOI4ModLoader/plugins/";
    internal static readonly string Staged = Path.Combine(Loader.ModsDir, "update", "plugins");
    private static readonly string Shipped = Path.Combine(Loader.ModsDir, "update", "plugins.txt"); // plugins the last update installed
    private static readonly string Moving = Path.Combine(Loader.ModsDir, "update", "moving");       // the staged plugins are being moved in
    private static readonly Logger Log = new("Updater");

    /// <summary>The release installed for the next start (e.g. "v1.2.0"), if any.</summary>
    internal static string? Installed { get; private set; }

    /// <summary>
    /// Plugins merged into others or dropped. Deleted at every start, so a manual install over an old one doesn't run them
    /// next to their replacements.
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

        // mimalloc, dropped in 1.3.1: slower than the Windows heap at speed 5.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Loader.ModsDir, "core"), "mimalloc*.dll"))
        {
            try { File.Delete(file); }
            catch (Exception) { }
        }

        var leftovers = Directory.EnumerateFiles(Loader.ModsDir, "*.old", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Loader.ModsDir, "*.new", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Loader.GameDir, "version.dll.*"));
        foreach (var file in leftovers.Where(f => !f.EndsWith("version.dll", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try { File.Delete(file); }
            catch (Exception) { } // still loaded: next start
        }

        if (!Directory.Exists(Staged)) return;
        var names = Directory.GetFiles(Staged).Select(f => Path.GetFileName(f)).ToArray();
        if (names.Length > 0)
        {
            // Replace the previous release's plugins; keep ones the player added. Once only: a move that failed midway leaves
            // the rest for the next start, which must not delete the ones already moved in.
            if (!File.Exists(Moving))
            {
                if (File.Exists(Shipped))
                    foreach (var name in File.ReadAllLines(Shipped).Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n)))
                    {
                        try { File.Delete(Path.Combine(pluginsDir, name!)); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"update: {name} stays: {e.Message}"); }
                    }
                File.WriteAllLines(Shipped, names);
                File.WriteAllText(Moving, "");
            }
            Directory.CreateDirectory(pluginsDir);
            foreach (var name in names) File.Move(Path.Combine(Staged, name), Path.Combine(pluginsDir, name), true);
            Log.Info($"plugins updated: {string.Join(", ", names)}");
        }
        File.Delete(Moving); // first: a later update must delete this one's plugins again
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
        foreach (var (entry, path) in entries) // write everything first, so a failure changes nothing
        {
            var target = Path.GetFullPath(path.StartsWith(PluginsPrefix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Staged, path[PluginsPrefix.Length..])
                : Path.Combine(Loader.GameDir, path));
            if (!target.StartsWith(Loader.GameDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"path outside the game folder: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target + ".new", true);
            files.Add(target);
        }
        var stamp = DateTime.UtcNow.Ticks; // unique .old: an earlier one may still be loaded
        var swapped = new List<string>();
        try
        {
            foreach (var file in files)
            {
                swapped.Add(file);
                if (File.Exists(file)) File.Move(file, $"{file}.{stamp}.old");
                File.Move(file + ".new", file);
            }
        }
        catch
        {
            // Half of two releases may not start: put the swapped files back (.new files go next start).
            foreach (var file in Enumerable.Reverse(swapped))
            {
                try
                {
                    // Swapped in (a new file too, so staged plugins don't move in next start); not the one whose rename failed.
                    if (File.Exists(file) && !File.Exists(file + ".new")) File.Move(file, file + ".new");
                    if (File.Exists($"{file}.{stamp}.old")) File.Move($"{file}.{stamp}.old", file);
                }
                catch (Exception e) { Log.Error($"restore {file}: {e.Message}"); }
            }
            throw;
        }
    }
}
