using System.Reflection;
using System.Text.RegularExpressions;

namespace HOI4ModLoader;

/// <summary>
/// Text in the game's language. Each assembly embeds localisation\&lt;language&gt;.yml files in the game's format, read
/// here without any HOI4 mod. A missing key falls back to English, then to the given text.
/// </summary>
public static partial class Loc
{
    /// <summary>The game's language, e.g. "korean" (settings.txt `language="l_korean"`), "english" if unknown.</summary>
    public static readonly string Language = ReadLanguage();

    private static Dictionary<string, string> _game = [], _english = [];

    /// <summary>The text of <paramref name="key"/> in the game's language, else in English, else <paramref name="fallback"/>.</summary>
    public static string Get(string key, string fallback) =>
        _game.TryGetValue(key, out var text) || _english.TryGetValue(key, out text) ? text : fallback;

    /// <summary><see cref="Get"/> with {0}, {1}... filled in (string.Format).</summary>
    public static string Format(string key, string fallback, params object[] args) => string.Format(Get(key, fallback), args);

    /// <summary>Reads the translations of <paramref name="assemblies"/>, replacing the last set.</summary>
    internal static void Load(IEnumerable<Assembly> assemblies)
    {
        Dictionary<string, string> game = [], english = [];
        foreach (var assembly in assemblies.Distinct())
        {
            Read(assembly, "english", english);
            if (Language != "english") Read(assembly, Language, game);
        }
        (_game, _english) = (game, english);
    }

    private static void Read(Assembly assembly, string language, Dictionary<string, string> into)
    {
        using var stream = assembly.GetManifestResourceStream($"localisation/{language}.yml");
        if (stream == null) return;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (Entry().Match(line) is { Success: true } m)
                into[m.Groups[1].Value] = m.Groups[2].Value.Replace("\\\"", "\"").Replace("\\n", "\n");
    }

    private static string ReadLanguage()
    {
        try
        {
            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paradox Interactive", "Hearts of Iron IV", "settings.txt");
            var match = Setting().Match(File.ReadAllText(settings));
            return match.Success ? match.Groups[1].Value : "english";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "english"; } // a type initializer must not throw
    }

    [GeneratedRegex("""^\s*([^\s:#]+):\d*\s*"(.*)"\s*$""")]
    private static partial Regex Entry();

    [GeneratedRegex(@"language\s*=\s*""l_(\w+)""")]
    private static partial Regex Setting();
}
