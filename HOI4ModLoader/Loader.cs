using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace HOI4ModLoader;

public static class Loader
{
    /// <summary>&lt;game&gt;\HOI4ModLoader (HOI4ModLoader.dll lives in HOI4ModLoader\core).</summary>
    public static readonly string ModsDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location)!, ".."));

    private static readonly string PluginsDir = Path.Combine(ModsDir, "plugins");
    private static readonly Logger Log = new("Loader");

    // Plugins loaded now, in load order.
    private static readonly List<Plugin> Loaded = [];

    /// <summary>The loader's version, e.g. "1.0.0".</summary>
    internal static readonly string Version = typeof(Loader).Assembly.GetName().Version!.ToString(3);

    /// <summary>Plugins loaded now, in load order.</summary>
    internal static IEnumerable<PluginAttribute> LoadedPlugins => Loaded.Select(p => p.Info);

    /// <summary>How many plugins are loaded now.</summary>
    internal static int LoadedCount => Loaded.Count;

    /// <summary>Every plugin found in the plugins folder at the last load, loaded or not, by id.</summary>
    internal static IReadOnlyList<PluginAttribute> Known { get; private set; } = [];
    private static long _reloadAt = long.MaxValue; // a requested reload runs once nothing new was requested for a moment
    private static string _loadedFiles = "", _changedFiles = "";
    private static int _generation;
    private static bool _gameFilesBuilt;

    /// <summary>Called once by the native bootstrap (version.dll), before the game's entry point.</summary>
    [UnmanagedCallersOnly]
    public static void Init()
    {
        try
        {
            Logger.Reset();
            var config = new ConfigFile(Path.Combine(ModsDir, "config", "HOI4ModLoader.cfg"));
            if (config.Bind("Debug", "WaitForDebugger", false, "Pause startup until a debugger attaches to hoi4.exe."))
                while (!Debugger.IsAttached) Thread.Sleep(100);
            var hotReload = config.Bind("Debug", "HotReload", true,
                "Reload all plugins when a DLL in HOI4ModLoader\\plugins changes, e.g. rebuilt while the game runs. Plugin state is lost.");
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
            try { Updater.Finish(PluginsDir); } // before the plugins load: an update staged last session moves in now
            catch (Exception e) { Log.Error($"update: {e.Message}"); }
            var updateHours = config.Bind("Update", "CheckHours", 6,
                "Check GitHub for a newer HOI4ModLoader at startup and then every this many hours, and install it for the next start. 0: only at startup, -1: never.");
            if (updateHours >= 0) Updater.Start(TimeSpan.FromHours(updateHours));
            var followMultiplayer = true;
            try { _ = Game.IsMultiplayer; } // resolves its signature now, before any game rule plugin loads
            catch (Exception e)
            {
                followMultiplayer = false;
                Multiplayer = true; // fail closed: unable to tell, game rule plugins could desync a multiplayer game
                Log.Error($"multiplayer check unavailable, game rule plugins stay off: {e.Message}");
            }
            // Signatures the core resolves on first use: a broken one (game update) is reported here, once, not in play.
            foreach (var signature in new[] { Commands.PostSig, Commands.SessionHolderSig, Gui.SetTextSig, Gui.HasTextBoxSig })
            {
                try { Sig.Find(signature); }
                catch (Exception e) { Log.Error($"core signature: {e.Message}"); }
            }

            // The loader's own hooks first: a plugin hooking the same function then stacks on top, and reloads cleanly.
            Patches.Owner = "HOI4ModLoader";
            try { GameFiles.Install(); }
            catch (Exception e) { Log.Error($"game files: {e.Message}"); }
            try { SettingsMods.Install(); }
            catch (Exception e) { Log.Error($"settings Mods tab: {e.Message}"); }
            try { VersionLabel.Install(); }
            catch (Exception e) { Log.Error($"version label: {e.Message}"); }
            Patches.Owner = null;

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
            if (!Multiplayer) Log.Error($"multiplayer check failed: {e.Message}");
        }
        if (multiplayer == Multiplayer) return;
        Multiplayer = multiplayer;
        Log.Info(Multiplayer ? "multiplayer: game rule plugins off" : "single player: game rule plugins on");
        Reload();
    }

    /// <summary>
    /// Reloads every plugin shortly (e.g. after a plugin was switched on or off): once no further request came for half a
    /// second, so a burst of clicks costs one reload.
    /// </summary>
    internal static void RequestReload() => _reloadAt = Environment.TickCount64 + 500;

    /// <summary>A plugin's config file.</summary>
    internal static ConfigFile Config(string id) => new(Path.Combine(ModsDir, "config", id + ".cfg"));

    /// <summary>A plugin's name in the player's language ("&lt;id&gt;.name" in the localisation), else its [Plugin] name.</summary>
    internal static string Name(PluginAttribute plugin) => Loc.Get($"{plugin.Id}.name", plugin.Name);

    /// <summary>Whether the plugin is loaded now.</summary>
    internal static bool IsLoaded(string id) => Loaded.Any(p => p.Info.Id == id);

    /// <summary>Enabled but waiting for single player: a rule-changing plugin in multiplayer. It starts by itself when the mode changes.</summary>
    internal static bool IsWaiting(PluginAttribute plugin) => !IsLoaded(plugin.Id) && IsEnabled(plugin.Id) && plugin.AffectsGameState && Multiplayer;

    /// <summary>Whether the game is a multiplayer one (as the plugins were last loaded for).</summary>
    internal static bool Multiplayer { get; private set; }

    /// <summary>Whether the plugin's config lets it load ([General] Enabled).</summary>
    internal static bool IsEnabled(string id) => IsEnabled(Config(id));

    private static bool IsEnabled(ConfigFile config) => config.Bind("General", "Enabled", true, EnabledDescription);

    /// <summary>Switches the plugin on or off in its config (takes effect at the next reload).</summary>
    internal static void SetEnabled(string id, bool on) => Config(id).Set("General", "Enabled", on, EnabledDescription);

    private const string EnabledDescription = "Load this plugin.";

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

        Known = [.. found.Values.Select(f => f.Info).OrderBy(i => i.Id)];
        Loc.Load([typeof(Loader).Assembly, .. found.Values.Select(f => f.Type.Assembly)]); // disabled plugins' names too
        if (!_gameFilesBuilt)
        {
            _gameFilesBuilt = true; // once: the game reads its interface files at startup, after this and before its main menu
            GameFiles.Build([typeof(Loader).Assembly, .. found.Values.Where(f => IsEnabled(f.Info.Id)).Select(f => f.Type.Assembly).Distinct()]);
        }
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
                if (!IsEnabled(config))
                {
                    log.Info("disabled in config");
                    return false;
                }
                if (info.AffectsGameState && Multiplayer)
                {
                    log.Info("off in multiplayer");
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

        protected override Assembly Load(AssemblyName name)
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
