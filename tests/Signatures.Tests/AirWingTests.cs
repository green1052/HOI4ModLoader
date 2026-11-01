using System.Reflection;
using HOI4ModLoader;

namespace Signatures.Tests;

public unsafe class AirWingTests
{
    [Fact]
    public void SimulationBaseLookupOnlyResolvesAReference()
    {
        var dir = typeof(AirWingTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "HOI4Dir").Value!;
        var image = Image.Load(Path.Combine(dir, "hoi4.exe"));
        var getter = image.FindAll(AirWing.PoolBaseGetterSig)[0];
        Assert.Equal(getter, AirWing.PoolBaseGetterAt(image.FindAll(AirWing.BaseLookupSig)[0]));
        Assert.Equal(image.FindAll(Game.ResolveRefSig)[0], AirWing.ReferenceResolverAt(getter));
    }

    [Fact]
    public void BaseReferenceComesFromThePoolInsteadOfSelectingOrFollowingATransfer()
    {
        var wing = stackalloc byte[0x60];
        new Span<byte>(wing, 0x60).Clear();
        var pool = stackalloc byte[0x28];
        new Span<byte>(pool, 0x28).Clear();
        const long currentBase = 329L << 32 | 65;
        const long transferTarget = 400L << 32 | 65;
        *(nint*)(wing + 0x38) = (nint)pool;
        *(long*)(pool + 0x18) = currentBase;
        *(long*)(wing + 0x48) = transferTarget;

        Assert.Equal(currentBase, AirWing.BaseReference((nint)wing));
        Assert.Equal(transferTarget, *(long*)(wing + 0x48));
    }

    [Fact]
    public void MissingWingPoolOrReferenceNeedsNoEngineCall()
    {
        var wing = stackalloc byte[0x60];
        new Span<byte>(wing, 0x60).Clear();
        var pool = stackalloc byte[0x28];
        new Span<byte>(pool, 0x28).Clear();
        Assert.Equal((nint)0, AirWing.Base(0));
        Assert.Equal((nint)0, AirWing.Base((nint)wing));
        *(nint*)(wing + 0x38) = (nint)pool;
        Assert.Equal((nint)0, AirWing.Base((nint)wing));
    }
}
