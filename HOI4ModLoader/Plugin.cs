namespace HOI4ModLoader;

/// <summary>Plugin metadata, like BepInEx's [BepInPlugin]. Required on every <see cref="Plugin"/> subclass.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PluginAttribute(string id, string name, string version) : Attribute
{
    /// <summary>Unique id, e.g. "com.example.myplugin". Names its config, config\&lt;Id&gt;.cfg.</summary>
    public string Id { get; } = id;
    /// <summary>English name. Localisation keys: "&lt;id&gt;.name" for the name, "&lt;id&gt;.desc" for the description.</summary>
    public string Name { get; } = name;

    /// <summary>The plugin's version, shown in the log, the Mods tab and the main menu tooltip.</summary>
    public string Version { get; } = version;

    /// <summary>
    /// Game version the plugin's offsets are for, a prefix of launcher-settings.json's rawVersion (e.g. "1.19.3"). On a
    /// mismatch the plugin is skipped unless its config sets IgnoreGameVersion.
    /// </summary>
    public string? GameVersion { get; init; }

    /// <summary>
    /// The plugin changes game rules, not just UI or the player's own commands. It would desync lockstep multiplayer, so
    /// it runs in single player only. A plugin with a multiplayer-safe mode leaves this off and checks
    /// <see cref="Plugin.Multiplayer"/> instead.
    /// </summary>
    public bool AffectsGameState { get; init; }
}

/// <summary>Load this plugin after <paramref name="id"/>; skip it if that plugin is missing or failed.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class DependencyAttribute(string id) : Attribute
{
    /// <summary>The <see cref="PluginAttribute.Id"/> of the plugin depended on.</summary>
    public string Id { get; } = id;
}

/// <summary>Base class for plugins. Subclasses in HOI4ModLoader\plugins\*.dll load before the game's entry point and again on hot reload.</summary>
public abstract class Plugin
{
    /// <summary>This plugin's [Plugin] attribute.</summary>
    public PluginAttribute Info { get; internal set; } = null!;

    /// <summary>HOI4ModLoader\config\&lt;Id&gt;.cfg</summary>
    public ConfigFile Config { get; internal set; } = null!;

    /// <summary>Writes to LogOutput.log under the plugin's name.</summary>
    public Logger Log { get; internal set; } = null!;

    /// <summary>
    /// Whether this load is for multiplayer. Plugins reload when it changes, so <see cref="Load"/> can choose between
    /// changing game rules and sending only vanilla commands.
    /// </summary>
    public static bool Multiplayer => Loader.Multiplayer;

    /// <summary>Find everything first, then patch through <see cref="Patches"/>. Throwing reverts every patch this plugin made.</summary>
    public abstract void Load();
}
