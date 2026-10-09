using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace HOI4ModLoader;

public static class Loader
{
    /// <summary>&lt;game&gt;\hoi4mods (HOI4ModLoader.dll lives in hoi4mods\core).</summary>
    public static readonly string ModsDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location)!, ".."));

    static readonly string PluginsDir = Path.Combine(ModsDir, "plugins");
    static readonly Logger Log = new("Loader");

    // Plugins loaded now, in load order (hooks keep reverted ones' detours alive: a game thread may still be in one).
    static readonly List<Plugin> Loaded = new();
    static string _loadedFiles = "", _changedFiles = "";
    static int _generation;
    static bool _multiplayer;

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

            Log.Info($"HOI4 {Game.Version}, .NET {Environment.Version}");
            LoadPlugins();

            Patches.Owner = "HOI4ModLoader";
            VersionLabel.Install(() => $" + HOI4ModLoader {typeof(Loader).Assembly.GetName().Version!.ToString(3)} ({Loaded.Count} plugins)");
            _ = Game.IsMultiplayer; // resolves its signature now, so a failure is reported once, at startup
            MainThread.Every(TimeSpan.FromMilliseconds(250), FollowMultiplayer);
            if (hotReload) MainThread.Every(TimeSpan.FromSeconds(1), ReloadIfChanged);
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
    static void ReloadIfChanged()
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
    static void FollowMultiplayer()
    {
        if (Game.IsMultiplayer == _multiplayer) return;
        _multiplayer = !_multiplayer;
        Log.Info(_multiplayer ? "multiplayer: game rule plugins off" : "single player: game rule plugins on");
        Reload();
    }

    static void Reload()
    {
        for (var i = Loaded.Count - 1; i >= 0; i--) Patches.Revert(Loaded[i].Info.Id);
        Loaded.Clear();
        LoadPlugins();
    }

    static string PluginFiles() => !Directory.Exists(PluginsDir) ? "" :
        string.Join("|", Directory.EnumerateFiles(PluginsDir, "*.dll").Order().Select(f => $"{f}@{File.GetLastWriteTimeUtc(f).Ticks}"));

    static void LoadPlugins()
    {
        _loadedFiles = PluginFiles(); // before reading them: a change during the load reloads again
        if (!Directory.Exists(PluginsDir)) return;
        var context = new PluginContext(PluginsDir, ++_generation);

        var found = new Dictionary<string, (Type Type, PluginAttribute Info)>();
        foreach (var file in Directory.EnumerateFiles(PluginsDir, "*.dll"))
        {
            try
            {
                var assembly = context.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(file)));
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

            var config = new ConfigFile(Path.Combine(ModsDir, "config", info.Id + ".cfg"));
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
            Patches.Owner = info.Id;
            try
            {
                plugin.Load();
                log.Info($"{info.Version} loaded");
                Loaded.Add(plugin);
                return true;
            }
            catch (Exception e)
            {
                Patches.Revert(info.Id);
                log.Error($"FAILED, all its patches reverted: {e.Message}");
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
    /// Anything not in the folder (HOI4ModLoader, Reloaded.Hooks, .NET) comes from the loader's context, so types are shared.
    /// </summary>
    sealed class PluginContext(string dir, int generation) : AssemblyLoadContext($"plugins #{generation}")
    {
        static readonly AssemblyLoadContext Core = GetLoadContext(typeof(Loader).Assembly)!;

        protected override Assembly? Load(AssemblyName name)
        {
            var dll = Path.Combine(dir, name.Name + ".dll");
            if (!File.Exists(dll)) return Core.LoadFromAssemblyName(name);
            var pdb = Path.ChangeExtension(dll, ".pdb");
            using var image = new MemoryStream(File.ReadAllBytes(dll));
            using var symbols = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
            return LoadFromStream(image, symbols);
        }
    }
}
