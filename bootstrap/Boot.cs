using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader.Bootstrap;

/// <summary>
/// version.dll, the proxy the game loads from its folder (like BepInEx's Doorstop), built with Native AOT. native.c
/// forwards the real version.dll's exports and calls <see cref="Start"/> at hoi4.exe's entry point. It starts the profiler,
/// prefetch and crash log, then boots .NET 10 through hostfxr and runs HOI4ModLoader.Loader.Init.
/// </summary>
internal static unsafe class Boot
{
    private const int LoadAssemblyAndGetFunctionPointer = 5; // hostfxr_delegate_type.hdt_load_assembly_and_get_function_pointer
    private static readonly StringBuilder HostError = new(); // hostfxr errors (the game has no stderr)

    [StructLayout(LayoutKind.Sequential)]
    private struct InitializeParameters // hostfxr_initialize_parameters
    {
        public nuint Size;
        public char* HostPath;
        public char* DotnetRoot;
    }

    /// <summary>Called by native.c at the entry point, on the main thread. Returns Loader.Init, or 0 (mods off).</summary>
    [UnmanagedCallersOnly(EntryPoint = "hm_boot")]
    private static nint Start()
    {
        try
        {
            var mods = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "HOI4ModLoader");
            Optional(() => Profiler.Start(mods)); // first, to see the whole start-up
            Optional(() => Prefetch.Start(mods));
            Optional(() => OpenCrashLog(mods));
            return StartDotnet(Path.Combine(mods, "core"));
        }
        catch (Exception e) { return Fail($"HOI4ModLoader failed to start: {e.Message}"); } // never into native code
    }

    /// <summary>Runs an optional part; its failure doesn't stop the mods.</summary>
    private static void Optional(Action start)
    {
        try { start(); }
        catch (Exception) { }
    }

    private static nint StartDotnet(string core)
    {
        // The installed .NET 10 Runtime (x64), at its default location.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        var fxrPath = FindHostFxr(root);
        if (fxrPath == null || !NativeLibrary.TryLoad(fxrPath, out var fxr))
            return Fail(".NET 10 Runtime (x64) not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and start the game again.");
        if (!NativeLibrary.TryGetExport(fxr, "hostfxr_initialize_for_runtime_config", out var init)
            || !NativeLibrary.TryGetExport(fxr, "hostfxr_get_runtime_delegate", out var getDelegate)
            || !NativeLibrary.TryGetExport(fxr, "hostfxr_close", out var close))
            return Fail($"{fxrPath} is too old for .NET 10.");
        if (NativeLibrary.TryGetExport(fxr, "hostfxr_set_error_writer", out var setErrorWriter))
            ((delegate* unmanaged<delegate* unmanaged<char*, void>, nint>)setErrorWriter)(&OnHostError);

        var config = Path.Combine(core, "HOI4ModLoader.runtimeconfig.json");
        nint context = 0;
        int status;
        fixed (char* exe = Environment.ProcessPath, dotnetRoot = root, configPath = config)
        {
            var parameters = new InitializeParameters { Size = (nuint)sizeof(InitializeParameters), HostPath = exe, DotnetRoot = dotnetRoot };
            status = ((delegate* unmanaged<char*, InitializeParameters*, nint*, int>)init)(configPath, &parameters, &context);
        }
        if (status < 0 || context == 0) return Fail($"Failed to start .NET with {config} (is .NET 10 installed?)");
        nint load = 0;
        var got = ((delegate* unmanaged<nint, int, nint*, int>)getDelegate)(context, LoadAssemblyAndGetFunctionPointer, &load);
        ((delegate* unmanaged<nint, int>)close)(context);

        var assembly = Path.Combine(core, "HOI4ModLoader.dll");
        nint loaderInit = 0;
        fixed (char* assemblyPath = assembly, type = "HOI4ModLoader.Loader, HOI4ModLoader", method = "Init")
            if (got < 0 || load == 0
                || ((delegate* unmanaged<char*, char*, char*, char*, nint, nint*, int>)load)(assemblyPath, type, method, (char*)-1 /* UNMANAGEDCALLERSONLY_METHOD */, 0, &loaderInit) != 0
                || loaderInit == 0)
                return Fail($"Failed to load HOI4ModLoader.Loader.Init from {assembly}");
        return loaderInit;
    }

    /// <summary>&lt;root&gt;\host\fxr\&lt;highest version&gt;\hostfxr.dll, or null.</summary>
    private static string? FindHostFxr(string root)
    {
        var fxr = Path.Combine(root, "host", "fxr");
        if (!Directory.Exists(fxr)) return null;
        return Directory.GetDirectories(fxr)
            .Select(dir => (dir, version: Version.TryParse(Path.GetFileName(dir).Split('-')[0], out var v) ? v : null)) // "10.0.0-rc.1" counts as 10.0.0
            .Where(c => c.version != null && File.Exists(Path.Combine(c.dir, "hostfxr.dll")))
            .OrderByDescending(c => c.version)
            .Select(c => Path.Combine(c.dir, "hostfxr.dll"))
            .FirstOrDefault();
    }

    [UnmanagedCallersOnly]
    private static void OnHostError(char* message) => HostError.Append(new string(message)).Append('\n');

    /// <summary>
    /// Opens HOI4ModLoader\crash.log, kept across starts and deleted past 1 MB. native.c's fault handler writes to it, and
    /// it is stderr for .NET's fatal errors.
    /// </summary>
    private static void OpenCrashLog(string mods)
    {
        var path = Path.Combine(mods, "crash.log");
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > 1 << 20 }) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        nint file;
        fixed (char* name = path)
            file = CreateFileW(name, FileAppendData, 7 /* share read, write, delete */, 0, 4 /* OPEN_ALWAYS */, 0x80 /* normal */, 0);
        if (file == -1) return;
        SetStdHandle(-12 /* STD_ERROR_HANDLE */, file); // before .NET starts (Console.Error caches it)
        var header = Encoding.ASCII.GetBytes($"---- {DateTime.Now:HH:mm:ss.fff} hoi4.exe started, pid {Environment.ProcessId}\r\n");
        fixed (byte* bytes = header)
            WriteFile(file, bytes, (uint)header.Length, out _, 0);
        hm_crash_log(file);
    }

    private static nint Fail(string message)
    {
        var text = HostError.Length == 0 ? message : $"{message}\n\n{HostError}";
        fixed (char* body = text, caption = "HOI4ModLoader: mods disabled")
            MessageBoxW(0, body, caption, 0x30 /* MB_ICONWARNING */);
        return 0;
    }

    private const uint FileAppendData = 4;

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern nint CreateFileW(char* name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern int WriteFile(nint file, byte* buffer, uint length, out uint written, nint overlapped);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern int SetStdHandle(int which, nint handle);

    [DllImport("user32", ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, char* text, char* caption, uint type);

    [DllImport("native")]
    private static extern void hm_crash_log(nint file);
}
