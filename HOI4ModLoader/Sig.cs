using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader;

/// <summary>
/// Marks a `const string` IDA-style pattern, so the signature tests check it against the installed hoi4.exe.
/// Arguments mirror <see cref="Image.FindAll"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SignatureAttribute(int count = 1) : Attribute
{
    /// <summary>How many matches the pattern must have.</summary>
    public int Count { get; } = count;

    /// <summary>A string the matching function must reference (see <see cref="Image.FindAll"/>), for patterns not unique alone.</summary>
    public string? Anchor { get; init; }
}

/// <summary>Marks a `const string` C++ class name for <see cref="Sig.VTable"/>, so the tests check it too.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class VTableAttribute : Attribute;

/// <summary>Finds code in hoi4.exe by IDA-style byte patterns ("48 8B ?? 05"), since the game ships no symbols.</summary>
public static class Sig
{
    /// <summary>The unique match in the running game; throws on 0 or 2+ matches.</summary>
    public static nint Find(string pattern, string? anchor = null) => Image.Game.FindAll(pattern, 1, anchor)[0];

    /// <summary>All matches; throws unless there are exactly <paramref name="count"/>.</summary>
    public static nint[] FindAll(string pattern, int count, string? anchor = null) => Image.Game.FindAll(pattern, count, anchor);

    /// <summary>vtable of the C++ class <paramref name="className"/> (e.g. "CSetResearchCommand"), found through its RTTI.</summary>
    public static nint VTable(string className) => Image.Game.VTable(className);
}

/// <summary>A PE image mapped in memory: the running game, or hoi4.exe mapped as data (tests).</summary>
public sealed unsafe partial class Image
{
    /// <summary>The running hoi4.exe.</summary>
    public static readonly Image Game = new(NativeLibrary.GetMainProgramHandle());

    /// <summary>
    /// Guards the lookup caches and <see cref="Patches.Originals"/>: game threads look up lazily while a reload adds to them.
    /// </summary>
    internal static readonly Lock Gate = new();

    private readonly nint _base;
    private readonly long _pointerBase; // base for absolute pointers: actual when loaded, preferred when mapped as data
    private readonly List<(nint Start, int Size, bool Code)> _sections = [];
    private readonly (int Begin, int End)[] _functions;
    private readonly Dictionary<nint, (int Version, byte[] Bytes)> _pristine = []; // code sections with original bytes restored
    private readonly Dictionary<object, object> _lookups = [];                        // FindAll and VTable results or exceptions
    private readonly string _version;                                                  // the exe's link time and size
    private Dictionary<string, string>? _stored;                                       // results from earlier starts

    private Image(nint b, bool relocated = true)
    {
        _base = b;
        var nt = b + *(int*)(b + 0x3C);
        int count = *(ushort*)(nt + 6);
        var opt = nt + 24;
        _pointerBase = relocated ? b : *(long*)(opt + 24);
        _version = $"{*(uint*)(nt + 8):x8}-{*(int*)(opt + 56):x8}"; // IMAGE_FILE_HEADER.TimeDateStamp, IMAGE_OPTIONAL_HEADER64.SizeOfImage
        var sec = opt + *(ushort*)(nt + 20);
        for (var i = 0; i < count; i++, sec += 40)
            _sections.Add((b + *(int*)(sec + 12), *(int*)(sec + 8), (*(uint*)(sec + 36) & 0x20000000) != 0)); // IMAGE_SCN_MEM_EXECUTE

        // .pdata (exception directory, data directory #3): RUNTIME_FUNCTION { Begin, End, UnwindInfo } RVAs, sorted by Begin.
        var dir = opt + 112 + 3 * 8;
        var pdata = new ReadOnlySpan<int>((void*)(b + *(int*)dir), *(int*)(dir + 4) / 4);
        _functions = new (int, int)[pdata.Length / 3];
        for (var i = 0; i < _functions.Length; i++) _functions[i] = (pdata[i * 3], pdata[i * 3 + 1]);
    }

    /// <summary>Maps an exe/dll read-only with sections at their RVAs, without running it.</summary>
    public static Image Load(string path)
    {
        const uint asDataFile = 0x2, asImageResource = 0x20; // LOAD_LIBRARY_AS_DATAFILE, LOAD_LIBRARY_AS_IMAGE_RESOURCE
        var h = LoadLibraryExW(path, 0, asDataFile | asImageResource);
        if (h == 0) throw new FileNotFoundException($"cannot map {path} (error {Marshal.GetLastPInvokeError()})");
        return new Image(h & ~3, relocated: false); // low bits tag data-file handles
    }

    /// <summary>
    /// Every match of <paramref name="pattern"/> in code. With <paramref name="anchor"/>, only matches in functions that
    /// reference that exact string. Throws unless exactly <paramref name="count"/> match.
    /// </summary>
    public nint[] FindAll(string pattern, int count = 1, string? anchor = null) =>
        [.. Cached((pattern, count, anchor), () => Search(pattern, count, anchor))];

    /// <summary>
    /// Caches a lookup for the whole run, failures too, so a broken signature resolved every frame doesn't rescan the exe.
    /// </summary>
    private T Cached<T>(object key, Func<T> find) where T : notnull
    {
        lock (Gate)
        {
            if (!_lookups.TryGetValue(key, out var result))
            {
                var name = key.ToString()!; // (pattern, count, anchor) or a class name
                if (Stored()?.TryGetValue(name, out var line) == true) result = Parse<T>(line);
                else
                {
                    try
                    {
                        result = find();
                        Store(name, result); // only hits: failures are retried next start
                    }
                    catch (InvalidOperationException e) { result = e; }
                }
                _lookups[key] = result;
            }
            return result is Exception error ? throw new InvalidOperationException(error.Message) : (T)result;
        }
    }

    // ---- Lookups saved per exe version, saving about 3 s a start ----

    private string StoreFile => Path.Combine(Loader.ModsDir, "cache", $"signatures-{_version}.txt");

    /// <summary>Earlier starts' results for this exe ("key\tresult" lines), or null for an image mapped as data.</summary>
    private Dictionary<string, string>? Stored()
    {
        if (this != Game) return null;
        if (_stored != null) return _stored;
        _stored = [];
        try
        {
            foreach (var line in File.Exists(StoreFile) ? File.ReadAllLines(StoreFile) : [])
                if (line.IndexOf('\t') is var tab and > 0 && !line.AsSpan(tab + 1).StartsWith('!')) // "!": an old stored failure, retried
                    _stored[line[..tab]] = line[(tab + 1)..];
        }
        catch (IOException) { _stored.Clear(); } // unreadable: scan
        return _stored;
    }

    /// <summary>Appends a hit: RVAs in hex (space-separated for FindAll).</summary>
    private void Store(string name, object result)
    {
        if (Stored() is not { } stored || name.Contains('\t') || name.Contains('\n')) return;
        var value = result is nint[] hits ? string.Join(' ', hits.Select(h => (h - _base).ToString("x"))) : ((nint)result - _base).ToString("x");
        stored[name] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
            File.AppendAllText(StoreFile, $"{name}\t{value}\n");
        }
        catch (IOException) { } // read-only folder: scan next start
        catch (UnauthorizedAccessException) { }
    }

    private object Parse<T>(string value)
    {
        var hits = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => _base + (nint)Convert.ToInt64(h, 16)).ToArray();
        return typeof(T) == typeof(nint[]) ? hits : hits[0];
    }

    private nint[] Search(string pattern, int count, string? anchor)
    {
        var hits = Scan(pattern);
        if (anchor != null)
        {
            var funcs = FunctionsReferencing(anchor);
            hits = [.. hits.Where(h => FunctionAt(h) is { } f && funcs.Contains(f))];
        }
        if (hits.Count != count)
            throw new InvalidOperationException($"pattern matched {hits.Count}x (expected {count}): {pattern}{(anchor != null ? $" in functions using \"{anchor}\"" : "")}");
        return [.. hits];
    }

    private List<nint> Scan(string pattern)
    {
        var pat = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.StartsWith('?') ? -1 : Convert.ToInt32(t, 16)).ToArray();
        // Search for the longest run of concrete bytes (vectorized IndexOf), then compare the rest.
        var (first, length) = (-1, 0);
        for (int i = 0, run = 0; i < pat.Length; i++)
        {
            run = pat[i] < 0 ? 0 : run + 1;
            if (run > length) (first, length) = (i - run + 1, run);
        }
        if (first < 0) throw new ArgumentException("pattern has no concrete byte", nameof(pattern));
        var needle = pat[first..(first + length)].Select(b => (byte)b).ToArray();
        var hits = new List<nint>();
        foreach (var (start, size, code) in _sections)
        {
            if (!code) continue;
            var span = Unpatched(start, size);
            for (int i = first, next; i < size && (next = span[i..].IndexOf(needle)) >= 0; i++)
            {
                i += next;
                var at = i - first;
                if (at + pat.Length > size) break;
                var k = 0;
                while (k < pat.Length && (pat[k] < 0 || span[at + k] == pat[k])) k++;
                if (k == pat.Length) hits.Add(start + at);
            }
        }
        return hits;
    }

    /// <summary>A code section as the game shipped it, with patched bytes restored.</summary>
    private ReadOnlySpan<byte> Unpatched(nint start, int size)
    {
        var live = new ReadOnlySpan<byte>((void*)start, size);
        if (this != Game || !Patches.Originals.Keys.Any(a => a >= start && a < start + size)) return live;
        if (_pristine.TryGetValue(start, out var copy) && copy.Version == Patches.OriginalsVersion) return copy.Bytes;
        // Code only changes through Patches, which records originals first, so the copy only needs its overlays redone.
        var bytes = copy.Bytes ?? [.. live];
        foreach (var (at, original) in Patches.Originals.Reverse()) // earliest last: it has the game's bytes
        {
            var offset = (int)(at - start);
            if (offset >= 0 && offset < size) original.AsSpan(0, Math.Min(original.Length, size - offset)).CopyTo(bytes.AsSpan(offset));
        }
        _pristine[start] = (Patches.OriginalsVersion, bytes);
        return bytes;
    }

    /// <summary>
    /// vtable of <paramref name="className"/> via MSVC RTTI: type descriptor ".?AV&lt;name&gt;@@" &lt;- complete object
    /// locator (signature 1, offset 0, by RVA) &lt;- vtable slot -1.
    /// </summary>
    public nint VTable(string className) => Cached(className, () => FindVTable(className));

    private nint FindVTable(string className)
    {
        var name = FindData(Encoding.ASCII.GetBytes($".?AV{className}@@\0"));
        if (name.Count != 1) throw new InvalidOperationException($"RTTI for {className} found {name.Count}x");
        var td = (int)(name[0] - 16 - _base);

        var locators = FindData(BitConverter.GetBytes(td)).Where(a => *(int*)(a - 12) == 1 && *(int*)(a - 8) == 0).Select(a => a - 12).ToList();
        var vtables = locators.SelectMany(col => FindData(BitConverter.GetBytes(_pointerBase + (col - _base)))).Select(a => a + 8).ToList();
        if (vtables.Count != 1) throw new InvalidOperationException($"vtable of {className} found {vtables.Count}x");
        return vtables[0];
    }

    /// <summary>
    /// Whether the C++ object at <paramref name="obj"/> is or derives from <paramref name="className"/>, like dynamic_cast,
    /// via the RTTI class hierarchy.
    /// </summary>
    public bool IsA(nint obj, string className)
    {
        var locator = *(nint*)(*(nint*)obj - 8);
        var hierarchy = _base + *(int*)(locator + 16);
        var bases = _base + *(int*)(hierarchy + 12);
        var name = $".?AV{className}@@";
        for (int i = 0, n = *(int*)(hierarchy + 8); i < n; i++)
            if (Marshal.PtrToStringAnsi(_base + *(int*)(_base + *(int*)(bases + 4 * i)) + 16) == name) return true;
        return false;
    }

    private List<nint> FindData(byte[] needle)
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
    private int? FunctionAt(nint address)
    {
        var rva = (int)(address - _base);
        int lo = 0, hi = _functions.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (rva < _functions[mid].Begin) hi = mid - 1;
            else if (rva >= _functions[mid].End) lo = mid + 1;
            else return _functions[mid].Begin;
        }
        return null;
    }

    /// <summary>Functions that take the address of the null-terminated ASCII string <paramref name="text"/> (lea/mov reg,[rip+disp32]).</summary>
    private HashSet<int> FunctionsReferencing(string text)
    {
        var strings = FindData(Encoding.ASCII.GetBytes(text + "\0")).Where(a => *(byte*)(a - 1) == 0).ToHashSet(); // not a longer string's tail

        var funcs = new HashSet<int>();
        foreach (var (start, size, code) in _sections)
        {
            if (!code) continue;
            var span = Unpatched(start, size); // a hook over the lea must not hide it
            for (var i = 0; i + 7 <= size; i++)
            {
                // REX.W (48/4C) + 8D (lea) or 8B (mov), ModRM mod=00 rm=101: [rip+disp32]
                if ((span[i] & 0xFB) != 0x48 || (span[i + 1] != 0x8D && span[i + 1] != 0x8B) || (span[i + 2] & 0xC7) != 0x05) continue;
                if (strings.Contains(start + i + 7 + MemoryMarshal.Read<int>(span[(i + 3)..])) && FunctionAt(start + i) is { } f) funcs.Add(f);
            }
        }
        return funcs;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string path, nint file, uint flags);
}
