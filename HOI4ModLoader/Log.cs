namespace HOI4ModLoader;

/// <summary>Writes to HOI4ModLoader\LogOutput.log (truncated every launch).</summary>
public sealed class Logger(string source)
{
    private static readonly string Path = System.IO.Path.Combine(Loader.ModsDir, "LogOutput.log");
    private static readonly Lock Gate = new();

    public void Info(string message) => Write("INFO ", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {level} [{source}] {message}{Environment.NewLine}");
    }

    internal static void Reset()
    {
        lock (Gate) File.WriteAllText(Path, "");
    }
}
