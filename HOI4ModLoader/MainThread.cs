using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// Periodic callbacks on the game's main (UI) thread, hooked into its message pump. The UI reads game state and sends
/// commands there, so plugins can too.
/// </summary>
public static class MainThread
{
    [Function(CallingConventions.Microsoft)]
    public delegate int PeekMessageFn(nint msg, nint hwnd, uint min, uint max, uint remove);

    private static readonly int Id = Environment.CurrentManagedThreadId; // plugins load on the main thread
    private static readonly List<Timer> Timers = [];
    private static IHook<PeekMessageFn>? _hook;
    private static readonly Logger Log = new("MainThread");

    /// <summary>
    /// Runs <paramref name="action"/> on the main thread at most every <paramref name="interval"/>, until the plugin that
    /// registered it is reverted or reloaded.
    /// </summary>
    public static void Every(TimeSpan interval, Action action)
    {
        Patches.RequireOwner();
        if (_hook == null) Install();
        var timer = new Timer(action, (long)interval.TotalMilliseconds,
            Breadcrumb.Label($"{Patches.Owner} timer {action.Method.DeclaringType?.FullName}.{action.Method.Name}"));
        Timers.Add(timer);
        Patches.Record(() => Timers.Remove(timer));
    }

    private static void Install()
    {
        var owner = Patches.Owner;
        Patches.Owner = "HOI4ModLoader"; // shared by all plugins, so not reverted with the first one
        try
        {
            var peek = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "PeekMessageW");
            _hook = Patches.Hook<PeekMessageFn>(peek, (msg, hwnd, min, max, remove) =>
            {
                var result = _hook!.OriginalFunction(msg, hwnd, min, max, remove);
                if (Environment.CurrentManagedThreadId == Id) Log.Guard("timers", Tick);
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
        Log.Guard("game tracking", Game.Track); // first, so a loaded save resets per-game state before timers run
        var now = Environment.TickCount64;
        var due = false; // most ticks have nothing due: skip copying the list
        foreach (var timer in Timers)
            if (now >= timer.Next) { due = true; break; }
        if (!due) return;
        foreach (var timer in Timers.ToArray()) // a timer may reload plugins, changing the list
        {
            if (now < timer.Next || !Timers.Contains(timer)) continue;
            timer.Next = now + timer.IntervalMs;
            var crumb = Breadcrumb.Enter(timer.Label);
            try { timer.Action(); }
            catch (Exception e)
            {
                if (timer.Failed) continue;
                timer.Failed = true; // log once
                Log.Error(e.ToString());
            }
            finally { Breadcrumb.Leave(crumb); }
        }
    }

    private sealed class Timer(Action action, long intervalMs, nint label)
    {
        public Action Action { get; } = action;
        public long IntervalMs { get; } = intervalMs;
        public nint Label { get; } = label;
        public long Next;
        public bool Failed;
    }
}
