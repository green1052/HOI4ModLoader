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

    private static readonly int Id = Environment.CurrentManagedThreadId; // plugins load on the main thread
    private static readonly List<Timer> Timers = new();
    private static IHook<PeekMessageFn>? _hook;
    private static readonly Logger Log = new("MainThread");

    /// <summary>
    /// Runs <paramref name="action"/> on the main thread at most every <paramref name="interval"/>, until the plugin that
    /// registered it (in <see cref="Plugin.Load"/>) is reverted or reloaded.
    /// </summary>
    public static void Every(TimeSpan interval, Action action)
    {
        if (_hook == null) Install();
        var timer = new Timer(action, (long)interval.TotalMilliseconds);
        Timers.Add(timer);
        Patches.Record(() => Timers.Remove(timer));
    }

    private static void Install()
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

    private static void Tick()
    {
        var now = Environment.TickCount64;
        foreach (var timer in Timers.ToArray()) // a timer may reload plugins, which removes and adds timers
        {
            if (now < timer.Next || !Timers.Contains(timer)) continue;
            timer.Next = now + timer.IntervalMs;
            try { timer.Action(); }
            catch (Exception e) { Log.Error(e.ToString()); }
        }
    }

    private sealed class Timer(Action action, long intervalMs)
    {
        public Action Action { get; } = action;
        public long IntervalMs { get; } = intervalMs;
        public long Next;
    }
}
