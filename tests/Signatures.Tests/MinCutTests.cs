using AiAssist;

namespace Signatures.Tests;

public class MinCutTests
{
    /// <summary>Enemy (0) attacks a and b (2, 3) in the open; c (4) behind a river leads to the core (1). Cheap ground is given up, dear ground held.</summary>
    [Fact]
    public void FallsBackBehindTheRiverUnlessGroundIsDear()
    {
        List<(int, int, int)> edges = [(0, 2, 100), (0, 3, 100), (2, 4, 40), (3, 4, 40), (4, 1, 1 << 28), (2, 1, 10), (3, 1, 10)];
        Assert.Equal([true, false, true, true, false], MinCut.SourceSide(5, edges, 0, 1));
        edges[5] = (2, 1, 500);
        edges[6] = (3, 1, 500);
        Assert.Equal([true, false, false, false, false], MinCut.SourceSide(5, edges, 0, 1));
    }
}
