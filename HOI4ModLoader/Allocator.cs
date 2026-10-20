using System.Runtime.InteropServices;

namespace HOI4ModLoader;

/// <summary>
/// The game's own heap (hoi4.exe's statically linked CRT). Memory the game frees, or that we free for it, must come from
/// here.
/// </summary>
public static unsafe class Allocator
{
    [Signature] public const string FreeSig = "48 85 C9 74 ? 53 48 83 EC 20 4C 8B C1 33 D2 48 8B 0D ? ? ? ? FF 15"; // _free_base
    [Signature] public const string MallocSig = "40 53 48 83 EC 20 48 8B D9 48 83 F9 E0 77 ? 48 85 C9 B8 01 00 00 00 48"; // _malloc_base
    [Signature] public const string CallocSig = "40 53 48 83 EC 20 4C 8B C2 48 8B D9 48 85 C9 74 ? 33 D2 48 8D 42 E0 48"; // _calloc_base

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
}
