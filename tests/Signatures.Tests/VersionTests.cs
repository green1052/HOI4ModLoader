using HOI4ModLoader;

namespace Signatures.Tests;

public class VersionTests
{
    /// <summary>A plugin made for 1.19.3 runs on its own rawVersion but not on a hotfix, a longer build or an unreadable version.</summary>
    [Theory]
    [InlineData("1.19.3.0", "1.19.3", true)] // the rawVersion's trailing .0 is padding
    [InlineData("1.19.3", "1.19.3", true)]
    [InlineData("1.19.3.1", "1.19.3", false)] // a hotfix: offsets may have moved
    [InlineData("1.19.30.0", "1.19.3", false)]
    [InlineData("1.19.4.0", "1.19.3", false)]
    [InlineData("1.19.3.0", "1.19", false)] // naming fewer parts is exact, not a prefix
    [InlineData("unknown", "1.19.3", false)]
    public void MatchesAtPartBoundaries(string game, string want, bool match) =>
        Assert.Equal(match, Loader.SameVersion(game, want));
}
