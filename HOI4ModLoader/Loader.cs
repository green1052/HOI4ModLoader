using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace HOI4ModLoader;

public static class Loader
{
    /// <summary>&lt;game&gt;\hoi4mods (HOI4ModLoader.dll lives in hoi4mods\core).</summary>
    public static readonly string ModsDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location)!, ".."));

    private static readonly string PluginsDir = Path.Combine(ModsDir, "plugins");
    private static readonly Logger Log = new("Loader");

    // Plugins loaded now, in load order (hooks keep reverted ones' detours alive: a game thread may still be in one).
    private static readonly List<Plugin> Loaded = new();

    /// <summary>Every plugin found in the plugins folder at the last load, loaded or not, by id.</summary>
    internal static IReadOnlyList<PluginAttribute> Known { get; private set; } = [];
    private static long _reloadAt = long.MaxValue; // a requested reload runs once nothing new was requested for a moment
    private static string _loadedFiles = "", _changedFiles = "";
    private static int _generation;
    private static bool _multiplayer;

    /// <summary>Called once by the native bootstrap (version.dll), before the game's entry point.</summary>
    [UnmanagedCallersOnly]
    public static void Init()
    {
        Logger.Reset();
        try
        {
            var config = new ConfigFile(Path.Combine(ModsDir, "config", "HOI4ModLoader.cfg"));
            if (config.Bind("Debug", "WaitForDebugger", false, "Pause startup until a debugger attaches (Rider: Run > Attach to Process > hoi4.exe)."))
                while (!Debugger.IsAttached) Thread.Sleep(100);
            var hotReload = config.Bind("Debug", "HotReload", true,
                "Reload all plugins when a DLL in hoi4mods\\plugins changes, e.g. rebuilt while the game runs. Plugin state is lost.");
            if (config.Bind("Performance", "Mimalloc", true, "Use mimalloc for the game's memory allocations instead of the Windows heap (takes effect at the next start)."))
            {
                Patches.Owner = "HOI4ModLoader"; // never reverted: mimalloc's blocks can't go back to the Windows heap
                try
                {
                    Allocator.UseMimalloc();
                    Log.Info("mimalloc on");
                }
                catch (Exception e) { Log.Error($"mimalloc: {e.Message}"); }
                finally { Patches.Owner = null; }
            }

            Log.Info($"HOI4 {Game.Version}, .NET {Environment.Version}");
            var followMultiplayer = true;
            try { _ = Game.IsMultiplayer; } // resolves its signature now, before any game rule plugin loads
            catch (Exception e)
            {
                followMultiplayer = false;
                _multiplayer = true; // fail closed: unable to tell, game rule plugins could desync a multiplayer game
                Log.Error($"multiplayer check unavailable, game rule plugins stay off: {e.Message}");
            }
            LoadPlugins();

            Patches.Owner = "HOI4ModLoader";
            if (followMultiplayer) MainThread.Every(TimeSpan.FromMilliseconds(250), FollowMultiplayer);
            if (hotReload) MainThread.Every(TimeSpan.FromSeconds(1), ReloadIfChanged);
            MainThread.Every(TimeSpan.FromMilliseconds(100), () =>
            {
                if (Environment.TickCount64 < _reloadAt) return;
                _reloadAt = long.MaxValue;
                Reload();
            });
            try { SettingsMods.Install(); }
            catch (Exception e) { Log.Error($"settings Mods page: {e.Message}"); }
            try
            {
                var version = typeof(Loader).Assembly.GetName().Version!.ToString(3);
                VersionLabel.Install(() => $" +{Loaded.Count}",
                    () => $"HOI4ModLoader {version}\n" + (Loaded.Count == 0 ? "No plugins loaded" : string.Join("\n", Loaded.Select(p => $"- {p.Info.Name} {p.Info.Version}"))));
            }
            catch (Exception e) { Log.Error($"version label: {e.Message}"); }
        }
        catch (Exception e)
        {
            Log.Error(e.ToString());
        }
        finally
        {
            Patches.Owner = null;
        }
    }

    // Reloads once the plugin DLLs changed and then stayed the same for a tick (a build may still be copying them).
    // ponytail: patches are rewritten while game threads run; one executing a hooked prologue at that instant could crash.
    // Pause the game first if a plugin hooks simulation code; suspending the other threads is the upgrade if that bites.
    private static void ReloadIfChanged()
    {
        var files = PluginFiles();
        if (files == _loadedFiles || files != _changedFiles)
        {
            _changedFiles = files;
            return;
        }
        Log.Info("plugin files changed: reloading");
        Reload();
    }

    // Multiplayer runs every player's simulation in lockstep, so a plugin changing game rules would desync anyone without it.
    // Those plugins are off from the multiplayer lobby on (vanilla to the other players) and back on in single player.
    private static void FollowMultiplayer()
    {
        bool multiplayer;
        try { multiplayer = Game.IsMultiplayer; }
        catch (Exception e)
        {
            multiplayer = true; // fail closed
            if (!_multiplayer) Log.Error($"multiplayer check failed: {e.Message}");
        }
        if (multiplayer == _multiplayer) return;
        _multiplayer = !_multiplayer;
        Log.Info(_multiplayer ? "multiplayer: game rule plugins off" : "single player: game rule plugins on");
        Reload();
    }

    /// <summary>
    /// Reloads every plugin shortly (e.g. after a plugin was switched on or off): once no further request came for half a
    /// second, so a burst of clicks costs one reload.
    /// </summary>
    internal static void RequestReload() => _reloadAt = Environment.TickCount64 + 500;

    /// <summary>A plugin's config file.</summary>
    internal static ConfigFile Config(string id) => new(Path.Combine(ModsDir, "config", id + ".cfg"));

    /// <summary>Whether the plugin is loaded now.</summary>
    internal static bool IsLoaded(string id) => Loaded.Any(p => p.Info.Id == id);

    /// <summary>Whether the game is a multiplayer one (as the plugins were last loaded for).</summary>
    internal static bool Multiplayer => _multiplayer;

    /// <summary>Whether the plugin's config lets it load ([General] Enabled).</summary>
    internal static bool IsEnabled(string id) => Config(id).Bind("General", "Enabled", true, "Load this plugin.");

    private static void Reload()
    {
        for (var i = Loaded.Count - 1; i >= 0; i--) Patches.Revert(Loaded[i].Info.Id);
        Loaded.Clear();
        LoadPlugins();
    }

    private static string PluginFiles() => !Directory.Exists(PluginsDir) ? "" :
        string.Join("|", Directory.EnumerateFiles(PluginsDir, "*.dll").Order().Select(f => $"{f}@{File.GetLastWriteTimeUtc(f).Ticks}"));

    private static void LoadPlugins()
    {
        _loadedFiles = PluginFiles(); // before reading them: a change during the load reloads again
        if (!Directory.Exists(PluginsDir)) return;
        var files = new Dictionary<string, string>(); // assembly name -> file (a file may be named differently)
        foreach (var file in Directory.EnumerateFiles(PluginsDir, "*.dll"))
        {
            try { files[AssemblyName.GetAssemblyName(file).Name!] = file; }
            catch (Exception e) { Log.Error($"{Path.GetFileName(file)}: {e.Message}"); }
        }
        var context = new PluginContext(files, ++_generation);

        var found = new Dictionary<string, (Type Type, PluginAttribute Info)>();
        foreach (var (name, file) in files)
        {
            try
            {
                var assembly = context.LoadFromAssemblyName(new AssemblyName(name));
                foreach (var type in assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Plugin)) && !t.IsAbstract))
                {
                    if (type.GetCustomAttribute<PluginAttribute>() is not { } info)
                        Log.Error($"{type.FullName}: missing [Plugin]");
                    else if (!found.TryAdd(info.Id, (type, info)))
                        Log.Error($"{info.Id}: duplicate plugin id ({type.FullName})");
                }
            }
            catch (Exception e)
            {
                Log.Error($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        Known = found.Values.Select(f => f.Info).OrderBy(i => i.Id).ToList();
        var state = new Dictionary<string, bool?>(); // null while loading: a dependency cycle reads as failed
        foreach (var id in found.Keys.Order()) Load(id);

        bool Load(string id)
        {
            if (state.TryGetValue(id, out var done)) return done == true;
            state[id] = null;
            return (state[id] = TryLoad(id)) == true;
        }

        bool TryLoad(string id)
        {
            var (type, info) = found[id];
            var log = new Logger(info.Name);
            foreach (var dep in type.GetCustomAttributes<DependencyAttribute>())
            {
                if (found.ContainsKey(dep.Id) && Load(dep.Id)) continue;
                log.Error($"skipped: dependency {dep.Id} missing, failed or circular");
                return false;
            }

            Patches.Owner = info.Id;
            try // anything a plugin brings (constructor, config file, Load) only skips that plugin
            {
                var config = Config(info.Id);
                if (!config.Bind("General", "Enabled", true, "Load this plugin."))
                {
                    log.Info("disabled in config");
                    return false;
                }
                if (info.AffectsGameState && _multiplayer)
                {
                    log.Info("off in multiplayer");
                    return false;
                }
                if (info.MultiplayerOnly && !_multiplayer)
                {
                    log.Info("multiplayer only");
                    return false;
                }
                if (info.GameVersion is { } want
                    && !config.Bind("General", "IgnoreGameVersion", false, $"Load even if the game is not HOI4 {want}, which this plugin was made for (may crash).")
                    && !Game.Version.StartsWith(want, StringComparison.Ordinal))
                {
                    log.Error($"skipped: made for HOI4 {want}, game is {Game.Version}");
                    return false;
                }

                var plugin = (Plugin)Activator.CreateInstance(type)!;
                plugin.Info = info;
                plugin.Config = config;
                plugin.Log = log;
                plugin.Load();
                log.Info($"{info.Version} loaded");
                Loaded.Add(plugin);
                return true;
            }
            catch (Exception e)
            {
                Patches.Revert(info.Id);
                log.Error($"FAILED, all its patches reverted: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
                return false;
            }
            finally
            {
                Patches.Owner = null;
            }
        }
    }

    /// <summary>
    /// One load of the plugins folder. Plugins are read into memory, so their files stay unlocked and can be rebuilt while
    /// the game runs; each reload needs a new context, as a context loads an assembly name only once. Old contexts stay
    /// loaded: hooks marshal plugin delegates to native code, which collectible (unloadable) contexts don't support.
    /// Whatever the loader's context has (HOI4ModLoader, Reloaded.Hooks, .NET) comes from there, even if a copy sits in the
    /// folder, so types are shared.
    /// </summary>
    private sealed class PluginContext(Dictionary<string, string> files, int generation) : AssemblyLoadContext($"plugins #{generation}")
    {
        private static readonly AssemblyLoadContext Core = GetLoadContext(typeof(Loader).Assembly)!;

        protected override Assembly? Load(AssemblyName name)
        {
            try { return Core.LoadFromAssemblyName(name); }
            catch (FileNotFoundException) when (files.ContainsKey(name.Name!)) { }
            var dll = files[name.Name!];
            var pdb = Path.ChangeExtension(dll, ".pdb");
            using var image = new MemoryStream(File.ReadAllBytes(dll));
            using var symbols = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
            return LoadFromStream(image, symbols);
        }
    }
}
