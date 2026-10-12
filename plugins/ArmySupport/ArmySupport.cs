using HOI4ModLoader;

namespace ArmySupport;

/// <summary>
/// Support for the army: air wings and railway guns for field marshals (FieldMarshal.cs), railway gun positioning
/// (RailwayGuns.cs), airbase send capacity (Airbases.cs), auto rebase (Rebase.cs), auto ground crews (GroundCrews.cs) and the Ctrl+Alt+G
/// army organizer (Organize.cs).
/// </summary>
[Plugin("com.green1052.armysupport", "Army Support", "1.0.0", GameVersion = "1.19.3")]
public partial class ArmySupport : Plugin
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
        var groundCrews = Config.Bind("Air", "AutoGroundCrews", false,
            "Turn on More Ground Crews (+10 % air efficiency in a region for command power, given back when turned off) where your air wings fought hardest in the last two days "
            + "(10+ planes lost and shot down where enemy planes are active), two regions at most, and off again where they stopped fighting for three days or command power falls under the reserve. "
            + "Only regions it turned on are turned off; one you turn off yourself is left alone.");
        var crewsReserve = Math.Clamp(Config.Bind("Air", "GroundCrewsReserve", 50,
            "Command power auto ground crews never spends below."), 0, 1000);
        var organize = Config.Bind("Organize", "Organize", true,
            "Ctrl+Alt+G with divisions selected: split them into armies of the game's army size (neighbours together), each under the best idle general, in the army group most of them came from or a new one under the best idle field marshal.");

        LoadSending();
        if (coverage > 0) LoadRebase(coverage);
        if (fieldMarshal) LoadFieldMarshal();
        if (organize) LoadOrganize();
        if (groundCrews) LoadGroundCrews(crewsReserve);
        if (!Multiplayer) LoadRailwayGuns(frontPenalty, noSea); // changes the game
    }
}
