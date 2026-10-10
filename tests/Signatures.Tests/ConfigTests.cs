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
}
