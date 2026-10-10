using System.Runtime.InteropServices;

namespace HOI4ModLoader;

/// <summary>
/// The game's own heap: hoi4.exe's statically linked CRT allocation functions. Memory the game frees (commands, string
/// buffers) or that we free for it must come from and go back to here. Optionally swaps in mimalloc (compiled into
/// version.dll, see bootstrap\allocator.c) underneath them, which is faster than the Windows heap for the game's many
/// small allocations.
/// </summary>
public static unsafe class Allocator
{
    [Signature] public const string FreeSig = "48 85 C9 74 ? 53 48 83 EC 20 4C 8B C1 33 D2 48 8B 0D ? ? ? ? FF 15"; // _free_base
    [Signature] public const string MallocSig = "40 53 48 83 EC 20 48 8B D9 48 83 F9 E0 77 ? 48 85 C9 B8 01 00 00 00 48"; // _malloc_base
    [Signature] public const string CallocSig = "40 53 48 83 EC 20 4C 8B C2 48 8B D9 48 85 C9 74 ? 33 D2 48 8D 42 E0 48"; // _calloc_base
    [Signature] public const string ReallocSig = "48 89 5C 24 08 57 48 83 EC 20 48 8B DA 48 8B F9 48 85 C9 75 ? 48 8B CA E8 ? ? ? ? EB ? 48 85 DB 75 ? E8 ? ? ? ? EB ? 48 83 FB E0 76"; // _realloc_base
    [Signature] public const string MsizeSig = "48 83 EC 28 48 85 C9 75 ? E8 ? ? ? ? C7 00 16 00 00 00 E8 ? ? ? ? 48 83 C8 FF 48 83 C4 28 C3 4C 8B C1 33 D2 48 8B 0D"; // _msize

    private static delegate* unmanaged<nint, nint> _malloc;
    private static delegate* unmanaged<nint, nint, nint> _calloc;
    private static delegate* unmanaged<nint, void> _free;

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
    /// Points the game's CRT allocation functions at mimalloc. Must run before the game's CRT starts (the loader runs right
    /// before hoi4.exe's entry point), and is never undone: blocks from mimalloc can't go back to the Windows heap.
    /// </summary>
    internal static void UseMimalloc()
    {
        var version = NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "version.dll"));
        nint Shim(string name) => NativeLibrary.GetExport(version, name);

        var free = Sig.Find(FreeSig);
        var heap = free + 22 + *(int*)(free + 18); // mov rcx, [rip+CrtHeap]
        ((delegate* unmanaged<nint, void>)Shim("hm_set_crt_heap"))(heap);

        // Free and size first: once the others hand out mimalloc blocks, these must already recognise them.
        Redirect(free, Shim("hm_free"));
        Redirect(Sig.Find(MsizeSig), Shim("hm_msize"));
        Redirect(Sig.Find(ReallocSig), Shim("hm_realloc"));
        Redirect(Sig.Find(CallocSig), Shim("hm_calloc"));
        Redirect(Sig.Find(MallocSig), Shim("hm_malloc"));
    }

    /// <summary>Overwrites the start of <paramref name="from"/> with `mov rax, to; jmp rax`.</summary>
    private static void Redirect(nint from, nint to)
    {
        var current = Convert.ToHexString(new ReadOnlySpan<byte>((void*)from, 12));
        Patches.Bytes(from, current, "48B8" + Convert.ToHexString(BitConverter.GetBytes((long)to)) + "FFE0");
    }
}
