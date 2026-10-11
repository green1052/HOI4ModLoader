using HOI4ModLoader;

namespace Signatures.Tests;

public class ConfigTests
{
    /// <summary>The Mods tab lists a plugin's settings in file order, so new entries go to the end of their section.</summary>
    [Fact]
    public void KeepsBindOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hoimod-{Guid.NewGuid():N}.cfg");
        try
        {
            var config = new ConfigFile(path);
            config.Bind("A", "First", 1, "1");
            config.Bind("B", "Other", true, "2");
            config.Bind("A", "Second", 2, "3");
            config.Bind("A", "Third", 3, "4");
            Assert.Equal(["A.First", "A.Second", "A.Third", "B.Other"],
                new ConfigFile(path).Entries().Select(e => $"{e.Section}.{e.Key}"));
            Assert.Equal(2, new ConfigFile(path).Bind("A", "Second", 0, "3"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Settings a plugin no longer binds leave the file with their description; [General] stays; a second pass changes nothing.</summary>
    [Fact]
    public void RemovesUnboundKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hoimod-{Guid.NewGuid():N}.cfg");
        try
        {
            var old = new ConfigFile(path);
            old.Bind("General", "Enabled", true, "on");
            old.Bind("A", "Kept", 1, "kept");
            old.Bind("A", "Gone", 2, "gone");
            old.Bind("B", "AlsoGone", 3, "gone too");
            var now = new ConfigFile(path);
            now.Bind("A", "Kept", 1, "kept");
            Assert.Equal(["A.Gone", "B.AlsoGone"], now.RemoveUnbound("General").Order());
            Assert.Equal(["General.Enabled", "A.Kept"], new ConfigFile(path).Entries().Select(e => $"{e.Section}.{e.Key}"));
            Assert.DoesNotContain("gone", File.ReadAllText(path));
            Assert.Empty(now.RemoveUnbound("General"));
        }
        finally { File.Delete(path); }
    }
}
