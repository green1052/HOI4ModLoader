using HOI4ModLoader;

namespace ArmySupport;

/// <summary>
/// Army helpers, one file each: field marshal attachments, railway gun placement, airbase capacity, auto rebase, auto
/// ground crews, auto aces, paradrops, invasion support and the Ctrl+Alt+G army organizer.
/// </summary>
[Plugin("com.green1052.armysupport", "Army Support", "1.0.0", GameVersion = "1.19.3")]
public partial class ArmySupport : Plugin
{
    public override void Load()
    {
        var fieldMarshal = Config.Bind("FieldMarshal", "FieldMarshal", true,
            "Right-click a field marshal's card with air wings or railway guns selected to attach them. Single player: to him; multiplayer: spread over his armies.");
        var frontPenalty = Math.Max(0, Config.Bind("RailwayGuns", "FrontPenalty", 500,
            "Score taken off front-line provinces so railway guns stay just behind the front (vanilla's bonus near fighting is about 100-200). 0: off. Single player."));
        var noSea = Config.Bind("RailwayGuns", "NoSeaTransfer", true, "Railway guns never go by sea, only along their own rail network. Single player.");
        var coverage = Math.Clamp(Config.Bind("Rebase", "Coverage", 100,
            "Air wings covering less than this % of their mission region fly to an airbase with room that covers more. 0: off."), 0, 100);
        var groundCrews = Config.Bind("Air", "AutoGroundCrews", false,
            "Turns More Ground Crews on in up to 2 regions where your air wings fought hardest in the last two days, "
            + "and off after three quiet days or when command power drops under the reserve. Regions you toggle yourself are left alone.");
        var crewsReserve = Math.Clamp(Config.Bind("Air", "GroundCrewsReserve", 50,
            "Auto ground crews never spends command power below this."), 0, 1000);
        var aces = Config.Bind("Air", "AutoAces", true,
            "Puts your idle aces on air wings without a living ace, wings on a mission first. Also takes fallen aces off their wings (every country's in single player).");
        var splitDrops = Config.Bind("Paradrop", "SplitDrops", true,
            "With several paratrooper divisions on a paradrop, each further destination click takes its share of them.");
        var transports = Config.Bind("Paradrop", "AutoTransports", true,
            "A paradrop with no transport planes on the paradrop mission at its airbase gets the idle transport wings that reach its target.");
        var splitInvasions = Config.Bind("Invasion", "SplitInvasions", true,
            "With several divisions on a naval invasion, each further beach click takes its share of them as another invasion from the same port.");
        var invasionSupport = Config.Bind("Invasion", "AutoSupport", true,
            "A naval invasion with no task force on naval invasion support gets your idle (holding) task forces on that mission over the seas by its port and beaches.");
        var organize = Config.Bind("Organize", "Organize", true,
            "Ctrl+Alt+G with divisions selected splits them into armies of the game's army size, each under the best idle general, in their army group or a new one.");

        LoadSending();
        if (coverage > 0) LoadRebase(coverage);
        if (fieldMarshal) LoadFieldMarshal();
        if (organize) LoadOrganize();
        if (groundCrews) LoadGroundCrews(crewsReserve);
        if (aces) LoadAces();
        if (splitDrops || transports) LoadParadrop(splitDrops, transports);
        if (splitInvasions) LoadInvasionSplit();
        if (invasionSupport) LoadInvasionSupport();
        if (!Multiplayer) LoadRailwayGuns(frontPenalty, noSea); // changes the game
    }
}
