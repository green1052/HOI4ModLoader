using System.Globalization;

namespace HOI4ModLoader;

/// <summary>
/// INI-style settings file, like BepInEx's ConfigFile. <see cref="Bind{T}"/> returns the stored value,
/// writing the default (with its description) the first time so players can find and edit it.
/// </summary>
public sealed class ConfigFile
{
    private readonly string _path;
    private readonly List<string> _lines;
    private readonly HashSet<(string Section, string Key)> _bound = []; // bound through this instance
    // Settings with a list of values, by file: the settings menu reads its own ConfigFile of the same file.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, string Section, string Key), Func<IReadOnlyList<string>>> ChoiceLists = new();

    public ConfigFile(string path)
    {
        _path = path;
        _lines = File.Exists(path) ? [.. File.ReadAllLines(path)] : [];
    }

    /// <summary>The values the settings menu's arrows step through for a text setting (made each time it is shown).</summary>
    public void Choices(string section, string key, Func<IReadOnlyList<string>> choices) => ChoiceLists[(_path, section, key)] = choices;

    internal IReadOnlyList<string>? ChoicesOf(string section, string key) => ChoiceLists.TryGetValue((_path, section, key), out var choices) ? choices() : null;

    public T Bind<T>(string section, string key, T defaultValue, string description) where T : IConvertible
    {
        _bound.Add((section, key));
        var (sectionAt, keyAt) = Find(section, key);
        if (keyAt >= 0)
        {
            var raw = _lines[keyAt][(_lines[keyAt].IndexOf('=') + 1)..].Trim();
            try { return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException) { return defaultValue; }
        }

        Reload();
        (sectionAt, keyAt) = Find(section, key);
        if (keyAt >= 0) return Bind(section, key, defaultValue, description); // written meanwhile
        var entry = new[] { $"# {description}", $"{key} = {Convert.ToString(defaultValue, CultureInfo.InvariantCulture)}", "" };
        if (sectionAt < 0)
        {
            if (_lines.Count > 0 && _lines[^1] != "") _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.AddRange(entry);
        }
        else
        {
            var end = sectionAt + 1; // after the section's last entry: the file lists settings in the order they're bound
            while (end < _lines.Count && !_lines[end].TrimStart().StartsWith('[')) end++;
            _lines.InsertRange(end, entry);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, _lines);
        return defaultValue;
    }

    /// <summary>Every entry in file order: section, key, raw value and its description (the comment lines above it).</summary>
    public List<(string Section, string Key, string Value, string Description)> Entries()
    {
        var entries = new List<(string, string, string, string)>();
        var section = "";
        var comment = new List<string>();
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) comment.Add(line.TrimStart('#').Trim());
            else if (line.StartsWith('[') && line.EndsWith(']')) (section, comment) = (line[1..^1], []);
            else if (line.Split('=', 2) is [var key, var value])
            {
                entries.Add((section, key.Trim(), value.Trim(), string.Join(" ", comment)));
                comment.Clear();
            }
            else comment.Clear();
        }
        return entries;
    }

    /// <summary>Stores <paramref name="value"/> (adding the entry with its description if it's missing) and saves the file.</summary>
    public void Set<T>(string section, string key, T value, string description) where T : IConvertible
    {
        Bind(section, key, value, description);
        Reload();
        _lines[Find(section, key).Key] = $"{key} = {Convert.ToString(value, CultureInfo.InvariantCulture)}";
        File.WriteAllLines(_path, _lines);
    }

    /// <summary>
    /// Deletes the entries this instance never bound (settings a plugin no longer has), with their description, except in
    /// section <paramref name="keep"/>; a section that loses all its entries goes too. Returns the "section.key"s deleted.
    /// </summary>
    internal List<string> RemoveUnbound(string keep)
    {
        Reload();
        var removed = new List<string>();
        var emptied = new HashSet<string>();
        var kept = new List<string>();
        var section = "";
        var skipBlank = false;
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (skipBlank && line == "") // the blank line after a removed entry
            {
                skipBlank = false;
                continue;
            }
            skipBlank = false;
            if (IsHeader(line)) section = line[1..^1];
            else if (IsEntry(line, out var key) && section != keep && !_bound.Contains((section, key)))
            {
                removed.Add($"{section}.{key}");
                emptied.Add(section);
                while (kept.Count > 0 && kept[^1].TrimStart().StartsWith('#')) kept.RemoveAt(kept.Count - 1); // its description
                skipBlank = true;
                continue;
            }
            kept.Add(raw);
        }
        if (removed.Count == 0) return removed;

        // A section that lost entries and has none left goes whole, up to the next section.
        for (var i = kept.Count - 1; i >= 0; i--)
        {
            var line = kept[i].Trim();
            if (!IsHeader(line) || !emptied.Contains(line[1..^1])) continue;
            var end = i + 1;
            while (end < kept.Count && !IsHeader(kept[end].Trim())) end++;
            if (!kept.Skip(i + 1).Take(end - i - 1).Any(l => IsEntry(l.Trim(), out _))) kept.RemoveRange(i, end - i);
        }
        _lines.Clear();
        _lines.AddRange(kept);
        File.WriteAllLines(_path, _lines);
        return removed;
    }

    private static bool IsHeader(string line) => line.StartsWith('[') && line.EndsWith(']');

    /// <summary>A `key = value` line (trimmed), as <see cref="Entries"/> reads them.</summary>
    private static bool IsEntry(string line, out string key)
    {
        key = line.Split('=', 2) is [var k, _] && !line.StartsWith('#') && !IsHeader(line) ? k.Trim() : "";
        return key.Length > 0;
    }

    private void Reload()
    {
        _lines.Clear();
        if (File.Exists(_path)) _lines.AddRange(File.ReadAllLines(_path));
    }

    private (int Section, int Key) Find(string section, string key)
    {
        var sectionAt = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i].Trim();
            if (line.StartsWith('['))
            {
                if (sectionAt >= 0) break;
                if (line == $"[{section}]") sectionAt = i;
            }
            else if (sectionAt >= 0 && line.Split('=', 2) is [var k, _] && k.Trim() == key)
            {
                return (sectionAt, i);
            }
        }
        return (sectionAt, -1);
    }
}
