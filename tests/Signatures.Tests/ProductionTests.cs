using System.Reflection;
using System.Runtime.InteropServices;
using HOI4ModLoader;

namespace Signatures.Tests;

public unsafe class ProductionTests
{
    [Fact]
    public void EquipmentLayoutOperandsMatchInstalledGame()
    {
        var dir = typeof(ProductionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "HOI4Dir").Value!;
        var image = Image.Load(Path.Combine(dir, "hoi4.exe"));
        var at = image.FindAll(ProductionLine.EquipmentLayout)[0];
        Assert.Equal(ProductionLine.VariantOfLine, *(int*)(at + 4));
        Assert.Equal(ProductionLine.TypeOfVariant, *(int*)(at + 11));
        Assert.Equal(ProductionLine.ArchetypeOfType, *(int*)(at + 18));
        Assert.Equal(ProductionLine.IndexOfArchetype, *(int*)(at + 29));
    }

    [Fact]
    public void EquipmentTraversalIgnoresOldScalarFields()
    {
        var memory = (nint)NativeMemory.AllocZeroed(4 * 0x1000);
        try
        {
            var line = memory;
            var variant = memory + 0x1000;
            var type = memory + 0x2000;
            var archetype = memory + 0x3000;
            // Actual 1.19.4.1 layout, including the scalar which produced the logged 0x5AE fault.
            *(nint*)(line + 0x88) = variant;
            *(nint*)(variant + 0x3F0) = type;
            *(nint*)(type + 0x4D8) = 0x16;
            *(nint*)(type + 0x538) = archetype;
            *(nint*)(archetype + 0x538) = archetype;
            *(int*)(archetype + 0x598) = 7;

            Assert.Equal(variant, ProductionLine.Variant(line));
            Assert.Equal(archetype, ProductionLine.Archetype(variant));
            Assert.Equal(7, ProductionLine.ArchetypeIndex(archetype));
            Assert.Equal((nint)0, ProductionLine.Archetype(0));
            Assert.Equal((nint)0, ProductionLine.Archetype(memory + 0x1800));
        }
        finally { NativeMemory.Free((void*)memory); }
    }
}
