using HOI4ModLoader;

namespace AiAssist;

/// <summary>
/// Add-on for Sheep Mod: fixes vanilla AI logic its scripts can't reach, with no bonuses. Does nothing without Sheep Mod;
/// single player only (it changes how AI countries play). Each feature has its own setting:
/// - Fronts: hold the line, beach defence, defensive postures (grads), refit priority.
/// - Attacks: breakthrough axis, encirclement, pincers, feints, attack targets, armor terrain, supply-aware attacks,
///   offensive timing, breaking off losing attacks, exploitation.
/// - Air and navy: air retreat, bombing targets, air training, ground crews, strategic invasions, naval training, shore bombardment.
/// - Other: army training, stable leaders, production lines, railway guns, supply hubs.
/// </summary>
[Plugin(Id, "AI Assist", "1.0.0", GameVersion = "1.19.4.1", AffectsGameState = true)]
public unsafe partial class AiAssist : Plugin
{
    private const string Id = "com.green1052.aiassist";
    private static int _failed; // AI hooks run on worker threads: log the first error only
    private static Logger? _log;

    public override void Load()
    {
        var refit = Config.Bind("AI", "RefitPriority", true,
            "Reinforcements and new equipment go to front-line divisions (in combat or next to enemy ground) first.");
        _holdLine = Config.Bind("AI", "HoldTheLine", true,
            "At war, every province facing the enemy keeps a division, holes are refilled, spare divisions counterattack break-ins, worn-out ones rest. "
            + "Short of divisions, the AI starts no attacks.");
        _beachDefense = Config.Bind("AI", "BeachDefense", true,
            "Free divisions near a beachhead (ground the enemy took from the sea in the last 10 days) wall it in and attack it at once.");
        _axis = Config.Bind("AI", "BreakthroughAxis", true,
            "Local attacks and close air support keep to one axis: the armoured spearhead, else the theatre's thinnest enemy sector.");
        _exploit = Config.Bind("AI", "Exploitation", true,
            "Fresh mobile divisions drive up to 4 provinces through gaps in the enemy line to a valuable target. Needs HoldTheLine.");
        _pincers = Config.Bind("AI", "Pincers", true,
            "Where the enemy masses divisions, the axis splits into two arms that close behind them. Needs BreakthroughAxis.") && _axis;
        _feints = Config.Bind("AI", "Feints", true,
            "Before a new main push, the AI attacks a crowded enemy province 5+ provinces away for 36 hours. Needs BreakthroughAxis.") && _axis;
        _timing = Config.Bind("AI", "OffensiveTiming", true,
            "A battle plan's offensive waits until its divisions are ready and the battle odds favour it. Needs HoldTheLine.") && _holdLine;
        _attackTargets = Config.Bind("AI", "AttackTargets", true,
            "Local attacks pick pocket-closing, then valuable, then front-shortening targets instead of random ones.");
        _armorTerrain = Config.Bind("AI", "ArmorTerrain", true,
            "Armored and motorized divisions make no local attacks into terrain that cripples them.");
        _airRetreat = Config.Bind("AI", "AirRetreat", true,
            "The AI pulls planes out of regions where it is losing the air war badly.");
        _bombingTargets = Config.Bind("AI", "BombingTargets", true,
            "Strategic bombers get twice the planes for enemy regions next to the land front, half for those far behind it.");
        var airTraining = Config.Bind("AI", "AirTraining", true,
            "Green air wings train instead of flying where nothing happens; half the wings at most.");
        var armyTraining = Config.Bind("AI", "ArmyTraining", true,
            "Idle AI armies (at war: those holding no front) train their divisions up to regular.");
        var navalTraining = Config.Bind("AI", "NavalTraining", true,
            "At peace, idle AI task forces train until their ships are regulars instead of waiting in port.");
        var shoreBombardment = Config.Bind("AI", "ShoreBombardment", true,
            "At war, AI task forces with guns or carriers hold the sea next to coastal land battles and bombard them.");
        _breakOff = Config.Bind("AI", "BreakOffLosingAttacks", true,
            "Losing AI attacks are broken off, and the province isn't attacked again for 3 days.");
        var stableLeaders = Config.Bind("AI", "StableLeaders", true,
            "The AI doesn't take a general or field marshal from an army or army group he commands.");
        var productionLines = Config.Bind("AI", "ProductionLines", true,
            "Small factory cuts to production lines with 35 %+ efficiency are held off for StickyDays.");
        var stickyDays = Math.Clamp(Config.Bind("AI", "StickyDays", 30,
            "Days a small cut to an efficient production line is held off (1-365)."), 1, 365);
        _supplyAware = Config.Bind("AI", "SupplyAwareAttacks", true,
            "Local attacks skip provinces that couldn't be supplied once taken.");
        _supplyShare = Math.Clamp(Config.Bind("AI", "SupplyAwareMinPercent", 25,
            "Supply (% of need) the attacking province must get, or a neighbour of the target must spare (1-200)."), 1, 200);
        _strategicInvasions = Config.Bind("AI", "StrategicInvasions", true,
            "Naval invasions pick thinly held, valuable beaches, wait while the beach is crowded, and get fighter and naval bomber cover.");
        _postures = Config.Bind("AI", "DefensivePostures", true,
            "Facing a much stronger enemy, the AI falls back to a defensible line (grad) and counterattacks only fought-out enemies. Needs HoldTheLine.") && _holdLine;
        var encircle = Config.Bind("AI", "Encirclement", true,
            "The axis goes for the neck of enemy salients holding 3+ divisions. Needs BreakthroughAxis.") && _axis;
        var railGuns = Config.Bind("AI", "RailwayGuns", true,
            "Railway guns go to the armies on the breakthrough axis first and stay there. Needs BreakthroughAxis.") && _axis;
        var supplyHubs = Config.Bind("AI", "SupplyHubs", true,
            "Where AI divisions are low on supply, the railway to their hub is upgraded; from 1941 rich countries also build new hubs.");
        var groundCrews = Config.Bind("AI", "GroundCrews", true,
            "The AI turns on More Ground Crews in the regions where its air wings fight hardest.");

        _log = Log;
        ResolveShared();
        _game = new PerGame(() =>
        {
            // A new world frees the old one's objects: every cached pointer must go.
            Sent.Clear();
            Gap.Clear();
            Posted.Clear();
            Home.Clear();
            DominantSince.Clear();
            Combat.Clear();
            Resting.Clear();
            FreshDivisions.Clear();
            Known.Clear();
            Axes.Clear();
            SecondArms.Clear();
            _axisCores = [];
            Feints.Clear();
            Beaches.Clear();
            Routes.Clear();
            Holds.Clear();
            LostAttacks.Clear();
            Line.Clear();
            FrontCache.Clear();
            Encircled.Clear();
            HoldSince.Clear();
            Grads.Clear();
            Stances.Clear();
            Attackers.Clear();
            Salients.Clear();
            ThinLines.Clear();
            HubPlans.Clear();
            RailwayPlans.Clear();
            TrainingPosted.Clear();
            Moved.Clear();
            Bombarding.Clear();
            Claimed.Clear();
            Beaten.Clear();
            ShortOfMen.Clear();
            WaitedAt.Clear();
            RaisedAt.Clear();
            Needs.Clear();
            LandWars.Clear();
            UnguardedAt.Clear();
            _portHolders = null;
            ClearRailGuns();
            _capitals = (0, []);
            _provinceCount = -1;
            (_controllers, _changed, _landed, _scanned) = ([], [], [], Never);
            _world = Game.Generation; // AI threads skip what TrackFronts recorded before this
        });
        MainThread.Every(TimeSpan.FromSeconds(1), _game.Check); // AI threads read these caches: reset them soon after a load
        if (_attackTargets || _armorTerrain || _axis || _supplyAware || _postures || _holdLine) LoadPockets(); // the thin-line steer lives in PocketFilter
        if (refit) LoadRefit();
        if (_airRetreat || _bombingTargets || _strategicInvasions) LoadRegionPriorities();
        if (airTraining) LoadAirTraining();
        if (armyTraining) LoadArmyTraining();
        if (_breakOff) LoadBreakOff();
        if (stableLeaders) Patches.Bytes(Sig.Find(Signatures.LeaderStealBranch) + 0xF, "75", "EB"); // jnz -> jmp: a leader of another group scores -100, as one whose group is fighting
        if (navalTraining || shoreBombardment) LoadNavy(navalTraining, shoreBombardment);
        if (productionLines) LoadProductionLines(stickyDays);
        if (_axis) LoadCas();
        if (_holdLine || _axis) LoadFronts();
        if (_holdLine || _strategicInvasions) LoadPostAiCommand(); // one hook; each part runs under its own setting
        if (_holdLine)
        {
            _ = Sig.Find(Commands.UnassignSig);
            _ = Sig.Find(Commands.AssignSig);
            MainThread.Every(TimeSpan.FromSeconds(1), Backfill); // the passes poll each second and run on game time (Due)
            if (_exploit) MainThread.Every(TimeSpan.FromSeconds(1), Exploit);
        }
        if (_strategicInvasions) LoadInvasions();
        if (encircle) LoadEncircle();
        if (railGuns) LoadRailGuns();
        if (supplyHubs) LoadSupplyHubs();
        if (groundCrews) LoadGroundCrews();
        if (_holdLine || _attackTargets) MainThread.Every(TimeSpan.FromSeconds(1), Micro);
        if (_holdLine) MainThread.Every(TimeSpan.FromSeconds(1), KeepHeadquarters);
        if (_beachDefense) MainThread.Every(TimeSpan.FromSeconds(1), DefendBeachheads);
        MainThread.Every(TimeSpan.FromMinutes(1), LogStats);
    }

    /// <summary>What the AI hooks changed in the last minute, for the log (they run too often to log one by one).</summary>
    private void LogStats()
    {
        var done = new List<string>();
        void Add(ref int counter, string what)
        {
            var n = Interlocked.Exchange(ref counter, 0);
            if (n > 0) done.Add(string.Format(what, n));
        }
        Add(ref _held, "held {0} AI attack(s) or landing(s) (thin line, grad or crowded beach)");
        Add(ref _supplyCapped, "kept {0} AI front(s) short of supply from asking for more divisions");
        Add(ref _brokenOff, "broke off {0} division(s)' losing attack(s) (no pocket, relief or feint to fight for)");
        Add(ref _landingsHeld, "{0} of those at a crowded beach");
        Add(ref _rescored, "re-scored {0} invasion beach candidate(s)");
        Add(ref _escorted, "put fighters over {0} AI invasion route(s)");
        Add(ref _steered, "skipped {0} local attack target(s) for better ones");
        Add(ref _unsupplied, "skipped {0} local attack target(s) the attackers couldn't be supplied in");
        Add(ref _taken, "sent {0} division(s) into empty enemy provinces");
        Add(ref _refitted, "put {0} front-line division-day(s) first for equipment");
        Add(ref _pulled, "lowered {0} lost air region(s)");
        Add(ref _bombing, "aimed {0} strategic bombing plan(s) at the front");
        Add(ref _training, "put {0} green air wing(s) on training");
        Add(ref _armiesTraining, "started training in {0} AI army/armies");
        Add(ref _armiesStopped, "stopped training in {0} AI army/armies");
        Add(ref _fleetsTraining, "put {0} AI task force(s) on training");
        Add(ref _fleetsSplit, "split {0} AI task force(s) to bombard two coastal battles");
        Add(ref _fleetsBombarding, "sent {0} AI task force(s) to bombard a coastal battle");
        Add(ref _headquartersPulled, "took {0} army HQ(s) off the front");
        Add(ref _keptOnLine, "kept {0} line division(s) from being reassigned off the line (pocket fronts)");
        Add(ref _keptBase, "kept {0} air wing(s) from flying back to the base they left this week");
        Add(ref _stuck, "held factories on efficient lines in {0} AI production plan(s)");
        Add(ref _raised, "raised {0} front request(s) to cover the line");
        Add(ref _frontOrdersDropped, "dropped {0} AI front order(s) on bystanders' borders or allies' lines overseas");
        Add(ref _overseas, "freed {0} front request(s) on allies' fronts overseas");
        Add(ref _bystanders, "freed {0} front request(s) on borders the AI marks as no target");
        Add(ref _unguarded, "freed {0} coast and home garrison request(s) of countries whose enemies hold no port");
        Add(ref _backfilled, "filled {0} hole(s) in AI lines");
        Add(ref _drives, "drove {0} mobile division(s) through gaps in enemy lines");
        Add(ref _pincered, "opened {0} pincer(s) around massed enemy divisions");
        Add(ref _feinted, "feinted {0} time(s) before a new push");
        Add(ref _waited, "held {0} offensive(s) until their divisions were ready");
        Add(ref _kept, "kept {0} line defender(s) from strategic redeployment to another front");
        Add(ref _reserved, "posted {0} reserve(s)");
        Add(ref _depth, "posted {0} second-line division(s) behind key line provinces");
        Add(ref _returned, "put {0} division(s) back on their own order");
        Add(ref _stopped, "stopped {0} running offensive(s) on thinly manned fronts");
        Add(ref _beatenStops, "called off {0} offensive(s) losing most of their attacks");
        Add(ref _trappedStops, "called off {0} offensive(s) pushing into a salient whose way back is closing");
        Add(ref _fireBrigade, "sent {0} reserve(s) against break-ins");
        Add(ref _rested, "rested {0} worn-out division(s)");
        Add(ref _escaped, "pulled {0} division(s) out of closing salients");
        Add(ref _defended, "answered {0} landing(s)");
        Add(ref _stationed, "left fresh arrivals in place {0} time(s)");
        Add(ref _deployed, "sent {0} new division(s) where they were needed");
        Add(ref _cas, "focused {0} CAS pass(es) on the axis");
        Add(ref _spearheads, "put {0} breakthrough axis/axes on the AI's armoured spearhead");
        Add(ref _fellBack, "{0} AI country/countries fell back to a grad");
        Add(ref _garrisoned, "garrisoned {0} division(s) on grads");
        Add(ref _countered, "counterattacked fought-out spearheads with {0} division(s)");
        Add(ref _pinning, "pinned {0} enemy stack(s) next to counterattacks");
        Add(ref _brokeOff, "broke off {0} worn-out counter or pinning attack(s)");
        Add(ref _salients, "put {0} breakthrough axis/axes on the neck of an enemy salient");
        Add(ref _closed, "closed {0} salient pocket(s)");
        Add(ref _gunned, "gave railway guns to {0} new set(s) of axis armies");
        Add(ref _railways, "upgraded {0} railway(s) to hubs of starving AI divisions");
        Add(ref _hubs, "queued {0} supply hub(s) where AI divisions starve");
        Add(ref _crewsOn, "turned on more ground crews in {0} air region(s)");
        Add(ref _crewsOff, "turned off more ground crews in {0} air region(s)");
        if (done.Count > 0) Log.Info($"last minute: {string.Join(", ", done)}");
    }

    // Game functions more than one feature calls: resolved once, whichever features are on.
    private static void ResolveShared()
    {
        // The core's game functions the AI hooks call: a broken pattern fails the load, not a hook on an AI thread.
        _ = Sig.Find(Game.ResolveRefSig) + Sig.Find(Game.IsAtWarWithSig) + Sig.Find(Game.SameOrAlliedSig) + Sig.Find(Province.FortLevelSig) + Sig.Find(Buildings.FindSig) + Sig.Find(Buildings.DbCallSig);
        var river = Sig.Find(Signatures.RiverPenalty);
        _riverSmall = river + 14 + *(int*)(river + 10); // mov rdx,[rip+disp] (7 bytes at +7)
        _riverLarge = river + 22 + *(int*)(river + 18); // cmovnz rdx,[rip+disp] (8 bytes at +14)
    }

    /// <summary>A ratio (organisation, strength: 1e5 = full) in % of full.</summary>
    private static long Percent(long ratio) => ratio * 100 / Native.FixedPoint;

    /// <summary>Runs a hook's own logic where Sheep Mod is in the game; an exception is logged (the first only) and the game goes on.</summary>
    private static void Safely(Action action)
    {
        if (!SheepMod.Active) return;
        try { action(); }
        catch (Exception e) { Failed(e); }
    }

    private static void Failed(Exception e)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0) _log?.Error($"AI hook: {e}");
    }
}
