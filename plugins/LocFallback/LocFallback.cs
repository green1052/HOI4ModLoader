using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LocFallback;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.4.1), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>LoadFolder(root, token, folder, a4, a5, a6) (sub_1422AF9F0): enumerates a localisation folder
    /// (relative "localisation", the game's merged virtual tree of vanilla + all mods) and parses every file
    /// whose name contains the language token.</summary>
    [Signature] public const string LoadFolder =
        "48 8B C4 44 88 48 20 44 88 40 18 48 89 50 10 48 89 48 08 55 53 56 57 41 54 41 55 41 56 41 57 48 8D A8 28 FF FF FF 48 81 EC 98 01 00 00";

    /// <summary>ParseFile(bytes, length, filename, a4, a5, a6) (sub_14241DB90): parses one loc file's bytes;
    /// the l_…: first line picks the map the keys land in.</summary>
    [Signature] public const string ParseFile =
        "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 55 41 54 41 55 41 56 41 57 48 8D 6C 24 D9 48 81 EC A0 00 00 00 45 0F B6 E1 4D 8B F8 48 63 FA";
}

public static partial class LocLines
{
    /// <summary>A loc entry line: whitespace, the key, an optional version number, whitespace, the opening quote.</summary>
    [GeneratedRegex(@"^[ \t]*([A-Za-z0-9_.\-]+):(\d+)?[ \t]*""")]
    public static partial Regex Entry();
}

/// <summary>Mods without localisation in your language show raw keys. The game parses one folder pass for the
/// player's language only; a second pass as English inserts English text for the keys that pass does not define.</summary>
[Plugin("com.green1052.locfallback", "Loc Fallback", "1.0.0", GameVersion = "1.19.4.1")]
public class LocFallback : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate nint LoadFolderFn(nint root, nint token, nint a3, nint a4, nint a5, nint a6);
    [Function(CallingConventions.Microsoft)] public delegate nint ParseFileFn(nint bytes, nint length, nint filename, nint a4, nint a5, nint a6);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Header = "l_english:"u8.ToArray();
    private static nint English = Marshal.StringToHGlobalAnsi("l_english"); // alive for the process

    private static readonly HashSet<string> _known = new(StringComparer.Ordinal); // keys the player's language defines
    [ThreadStatic] private static bool _learning; // the game's own pass: collect its keys
    [ThreadStatic] private static bool _filling;  // our pass: insert English for unknown keys only
    [ThreadStatic] private static int _files;
    [ThreadStatic] private static int _keys;
    private static bool _done;
    private IHook<LoadFolderFn>? _loadFolder;
    private IHook<ParseFileFn>? _parseFile;

    public override void Load()
    {
        if (Loc.Language == "english") return;
        _parseFile = Patches.Hook<ParseFileFn>(Sig.Find(Signatures.ParseFile), File);
        _loadFolder = Patches.Hook<LoadFolderFn>(Sig.Find(Signatures.LoadFolder), Folder);
    }

    private nint Folder(nint root, nint token, nint a3, nint a4, nint a5, nint a6)
    {
        _learning = true;
        var result = _loadFolder!.OriginalFunction(root, token, a3, a4, a5, a6);
        _learning = false;
        if (_done || _filling || Marshal.PtrToStringAnsi(token) != $"l_{Loc.Language}") return result;

        _done = true;
        _filling = true;
        (_files, _keys) = (0, 0);
        try { _loadFolder.OriginalFunction(root, English, a3, a4, a5, a6); }
        finally { _filling = false; }
        if (_files > 0) Log.Info($"loc fallback: english -> {Loc.Language} ({_files} file(s), {_keys} new key(s))");
        return result;
    }

    private nint File(nint bytes, nint length, nint filename, nint a4, nint a5, nint a6)
    {
        if (!_learning && !_filling)
            return _parseFile!.OriginalFunction(bytes, length, filename, a4, a5, a6);

        var data = new byte[(int)length];
        Marshal.Copy(bytes, data, 0, (int)length);
        var text = Utf8(data);

        if (_learning)
        {
            foreach (var line in text.Split('\n'))
            {
                var key = LocLines.Entry().Match(line).Groups[1].Value;
                if (key != "") _known.Add(key);
            }
            return _parseFile!.OriginalFunction(bytes, length, filename, a4, a5, a6);
        }

        var start = data.AsSpan(0, Bom.Length).SequenceEqual(Bom) ? Bom.Length : 0;
        if (!data.AsSpan(start).StartsWith(Header)) // not an English file: parse it as it is
            return _parseFile!.OriginalFunction(bytes, length, filename, a4, a5, a6);

        // the player's language header, then only the lines whose key that language does not define
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        var added = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (i == 0) continue; // the l_english: header line, replaced below
            var key = LocLines.Entry().Match(lines[i]).Groups[1].Value;
            if (key != "" && _known.Contains(key)) continue; // the player's language has this key: never clobber it
            kept.Add(lines[i]);
            if (key != "") added++;
        }
        var copy = Encoding.UTF8.GetBytes((start == Bom.Length ? "\uFEFF" : "") + $"l_{Loc.Language}:\n" + string.Join('\n', kept));
        _files++;
        _keys += added;
        try
        {
            Marshal.Copy(copy, 0, bytes = Marshal.AllocHGlobal(copy.Length), copy.Length);
            return _parseFile!.OriginalFunction(bytes, copy.Length, filename, a4, a5, a6);
        }
        finally { Marshal.FreeHGlobal(bytes); }
    }

    private static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);
}
