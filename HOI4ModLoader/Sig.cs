using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader;

/// <summary>
/// Marks a `const string` IDA-style pattern a mod uses, so the signature tests can check it against the
/// installed hoi4.exe without starting the game. Arguments mirror <see cref="Image.FindAll"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SignatureAttribute(int count = 1) : Attribute
{
    public int Count { get; } = count;
    public string? Anchor { get; init; }
}

/// <summary>Finds code in hoi4.exe by IDA-style byte patterns ("48 8B ?? 05"). No symbols ship with the game, so this is how a target is named.</summary>
public static class Sig
{
    /// <summary>The unique match in the running game; throws if it matches 0 or 2+ times (e.g. after a game update).</summary>
    public static nint Find(string pattern, string? anchor = null) => Image.Game.FindAll(pattern, 1, anchor)[0];
}

/// <summary>A PE image mapped in memory: the running game, or hoi4.exe mapped as data (tests).</summary>
public sealed unsafe class Image
{
    public static readonly Image Game = new(NativeLibrary.GetMainProgramHandle());

    readonly nint _base;
    readonly List<(nint Start, int Size, bool Code)> _sections = new();
    readonly (int Begin, int End)[] _functions;

    Image(nint b)
    {
        _base = b;
        var nt = b + *(int*)(b + 0x3C);
        int count = *(ushort*)(nt + 6);
        var opt = nt + 24;
        var sec = opt + *(ushort*)(nt + 20);
        for (int i = 0; i < count; i++, sec += 40)
            _sections.Add((b + *(int*)(sec + 12), *(int*)(sec + 8), (*(uint*)(sec + 36) & 0x20000000) != 0)); // IMAGE_SCN_MEM_EXECUTE

        // .pdata (exception directory, data directory #3): RUNTIME_FUNCTION { Begin, End, UnwindInfo } RVAs, sorted by Begin.
        var dir = opt + 112 + 3 * 8;
        var pdata = new ReadOnlySpan<int>((void*)(b + *(int*)dir), *(int*)(dir + 4) / 4);
        _functions = new (int, int)[pdata.Length / 3];
        for (int i = 0; i < _functions.Length; i++) _functions[i] = (pdata[i * 3], pdata[i * 3 + 1]);
    }

    /// <summary>Maps an exe/dll read-only with sections at their RVAs, without running it.</summary>
    public static Image Load(string path)
    {
        const uint LOAD_LIBRARY_AS_DATAFILE = 0x2, LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
        var h = LoadLibraryExW(path, 0, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (h == 0) throw new FileNotFoundException($"cannot map {path} (error {Marshal.GetLastPInvokeError()})");
        return new Image(h & ~3); // low bits tag data-file handles
    }

    /// <summary>
    /// Every match of <paramref name="pattern"/> in code. With <paramref name="anchor"/>, only matches inside functions that
    /// reference that exact string (engine strings survive updates better than code). Throws unless exactly <paramref name="count"/> match.
    /// </summary>
    public nint[] FindAll(string pattern, int count = 1, string? anchor = null)
    {
        var hits = Scan(pattern);
        if (anchor != null)
        {
            var funcs = FunctionsReferencing(anchor);
            hits = hits.Where(h => FunctionAt(h) is { } f && funcs.Contains(f)).ToList();
        }
        if (hits.Count != count)
            throw new InvalidOperationException($"pattern matched {hits.Count}x (expected {count}): {pattern}{(anchor != null ? $" in functions using \"{anchor}\"" : "")}");
        return hits.ToArray();
    }

    List<nint> Scan(string pattern)
    {
        var pat = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.StartsWith('?') ? -1 : Convert.ToInt32(t, 16)).ToArray();
        int first = Array.FindIndex(pat, b => b >= 0);
        if (first < 0) throw new ArgumentException("pattern has no concrete byte", nameof(pattern));
        var hits = new List<nint>();
        foreach (var (start, size, code) in _sections)
        {
            if (!code) continue;
            var span = new ReadOnlySpan<byte>((void*)start, size);
            // Jump between occurrences of the first concrete byte (vectorized IndexOf), then compare the rest.
            for (int i = first, next; (next = span[i..].IndexOf((byte)pat[first])) >= 0; i++)
            {
                i += next;
                int at = i - first;
                if (at + pat.Length > size) break;
                int k = 0;
                while (k < pat.Length && (pat[k] < 0 || span[at + k] == pat[k])) k++;
                if (k == pat.Length) hits.Add(start + at);
            }
        }
        return hits;
    }

    /// <summary>Begin RVA of the .pdata function containing <paramref name="address"/>.</summary>
    int? FunctionAt(nint address)
    {
        int rva = (int)(address - _base);
        int lo = 0, hi = _functions.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (rva < _functions[mid].Begin) hi = mid - 1;
            else if (rva >= _functions[mid].End) lo = mid + 1;
            else return _functions[mid].Begin;
        }
        return null;
    }

    /// <summary>Functions that take the address of the null-terminated ASCII string <paramref name="text"/> (lea/mov reg,[rip+disp32]).</summary>
    HashSet<int> FunctionsReferencing(string text)
    {
        var needle = Encoding.ASCII.GetBytes(text + "\0");
        var strings = new HashSet<nint>();
        foreach (var (start, size, code) in _sections)
        {
            if (code) continue;
            var span = new ReadOnlySpan<byte>((void*)start, size);
            for (int i = 0, n; (n = span[i..].IndexOf(needle)) >= 0; i += n + 1)
                if (i + n == 0 || span[i + n - 1] == 0) strings.Add(start + i + n); // whole string, not a suffix of a longer one
        }

        var funcs = new HashSet<int>();
        foreach (var (start, size, code) in _sections)
        {
            if (!code) continue;
            var span = new ReadOnlySpan<byte>((void*)start, size);
            for (int i = 0; i + 7 <= size; i++)
            {
                // REX.W (48/4C) + 8D (lea) or 8B (mov), ModRM mod=00 rm=101: [rip+disp32]
                if ((span[i] & 0xFB) != 0x48 || (span[i + 1] != 0x8D && span[i + 1] != 0x8B) || (span[i + 2] & 0xC7) != 0x05) continue;
                if (strings.Contains(start + i + 7 + *(int*)(start + i + 3)) && FunctionAt(start + i) is { } f) funcs.Add(f);
            }
        }
        return funcs;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint LoadLibraryExW(string path, nint file, uint flags);
}
