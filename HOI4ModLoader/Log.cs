namespace HOI4ModLoader;

/// <summary>Writes to hoi4mods\hoimod.log (truncated every launch).</summary>
public sealed class Logger(string source)
{
    static readonly string Path = System.IO.Path.Combine(Loader.ModsDir, "hoimod.log");
    static readonly object Gate = new();

    public void Info(string message) => Write("INFO ", message);
    public void Error(string message) => Write("ERROR", message);

    void Write(string level, string message)
    {
        lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {level} [{source}] {message}{Environment.NewLine}");
    }

    internal static void Reset()
    {
        lock (Gate) File.WriteAllText(Path, "");
    }
}
