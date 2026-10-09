using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace HOI4ModLoader;

public static class Loader
{
    /// <summary>&lt;game&gt;\hoi4mods (HOI4ModLoader.dll lives in hoi4mods\core).</summary>
    public static readonly string ModsDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Loader).Assembly.Location)!, ".."));

    static readonly Logger Log = new("Loader");

    // Keeps plugin instances (and so their hook detours) alive for the whole session.
    static readonly List<Plugin> Loaded = new();

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

            Log.Info($"HOI4 {Game.Version}, .NET {Environment.Version}");
            LoadPlugins(Path.Combine(ModsDir, "plugins"));

            Patches.Owner = "HOI4ModLoader";
            Checksum.Install(Loaded.Select(p => p.Info).Where(i => i.AffectsGameState).ToList(), Log);
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

    static void LoadPlugins(string dir)
    {
        if (!Directory.Exists(dir)) return;

        // Plugins share the loader's load context so they see the same HOI4ModLoader/Reloaded types.
        var alc = AssemblyLoadContext.GetLoadContext(typeof(Loader).Assembly)!;
        alc.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };

        var found = new Dictionary<string, (Type Type, PluginAttribute Info)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
        {
            try
            {
                foreach (var type in alc.LoadFromAssemblyPath(file).GetTypes().Where(t => t.IsSubclassOf(typeof(Plugin)) && !t.IsAbstract))
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
}
