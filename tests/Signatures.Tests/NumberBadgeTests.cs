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
        var once = Gui.Numbered("Industry"u8, 3);
        Assert.Equal("£hoimod_numbers|3 Industry", Show(once));
        var twice = Gui.Numbered(once, 31);
        Assert.Equal("[31] Industry", Show(twice));
        Assert.Equal("£hoimod_numbers|12 Industry", Show(Gui.Numbered(twice, 12)));
        Assert.Equal("Industry", Show(Gui.Numbered(once, 0)));
        Assert.Equal("[Draft] law", Show(Gui.Numbered("[Draft] law"u8, 0))); // not a badge
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
