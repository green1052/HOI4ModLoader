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

    public ConfigFile(string path)
    {
        _path = path;
        _lines = File.Exists(path) ? [.. File.ReadAllLines(path)] : [];
    }

    public T Bind<T>(string section, string key, T defaultValue, string description) where T : IConvertible
    {
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
            _lines.InsertRange(sectionAt + 1, entry);
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
