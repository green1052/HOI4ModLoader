namespace HOI4ModLoader;

/// <summary>Plugin metadata, like BepInEx's [BepInPlugin]. Required on every <see cref="Plugin"/> subclass.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PluginAttribute(string id, string name, string version) : Attribute
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Version { get; } = version;

    /// <summary>
    /// Game version the plugin was reverse engineered against, matched as a prefix of launcher-settings.json's rawVersion
    /// (e.g. "1.19.3"). Struct offsets are only known good for it: a signature can still match after an update while a field
    /// moved. On mismatch the plugin is skipped unless its config sets IgnoreGameVersion.
    /// </summary>
    public string? GameVersion { get; init; }

    /// <summary>The plugin changes simulation results. It is mixed into the game checksum, so a multiplayer lobby shows a
    /// checksum mismatch unless every player runs the same such plugins (instead of desyncing mid-game).</summary>
    public bool AffectsGameState { get; init; }
}

/// <summary>Load this plugin after <paramref name="id"/>; skip it if that plugin is missing or failed.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class DependencyAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>Base class for plugins. Every subclass in hoi4mods\plugins\*.dll is loaded once, before the game's entry point.</summary>
public abstract class Plugin
{
    public PluginAttribute Info { get; internal set; } = null!;

    /// <summary>hoi4mods\config\&lt;Id&gt;.cfg</summary>
    public ConfigFile Config { get; internal set; } = null!;

    public Logger Log { get; internal set; } = null!;

    /// <summary>Find everything first, then patch through <see cref="Patches"/>. Throwing reverts every patch this plugin made.</summary>
    public abstract void Load();
}
