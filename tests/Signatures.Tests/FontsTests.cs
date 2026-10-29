using System.Reflection;

namespace Signatures.Tests;

public class FontsTests
{
    private static readonly string GameDir = typeof(FontsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "HOI4Dir").Value!;

    /// <summary>The game's Korean UI font drawn anew with Malgun Gothic: every character back, Hangul from GDI, the texture readable again.</summary>
    [Fact]
    public void RedrawsAGameFont()
    {
        var fnt = Path.Combine(GameDir, "gfx", "fonts", "korean", "hoi_18mbs.fnt");
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.True(Fonts.Fonts.Draw(fnt, "Malgun Gothic", 100, dir));
        var lines = File.ReadAllLines(Path.Combine(dir, "hoi_18mbs.fnt"));
        Assert.Equal(File.ReadAllLines(fnt).Count(l => l.StartsWith("char ")), lines.Count(l => l.StartsWith("char ")));
        Assert.Contains(lines, l => l.StartsWith("char id=44032 ") && !l.Contains("width=0 "));
        Assert.NotNull(Fonts.Bitmap.Read(Path.Combine(dir, "hoi_18mbs.dds")));
        Directory.Delete(dir, true);
    }

    /// <summary>Windows matches a family by any of its names; a name it doesn't have means the font isn't installed.</summary>
    [Fact]
    public void KnowsInstalledFamilies()
    {
        using (var gdi = new Fonts.Gdi("Malgun Gothic", 20, 400)) Assert.Contains("맑은 고딕", gdi.FamilyNames());
        using (var gdi = new Fonts.Gdi("No Such Font 12345", 20, 400)) Assert.DoesNotContain("No Such Font 12345", gdi.FamilyNames());
        var all = Fonts.Gdi.Families();
        Assert.Contains("Segoe UI", all);
        Assert.DoesNotContain(all, f => f.StartsWith('@'));
        Assert.Equal("Malgun Gothic", Fonts.Fonts.FamilyName(File.ReadAllBytes(@"C:\Windows\Fonts\malgun.ttf")));
    }
}
