using HOI4ModLoader;

namespace Signatures.Tests;

public class ChoiceTests
{
    /// <summary>Ctrl+click jumps by initial (Hangul by its initial consonant), wrapping; back lands on the start of a run.</summary>
    [Fact]
    public void JumpsToTheNextInitial()
    {
        string[] fonts = ["", "굴림", "궁서", "돋움", "맑은 고딕", "Noto Sans KR", "Noto Serif KR", "Pretendard"];
        Assert.Equal(3, SettingsMods.NextInitial(fonts, 1, +1));  // 굴림 (ㄱ) -> 돋움 (ㄷ)
        Assert.Equal(7, SettingsMods.NextInitial(fonts, 5, +1));  // Noto.. -> Pretendard
        Assert.Equal(0, SettingsMods.NextInitial(fonts, 7, +1));  // wraps to (none)
        Assert.Equal(5, SettingsMods.NextInitial(fonts, 7, -1));  // Pretendard -> the first Noto
        Assert.Equal(1, SettingsMods.NextInitial(fonts, 3, -1));  // 돋움 -> 굴림, the first ㄱ
    }

    [Fact]
    public void CutsLongValues()
    {
        Assert.Equal("Pretendard", SettingsMods.Fit("Pretendard"));
        Assert.True(SettingsMods.Fit("Noto Sans KR Medium").Length <= 16);
        Assert.EndsWith("..", SettingsMods.Fit("가나다라마바사아자차"));
    }
}
