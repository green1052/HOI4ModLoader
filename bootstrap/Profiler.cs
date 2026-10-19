using System.Runtime.InteropServices;
using System.Text;

namespace HOI4ModLoader.Bootstrap;

/// <summary>
/// Sampling profiler (profiler.c, a pass about every millisecond).
/// - HOI4ModLoader\profile-startup: profiles the first 150 s into startup-profile.txt.
/// - Creating HOI4ModLoader\profile-now: profiles the next 60 s into profile-&lt;tick&gt;.txt.
/// The report lists each thread's running time, the hoi4.exe functions on running stacks (inclusive), the main thread's
/// own stacks in every module (tbb.dll's waits for parallel work included), and where the main thread waits.
/// </summary>
internal static unsafe class Profiler
{
    private const int StartupMs = 150000, OnDemandMs = 60000, Slots = 1 << 18;
    private static string _mods = "";
    private static uint _main;

    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        public ulong Key;
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadStats
    {
        public uint Id;
        public ulong Samples, Waiting;
    }

    /// <summary>Called first thing at the exe's entry point, on the main thread.</summary>
    public static void Start(string mods)
    {
        _mods = mods;
        _main = GetCurrentThreadId();
        var startup = File.Exists(Path.Combine(mods, "profile-startup"));
        new Thread(() => Run(startup)) { IsBackground = true, Name = "HOI4ModLoader profiler" }.Start();
    }

    private static void Run(bool startup)
    {
        // An exception on this thread would end the game, so a failed profile is skipped.
        try { if (startup) Profile(StartupMs, Path.Combine(_mods, "startup-profile.txt")); }
        catch (Exception) { }
        for (var trigger = Path.Combine(_mods, "profile-now");; Thread.Sleep(1000))
        {
            try
            {
                if (!File.Exists(trigger)) continue;
                File.Delete(trigger);
                Profile(OnDemandMs, Path.Combine(_mods, $"profile-{Environment.TickCount64}.txt"));
            }
            catch (Exception) { }
        }
    }

    private static void Profile(int ms, string output)
    {
        timeBeginPeriod(1); // 1 ms samples
        hm_profile((uint)ms, _main);
        timeEndPeriod(1);

        int count;
        ulong samples;
        var threads = new ReadOnlySpan<ThreadStats>(hm_profile_threads(&count, &samples), count).ToArray();
        var most = threads.Length == 0 ? 0 : threads.Max(t => t.Samples); // a thread alive throughout: one per pass
        var text = new StringBuilder($"samples {samples} over {ms} ms; a thread alive throughout was sampled {most} times (every {(most == 0 ? 0 : (double)ms / most):0.0} ms)\r\n");
        text.Append("== threads (id: running / sampled)\r\n");
        foreach (var t in threads.OrderByDescending(t => t.Samples - t.Waiting))
            text.Append($"{t.Id}{(t.Id == _main ? " (main)" : "")}: {t.Samples - t.Waiting} / {t.Samples}\r\n");
        Section(text, "hoi4.exe functions on running stacks (inclusive)", 1, 400);
        Section(text, "main thread: functions on running stacks, any module (inclusive)", 3, 400);
        Section(text, "main thread waits, by the hoi4.exe function they are called from", 2, 100);
        Section(text, "leaf addresses", 0, 300);
        File.WriteAllText(output, text.ToString());
    }

    private static void Section(StringBuilder text, string title, int table, int top)
    {
        ulong dropped;
        var slots = new ReadOnlySpan<Slot>(hm_profile_table(table, &dropped), Slots);
        var hits = new List<Slot>();
        foreach (var slot in slots)
            if (slot.Key != 0) hits.Add(slot);
        text.Append($"== {title} (dropped {dropped})\r\n");
        foreach (var hit in hits.OrderByDescending(h => h.Count).Take(top))
            text.Append($"{Where(hit.Key)} {hit.Count}\r\n");
    }

    /// <summary>"hoi4.exe+0x1a2b3c", or the bare address outside every module.</summary>
    private static string Where(ulong at)
    {
        nint module;
        var path = stackalloc char[260];
        if (GetModuleHandleExW(6 /* from address, unchanged refcount */, (char*)at, &module) != 0 && GetModuleFileNameW(module, path, 260) is > 0 and var length)
            return $"{Path.GetFileName(new string(path, 0, (int)length))}+0x{at - (ulong)module:x}";
        return $"0x{at:x}";
    }

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern int GetModuleHandleExW(uint flags, char* address, nint* module);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern uint GetModuleFileNameW(nint module, char* path, uint size);

    [DllImport("winmm", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint ms);

    // profiler.c
    [DllImport("native")]
    private static extern void hm_profile(uint ms, uint mainThread);

    [DllImport("native")]
    private static extern Slot* hm_profile_table(int which, ulong* dropped);

    [DllImport("native")]
    private static extern ThreadStats* hm_profile_threads(int* count, ulong* samples);
}
