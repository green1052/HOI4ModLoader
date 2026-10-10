namespace HOI4ModLoader;

/// <summary>Plugin metadata, like BepInEx's [BepInPlugin]. Required on every <see cref="Plugin"/> subclass.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PluginAttribute(string id, string name, string version) : Attribute
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Version { get; } = version;

    /// <summary>One or two sentences on what the plugin does, shown in the settings window's Mods tab.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Game version the plugin was reverse engineered against, matched as a prefix of launcher-settings.json's rawVersion
    /// (e.g. "1.19.3"). Struct offsets are only known good for it: a signature can still match after an update while a field
    /// moved. On mismatch the plugin is skipped unless its config sets IgnoreGameVersion.
    /// </summary>
    public string? GameVersion { get; init; }

    /// <summary>
    /// The plugin changes game rules (simulation results), not just what one player sees or clicks. Multiplayer runs every
    /// player's simulation in lockstep, so it would desync anyone without the plugin: it is off in multiplayer and works in
    /// single player only. Everything else (UI, sending the player's own commands) works in multiplayer too.
    /// </summary>
    public bool AffectsGameState { get; init; }

    /// <summary>Load only in multiplayer, e.g. a stand-in for an <see cref="AffectsGameState"/> plugin that only sends vanilla commands.</summary>
    public bool MultiplayerOnly { get; init; }
}

/// <summary>Load this plugin after <paramref name="id"/>; skip it if that plugin is missing or failed.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class DependencyAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>Base class for plugins. Every subclass in hoi4mods\plugins\*.dll is loaded before the game's entry point, and anew on hot reload.</summary>
public abstract class Plugin
{
    public PluginAttribute Info { get; internal set; } = null!;

    /// <summary>hoi4mods\config\&lt;Id&gt;.cfg</summary>
    public ConfigFile Config { get; internal set; } = null!;

    public Logger Log { get; internal set; } = null!;

    /// <summary>Find everything first, then patch through <see cref="Patches"/>. Throwing reverts every patch this plugin made.</summary>
    public abstract void Load();
}
