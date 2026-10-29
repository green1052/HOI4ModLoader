using System.Reflection;
using HOI4ModLoader;

namespace Signatures.Tests;

public class GroundCrewsTests
{
    private static readonly string GameDir = typeof(GroundCrewsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "HOI4Dir").Value!;

    /// <summary>The operands GroundCrews takes out of CanExecute: the call lands on IsOn (country+0x4D8 map, on at +0x38), the cost on the define.</summary>
    [Fact]
    public unsafe void CanExecuteOperandsResolve()
    {
        var image = Image.Load(Path.Combine(GameDir, "hoi4.exe"));
        var at = image.FindAll(GroundCrews.CanExecuteSig)[0];
        var isOn = image.FindAll("48 89 54 24 10 53 48 83 EC 20 48 8B D9 4C 8D 44 24 38 48 81 C1 D8 04 00 00 48 8D 54 24 30 E8 ? ? ? ? 48 8B 44 24 30 48 3B 83 E0 04 00 00 74 ? 48 8B 40 18 0F B6 40 38")[0];
        Assert.Equal(isOn, GroundCrews.IsOnAt(at));
        Assert.Equal(at - 0x19D3D02 + 0x33E29C0, GroundCrews.CostAt(at)); // 1.19.4.1 RVAs: the match (moved +0x2B0 from 1.19.4), AIR_MORE_GROUND_CREWS_COST (unmoved)
    }
}
