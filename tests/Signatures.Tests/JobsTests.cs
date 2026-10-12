using HOI4ModLoader;

namespace Signatures.Tests;

public class JobsTests
{
    /// <summary>Each plugin's set replaces only its own; a plugin never counts its own divisions as taken.</summary>
    [Fact]
    public void OwnersReplaceOnlyTheirOwnDivisions()
    {
        Jobs.Set("a", [1, 2]);
        Jobs.Set("b", [3]);
        Assert.True(Jobs.Taken(1, "b"));
        Assert.False(Jobs.Taken(1, "a"));
        Jobs.Set("a", [2]);
        Assert.False(Jobs.Taken(1));
        Assert.True(Jobs.Taken(2) && Jobs.Taken(3, "a"));
        Jobs.Set("a", []);
        Jobs.Set("b", []);
        Assert.False(Jobs.Taken(2) || Jobs.Taken(3));
    }
}
