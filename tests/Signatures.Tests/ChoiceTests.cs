using HOI4ModLoader;

namespace Signatures.Tests;

public class ChoiceTests
{
    [Fact]
    public void CutsLongValues()
    {
        Assert.Equal("Pretendard", SettingsMods.Fit("Pretendard"));
        Assert.EndsWith("..", SettingsMods.Fit("Noto Sans KR Medium"));
        Assert.EndsWith("..", SettingsMods.Fit("가나다라마바사아자"));
    }
}
