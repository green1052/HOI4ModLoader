using HOI4ModLoader;

namespace ArmySupport;

/// <summary>
/// Support for the army: air wings and railway guns for field marshals (FieldMarshal.cs), railway gun positioning
/// (RailwayGuns.cs), airbase send capacity (Airbases.cs) and auto rebase (Rebase.cs).
/// </summary>
[Plugin("com.green1052.armysupport", "Army Support", "1.0.0", GameVersion = "1.19.3")]
public unsafe partial class ArmySupport : Plugin
{
    public override void Load()
    {
        var fieldMarshal = Config.Bind("FieldMarshal", "FieldMarshal", true,
            "Right-click a field marshal's card with air wings or railway guns selected: single player attaches them to him (changes the game), multiplayer spreads them over his armies.");
        var frontPenalty = Math.Max(0, Config.Bind("RailwayGuns", "FrontPenalty", 500,
            "Score taken off provinces on the front line (next to enemy-controlled provinces or hostile units) for railway guns; 0: off. Vanilla bonuses for being near the fighting are about 100-200. Single player."));
        var noSea = Config.Bind("RailwayGuns", "NoSeaTransfer", true, "Never ship railway guns by sea; they only move along their own rail network. Single player.");
        var coverage = Math.Clamp(Config.Bind("Rebase", "Coverage", 100,
            "Fly a wing reaching less than this much (%) of its mission region to a usable airbase with room that reaches more (wings attached to an army: the game does it); 0: off."), 0, 100);

        LoadSending();
        if (coverage > 0) LoadRebase(coverage);
        if (fieldMarshal) LoadFieldMarshal();
        if (!Multiplayer) LoadRailwayGuns(frontPenalty, noSea); // changes the game
    }
}
