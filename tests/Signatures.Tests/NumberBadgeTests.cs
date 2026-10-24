using System.Text;
using HOI4ModLoader;

namespace Signatures.Tests;

public class NumberBadgeTests
{
    private static string Show(byte[] text) => Encoding.Latin1.GetString(text);

    /// <summary>A badge replaces the earlier one, past the icons it is "[N] ", and position 0 takes it off.</summary>
    [Fact]
    public void BadgesReplaceEachOther()
    {
        var once = Gui.Numbered("Industry"u8, 3, false);
        Assert.Equal("£hoimod_numbers|3 Industry", Show(once));
        var twice = Gui.Numbered(once, 31, false);
        Assert.Equal("[31] Industry", Show(twice));
        Assert.Equal("£hoimod_numbers|12 Industry", Show(Gui.Numbered(twice, 12, false)));
        Assert.Equal("Industry", Show(Gui.Numbered(once, 0, false)));
        Assert.Equal("[Draft] law", Show(Gui.Numbered("[Draft] law"u8, 0, false))); // not a badge
    }

    /// <summary>Gui.Number skips texts whose badge already shows the position, so it reads the number back.</summary>
    [Fact]
    public void ReadsTheBadgeBack()
    {
        Assert.Equal((18, 3), Gui.Badge(Gui.Numbered("Industry"u8, 3, false)));
        Assert.Equal((5, 31), Gui.Badge(Gui.Numbered("Industry"u8, 31, false)));
        Assert.Equal((0, 0), Gui.Badge("Industry"u8));
        Assert.Equal((0, 0), Gui.Badge("[Draft] law"u8));
    }

    /// <summary>
    /// UTF-8 text (a Korean focus name) gets "[N] ": a lone 0xA3 failed the game's conversion and blanked the name, and a UTF-8 £
    /// showed as text. An older version's UTF-8 icon is taken off.
    /// </summary>
    [Fact]
    public void Utf8TextGetsABracketBadge()
    {
        var name = Encoding.UTF8.GetBytes("지도자원리");
        var once = Gui.Numbered(name, 1);
        Assert.Equal("[1] 지도자원리", Encoding.UTF8.GetString(once));
        Assert.True(System.Text.Unicode.Utf8.IsValid(once));
        Assert.Equal("지도자원리", Encoding.UTF8.GetString(Gui.Numbered(once, 0)));
        Assert.Equal("[2] 지도자원리", Encoding.UTF8.GetString(Gui.Numbered(Encoding.UTF8.GetBytes("£hoimod_numbers|1 지도자원리"), 2)));
    }

    /// <summary>The icon strip: a TGA of 30 frames, opaque in the middle of each disc, clear in its corners.</summary>
    [Fact]
    public void WritesTheIconStrip()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Gui.WriteNumberIcons(dir);
        var tga = File.ReadAllBytes(Path.Combine(dir, "gfx", "interface", "hoimod_numbers.tga"));
        Assert.Equal(24 * 30, BitConverter.ToUInt16(tga, 12));
        Assert.Equal(255, tga[18 + (9 * 720 + 7) * 4 + 3]); // frame 1, left of the digit
        Assert.Equal(0, tga[18 + 3]);                       // top left corner
        Assert.Contains("noOfFrames = 30", File.ReadAllText(Path.Combine(dir, "interface", "hoimod_numbers.gfx")));
        Directory.Delete(dir, true);
    }
}
