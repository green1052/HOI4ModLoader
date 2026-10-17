using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader.Bootstrap;

/// <summary>
/// Start-up prefetch, since a cold start mostly waits on the disk. Each start records the files the game reads in its first
/// two minutes (prefetch.c, into HOI4ModLoader\cache\prefetch.txt). The next start reads them into the file cache on four
/// threads before the game asks. HOI4ModLoader\no-prefetch turns it off.
/// </summary>
internal static unsafe class Prefetch
{
    private const int RecordMs = 120000, PrefetchMs = 90000, Readers = 4;
    private static string[] _files = [];
    private static int _next = -1;
    private static long _deadline;

    /// <summary>Called first thing at the exe's entry point, before the game reads anything.</summary>
    public static void Start(string mods)
    {
        if (File.Exists(Path.Combine(mods, "no-prefetch"))) return;
        var list = Path.Combine(Directory.CreateDirectory(Path.Combine(mods, "cache")).FullName, "prefetch.txt");
        try { _files = File.ReadAllLines(list).Where(line => line.Length > 0).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // first start: no list yet
        _deadline = Environment.TickCount64 + PrefetchMs;
        for (var i = 0; i < Readers && _files.Length > 0; i++) new Thread(Read) { IsBackground = true, Name = "HOI4ModLoader prefetch" }.Start();
        if (hm_record_start() != 0) new Thread(() => Save(list)) { IsBackground = true, Name = "HOI4ModLoader prefetch list" }.Start();
    }

    private static void Read()
    {
        var buffer = new byte[1 << 20];
        for (int i; (i = Interlocked.Increment(ref _next)) < _files.Length && Environment.TickCount64 < _deadline;)
        {
            try
            {
                using var file = File.OpenHandle(_files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
                for (long at = 0, got; (got = RandomAccess.Read(file, buffer, at)) > 0; at += got) { }
            }
            catch (Exception) { } // an exception here would end the game
        }
    }

    private static void Save(string list)
    {
        Thread.Sleep(RecordMs);
        nuint length;
        var recorded = hm_record_stop(&length);
        try
        {
            if (length == 0) return;
            var seen = new HashSet<string>();
            var text = new StringBuilder();
            foreach (var path in new string(recorded, 0, (int)length).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (seen.Add(path)) text.Append(path).Append('\n'); // first-open order
            File.WriteAllText(list + ".tmp", text.ToString());
            File.Move(list + ".tmp", list, true);
        }
        catch (Exception) { } // keep the old list; an exception here would end the game
        finally { hm_record_free(recorded); }
    }

    // prefetch.c
    [DllImport("native")]
    private static extern int hm_record_start();

    [DllImport("native")]
    private static extern char* hm_record_stop(nuint* length);

    [DllImport("native")]
    private static extern void hm_record_free(char* list);
}
