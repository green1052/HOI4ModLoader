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

/// <summary>Marks a `const string` C++ class name whose vtable a mod looks up with <see cref="Sig.VTable"/>, so the tests check it too.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class VTableAttribute : Attribute;

/// <summary>Finds code in hoi4.exe by IDA-style byte patterns ("48 8B ?? 05"). No symbols ship with the game, so this is how a target is named.</summary>
public static class Sig
{
    /// <summary>The unique match in the running game; throws if it matches 0 or 2+ times (e.g. after a game update).</summary>
    public static nint Find(string pattern, string? anchor = null) => Image.Game.FindAll(pattern, 1, anchor)[0];

    /// <summary>All matches; throws unless there are exactly <paramref name="count"/> (e.g. the same code inlined twice).</summary>
    public static nint[] FindAll(string pattern, int count, string? anchor = null) => Image.Game.FindAll(pattern, count, anchor);

    /// <summary>vtable of the C++ class <paramref name="className"/> (e.g. "CSetResearchCommand"), found through its RTTI.</summary>
    public static nint VTable(string className) => Image.Game.VTable(className);
}

/// <summary>A PE image mapped in memory: the running game, or hoi4.exe mapped as data (tests).</summary>
public sealed unsafe class Image
{
    public static readonly Image Game = new(NativeLibrary.GetMainProgramHandle());

    readonly nint _base;
    readonly long _pointerBase; // what absolute pointers in the image are relative to: actual base when loaded, preferred base when mapped as data
    readonly List<(nint Start, int Size, bool Code)> _sections = new();
    readonly (int Begin, int End)[] _functions;

    Image(nint b, bool relocated = true)
    {
        _base = b;
        var nt = b + *(int*)(b + 0x3C);
        int count = *(ushort*)(nt + 6);
        var opt = nt + 24;
        _pointerBase = relocated ? b : *(long*)(opt + 24);
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
        return new Image(h & ~3, relocated: false); // low bits tag data-file handles
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

    /// <summary>
    /// vtable of <paramref name="className"/> via MSVC RTTI: the type descriptor holds ".?AV&lt;name&gt;@@", the complete
    /// object locator (signature 1, offset 0) points at it by RVA, and the vtable's slot -1 points at the locator.
    /// </summary>
    public nint VTable(string className)
    {
        var name = FindData(Encoding.ASCII.GetBytes($".?AV{className}@@\0"));
        if (name.Count != 1) throw new InvalidOperationException($"RTTI for {className} found {name.Count}x");
        var td = (int)(name[0] - 16 - _base);

        var locators = FindData(BitConverter.GetBytes(td)).Where(a => *(int*)(a - 12) == 1 && *(int*)(a - 8) == 0).Select(a => a - 12).ToList();
        var vtables = locators.SelectMany(col => FindData(BitConverter.GetBytes(_pointerBase + (col - _base)))).Select(a => a + 8).ToList();
        if (vtables.Count != 1) throw new InvalidOperationException($"vtable of {className} found {vtables.Count}x");
        return vtables[0];
    }

    List<nint> FindData(byte[] needle)
    {
        var hits = new List<nint>();
        foreach (var (start, size, code) in _sections)
        {
            if (code) continue;
            var span = new ReadOnlySpan<byte>((void*)start, size);
            for (int i = 0, n; (n = span[i..].IndexOf(needle)) >= 0; i += n + 1) hits.Add(start + i + n);
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
        // whole string, not the tail of a longer one
        var strings = FindData(Encoding.ASCII.GetBytes(text + "\0")).Where(a => *(byte*)(a - 1) == 0).ToHashSet();

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
