using HOI4ModLoader;

namespace AiAssist;

/// <summary>
/// An add-on for Sheep Mod (the "Discord mod") that fixes vanilla AI logic its scripts can't reach, without bonuses.
/// Only works where Sheep Mod is in the game; single player (it changes how AI countries play).
/// - Production: the AI keeps factories on equipment it has plenty of while it is short of other equipment (its need
///   includes a stock buffer and its factory split is a greedy weighting). Before vanilla applies its factory targets,
///   half the factories of a big surplus (all of them for equipment nothing in its army uses) go to the equipment it lacks,
///   by how much production that shortage is worth;
///   the air and land totals stay as vanilla split them, ships are left alone.
/// - Offensive readiness: the battle plan AI decides to attack from faction strength and preparation alone, never from how
///   worn its divisions are (Sheep can only switch whole countries to careful on rifle counts). An "attack on" for an army
///   whose divisions are worn out is dropped; the AI asks again every hour, so the attack starts once they recover. It never
///   stops a running attack, and invasions and paradrops are left alone.
/// - Supply-aware attacks: local attacks skip targets whose supply the attackers can't follow (supply only flows through our
///   side's provinces; the attack's province is short and none next to the target has spare), unless the target closes a pocket.
/// - Refit priority: the daily stockpile distribution ranks divisions only by template and theatre priority, so a garrison
///   division and a front-line division get new equipment alike. AI divisions in combat or next to enemy ground now come
///   first (until they are 75 % full), for reinforcements and upgrades alike.
/// - Pocket closing: the AI's local attacks pick a random target among those that pass their checks (Sheep's pocket test
///   in script is never read). Where a target would cut off enemy divisions, only such targets stay in the pick.
/// </summary>
[Plugin("com.green1052.aiassist", "AI Assist", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe partial class AiAssist : Plugin
{
    private static int _failed; // AI hooks run on worker threads: log the first error only
    private static Logger? _log;

    public override void Load()
    {
        var refit = Config.Bind("AI", "RefitPriority", true,
            "Reinforcements and new equipment go to the AI's front-line divisions (in combat or next to enemy-held ground) before its rear and garrison divisions.");
        var production = Config.Bind("AI", "Production", true,
            "The AI moves factories from equipment it has plenty of to equipment it is short of, instead of stockpiling surpluses.");
        _surplusPercent = Math.Max(0, Config.Bind("AI", "SurplusPercent", 50, "Equipment more than this much (%) above what its army needs counts as surplus."));
        var readiness = Config.Bind("AI", "OffensiveReadiness", true,
            "The AI holds an attack while the army's divisions are worn out (average organisation or strength below the settings below); it starts once they recover.");
        _minOrg = Math.Clamp(Config.Bind("AI", "MinOrganisation", 50, "Average organisation (%) an army needs to start an attack."), 0, 100) * Native.FixedPoint / 100;
        _minStrength = Math.Clamp(Config.Bind("AI", "MinStrength", 60, "Average strength (%: equipment and manpower) an army needs to start an attack."), 0, 100) * Native.FixedPoint / 100;
        _minSupply = Math.Clamp(Config.Bind("AI", "MinSupply", 0, "Average supply (%) an army needs to start an attack (0: not checked; colonial fronts are always short)."), 0, 100) * Native.FixedPoint / 100;
        _pocketClosing = Config.Bind("AI", "PocketClosing", true,
            "The AI's local attacks go for provinces that cut off enemy divisions first, when there are any.");
        _frontShortening = Config.Bind("AI", "FrontShortening", true,
            "Otherwise the AI's local attacks prefer provinces whose capture shortens the front over ones that lengthen it.");
        _armorTerrain = Config.Bind("AI", "ArmorTerrain", true,
            "Armored and motorized divisions don't make local attacks into terrain that cripples them (forest, hills, marsh...) or across a river into mountains, marsh, jungle or cities.");
        _roles = Config.Bind("AI", "Roles", false,
            "Line infantry holds the line: provinces with only infantry make local attacks only into empty provinces or pockets; armored and breakthrough divisions do the pushing (not for armies with no such divisions).");
        _holdLine = Config.Bind("AI", "HoldTheLine", true,
            "At war, the AI keeps a division in every province of a front that borders the enemy (enemy divisions or not); it no longer ships them all off (supply crisis, another front) and leaves the line empty while fighting. "
            + "Its regrouping can't take a province's last defender or the divisions walling in a pocket, free divisions walk back into empty line provinces, and a country short of divisions for its line doesn't start attacks (advancing would widen it and get the spearhead cut off).");
        _concentration = Config.Bind("AI", "Concentration", true,
            "The AI keeps only a thin screen on fronts with no enemy at war (neutral or friendly borders) and sends the rest to fronts facing more enemy divisions than they hold; borders of a country in a warring country's faction keep their garrison, and fronts at war are never thinned.");
        _targetValue = Config.Bind("AI", "TargetValue", true,
            "The AI's local attacks go for valuable provinces first: victory points, capitals, supply hubs, airfields, resources, empty or worn-out defences (after pockets, before front shortening).");
        var leaders = Config.Bind("AI", "Leaders", true,
            "The AI gives its best field marshals and generals to its biggest, fighting, attacking fronts first, and counts the traits it ignores: infantry traits for infantry armies, offensive or defensive doctrine for attacking or holding fronts, bad status.");
        _airRetreat = Config.Bind("AI", "AirRetreat", true,
            "The AI pulls its planes out of regions where it is losing the air war badly (low air superiority, heavy losses, few kills) and uses them where it can win.");
        _airbaseEvac = Config.Bind("AI", "AirbaseEvacuation", true,
            "The AI's planes leave airbases the enemy is about to overrun instead of sitting there until the base falls.");
        _casConcentration = Config.Bind("AI", "CasConcentration", true,
            "The AI's close air support backs its breakthrough axis first: battles on the axis get the planes before ones elsewhere in the region.");
        _entrenchment = Config.Bind("AI", "Entrenchment", true,
            "The AI's local attacks count the defenders' dig-in: a province held by entrenched divisions is a worse target than its garrison numbers alone say.");
        _supplyAware = Config.Bind("AI", "SupplyAwareAttacks", true,
            "The AI's local attacks skip provinces its divisions couldn't be supplied in once taken: supply only flows through provinces of ours or our allies, and the attack's own province gets too little while no other of ours next to the target has some to spare. Pocket-closing targets are still taken.");
        _supplyShare = Math.Clamp(Config.Bind("AI", "SupplyAwareMinPercent", 25,
            "Supply (% of what the attacking province needs) it must get, or another province of ours next to the target must have to spare, for a local attack."), 1, 200);
        _stationing = Config.Bind("AI", "Stationing", true,
            "A division the theatre shuffle just walked to a new front stays there for a week; vanilla re-shuffles freshly arrived divisions back and forth.");
        _defensePriority = Config.Bind("AI", "DefensePriority", true,
            "Holes are refilled where holding counts: ports, victory points, river banks, cities and mountains first.");
        _axis = Config.Bind("AI", "BreakthroughAxis", true,
            "The AI's local attacks keep to a breakthrough axis: its front's thinnest enemy sector (few divisions, no forts, open terrain), held for a day.");
        _reserve = Config.Bind("AI", "Reserves", true,
            "A country with divisions to spare keeps reserve posts a province behind its line, covering the most front.");
        _beachDefense = Config.Bind("AI", "BeachDefense", true,
            "Naval landings are answered at once: free divisions next to the beachhead wall it in and attack into it; vanilla leaves it alone until the front collapses.");
        _navalSortie = Config.Bind("AI", "NavalSortie", true,
            "AI task forces pick their fights by the strength they see: clearly stronger than the enemy in their region they press, half as strong they slip away, and pairs of ships only shadow.");
        _navalEscort = Config.Bind("AI", "NavalEscort", true,
            "While a country is losing convoys fast, its naval fuel budget for convoy defence is doubled for that pass of the task force allocator.");

        _log = Log;
        ResolveShared();
        _game = new PerGame(() =>
        {
            // Everything cached across games must go: a new world frees the old one's fronts,
            // provinces and units, and a cached pointer into it is a crash.
            _sent.Clear();
            _gap.Clear();
            _posted.Clear();
            Axes.Clear();
            Factors.Clear();
            LastBattle.Clear();
            Breakthroughs.Clear();
            BaseThreat.Clear();
            TraitNames.Clear();
            _line.Clear();
            FrontCache.Clear();
            _capitals = (0, []);
            _beachheads = [];
            _provinceCount = -1;
        });
        MainThread.Every(TimeSpan.FromSeconds(1), _game.Check); // AI threads read these caches: reset them soon after a load
        if (readiness) LoadReadiness();
        if (_pocketClosing || _frontShortening || _armorTerrain || _roles || _targetValue || _axis || _supplyAware) LoadPockets();
        if (refit) LoadRefit();
        if (_airRetreat || _airbaseEvac) LoadAirRetreat();
        if (_casConcentration) LoadCas();
        if (leaders) LoadLeaders();
        if (_concentration || _holdLine || _reserve || _axis || _stationing) LoadConcentration();
        if (_navalSortie || _navalEscort) LoadNavy();
        if (_holdLine || _reserve) MainThread.Every(TimeSpan.FromSeconds(20), Backfill);
        if (_beachDefense) MainThread.Every(TimeSpan.FromSeconds(30), DefendBeachheads);
        MainThread.Every(TimeSpan.FromMinutes(1), LogStats);
        if (!production) return;
        _balance = (delegate* unmanaged<nint, long*, nint, long*>)Sig.Find(Signatures.GetBalance);
        _need = (delegate* unmanaged<nint, long*, nint, nint, long*>)Sig.Find(Signatures.GetNeed);
        _isAir = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.IsAir);
        _isNaval = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.IsNaval);
        _applyTargets = Patches.Hook<ApplyTargetsFn>(Sig.Find(Signatures.ApplyFactoryTargets), (minister, lines, byArchetype, byRole) =>
        {
            Safely(() => Rebalance(minister, lines - MilitaryLines));
            _applyTargets!.OriginalFunction(minister, lines, byArchetype, byRole);
        });
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
        Add(ref _held, "held {0} AI attack(s) until the army recovers");
        Add(ref _steered, "skipped {0} local attack target(s) for better ones");
        Add(ref _unsupplied, "skipped {0} local attack target(s) the attackers couldn't be supplied in");
        Add(ref _refitted, "put {0} front-line division-day(s) first for equipment");
        Add(ref _pulled, "lowered {0} lost air region(s)");
        Add(ref _concentrated, "rescaled {0} front request(s)");
        Add(ref _backfilled, "filled {0} hole(s) in AI lines");
        Add(ref _reserved, "posted {0} reserve(s)");
        Add(ref _defended, "answered {0} landing(s)");
        Add(ref _sortied, "chose {0} engagement level(s)");
        Add(ref _escorted, "escorted {0} convoy run(s)");
        Add(ref _stationed, "left {0} fresh arrival(s) in place");
        Add(ref _evacuated, "emptied {0} threatened airbase(s)");
        Add(ref _cas, "focused {0} CAS pass(es) on the axis");
        if (done.Count > 0) Log.Info($"last minute: {string.Join(", ", done)}");
    }

    // Game functions more than one feature calls: resolved once, whichever features are on.
    private static delegate* unmanaged<nint, nint> _resolve;
    private static delegate* unmanaged<nint, int*, byte> _atWar;
    private static delegate* unmanaged<nint, int*, byte, byte, byte> _allied;
    private static delegate* unmanaged<nint, int> _fortLevel;
    private static delegate* unmanaged<nint, nint, nint> _findBuilding;
    private static nint _buildingDb; // address of the building database global
    private static int _navalBaseSlot;

    private static void ResolveShared()
    {
        _resolve = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.ResolveRef);
        _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        _allied = (delegate* unmanaged<nint, int*, byte, byte, byte>)Sig.Find(Signatures.SameOrAllied);
        _fortLevel = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FortLevel);
        _findBuilding = (delegate* unmanaged<nint, nint, nint>)Sig.Find(Signatures.FindBuilding);
        var at = Sig.Find(Signatures.BuildingDbCall);
        var getter = at + 5 + *(int*)(at + 1);
        _buildingDb = getter + 18 + *(int*)(getter + 14);
        _navalBaseSlot = *(int*)(at + 8); // `mov rcx,[rax+3B8h]`: the naval base type, as the game reads it
    }

    private static int Hour => *(int*)(Game.State + HourOfGameState);

    private static int Divisions(nint province) => *(int*)(province + DivisionsInProvince);

    private static nint Terrain(nint province)
    {
        var info = *(nint*)(province + StaticOfProvince);
        return info == 0 ? 0 : *(nint*)(info + TerrainOfStatic);
    }

    /// <summary>A CArmy ratio (organisation or strength slot), 1e5 = full.</summary>
    private static long Ratio(nint unit, int slot)
    {
        long value;
        ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[slot])(unit, &value);
        return value;
    }

    private static bool AtWar(nint me, int tag)
    {
        if (tag <= 0) return false;
        var other = stackalloc int[] { tag, Game.TagIndex(tag) };
        return _atWar(me, other) != 0;
    }

    private static bool Allied(nint me, int tag)
    {
        if (tag <= 0) return false;
        var other = stackalloc int[] { tag, Game.TagIndex(tag) };
        return _allied(me, other, 0, 0) != 0;
    }

    private static bool Enemy(nint me, int tag, nint province)
    {
        var controller = *(int*)(province + ControllerOfProvince);
        return controller != tag && AtWar(me, controller); // 0: sea
    }

    private static bool Friendly(nint me, int tag, nint province)
    {
        var controller = *(int*)(province + ControllerOfProvince);
        return controller == tag || Allied(me, controller);
    }

    /// <summary>The country's faction, or 0 when it is in none (CIsInFactionWith compares these pointers).</summary>
    private static nint FactionOf(nint country)
    {
        var member = country == 0 ? 0 : *(nint*)(country + FactionMemberOfCountry);
        return member == 0 ? 0 : *(nint*)(member + FactionOfMember);
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
