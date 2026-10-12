using System.Reflection;
using HOI4ModLoader;

namespace Signatures.Tests;

public class DivisionTests
{
    private static readonly string GameDir = typeof(DivisionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "HOI4Dir").Value!;

    /// <summary>The call in HeadquartersSig lands on the template data getter: [unit+3B8h] (the template) + 18h, or 0.</summary>
    [Fact]
    public void HeadquartersReadsTemplateData()
    {
        var image = Image.Load(Path.Combine(GameDir, "hoi4.exe"));
        var at = image.FindAll(Division.HeadquartersSig)[0];
        var getter = image.FindAll("48 8B 91 B8 03 00 00 33 C9 48 85 D2 48 8D 42 18 48 0F 44 C1 C3")[0];
        Assert.Equal(getter, Division.TemplateDataAt(at));
    }
}
