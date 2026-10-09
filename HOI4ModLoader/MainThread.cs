using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// Periodic callbacks on the game's main (UI) thread, which pumps window messages every frame. That's where the UI
/// itself reads game state and sends commands, so plugins doing the same are safe there.
/// </summary>
public static class MainThread
{
    [Function(CallingConventions.Microsoft)]
    public delegate int PeekMessageFn(nint msg, nint hwnd, uint min, uint max, uint remove);

    static readonly int Id = Environment.CurrentManagedThreadId; // plugins load on the main thread
    static readonly List<(Action Action, long IntervalMs, long[] Next)> Timers = new();
    static IHook<PeekMessageFn>? _hook;
    static readonly Logger Log = new("MainThread");

    /// <summary>Runs <paramref name="action"/> on the main thread at most every <paramref name="interval"/>.</summary>
    public static void Every(TimeSpan interval, Action action)
    {
        if (_hook == null) Install();
        Timers.Add((action, (long)interval.TotalMilliseconds, new long[1]));
    }

    static void Install()
    {
        var owner = Patches.Owner;
        Patches.Owner = "HOI4ModLoader"; // shared by every plugin: don't revert it with whichever plugin asked first
        try
        {
            var peek = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "PeekMessageW");
            _hook = Patches.Hook<PeekMessageFn>(peek, (msg, hwnd, min, max, remove) =>
            {
                var result = _hook!.OriginalFunction(msg, hwnd, min, max, remove);
                if (Environment.CurrentManagedThreadId == Id) Tick();
                return result;
            });
        }
        finally
        {
            Patches.Owner = owner;
        }
    }

    static void Tick()
    {
        var now = Environment.TickCount64;
        foreach (var (action, interval, next) in Timers)
        {
            if (now < next[0]) continue;
            next[0] = now + interval;
            try { action(); }
            catch (Exception e) { Log.Error(e.ToString()); }
        }
    }
}
