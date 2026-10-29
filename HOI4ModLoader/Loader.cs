using System.Diagnostics;
using System.Reflection;

using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace HOI4ModLoader;

/// <summary>Loads the plugins and runs the loader's own parts; its folders for plugins to use.</summary>
public static class Loader
{
    /// <summary>&lt;game&gt;\HOI4ModLoader (HOI4ModLoader.dll lives in HOI4ModLoader\core).</summary>
    public static readonly string ModsDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location)!, ".."));

    /// <summary>The game's folder (hoi4.exe, version.dll and the vanilla files).</summary>
    public static readonly string GameDir = Path.GetDirectoryName(ModsDir)!;

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
    private static FileSystemWatcher? _pluginWatcher;
    private static volatile bool _pluginsTouched = true; // a DLL in plugins\ was written: check (on the main thread) until it settles
    private static int _generation;
    private static bool _avLogged;

    /// <summary>Plugins are loading for the first time, before the game reads its files; false on later (hot) reloads.</summary>
    public static bool AtStartup { get; private set; } = true;

    /// <summary>Called once by the native bootstrap (version.dll), before the game's entry point.</summary>
    [UnmanagedCallersOnly]
    public static void Init()
    {
        try
        {
            Logger.Reset();
            // Managed AVs crash the game with faulting module "unknown" and leave nothing in crash.log; catch them first-chance
            // (they are uncatchable at the throw site) and write the managed stack while the process can still write.
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (e.Exception is not AccessViolationException) return;
                lock (typeof(Loader))
                {
                    if (_avLogged) return;
                    _avLogged = true;
                }
                Log.Error($"access violation in managed code, stack:\n{Environment.StackTrace}");
            };
            var config = new ConfigFile(Path.Combine(ModsDir, "config", "HOI4ModLoader.cfg"));
            if (config.Bind("Debug", "WaitForDebugger", false, "Pause startup until a debugger attaches to hoi4.exe."))
                while (!Debugger.IsAttached) Thread.Sleep(100);
            var hotReload = config.Bind("Debug", "HotReload", true,
                "Reload all plugins when a DLL in HOI4ModLoader\\plugins changes. Plugin state is lost.");
            Logger.Verbose = config.Bind("Debug", "VerboseLog", false,
                "Also write DEBUG lines to LogOutput.log: every unit move and front change the plugins make. Off: only actions, summaries and errors.");
            MapArrows.Enabled = config.Bind("Debug", "AiActionArrows", false,
                "Flash a map arrow for each front line action the AI plugins take (encirclements, breakouts, assaults, filling holes).");
            Log.Info($"HOI4 {Game.Version}, .NET {Environment.Version}, language {Loc.Language}");
            try { Updater.Finish(PluginsDir); } // installs an update staged last session, before plugins load
            catch (Exception e) { Log.Error($"update: {e.Message}"); }
            var updateHours = config.Bind("Update", "CheckHours", 6,
                "Check GitHub for a new HOI4ModLoader at startup and every this many hours; it installs at the next start. 0: startup only, -1: never.");
            if (updateHours >= 0) Updater.Start(TimeSpan.FromHours(updateHours));
            var followMultiplayer = true;
            try { _ = Game.IsMultiplayer; _ = Game.State; } // resolve now: plugins read them from game threads
            catch (Exception e)
            {
                followMultiplayer = false;
                Multiplayer = true; // fail closed: game rule plugins could desync multiplayer
                Log.Error($"multiplayer check unavailable, game rule plugins stay off: {e.Message}");
            }
            // Check every core signature now, so one broken by a game update is reported once at startup, not in play.
            foreach (var field in typeof(Loader).Assembly.GetTypes().SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)))
            {
                if (!field.IsLiteral || field.GetRawConstantValue() is not string value) continue;
                try
                {
                    if (field.GetCustomAttribute<SignatureAttribute>() is { } signature) Sig.FindAll(value, signature.Count, signature.Anchor);
                    else if (field.IsDefined(typeof(VTableAttribute))) Sig.VTable(value);
                }
                catch (Exception e) { Log.Error($"core signature {field.DeclaringType?.Name}.{field.Name}: {e.Message}"); }
            }

            // The loader's hooks first, so plugin hooks on the same function stack on top and reload cleanly.
            Patches.Owner = "HOI4ModLoader";
            try { GameFiles.Install(); }
            catch (Exception e) { Log.Error($"game files: {e.Message}"); }
            try { SettingsMods.Install(); }
            catch (Exception e) { Log.Error($"settings Mods tab: {e.Message}"); }
            try { VersionLabel.Install(); }
            catch (Exception e) { Log.Error($"version label: {e.Message}"); }
            try { MapArrows.Install(); }
            catch (Exception e) { Log.Error($"map arrows: {e.Message}"); }
            try { GameFixes.Install(); }
            catch (Exception e) { Log.Error($"game fixes: {e.Message}"); }
            Patches.Owner = null;

            LoadPlugins();
            AtStartup = false;

            Patches.Owner = "HOI4ModLoader";
            if (followMultiplayer) MainThread.Every(TimeSpan.FromMilliseconds(250), FollowMultiplayer);
            if (hotReload)
            {
                // Listing the plugin files each second cost the main thread a few ms; the watcher only flags a change.
                Directory.CreateDirectory(PluginsDir);
                _pluginWatcher = new FileSystemWatcher(PluginsDir, "*.dll") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                _pluginWatcher.Changed += (_, _) => _pluginsTouched = true;
                _pluginWatcher.Created += (_, _) => _pluginsTouched = true;
                _pluginWatcher.Deleted += (_, _) => _pluginsTouched = true;
                _pluginWatcher.Renamed += (_, _) => _pluginsTouched = true;
                _pluginWatcher.EnableRaisingEvents = true;
                MainThread.Every(TimeSpan.FromSeconds(1), ReloadIfChanged);
            }
            MainThread.Every(TimeSpan.FromMilliseconds(100), () =>
            {
                if (Environment.TickCount64 < _reloadAt) return;
                if (Multiplayer) return; // FollowMultiplayer reloads on return to single player
                _reloadAt = long.MaxValue;
                Reload();
            });
            var crashTest = config.Bind("Debug", "CrashTest", 0, CrashTestDescription);
            RemoveDroppedSettings(config, "", Log); // every setting is bound by now
            if (crashTest is 1 or 2)
            {
                config.Set("Debug", "CrashTest", 0, CrashTestDescription); // once
                var at = Environment.TickCount64 + 30_000;
                MainThread.Every(TimeSpan.FromSeconds(1), () => { if (Environment.TickCount64 >= at) CrashTest(crashTest); });
            }
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

    private const string CrashTestDescription =
        "Crash the game once, 30 seconds after start, to test HOI4ModLoader\\crash.log. 1: native fault in a timer, 2: wild memory write in managed code. Resets to 0.";

    private static unsafe void CrashTest(int kind)
    {
        if (kind == 1) ((delegate* unmanaged<void>)0x10)(); // native access violation
        *(int*)unchecked((nint)0x7FFF_FFFF_0000) = 0;       // wild write: .NET fatal error
    }

    // Reloads once the plugin DLLs changed and then stayed the same for a tick (a build may still be copying them).
    // Never in multiplayer. ponytail: hooks are rewritten while game threads run, which can crash a thread inside a hooked
    // prologue; suspend the other threads if that bites.
    private static void ReloadIfChanged()
    {
        if (Multiplayer || !_pluginsTouched) return;
        var files = PluginFiles();
        if (files == _loadedFiles || files != _changedFiles) // unchanged, or still being written: look again in a second
        {
            _pluginsTouched = files != _loadedFiles;
            _changedFiles = files;
            return;
        }
        _pluginsTouched = false;
        Log.Info("plugin files changed: reloading");
        Reload();
    }

    // Multiplayer is lockstep, so game rule plugins would desync other players. They are off from the multiplayer lobby on
    // and back on in single player.
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
        _reloadAt = long.MaxValue; // this reload covers any deferred one
        Reload();
    }

    /// <summary>
    /// Reloads every plugin once no further request came for half a second, so a burst of clicks costs one reload.
    /// </summary>
    internal static void RequestReload() => _reloadAt = Environment.TickCount64 + 500;

    /// <summary>A plugin's config file.</summary>
    internal static ConfigFile Config(string id) => new(Path.Combine(ModsDir, "config", id + ".cfg"));

    /// <summary>A plugin's name in the player's language ("&lt;id&gt;.name" in the localisation), else its [Plugin] name.</summary>
    internal static string Name(PluginAttribute plugin) => Loc.Get($"{plugin.Id}.name", plugin.Name);

    /// <summary>Whether the plugin is loaded now.</summary>
    internal static bool IsLoaded(string id) => Loaded.Any(p => p.Info.Id == id);

    /// <summary>Enabled but waiting for single player: a game rule plugin in multiplayer.</summary>
    internal static bool IsWaiting(PluginAttribute plugin) => !IsLoaded(plugin.Id) && IsEnabled(plugin.Id) && plugin.AffectsGameState && Multiplayer;

    /// <summary>Whether the plugins were last loaded for multiplayer.</summary>
    internal static bool Multiplayer { get; private set; }

    /// <summary>Whether the plugin's config lets it load ([General] Enabled).</summary>
    internal static bool IsEnabled(string id) => IsEnabled(Config(id));

    private static bool IsEnabled(ConfigFile config) => config.Bind("General", "Enabled", true, EnabledDescription);

    /// <summary>Switches the plugin on or off in its config, from the next reload.</summary>
    internal static void SetEnabled(string id, bool on) => Config(id).Set("General", "Enabled", on, EnabledDescription);

    private const string EnabledDescription = "Load this plugin.";

    private static void Reload()
    {
        for (var i = Loaded.Count - 1; i >= 0; i--) Patches.Revert(Loaded[i].Info.Id);
        Loaded.Clear();
        MapArrows.HideAll();
        Jobs.Reset();
        LoadPlugins();
    }

    private static string PluginFiles() => !Directory.Exists(PluginsDir) ? "" :
        string.Join("|", Directory.EnumerateFiles(PluginsDir, "*.dll").Order().Select(f => $"{f}@{File.GetLastWriteTimeUtc(f).Ticks}"));

    private static void LoadPlugins()
    {
        _loadedFiles = PluginFiles(); // before reading, so a change during the load reloads again
        var folder = Directory.Exists(PluginsDir);
        var allRead = folder; // every DLL was read, so the plugins found are all there are
        var files = new Dictionary<string, string>(); // assembly name -> file
        foreach (var file in folder ? Directory.EnumerateFiles(PluginsDir, "*.dll") : [])
        {
            try { files[AssemblyName.GetAssemblyName(file).Name!] = file; }
            catch (Exception e)
            {
                allRead = false;
                Log.Error($"{Path.GetFileName(file)}: {e.Message}");
            }
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
                    {
                        allRead = false;
                        Log.Error($"{type.FullName}: missing [Plugin]");
                    }
                    else if (!found.TryAdd(info.Id, (type, info)))
                        Log.Error($"{info.Id}: duplicate plugin id ({type.FullName})");
                }
            }
            catch (Exception e)
            {
                allRead = false;
                Log.Error($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        Known = [.. found.Values.Select(f => f.Info).OrderBy(i => i.Id, StringComparer.Ordinal)];
        Loc.Load([typeof(Loader).Assembly, .. found.Values.Select(f => f.Type.Assembly)]); // disabled plugins too, for their names
        if (AtStartup) // the game reads its interface files once, after this
        {
            GameFiles.Build([typeof(Loader).Assembly, .. found.Values.Where(f => IsEnabled(f.Info.Id)).Select(f => f.Type.Assembly).Distinct()]);
            // Not on hot reload: a DLL may be mid-copy. Nor with plugins still staged: an update that stopped midway.
            if (allRead && !Directory.Exists(Updater.Staged)) RemoveOrphanConfigs(found.Keys);
        }
        var state = new Dictionary<string, bool?>(); // null while loading, so a dependency cycle fails
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
            try // a plugin that throws is skipped alone
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
                    && !config.Bind("General", "IgnoreGameVersion", false, $"Load even if the game is not HOI4 {want}, the version this plugin was made for. May crash.")
                    && !SameVersion(Game.Version, want))
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
                RemoveDroppedSettings(config, "General", log);
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
    /// The game is the version <paramref name="want"/> was made for: parts compared one by one, an absent part (-1)
    /// counting as 0. The prefix match this replaces loaded a plugin made for 1.19.3 on a 1.19.3.1 hotfix, whose offsets
    /// may have moved.
    /// </summary>
    internal static bool SameVersion(string game, string want) =>
        System.Version.TryParse(game, out var a) && System.Version.TryParse(want, out var b)
        && Part(a.Major) == Part(b.Major) && Part(a.Minor) == Part(b.Minor)
        && Part(a.Build) == Part(b.Build) && Part(a.Revision) == Part(b.Revision);

    private static int Part(int part) => part < 0 ? 0 : part;

    /// <summary>
    /// Deletes settings that weren't bound on this load (outside <paramref name="keep"/>), e.g. ones a newer version dropped.
    /// A plugin's [General] belongs to the loader. Plugins must bind every setting in Load, even for features turned off.
    /// </summary>
    private static void RemoveDroppedSettings(ConfigFile config, string keep, Logger log)
    {
        try
        {
            var removed = config.RemoveUnbound(keep);
            if (removed.Count > 0) log.Info($"removed settings it no longer has: {string.Join(", ", removed)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Error($"config cleanup: {e.Message}"); }
    }

    /// <summary>
    /// Deletes config\com.green1052.*.cfg files whose plugin is gone. Other authors' configs are kept.
    /// </summary>
    private static void RemoveOrphanConfigs(IEnumerable<string> ids)
    {
        var dir = Path.Combine(ModsDir, "config");
        if (!Directory.Exists(dir)) return;
        var known = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "com.green1052.*.cfg").ToList())
        {
            if (known.Contains(Path.GetFileNameWithoutExtension(file))) continue;
            try
            {
                File.Delete(file);
                Log.Info($"removed {Path.GetFileName(file)}: its plugin is gone");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"remove {Path.GetFileName(file)}: {e.Message}"); }
        }
    }

    /// <summary>
    /// One load of the plugins folder. Plugins are read into memory, so their files stay unlocked for rebuilds. Each reload
    /// needs a new context; old ones stay loaded, since hooks marshal plugin delegates to native code. Assemblies the
    /// loader has (HOI4ModLoader, Reloaded.Hooks, .NET) come from its context, so types are shared.
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
