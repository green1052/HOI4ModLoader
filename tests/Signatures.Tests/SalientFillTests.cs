using AiAssist;

namespace Signatures.Tests;

public class SalientFillTests
{
    // Enemy main ground 10..14 (bigger than the pocket limit 3), neck 5, salient 6-7 (2 divisions each), our flanks 1 and 2.
    private static readonly Dictionary<int, List<int>> Map = new()
    {
        [1] = [5, 6], [2] = [5, 7], [5] = [10, 6, 1, 2], [6] = [5, 7, 1], [7] = [6, 2],
        [10] = [5, 11], [11] = [10, 12], [12] = [11, 13], [13] = [12, 14], [14] = [13], [9] = [],
    };

    private static int Cut(int[] neck, Dictionary<int, List<int>> map) =>
        SalientFill.Cut(neck, id => map[id], id => id is 1 or 2 ? SalientFill.Ours : id == 9 ? SalientFill.Neutral : SalientFill.Theirs, id => id is 6 or 7 ? 2 : 0, 3);

    [Fact]
    public void TakingTheNeckCutsOffTheSalient() => Assert.Equal(4, Cut([5], Map));

    [Fact]
    public void NeutralLandIsAWayOut()
    {
        var map = new Dictionary<int, List<int>>(Map) { [7] = [6, 2, 9] };
        Assert.Equal(0, Cut([5], map));
    }

    [Fact]
    public void NoPocketWithoutTheEnemysMainGroundBehindTheNeck()
    {
        var map = new Dictionary<int, List<int>>(Map) { [5] = [6, 1, 2] };
        Assert.Equal(0, Cut([5], map));
    }
}
