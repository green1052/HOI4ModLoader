namespace HOI4ModLoader;

/// <summary>Writes to HOI4ModLoader\LogOutput.log (truncated every launch).</summary>
public sealed class Logger(string source)
{
    private static readonly string Path = System.IO.Path.Combine(Loader.ModsDir, "LogOutput.log");
    private static readonly Lock Gate = new();

    private readonly HashSet<string> _failed = [];

    public void Info(string message) => Write("INFO ", message);
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
            if (_failed.Add(what)) Error($"{what}: {e}");
        }
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {level} [{source}] {message}{Environment.NewLine}");
        }
        catch (IOException) { } // a locked or read-only log must not take the game down
    }

    internal static void Reset()
    {
        try
        {
            lock (Gate) File.WriteAllText(Path, "");
        }
        catch (IOException) { }
    }
}
