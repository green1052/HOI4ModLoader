using HOI4ModLoader;

namespace Signatures.Tests;

public class ChoiceTests
{
    [Fact]
    public void CutsLongValues()
    {
        Assert.Equal("Noto Sans KR Medium", SettingsMods.Fit("Noto Sans KR Medium"));
        Assert.EndsWith("..", SettingsMods.Fit("Segoe UI Variable Display Light"));
        Assert.EndsWith("..", SettingsMods.Fit("가나다라마바사아자차카타파하"));
    }
}
