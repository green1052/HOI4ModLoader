using System.Linq.Expressions;
using System.Runtime.InteropServices;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;

namespace HOI4ModLoader;

/// <summary>
/// Every way a plugin changes the game. Each patch is recorded against the plugin being loaded, so a failing
/// <see cref="Plugin.Load"/> is rolled back entirely, and two plugins can't silently overwrite the same bytes.
/// Hooking itself is Reloaded.Hooks (https://github.com/Reloaded-Project/Reloaded.Hooks).
/// </summary>
public static unsafe partial class Patches
{
    /// <summary>Plugin id patches are recorded against (set by the loader around <see cref="Plugin.Load"/>).</summary>
    internal static string? Owner;

    private static readonly Dictionary<long, string> Claims = [];
    private static readonly Dictionary<string, List<Action>> Undo = [];
    // Holding them is the point: a thread may still be inside a reverted hook.
    // ReSharper disable once CollectionNeverQueried.Local
    private static readonly List<object> KeepAlive = [];

    /// <summary>
    /// The game's own bytes at every address a patch or hook changed (first one wins: a hook over a hook keeps the game's),
    /// so signature scans still find functions another plugin already hooked.
    /// </summary>
    internal static readonly Dictionary<nint, byte[]> Originals = [];
    internal static int OriginalsVersion;
    private static readonly Logger Log = new("Patches");

    private static IReloadedHooks Hooks => ReloadedHooks.Instance;

    /// <summary>
    /// Writes <paramref name="replace"/> over <paramref name="expect"/> (hex, same length) at <paramref name="at"/>.
    /// Throws if the bytes there aren't <paramref name="expect"/>: a game update, or another plugin's patch/hook.
    /// </summary>
    public static void Bytes(nint at, string expect, string replace)
    {
        RequireOwner();
        var want = Convert.FromHexString(expect.Replace(" ", ""));
        var with = Convert.FromHexString(replace.Replace(" ", ""));
        if (want.Length != with.Length) throw new ArgumentException("expect and replace differ in length");
        var found = new ReadOnlySpan<byte>((void*)at, want.Length);
        if (!found.SequenceEqual(want))
            throw new InvalidOperationException($"unexpected bytes at 0x{at:X}: {Convert.ToHexString(found)} (wanted {Convert.ToHexString(want)})");
        Claim(at, want.Length);
        RememberOriginal(at, want);
        WriteCode(at, with);
        Record(() => WriteCode(at, want));
    }

    /// <summary>
    /// Function hook, i.e. Harmony prefix/postfix: the detour runs instead of the function and calls
    /// <c>hook.OriginalFunction</c> when it wants. A managed exception in the detour is logged once and the
    /// original runs instead, so a plugin bug can't crash the game (call the original last, or it may run twice).
    /// </summary>
    public static IHook<T> Hook<T>(nint at, T detour) where T : Delegate
    {
        RequireOwner();
        IHook<T>? hook = null;
        var owner = Owner;
        var logged = false;
        Action<Exception> onError = e =>
        {
            if (logged) return;
            logged = true;
            Log.Error($"{owner}: hook at 0x{at:X} threw, running the original instead: {e}");
        };

        var args = typeof(T).GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        IEnumerable<Expression> argValues = args;
        var ex = Expression.Parameter(typeof(Exception), "e");
        // ReSharper disable once AccessToModifiedClosure
        Expression<Func<T>> original = () => hook!.OriginalFunction; // reads `hook` when called, i.e. after it's assigned below
        var crumb = Expression.Variable(typeof(nint), "crumb");
        var label = Breadcrumb.Label($"{owner} hook {Breadcrumb.Where(at)}");
        // The crash handler reads the breadcrumb: a native fault in the detour or in the original it calls names this hook.
        var guarded = Expression.Lambda<T>(Expression.Block([crumb],
            Expression.Assign(crumb, Expression.Call(typeof(Breadcrumb).GetMethod(nameof(Breadcrumb.Enter))!, Expression.Constant(label))),
            Expression.TryFinally(
                Expression.TryCatch(
                    Expression.Invoke(Expression.Constant(detour), argValues),
                    Expression.Catch(ex, Expression.Block(
                        Expression.Invoke(Expression.Constant(onError), ex),
                        Expression.Invoke(original.Body, argValues)))),
                Expression.Call(typeof(Breadcrumb).GetMethod(nameof(Breadcrumb.Leave))!, crumb))), args).Compile();

        hook = Hooks.CreateHook(guarded, at);
        Restorable(at, () => hook.Activate(), hook.Disable, hook);
        return hook;
    }

    /// <summary>
    /// Mid-function assembly hook, i.e. Harmony transpiler: replaces the instructions in [at, +length) with
    /// <paramref name="asm"/> (FASM syntax, "use64" first), then continues after them.
    /// </summary>
    public static IAsmHook Asm(nint at, int length, params string[] asm)
    {
        RequireOwner();
        Claim(at, length);
        var hook = Hooks.CreateAsmHook(asm, at, AsmHookBehaviour.DoNotExecuteOriginal, length);
        var written = Restorable(at, () => hook.Activate(), hook.Disable, hook);
        // The jump may need more bytes than given; then it cuts into the next instruction, which runs broken when the hook
        // returns there. Fail the load (reverting it) instead: give a length covering whole instructions past the jump.
        if (written > length) throw new InvalidOperationException($"asm hook at 0x{at:X} needs {written} bytes, {length} given");
        return hook;
    }

    /// <summary>Callable delegate for a game function.</summary>
    public static T Function<T>(nint at) where T : Delegate => Hooks.CreateWrapper<T>(at, out _);

    private static void Claim(nint at, int length)
    {
        var owner = Owner ?? "?";
        for (long a = at; a < at + length; a++)
            if (Claims.TryGetValue(a, out var other) && other != owner)
                throw new InvalidOperationException($"0x{a:X} is already patched by {other}");
        for (long a = at; a < at + length; a++) Claims[a] = owner;
    }

    /// <summary>
    /// Activates a hook and records how to take it out again: disable it (a thread already past the jump then runs the
    /// original), then put back the bytes it overwrote, so the code reads as never hooked and signatures match again
    /// (hot reload loads the plugin anew). Unless something hooked over it since: then it stays in the chain, disabled.
    /// </summary>
    private static int Restorable(nint at, Action activate, Action disable, object hook)
    {
        const int window = 64; // a hook's jump plus the instructions it cut through
        var before = new ReadOnlySpan<byte>((void*)at, window).ToArray();
        activate();
        var after = new ReadOnlySpan<byte>((void*)at, window).ToArray();
        var changed = window;
        while (changed > 0 && before[changed - 1] == after[changed - 1]) changed--;
        RememberOriginal(at, before[..changed]);
        Record(() =>
        {
            disable();
            if (new ReadOnlySpan<byte>((void*)at, changed).SequenceEqual(after.AsSpan(0, changed))) WriteCode(at, before[..changed]);
        }, hook); // also keeps the hook's delegates alive: a thread may still be inside it
        return changed;
    }

    private static void RememberOriginal(nint at, byte[] bytes)
    {
        lock (Image.Gate)
        {
            if (bytes.Length == 0 || !Originals.TryAdd(at, bytes)) return;
            OriginalsVersion++;
        }
    }

    /// <summary>Throws unless a plugin (or the loader) is being loaded: a patch nobody owns would never be reverted.</summary>
    internal static void RequireOwner()
    {
        if (Owner == null) throw new InvalidOperationException("patches belong to a plugin: make them in Plugin.Load");
    }

    /// <summary>Runs <paramref name="undo"/> when the current <see cref="Owner"/> is reverted (failed load, hot reload).</summary>
    internal static void Record(Action undo, object? keepAlive = null)
    {
        if (keepAlive != null) KeepAlive.Add(keepAlive);
        var owner = Owner ?? "?";
        if (!Undo.TryGetValue(owner, out var list)) Undo[owner] = list = [];
        list.Add(undo);
    }

    /// <summary>Undoes everything <paramref name="owner"/> patched, newest first.</summary>
    internal static void Revert(string owner)
    {
        if (!Undo.Remove(owner, out var list)) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            try { list[i](); }
            catch (Exception e) { Log.Error($"{owner}: revert: {e.Message}"); } // the rest still go: they are already off the list
        }
        foreach (var a in Claims.Where(c => c.Value == owner).Select(c => c.Key).ToList()) Claims.Remove(a);
    }

    private static void WriteCode(nint at, byte[] bytes)
    {
        if (!VirtualProtect(at, bytes.Length, 0x40, out var old)) // PAGE_EXECUTE_READWRITE
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"VirtualProtect 0x{at:X}");
        bytes.CopyTo(new Span<byte>((void*)at, bytes.Length));
        VirtualProtect(at, bytes.Length, old, out _);
        _ = FlushInstructionCache(-1, at, bytes.Length);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(nint address, nint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(nint process, nint address, nint size);
}
