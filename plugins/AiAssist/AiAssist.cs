using HOI4ModLoader;

namespace AiAssist;

/// <summary>
/// An add-on for Sheep Mod (the "Discord mod") that fixes vanilla AI logic its scripts can't reach, without bonuses.
/// Only works where Sheep Mod is in the game; single player (it changes how AI countries play).
/// - Hold the line: at war, every front province keeps a defender (front requests, regrouping and army balancing can't
///   take it), holes are refilled from nearby (key ground first), reserves stand a province behind, freshly arrived
///   divisions stay a week, worn-out divisions rest behind the line and divisions in a closing salient pull out (both go
///   back once rested), and no "attack on" starts while the threatened line is thinly manned.
/// - Beach defence: landings are walled in and attacked at once.
/// - Breakthrough axis: local attacks (and close air support) keep to the theatre's thinnest enemy sector for a day.
/// - Attack targets: local attacks pick pocket-closing, then valuable, then front-shortening targets instead of at random
///   (Sheep's pocket test in script is never read); fresh divisions take empty enemy provinces next to the line.
/// - Armor terrain, supply-aware attacks: local attacks skip targets that cripple armor or that supply can't follow.
/// - Air retreat: regions the AI is losing the air war in get fewer planes.
/// - Refit priority: front-line divisions get reinforcements and upgrades first (until they are 75 % full).
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
        _holdLine = Config.Bind("AI", "HoldTheLine", true,
            "At war, the AI keeps a division in every province of a front that borders the enemy; its regrouping can't take a province's last defender, the divisions walling in a pocket, "
            + "a reserve or a division that arrived this week. Free divisions walk back into empty line provinces (ports, victory points, river banks, cities and mountains first), "
            + "a country with divisions to spare keeps reserves a province behind its line, and one short of divisions for its line doesn't start attacks. "
            + "Worn-out divisions in contact rest behind the line where another of ours holds their province, and divisions in a salient whose way back is closing pull out; both go back to a front once rested.");
        _beachDefense = Config.Bind("AI", "BeachDefense", true,
            "Naval landings are answered at once: free divisions next to the beachhead wall it in and attack into it; vanilla leaves it alone until the front collapses.");
        _axis = Config.Bind("AI", "BreakthroughAxis", true,
            "The AI's local attacks keep to a breakthrough axis: its front's thinnest enemy sector (few divisions, no forts, open terrain), held for a day. Its close air support backs battles on the axis first.");
        _attackTargets = Config.Bind("AI", "AttackTargets", true,
            "The AI's local attacks pick their target instead of at random: provinces that cut off enemy divisions first, then valuable ones (victory points, capitals, supply hubs, airfields, resources, "
            + "empty or worn-out defences; dug-in defenders count against), then ones that shorten the front. Fresh divisions also take empty enemy provinces next to the line, where another of ours stays.");
        _armorTerrain = Config.Bind("AI", "ArmorTerrain", true,
            "Armored and motorized divisions don't make local attacks into terrain that cripples them (forest, hills, marsh...) or across a river into mountains, marsh, jungle or cities.");
        _airRetreat = Config.Bind("AI", "AirRetreat", true,
            "The AI pulls its planes out of regions where it is losing the air war badly (low air superiority, heavy losses, few kills) and uses them where it can win.");
        _supplyAware = Config.Bind("AI", "SupplyAwareAttacks", true,
            "The AI's local attacks skip provinces its divisions couldn't be supplied in once taken: supply only flows through provinces of ours or our allies, and the attack's own province gets too little while no other of ours next to the target has some to spare. Pocket-closing targets are still taken.");
        _supplyShare = Math.Clamp(Config.Bind("AI", "SupplyAwareMinPercent", 25,
            "Supply (% of what the attacking province needs) it must get, or another province of ours next to the target must have to spare, for a local attack."), 1, 200);

        _log = Log;
        ResolveShared();
        _game = new PerGame(() =>
        {
            // Everything cached across games must go: a new world frees the old one's fronts,
            // provinces and units, and a cached pointer into it is a crash.
            _sent.Clear();
            _gap.Clear();
            _posted.Clear();
            _resting.Clear();
            Axes.Clear();
            _line.Clear();
            FrontCache.Clear();
            _capitals = (0, []);
            _beachheads = [];
            _provinceCount = -1;
        });
        MainThread.Every(TimeSpan.FromSeconds(1), _game.Check); // AI threads read these caches: reset them soon after a load
        if (_attackTargets || _armorTerrain || _axis || _supplyAware) LoadPockets();
        if (refit) LoadRefit();
        if (_airRetreat) LoadAirRetreat();
        if (_axis) LoadCas();
        if (_holdLine || _axis) LoadFronts();
        if (_holdLine)
        {
            LoadAttackGate();
            _unassign = (delegate* unmanaged<nint, nint, nint, byte, byte, nint>)Sig.Find(Signatures.UnassignCommand);
            MainThread.Every(TimeSpan.FromSeconds(20), Backfill);
        }
        if (_holdLine || _attackTargets) MainThread.Every(TimeSpan.FromSeconds(5), Micro);
        if (_beachDefense) MainThread.Every(TimeSpan.FromSeconds(30), DefendBeachheads);
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
        Add(ref _held, "held {0} AI attack(s) while the line is thin");
        Add(ref _steered, "skipped {0} local attack target(s) for better ones");
        Add(ref _unsupplied, "skipped {0} local attack target(s) the attackers couldn't be supplied in");
        Add(ref _taken, "sent {0} division(s) into empty enemy provinces");
        Add(ref _refitted, "put {0} front-line division-day(s) first for equipment");
        Add(ref _pulled, "lowered {0} lost air region(s)");
        Add(ref _raised, "raised {0} front request(s) to cover the line");
        Add(ref _backfilled, "filled {0} hole(s) in AI lines");
        Add(ref _reserved, "posted {0} reserve(s)");
        Add(ref _rested, "rested {0} worn-out division(s)");
        Add(ref _escaped, "pulled {0} division(s) out of closing salients");
        Add(ref _defended, "answered {0} landing(s)");
        Add(ref _stationed, "left {0} fresh arrival(s) in place");
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

    private const int StrengthRatioSlot = 34, OrgRatioSlot = 39; // CArmy vtable: (unit, long* out) -> out

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
