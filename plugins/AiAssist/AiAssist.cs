using HOI4ModLoader;

namespace AiAssist;

/// <summary>
/// An add-on for Sheep Mod (the "Discord mod") that fixes vanilla AI logic its scripts can't reach, without bonuses.
/// Only works where Sheep Mod is in the game; single player (it changes how AI countries play).
/// - Hold the line: at war, every front province keeps a defender (front requests, regrouping and army balancing can't
///   take it), holes are refilled from nearby (key ground first, the same front's divisions first), reserves stand a province
///   behind and counterattack break-ins (fire brigade), key line provinces get a second line, freshly arrived divisions stay a
///   week, divisions worn by fighting rest behind the line and divisions in a closing salient pull out (both go back on their own
///   order once rested), and no "attack on" starts, and running front offensives stop, while the threatened line is thinly
///   manned (unless the AI outguns the enemy there two to one). New divisions go to the grad's or the line's holes, then the most
///   thinly held front, first. Its passes run on game time.
/// - Beach defence: landings (ground taken from the sea in the last ten days) are walled in and attacked at once.
/// - Breakthrough axis: local attacks (and close air support) keep to the AI's own armoured spearhead while it attacks, moving on
///   with it, else to the theatre's thinnest enemy sector for a day; off the axis only pinning attacks on enemy stacks next to it.
/// - Defensive postures: facing a much stronger enemy, a country gives up exposed ground for its grad (a minimum cut through its
///   provinces: rivers, rough ground, forts, few attack directions), holds it, and counterattacks only fought-out spearheads.
/// - Strategic invasions: naval invasions pick thinly held, valuable beaches and wait (a week at most) while their beach is crowded;
///   fighters and naval bombers cover the sea regions on a planned landing's route.
/// - Strategic paradrops: paratroopers of an army on the axis drop onto empty, valuable enemy ground behind the line near it.
/// - Attack targets: local attacks pick pocket-closing, then valuable, then front-shortening targets instead of at random
///   (Sheep's pocket test in script is never read); next to a pocket of our own, the target that opens a corridor to it
///   comes first (and breakouts from inside); fresh divisions take empty enemy provinces next to the line.
/// - Armor terrain, supply-aware attacks: local attacks skip targets that cripple armor or that supply can't follow.
/// - Air retreat: regions the AI is losing the air war in get fewer planes.
/// - Bombing targets: strategic bombers ask for more planes for the enemy regions next to the land front, less (and a lower
///   score) for ones far behind it.
/// - Air training: green wings train (the training mission) instead of flying where nothing happens, half the air force at most.
/// - Production lines: small factory cuts on efficiently running production lines are held for a while (vanilla restarts the
///   freed factories elsewhere at 10 % efficiency).
/// - Refit priority: front-line divisions get reinforcements and upgrades first (until they are 75 % full).
/// - Encirclement: enemy salients are cut at their neck from both flanks instead of pushed head-on.
/// - Railway guns: the armies on the breakthrough axis get the railway guns first and keep them.
/// - Supply hubs: the railway from the hub feeding starving AI divisions to the capital is upgraded, and (late, rich countries)
///   a new hub goes where they starve with none near (vanilla only upgrades and links hubs).
/// - Ground crews: spare command power goes to More Ground Crews in the regions where the AI's wings fight hardest (vanilla's AI never uses it).
/// </summary>
[Plugin(Id, "AI Assist", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe partial class AiAssist : Plugin
{
    private const string Id = "com.green1052.aiassist";
    private static int _failed; // AI hooks run on worker threads: log the first error only
    private static Logger? _log;

    public override void Load()
    {
        var refit = Config.Bind("AI", "RefitPriority", true,
            "Reinforcements and new equipment go to the AI's front-line divisions (in combat or next to enemy-held ground) before its rear and garrison divisions.");
        _holdLine = Config.Bind("AI", "HoldTheLine", true,
            "At war, the AI keeps a division in every province of a front that borders the enemy, and on the front at least as many divisions as the enemy has across it (other fronts can't take them); its regrouping can't take the last defender of a province next to enemy ground (nor one of no more than the enemy divisions next to it), the divisions walling in a pocket, "
            + "a reserve or a division that arrived this week, and no division on or right behind a line with enemy divisions near is strategically redeployed to another front. Free divisions walk back into empty line provinces (ports, victory points, river banks, cities and mountains first), "
            + "the same front's divisions first (one from another front is held there). A country with divisions to spare keeps reserves a province behind its line and a second division behind key line provinces; "
            + "ready reserves counterattack break-ins near them (enemy troops inside, ground lost in the last two days, a landing) and go back to their post. "
            + "One short of divisions for its line doesn't start attacks and stops the running offensives of its thinly held fronts, unless it outguns the enemy there two to one. "
            + "Divisions worn out by fighting rest behind the line (a quarter of those around at most), and divisions in a salient whose way back is closing under an enemy at least as strong pull out, where another of ours holds their province; "
            + "neither where the AI outguns the enemy two to one. Rested divisions, and reserves the line moved away from, go back on their own order. "
            + "Newly deployed divisions go where they are needed most first: the grad's holes (DefensivePostures), else empty line provinces, then the most thinly held front. "
            + "Against enemies that hold no port (so can't land), the AI keeps no coast and home garrisons: those divisions stay on the land front. For a month after its line was short and while a war on its home land border is on or coming (a justification, war goal, focus or war preparation either way), they keep a tenth of its divisions; otherwise, at war on a land line (its own or an ally's homeland), they take at most a third. "
            + "A front already twice the enemy keeps one per province, so its spare divisions can go where the line is short. Offensives that lose most of their attacks, or push a division into a salient "
            + "whose way back closes, are called off; army HQs stay off the fronts.");
        _beachDefense = Config.Bind("AI", "BeachDefense", true,
            "Naval landings are answered at once: free divisions near the beachhead (ground the enemy took from the sea in the last ten days, or a small enemy pocket on the coast) wall it in and attack into it; "
            + "vanilla leaves it alone until the front collapses.");
        _axis = Config.Bind("AI", "BreakthroughAxis", true,
            "The AI's local attacks keep to a breakthrough axis: while the AI's own armoured spearhead (the game's force concentration) attacks, where its path crosses the enemy line, moving forward with it; "
            + "otherwise the theatre's thinnest enemy sector (few divisions, no forts, open terrain), held three days and moved on only once taken. Off the axis, only attacks that pin enemy divisions next to it (they can't shift into the breakthrough while under attack). "
            + "Its close air support backs battles on the axis first.");
        _exploit = Config.Bind("AI", "Exploitation", true,
            "Through a gap in the enemy line (empty enemy ground no enemy division covers), fresh mobile divisions of the AI drive up to four provinces deep "
            + "to the most valuable province there (a capital, victory points, a supply hub, airfields), off their battle plan for a week; two at most per target, four per country every three hours. "
            + "Vanilla only takes the next province, so an empty capital behind a broken line stays untaken. Needs HoldTheLine.");
        _pincers = Config.Bind("AI", "Pincers", true,
            "Needs BreakthroughAxis. Where the enemy masses divisions on the line (3+ in a province, twice the line's average), the AI's axis becomes two arms: "
            + "the thinnest enemy sector on each side of the mass, 2-5 line provinces away, so the attacks close behind it; kept three days while both arms' targets are still the enemy's.") && _axis;
        _feints = Config.Bind("AI", "Feints", true,
            "Needs BreakthroughAxis. Before a new main push (a thin sector or a pincer), the AI's local attacks first hit, for a day and a half, the enemy line province with the most divisions "
            + "at least five provinces away, so its reserves move there; the push waits meanwhile.") && _axis;
        _timing = Config.Bind("AI", "OffensiveTiming", true,
            "Needs HoldTheLine. The AI starts a battle plan's offensive only once its divisions are ready: not while they average under 60% organisation, "
            + "nor while over 30% of them are still on their way to the line, nor while the enemy holds over 70% of the air over most of them, nor while most of them stand in mud.") && _holdLine;
        _attackTargets = Config.Bind("AI", "AttackTargets", true,
            "The AI's local attacks pick their target instead of at random: provinces that cut off enemy divisions first, then valuable ones (victory points, capitals, supply hubs, airfields, resources, "
            + "empty or worn-out defences; dug-in defenders count against), then ones that shorten the front. Next to a pocket of its own divisions, the province that opens a corridor to it "
            + "comes first (also breakouts from inside), ahead of pocket closing when at least as many of its divisions are trapped as the attack would cut off. "
            + "Fresh divisions also take empty enemy provinces next to the line, where another of ours stays.");
        _armorTerrain = Config.Bind("AI", "ArmorTerrain", true,
            "Armored and motorized divisions don't make local attacks into terrain that cripples them (forest, hills, marsh...) or across a river into mountains, marsh, jungle or cities.");
        _airRetreat = Config.Bind("AI", "AirRetreat", true,
            "The AI pulls its planes out of regions where it is losing the air war badly (low air superiority, heavy losses, few kills) and uses them where it can win.");
        _bombingTargets = Config.Bind("AI", "BombingTargets", true,
            "The AI's strategic bombers go for the enemy regions next to its land front first (the factories, supply hubs and airfields feeding the battle): they ask for twice the planes there, "
            + "while regions far behind the front get half the planes and a lower priority.");
        var airTraining = Config.Bind("AI", "AirTraining", true,
            "The AI trains its green air wings (under the second experience level) on the training mission instead of flying them where nothing happens: at peace any wing it would send on a mission, "
            + "at war fighters it would send to patrol a land region no enemy plane flies in; wings with no mission at all train too (daily, at war as well), carrier wings never. "
            + "At most half of a country's wings train at once; trained ones go back to the AI's missions. A wing isn't flown back to the base it left this week (vanilla flipped wings between two bases daily).");
        var armyTraining = Config.Bind("AI", "ArmyTraining", true,
            "AI armies with nothing to do train their divisions up to regular: at peace any army, at war armies holding no front (garrisons, reserves), "
            + "while none of their divisions fights, stands next to enemy ground or is under 70 % strength; none while the AI expects a war (a war preparation strategy, a focus aimed either way). "
            + "Vanilla stops every exercise once a war starts and never trains armies on no front.");
        var navalTraining = Config.Bind("AI", "NavalTraining", true,
            "At peace, AI task forces no naval objective takes train until their ships are regulars, instead of waiting in port (vanilla stops training once world tension passes 50 %); "
            + "none while the AI expects a war.");
        var shoreBombardment = Config.Bind("AI", "ShoreBombardment", true,
            "At war, AI task forces with guns or carriers go to the sea next to the country's coastal land battles and hold there, where their shore bombardment (or the carriers' wings) "
            + "weakens the enemy divisions. Taken before the naval AI's objectives do, off patrol, strike force, hold or training (never invasion support, convoys, raiding or mines): one per battle no other covers, "
            + "within 6 sea provinces; a week at the battle, up to two weeks waiting at sea for the next, halved (4+ ships) when a second battle has none. Vanilla keeps them on its objectives or sends them to port.");
        var stableLeaders = Config.Bind("AI", "StableLeaders", true,
            "The AI doesn't take a general or field marshal from an army or army group he commands: vanilla's daily leader choice took them from each other's groups "
            + "and back again (a group left without a leader took the next best, even one leading elsewhere), so every army changed its leader every day or two.");
        var productionLines = Config.Bind("AI", "ProductionLines", true,
            "The AI keeps factories on production lines that have built up efficiency: a small cut (up to a quarter) to equipment whose lines average 35 %+ efficiency is held for a while "
            + "and the factories come off the smallest increases elsewhere; vanilla moves them to other equipment, where they start over at 10 %. Big cuts and full stops go through.");
        var stickyDays = Math.Clamp(Config.Bind("AI", "StickyDays", 30,
            "Days a small cut to an efficient production line is held; if the AI still wants it after that, it goes through."), 1, 365);
        _supplyAware = Config.Bind("AI", "SupplyAwareAttacks", true,
            "The AI's local attacks skip provinces its divisions couldn't be supplied in once taken: supply only flows through provinces of ours or our allies, and the attack's own province gets too little while no other of ours next to the target has some to spare. Pocket-closing targets and the corridor to a pocket of ours are still taken.");
        _supplyShare = Math.Clamp(Config.Bind("AI", "SupplyAwareMinPercent", 25,
            "Supply (% of what the attacking province needs) it must get, or another province of ours next to the target must have to spare, for a local attack."), 1, 200);
        _strategicInvasions = Config.Bind("AI", "StrategicInvasions", true,
            "The AI picks the beach for a naval invasion by what is there: thinly held coast near victory points, capitals, supply hubs, airfields and resources first, "
            + "beaches with enemy divisions nearby, land forts or coastal bunkers last. A planned landing waits (a week at most) while the enemy divisions around its beach outnumber half the landing. "
            + "The sea regions on a planned landing's route get twice the fighters the air AI wants there (100 at least) and twice the naval bombers.");
        _paradrops = Config.Bind("AI", "StrategicParadrops", true,
            "The AI uses its paratroopers (needs BreakthroughAxis): up to four paradrop-capable divisions of an army of the theatre its breakthrough axis is in fly from the airbase nearest the axis that can carry them "
            + "onto an empty enemy province behind the line near the axis (victory points, supply hubs, airfields, pockets first). They jump only when vanilla allows the drop "
            + "(prepared, enough transport planes, air superiority on the route). With no axis drop, behind a beachhead of its own from the last ten days instead (from the airbase nearest it). "
            + "Paratroopers are kept off the fronts and wait at the best airbase within reach. "
            + "Vanilla's AI never paradrops, and Sheep Mod's AI builds few paratroopers or transport planes (see AirborneUnits); the log counts which check stopped them.") && _axis;
        // Game files count toward the checksum: added at startup only (the game reads them once), so a change waits for a restart.
        if (Config.Bind("AI", "AirborneUnits", true,
                "Germany, the UK, the US, Japan, the USSR and Italy research paratroopers and transport planes (from 1938, 30+ military factories), keep about 4% paratrooper divisions and build transport planes "
                + "(with the paratrooper tech, 40+ military factories), so that StrategicParadrops has something to drop with. An AI strategy file put in front of the game's own at startup: its values only add, "
                + "so Sheep Mod's deliberate aircraft bans still win. It changes the checksum: turn it off (and restart) to join a multiplayer game of players without it. Takes effect at the next start.")
            && Loader.AtStartup)
            AddGameFile("common/ai_strategy/hoimod_ai_airborne.txt");
        _postures = Config.Bind("AI", "DefensivePostures", true,
            "An AI country facing a much stronger enemy (twice its share of power along the line, or 1.6x while it lost line provinces in the last week) gives up exposed ground and holds its grad: "
            + "the most defensible short line between that enemy and its capital (rivers, mountains, marsh, forests, hills, cities and forts in front, few directions to be attacked from), redrawn weekly, "
            + "or one line back once the enemy breaks in. Its divisions walk there and stay (victory point cities get one more), it starts no offensives or paradrops (landings still go), and it attacks only "
            + "fought-out enemy stacks next to the line, with two fresh divisions or more (reserves behind the grad too), while single divisions pin the enemy stacks that could come to their aid; worn-out attackers break off. "
            + "Once every enemy it fell back from stays under 80 % of its power for a week (a month after falling back at the earliest), its divisions go back on their orders, and for a month only twice its strength "
            + "puts it back on a grad. Out of contact, the posture stays. Needs HoldTheLine.") && _holdLine;
        var encircle = Config.Bind("AI", "Encirclement", true,
            "The breakthrough axis goes for enemy salients first: where enemy ground bulges into the AI's line through a neck of one to three provinces, and taking the neck cuts off 3+ enemy divisions "
            + "for less than it costs, the local attacks and close air support of both flanks go for the neck instead of pushing the salient head-on; kept until the pocket closes, a week at most. Needs BreakthroughAxis.") && _axis;
        var railGuns = Config.Bind("AI", "RailwayGuns", true,
            "The AI's railway guns go to the armies on its breakthrough axis first and stay there: those armies are served first and other armies can't take their guns. Needs BreakthroughAxis.") && _axis;
        var supplyHubs = Config.Bind("AI", "SupplyHubs", true,
            "Where an AI country's divisions are low on supply, the railway from the supply hub feeding them to the capital is upgraded a level (one a week, the same hub's again after 60 days; a railway level costs little next to a hub). "
            + "From 1941 on, where the divisions of an AI country with 60+ civilian factories starve with no supply hub within 3 provinces, it also builds one (railway-connected provinces first); checked weekly, two a month at most. "
            + "A hub costs 20000, so earlier or poorer it doesn't. Vanilla's AI only upgrades and links the hubs it has.");
        var groundCrews = Config.Bind("AI", "GroundCrews", true,
            "The AI uses More Ground Crews (+10 % air efficiency in a region for 20 command power, given back when turned off): every two days, in the regions where its wings fought hardest in the last three days "
            + "(10+ planes lost and shot down where enemy planes are active), two at most, while 50 command power is left after the cost; off again where its wings stopped fighting for a week, or one a pass while command power is under 50. Vanilla's AI never turns them on.");

        _log = Log;
        ResolveShared();
        _game = new PerGame(() =>
        {
            // Everything cached across games must go: a new world frees the old one's fronts,
            // provinces and units, and a cached pointer into it is a crash.
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
            Feints.Clear();
            AxisArmies.Clear();
            Beaches.Clear();
            Routes.Clear();
            Holds.Clear();
            ClearParadrops();
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
        if (stableLeaders) Patches.Bytes(Sig.Find(Signatures.LeaderStealBranch) + 0xF, "75", "EB"); // jnz -> jmp: a leader of another group scores -100, as one whose group is fighting
        if (navalTraining || shoreBombardment) LoadNavy(navalTraining, shoreBombardment);
        if (productionLines) LoadProductionLines(stickyDays);
        if (_axis) LoadCas();
        if (_holdLine || _axis) LoadFronts();
        if (_holdLine || _strategicInvasions || _paradrops) LoadPostAiCommand(); // one hook; each part runs under its own setting
        if (_holdLine)
        {
            _ = Sig.Find(Commands.UnassignSig);
            _ = Sig.Find(Commands.AssignSig);
            MainThread.Every(TimeSpan.FromSeconds(1), Backfill); // the passes poll each second and run on game time (Due)
            if (_exploit) MainThread.Every(TimeSpan.FromSeconds(1), Exploit);
        }
        if (_strategicInvasions) LoadInvasions();
        if (_paradrops) LoadParadrops();
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
        Add(ref _stationed, "left {0} fresh arrival(s) in place");
        Add(ref _deployed, "sent {0} new division(s) where they were needed");
        Add(ref _cas, "focused {0} CAS pass(es) on the axis");
        Add(ref _spearheads, "put {0} breakthrough axis/axes on the AI's armoured spearhead");
        Add(ref _paradropped, "launched {0} AI paradrop(s)");
        Add(ref _beachDrops, "readied {0} AI paradrop(s) behind a beachhead");
        Add(ref _paratroopersReserved, "took {0} paratrooper division(s) off the front for drops");
        Add(ref _noParatroopers, "paradrops: {0} axis check(s) found no ready paratroopers");
        Add(ref _noAirbase, "paradrops: {0} found no source airbase with transport planes");
        Add(ref _noTarget, "paradrops: {0} found no target");
        Add(ref _notReady, "paradrops: {0} not ready (not allowed, not prepared, too few planes or no air superiority)");
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

    /// <summary>One of the embedded gamefiles\* in front of the game's own files (GameFiles.Add).</summary>
    private static void AddGameFile(string path)
    {
        using var stream = typeof(AiAssist).Assembly.GetManifestResourceStream("gamefiles/" + Path.GetFileName(path))
            ?? throw new FileNotFoundException($"no embedded {path}");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        GameFiles.Add(path, bytes.ToArray());
    }

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
