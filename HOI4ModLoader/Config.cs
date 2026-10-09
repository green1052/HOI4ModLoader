using System.Globalization;

namespace HOI4ModLoader;

/// <summary>
/// INI-style settings file, like BepInEx's ConfigFile. <see cref="Bind{T}"/> returns the stored value,
/// writing the default (with its description) the first time so players can find and edit it.
/// </summary>
public sealed class ConfigFile
{
    readonly string _path;
    readonly List<string> _lines;

    public ConfigFile(string path)
    {
        _path = path;
        _lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new();
    }

    public T Bind<T>(string section, string key, T defaultValue, string description) where T : IConvertible
    {
        var (sectionAt, keyAt) = Find(section, key);
        if (keyAt >= 0)
        {
            var raw = _lines[keyAt][(_lines[keyAt].IndexOf('=') + 1)..].Trim();
            try { return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture); }
            catch (FormatException) { return defaultValue; }
        }

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

    (int Section, int Key) Find(string section, string key)
    {
        int sectionAt = -1;
        for (int i = 0; i < _lines.Count; i++)
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
