using System.Runtime.InteropServices;

namespace HOI4ModLoader;

/// <summary>
/// Writes to HOI4ModLoader\LogOutput.log (truncated every launch). Past MaxBytes it rolls over to LogOutput.old.log: a long
/// game wrote 40 MB, most of it the AI plugins' per-division moves (now DEBUG lines).
/// </summary>
public sealed class Logger(string source)
{
    private static readonly string Path = System.IO.Path.Combine(Loader.ModsDir, "LogOutput.log");
    private static readonly string OldPath = System.IO.Path.Combine(Loader.ModsDir, "LogOutput.old.log");
    private const long MaxBytes = 20L << 20;

    /// <summary>[Debug] VerboseLog: <see cref="Debug"/> lines are written too.</summary>
    public static bool Verbose { get; internal set; }
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer; // kept open: opening the file per line cost every logged AI move a file open and close

    private readonly HashSet<string> _failed = [];

    /// <summary>Writes an INFO line: what happened, for reading back what a plugin did.</summary>
    public void Info(string message) => Write("INFO ", message);

    /// <summary>
    /// Writes a DEBUG line, only with [Debug] VerboseLog on: what a plugin does many times a game day (each division moved,
    /// each front's numbers), for tracing its decisions rather than reading back what it did.
    /// </summary>
    public void Debug(string message)
    {
        if (Verbose) Write("DEBUG", message);
    }

    /// <summary>Writes an ERROR line: something failed or is missing (a signature, a file, a setting).</summary>
    public void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Runs <paramref name="action"/>, logging its first exception (per <paramref name="what"/>) instead of throwing: for
    /// code after a hook's original (the hook guard would run the original again), timers and UI callbacks.
    /// </summary>
    public void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception e)
        {
            bool first;
            lock (_failed) first = _failed.Add(what); // hooks on AI threads guard too
            if (first) Error($"{what}: {e}");
        }
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_writer != null && _writer.BaseStream.Position > MaxBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                    File.Move(Path, OldPath, true); // a viewer holding it open without sharing: the next line tries again
                }
                _writer ??= new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} [{source}] {message}");
            }
        }
        catch (IOException) { } // a locked or read-only log must not take the game down
    }

    internal static void Reset()
    {
        try
        {
            lock (Gate)
            {
                _writer?.Dispose();
                _writer = null;
                File.WriteAllText(Path, "");
                File.Delete(OldPath); // the last launch's
            }
        }
        catch (IOException) { }
    }
}

/// <summary>
/// What loader or plugin code the current thread is in, kept natively (version.dll), where its crash handler reads it: a
/// native fault on the thread then names the hook or timer in HOI4ModLoader\crash.log. <see cref="Patches.Hook"/> and
/// <see cref="MainThread.Every"/> set it themselves; wrap any other code a game thread enters (an UnmanagedCallersOnly
/// thunk called from a <see cref="Patches.Asm"/> hook) in <see cref="Enter"/> / <see cref="Leave"/>.
/// </summary>
public static unsafe class Breadcrumb
{
    private static readonly delegate* unmanaged[SuppressGCTransition]<nint, nint> EnterFn = (delegate* unmanaged[SuppressGCTransition]<nint, nint>)Export("hm_enter");
    private static readonly delegate* unmanaged[SuppressGCTransition]<nint, void> LeaveFn = (delegate* unmanaged[SuppressGCTransition]<nint, void>)Export("hm_leave");
    private static readonly nint Exe = NativeLibrary.GetMainProgramHandle();
    private static readonly int ExeSize = *(int*)(Exe + *(int*)(Exe + 0x3C) + 0x50); // IMAGE_NT_HEADERS64.OptionalHeader.SizeOfImage

    /// <summary>A label for <see cref="Enter"/>: a native copy, never freed (a thread may still be inside).</summary>
    public static nint Label(string text) => Marshal.StringToHGlobalAnsi(text);

    /// <summary>"hoi4.exe+0x1A2B3C" (as the crash log and IDA name it), else the absolute address.</summary>
    public static string Where(nint at) => at >= Exe && at - Exe < ExeSize ? $"hoi4.exe+0x{at - Exe:X}" : $"0x{at:X}";

    /// <summary>Marks this thread as in <paramref name="label"/>; returns what to pass to <see cref="Leave"/> (in a finally).</summary>
    public static nint Enter(nint label) => EnterFn == null ? 0 : EnterFn(label);

    /// <summary>Restores the mark <see cref="Enter"/> returned.</summary>
    public static void Leave(nint previous)
    {
        if (LeaveFn != null) LeaveFn(previous);
    }

    private static nint Export(string name) =>
        NativeLibrary.TryLoad(Path.Combine(Loader.GameDir, "version.dll"), out var version)
        && NativeLibrary.TryGetExport(version, name, out var function) ? function : 0; // an older version.dll: no breadcrumbs
}
