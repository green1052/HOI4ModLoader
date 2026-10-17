using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace HOI4ModLoader;

/// <summary>
/// The game's own heap (hoi4.exe's statically linked CRT). Memory the game frees, or that we free for it, must come from
/// here. Can put mimalloc (core\mimalloc.dll) under it through shims in version.dll (bootstrap\allocator.c); small frees
/// are quarantined because the game reads some blocks right after freeing them.
/// </summary>
public static unsafe class Allocator
{
    [Signature] public const string FreeSig = "48 85 C9 74 ? 53 48 83 EC 20 4C 8B C1 33 D2 48 8B 0D ? ? ? ? FF 15"; // _free_base
    [Signature] public const string MallocSig = "40 53 48 83 EC 20 48 8B D9 48 83 F9 E0 77 ? 48 85 C9 B8 01 00 00 00 48"; // _malloc_base
    [Signature] public const string CallocSig = "40 53 48 83 EC 20 4C 8B C2 48 8B D9 48 85 C9 74 ? 33 D2 48 8D 42 E0 48"; // _calloc_base
    [Signature] public const string ReallocSig = "48 89 5C 24 08 57 48 83 EC 20 48 8B DA 48 8B F9 48 85 C9 75 ? 48 8B CA E8 ? ? ? ? EB ? 48 85 DB 75 ? E8 ? ? ? ? EB ? 48 83 FB E0 76"; // _realloc_base
    [Signature] public const string MsizeSig = "48 83 EC 28 48 85 C9 75 ? E8 ? ? ? ? C7 00 16 00 00 00 E8 ? ? ? ? 48 83 C8 FF 48 83 C4 28 C3 4C 8B C1 33 D2 48 8B 0D"; // _msize

    /// <summary>
    /// ActivateDecision(rcx, rdx unused, r8 = country) (decision.cpp): runs a decision's effect, then calls a virtual on the
    /// country's targeted-decision instance, which the effect may have freed.
    /// </summary>
    [Signature] public const string ActivateDecisionSig = "48 89 5C 24 10 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 50 FF FF FF 48 81 EC B0 01 00 00 49 8B F8 4C 8B F1 41 80 B8 01 0B 00 00 00";

    [Function(CallingConventions.Microsoft)] public delegate nint ActivateDecisionFn(nint a, nint b, nint c, nint d);

    private static IHook<ActivateDecisionFn>? _activateDecision;
    private static delegate* unmanaged<int, void> _defer;
    private static delegate* unmanaged<nint, nint> _malloc;
    private static delegate* unmanaged<nint, nint, nint> _calloc;
    private static delegate* unmanaged<nint, void> _free;
    private static readonly Logger Log = new("Allocator");

    private static void Resolve()
    {
        if (_free != null) return;
        _malloc = (delegate* unmanaged<nint, nint>)Sig.Find(MallocSig);
        _calloc = (delegate* unmanaged<nint, nint, nint>)Sig.Find(CallocSig);
        _free = (delegate* unmanaged<nint, void>)Sig.Find(FreeSig);
    }

    /// <summary>malloc on the game's heap.</summary>
    public static nint Alloc(int size)
    {
        Resolve();
        return _malloc(size);
    }

    /// <summary>calloc on the game's heap.</summary>
    public static nint AllocZeroed(int size)
    {
        Resolve();
        return _calloc(1, size);
    }

    /// <summary>free on the game's heap.</summary>
    public static void Free(nint p)
    {
        Resolve();
        _free(p);
    }

    /// <summary>
    /// Points the game's CRT allocation functions at mimalloc. Must run before the game's CRT starts, and is never undone:
    /// mimalloc blocks can't go back to the Windows heap.
    /// </summary>
    internal static void UseMimalloc()
    {
        // Only the game's CRT should change, not the process's ucrtbase malloc.
        Environment.SetEnvironmentVariable("MIMALLOC_DISABLE_REDIRECT", "1");
        // Reset unused pages instead of decommitting them: the game reads freed memory through stale pointers.
        Environment.SetEnvironmentVariable("MIMALLOC_PURGE_DECOMMITS", "0");
        var mimalloc = NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(typeof(Allocator).Assembly.Location)!, "mimalloc.dll"));
        nint Mi(string name) => NativeLibrary.GetExport(mimalloc, name);
        var probe = NativeMemory.Alloc(16); // ucrtbase's malloc
        var redirected = ((delegate* unmanaged<void*, byte>)Mi("mi_is_in_heap_region"))(probe) != 0;
        NativeMemory.Free(probe);
        if (redirected) Log.Error("mimalloc also took over the process's other (ucrtbase) allocations");

        var version = NativeLibrary.Load(Path.Combine(Loader.GameDir, "version.dll"));
        nint Shim(string name) => NativeLibrary.GetExport(version, name);

        var free = Sig.Find(FreeSig);
        var heap = free + 22 + *(int*)(free + 18); // mov rcx, [rip+CrtHeap]
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, void>)Shim("hm_init"))(heap,
            Mi("mi_malloc"), Mi("mi_calloc"), Mi("mi_realloc"), Mi("mi_free"), Mi("mi_usable_size"), Mi("mi_is_in_heap_region"));

        // Free and size first, so they recognise mimalloc blocks before anything hands them out.
        Redirect(free, Shim("hm_free"));
        Redirect(Sig.Find(MsizeSig), Shim("hm_msize"));
        Redirect(Sig.Find(ReallocSig), Shim("hm_realloc"));
        Redirect(Sig.Find(CallocSig), Shim("hm_calloc"));
        Redirect(Sig.Find(MallocSig), Shim("hm_malloc"));

        // ActivateDecision uses the targeted-decision instance after the effect freed it. The Windows heap leaves the block
        // intact; mimalloc overwrites its vtable. So frees during the call wait until it returns.
        _defer = (delegate* unmanaged<int, void>)Shim("hm_defer");
        try
        {
            _activateDecision = Patches.Hook<ActivateDecisionFn>(Sig.Find(ActivateDecisionSig), (a, b, c, d) =>
            {
                _defer(1);
                try { return _activateDecision!.OriginalFunction(a, b, c, d); }
                finally { _defer(0); }
            });
        }
        catch (Exception e) { Log.Error($"decision free deferral (mimalloc may crash on targeted decisions): {e.Message}"); }
    }

    /// <summary>Overwrites the start of <paramref name="from"/> with `mov rax, to; jmp rax`.</summary>
    private static void Redirect(nint from, nint to)
    {
        var current = Convert.ToHexString(new ReadOnlySpan<byte>((void*)from, 12));
        Patches.Bytes(from, current, "48B8" + Convert.ToHexString(BitConverter.GetBytes(to)) + "FFE0");
    }
}
