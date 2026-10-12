namespace Signatures.Tests;

public class GameFilesTests
{
    /// <summary>AI Assist's AirborneUnits puts this strategy file in front of the game's own: it must be in the plugin.</summary>
    [Fact]
    public void AiAssistCarriesItsStrategyFile()
    {
        using var stream = typeof(AiAssist.AiAssist).Assembly.GetManifestResourceStream("gamefiles/hoimod_ai_airborne.txt");
        Assert.NotNull(stream);
        Assert.Contains("hoimod_ai_airborne_units", new StreamReader(stream!).ReadToEnd());
    }
}
