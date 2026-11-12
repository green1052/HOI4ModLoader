namespace HOI4ModLoader;

/// <summary>Read-only CAirWing queries shared by player automation and AI worker threads. HOI4 1.19.5.0.</summary>
public static unsafe class AirWing
{
    private const int PoolOfWing = 0x38, BaseOfPool = 0x18;

    /// <summary>The simulation's AssignWing reads wing+38h, then calls the pool's pure base getter.</summary>
    [Signature] public const string BaseLookupSig = "48 8B 4B 38 E8 ? ? ? ? 48 3B E8 0F 84 ? ? ? ? 48 3B EE 0F 84";

    /// <summary>Pool getter: resolve the CRef at pool+18h, or return null. No UI calls.</summary>
    [Signature] public const string PoolBaseGetterSig = "48 83 C1 18 83 39 00 75 09 83 79 04 00 75 03 33 C0 C3 E9 ? ? ? ?";

    /// <summary>Verify the layout and reference resolver before automation starts reading wings.</summary>
    public static void ResolveBase()
    {
        var getter = Sig.Find(PoolBaseGetterSig);
        if (PoolBaseGetterAt(Sig.Find(BaseLookupSig)) != getter || ReferenceResolverAt(getter) != Sig.Find(Game.ResolveRefSig))
            throw new InvalidOperationException("air wing base lookup no longer resolves the pool's base reference");
    }

    internal static nint PoolBaseGetterAt(nint lookup) => lookup + 9 + *(int*)(lookup + 5);
    internal static nint ReferenceResolverAt(nint getter) => getter + 23 + *(int*)(getter + 19);

    internal static long BaseReference(nint wing)
    {
        var pool = wing == 0 ? 0 : *(nint*)(wing + PoolOfWing);
        return pool == 0 ? 0 : *(long*)(pool + BaseOfPool);
    }

    /// <summary>The pool's current airbase, or 0. Does not select a base, open a window or follow a transfer target.</summary>
    public static nint Base(nint wing)
    {
        var reference = BaseReference(wing);
        return reference == 0 ? 0 : Game.Resolve(reference);
    }
}
