using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CAIInteriorMinister: turn its factories wanted per equipment archetype (minister+696, int each) into production line
    /// changes (minister, CPdxArray&lt;CProductionLine*&gt;* production+88, current per archetype, current per naval role).
    /// Every 2nd day, on the country's AI worker thread.
    /// </summary>
    [Signature] public const string ApplyFactoryTargets = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 55 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 90 00 00 00 4D 8B F9";

    /// <summary>CLogistics::GetBalance(logistics, long* out, CEquipmentType* archetype): stockpile - need - net lend-lease (fixed point).</summary>
    [Signature] public const string GetBalance = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 49 8B D8 48 8B F2 48 8B F9 E8 ? ? ? ? 45 33 C9";

    /// <summary>CLogistics::GetNeed(logistics, long* out, CEquipmentType* archetype, null): missing in fielded divisions and reserves (fixed point).</summary>
    [Signature] public const string GetNeed = "48 8B C4 4C 89 48 ? 4C 89 40 ? 48 89 50 ? 48 89 48 ? 56";

    /// <summary>CEquipmentType::IsAir(type) / IsNaval(type) -> bool.</summary>
    [Signature] public const string IsAir = "40 53 48 83 EC 20 48 8B D9 48 8B 89 ? ? ? ? 48 3B CB 74 ? E8 ? ? ? ? 84 C0 74 ? B0 01 48 83 C4 20 5B C3 48 8B 83 ? ? ? ? 48 8B 8B ? ? ? ? 48 85 C0 75 ? 48 8B 83 ? ? ? ? 48 8B 80 ? ? ? ? 48 0B C8 E8 ? ? ? ? 48 B9 00 FC 37 00 1F 00 00 00";
    [Signature] public const string IsNaval = "40 53 48 83 EC 20 48 8B D9 48 8B 89 ? ? ? ? 48 3B CB 74 ? E8 ? ? ? ? 84 C0 74 ? B0 01 48 83 C4 20 5B C3 48 8B 83 ? ? ? ? 48 8B 8B ? ? ? ? 48 85 C0 75 ? 48 8B 83 ? ? ? ? 48 8B 80 ? ? ? ? 48 0B C8 E8 ? ? ? ? 48 B9 C1 03 40 00 80 00 00 00";

    /// <summary>PostAICommand(command, int* postingTag) -> bool: every AI command, from every AI path; an invalid one is deleted and 0 returned.</summary>
    [Signature] public const string PostAICommand = "40 53 48 83 EC 40 80 3D ? ? ? ? 00 48 8B D9 0F 84 ? ? ? ? 80 3D ? ? ? ? 00 75 ? 48 8B CA E8";

    /// <summary>
    /// CMoveCommand ctor (mem[0x88], CArmy* unit, int province, bool clearQueue, int moveType, ...): a plain move, the same
    /// one the battle plan UI sends. The command holds the unit as a CRef at +0x38 and its provinces at +0x40.
    /// </summary>
    [Signature] public const string MoveCommand = "48 89 4C 24 ? 53 48 83 EC 60 48 8B D9 C6 41 08 00 C7 41 0C FF FF FF FF 33 C9 89 4B 10 C7 43 14 00 00 FF FF 66 89 4B 18 48 89 4B 1C 48 8D 05 ? ? ? ? 48 89 03 48 8D 4B 28";

    /// <summary>The AI theatre distribution: collect divisions it may move to other fronts (ctx, CPdxArray&lt;{CArmy*, slot*}&gt;* out): every army member on no order.</summary>
    [Signature] public const string CollectReassignableUnits = "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 98 00 00 00 4C 8B FA";

    /// <summary>CAIFront: put its divisions into armies (front, CPdxArray&lt;CArmy*&gt;* units, CPdxArray&lt;CPdxArray&lt;int&gt;&gt;* partitions by army, specs, log): assigns, or makes new armies.</summary>
    [Signature] public const string ApplyArmiesBalancing = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 41 56 48 83 EC 50 49 8B F9 49 8B F0";

    /// <summary>CRef::Resolve(int* ref) -> object.</summary>
    [Signature] public const string ResolveRef = "48 83 EC 28 8B 01 3D 68 12 00 00 76";

    /// <summary>COrdersGroup::FindOrder(group, int id) -> COrder* (its own orders, then its parents').</summary>
    [Signature] public const string FindOrder = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 20 8B EA 48 8B D9 0F 1F 80 00 00 00 00 33 FF 39 BB A4 00 00 00 7E";

    /// <summary>
    /// The AI's micro attacks, building a source province's target candidates: `mov r8,rdi; mov rdx,r14; lea rcx,[rbp+..];
    /// call CantMoveInto; test al,al; jnz skip`. The call (at +10) is replaced with <see cref="AiAssist.PocketFilter"/>.
    /// </summary>
    [Signature] public const string MicroAttackCandidate = "4C 8B C7 49 8B D6 48 8D 4D ? E8 ? ? ? ? 84 C0 0F 85 ? ? ? ? 44 8B B7 1C 01 00 00 44 8B BF A4 00 00 00";

    /// <summary>CantMoveInto(int* {tag, tag index}, CProvince* from, CProvince* to) -> bool (diplomatic block); shared with unit movement.</summary>
    [Signature] public const string CantMoveInto = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 49 8D 98 88 01 00 00 48 8B F1 83 3B 00 48 8D 8A 88 01 00 00";

    /// <summary>CCountry::IsAtWarWith(country, int* {tag, tag index}) -> bool.</summary>
    [Signature] public const string IsAtWarWith = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 8B 41 08 48 8B FA 8B 12 48 8B F1 3B D0 74 ? 85 D2 74 ? 85 C0 74 ? 48 83 C1 08 E8 ? ? ? ? 39 47 04 74 ? 83 7E 08 00 7E ? 48 8B 86 ? ? ? ?";

    /// <summary>CCountry: the same country or an ally (country, int* {tag, tag index}, 0, 0) -> bool.</summary>
    [Signature] public const string SameOrAllied = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 20 8B 41 08 48 8B F9 48 83 C1 08 48 8B DA 8B 12 41 0F B6 F1";

    /// <summary>FortLevel(CProvince*) -> int: land forts, as the micro attacks weigh them.</summary>
    [Signature] public const string FortLevel = "4C 63 91 EC 01 00 00 33 C0 4D 85 D2 7E ? 4C 8B 99 E0 01 00 00 44 8B C8 0F 1F 84 00 00 00 00 00 4B 8B 0C CB 49 FF C1 48 8B 91 E0 01 00 00 44 0F BF 41 42 44 0F AF 82 08 03 00 00";

    [VTable] public const string ExecuteCommand = "COrderExecuteCommand";

    /// <summary>River crossing in land combat: `mov r8,[rcx+138h]; mov rdx,[rip+RIVER_CROSSING_PENALTY]; cmovnz rdx,[rip+..._LARGE]`.</summary>
    [Signature] public const string RiverPenalty = "4C 8B 81 38 01 00 00 48 8B 15 ? ? ? ? 48 0F 45 15 ? ? ? ?";

    /// <summary>
    /// AIAir: score every strategic region for the country's air force (aiObj, CCountry*, scratch, buffer, CPdxArray&lt;entry 0x4E8&gt;*
    /// entries, bases, long totals[18], buffer); entry+0x10 is the score, the share of planes it gets (&lt;= 0: none).
    /// </summary>
    [Signature] public const string UpdateRegionPriorities = "48 89 5C 24 ? 4C 89 4C 24 ? 4C 89 44 24 ? 48 89 54 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8B EC 48 83 EC 70 80 3D ? ? ? ? 00 0F 84";

    /// <summary>GetStrategicAirForCountry (twice, identical): `mov rbx,[rax+690h]; ...; cmp dword [rbx+3Ch],0; ...; mov rax,[rbx+30h]; mov rax,[rax+rcx*8]`.</summary>
    [Signature(2)] public const string StrategicAir = "48 8B 98 90 06 00 00 48 85 DB 74 ? 83 7B ? ? 74 ? 83 7F ? ? 48 8D 4F ? 75 ? 33 C0 48 63 C8 48 8B 43 ? 48 8B 04 C8";

    /// <summary>CStrategicAir::GetRegionAirData: superiority records at sa+0xE0, 0xA0 each, by region+0x60 (pins those offsets).</summary>
    [Signature] public const string RegionAirData = "48 85 D2 75 ? 48 8D 05 ? ? ? ? C3 48 63 42 60 48 8D 04 80 48 C1 E0 05 48 03 81 E0 00 00 00 C3";

    /// <summary>CAIGeneral::UpdateLeaders(general): daily, its fronts in array order (general+0x48) each take the best free leader.</summary>
    [Signature] public const string UpdateLeaders = "48 89 4C 24 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC 88 02 00 00 0F 29 B4 24 ? ? ? ? 4C 8B F1 45 33 E4";

    /// <summary>CAIBaseGeneral::ScoreLeader(leader, COrdersGroup*, operation, CAIFront*, bool defensive) -> int (&lt; 0: not allowed).</summary>
    [Signature] public const string ScoreLeader = "4C 89 4C 24 20 4C 89 44 24 18 48 89 54 24 10 55 56 57 41 57 48 83 EC 68 48 8B EA 4C 8B F9 E8 ? ? ? ? 84 C0 75 ? 80 7D 39 00 BE E8 03 00 00 41 8B 87 7C 0E 00 00";

    /// <summary>
    /// CAIGeneral::UpdateFrontUnitRequests(general): every front of the theatre sets its Min/Desired/Max divisions
    /// (CAIFront+0x90/+0x94/+0x98); the theatre's divisions are shared out by them right after.
    /// </summary>
    [Signature] public const string UpdateFrontUnitRequests = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 30 48 8B F1 48 8B 49 08 48 8B 01 FF 50 08 48 8B C8 E8 ? ? ? ? 48 8B C8 48 8B D8 48 8B 90 88 0F 00 00 83 BA 04 01 00 00 00";

    /// <summary>BuildingContainer::Find(container, CBuildingType*) -> CBuilding* or null (+0x40 level, +0x42 working level).</summary>
    [Signature] public const string FindBuilding = "80 7A 20 00 74 ? 83 79 2C 00 74 ? 48 63 92 E0 02 00 00 48 8B 41 20";

    /// <summary>A call of the building database getter, `call Get; mov rcx,[rax+3B8h]`; the getter reads [rip+db].</summary>
    [Signature] public const string BuildingDbCall = "E8 ? ? ? ? 48 8B 88 B8 03 00 00 E8 ? ? ? ? 8B D8";

    /// <summary>CStrategicAir::GetOrCreateRegionCombatData: losses and kills per region at sa+0x170 (pins that offset; never called).</summary>
    [Signature] public const string RegionCombatData = "48 89 74 24 ? 57 48 83 EC 20 48 63 C2 48 8B F9 48 8D 34 C5 00 00 00 00 48 8B 81 70 01 00 00";

    /// <summary>
    /// The naval AI's task force allocator (navalAI): scores its objectives, sorts them into strike / convoy defence /
    /// other buckets and hands its task forces out under fuel budgets read from NAI defines. Country AI worker thread.
    /// </summary>
    [Signature] public const string NavalAllocator = "48 89 4C 24 08 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 D8 F6 FF FF 48 81 EC 28 0A 00 00";

    /// <summary>CSetNavyEngagementCommand ctor (cmd, CTaskForce*, int level 0-4): every AI aggression change posts it; the player's button uses it too.</summary>
    [Signature] public const string NavyEngagement = "48 89 5C 24 20 48 89 4C 24 08 55 56 57 48 83 EC 30 41 8B F0 48 8B D9 C6 41 08 00 C7 41 0C FF FF FF FF 33 C9 89 4B 10";

    /// <summary>
    /// CAirMission::CollectTacticalTargets(mission, CPdxArray&lt;STacticalTarget&gt;* targets, ...): the battles a close air
    /// support mission sees, in region province-list order — the order the plane budget is spent in. Main thread.
    /// </summary>
    [Signature] public const string CollectTacticalTargets = "4C 8B DC 49 89 4B 08 56 57 41 55 48 81 EC C0 00 00 00 83 79 44 00 41 8B F9 49 63 F0 4C 8B EA 0F 84 ? ? ? ? 48 8B 41 30 48 83 B8 98 00 00 00 00";
}

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
/// - Pocket closing: the AI's local attacks pick a random target among those that pass their checks (Sheep's pocket test
///   in script is never read). Where a target would cut off enemy divisions, only such targets stay in the pick.
/// </summary>
[Plugin("com.green1052.aiassist", "AI Assist", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe class AiAssist : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int MilitaryLines = 88, CountryOfProduction = 656, BestVariants = 208; // CCountryProduction
    private const int LogisticsOfCountry = 0xF98, DesiredFactories = 696;                // CAIInteriorMinister: int per archetype
    private const int TypeOfVariant = 1008, ArchetypeOfType = 1240, CostOfVariant = 976;
    private const int Air = 0, Land = 1;

    [Function(CallingConventions.Microsoft)] public delegate void ApplyTargetsFn(nint minister, nint lines, nint currentByArchetype, nint currentByRole);

    private IHook<ApplyTargetsFn>? _applyTargets;
    private delegate* unmanaged<nint, long*, nint, long*> _balance;
    private delegate* unmanaged<nint, long*, nint, nint, long*> _need;
    private delegate* unmanaged<nint, byte> _isAir, _isNaval;
    private int _surplusPercent;
    private int _failed; // AI hooks run on worker threads: log the first error only

    public override void Load()
    {
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

        if (readiness) LoadReadiness();
        if (_targetValue || _defensePriority) LoadTargetValue();
        if (_pocketClosing || _frontShortening || _armorTerrain || _roles || _targetValue || _axis) LoadPockets();
        if (_airRetreat || _airbaseEvac) LoadAirRetreat();
        if (_casConcentration) LoadCas();
        if (leaders) LoadLeaders();
        if (_concentration || _holdLine || _reserve || _axis || _stationing) LoadConcentration();
        if (_navalSortie || _navalEscort) LoadNavy();
        if (_holdLine || _reserve || _beachDefense)
        {
            _move = (delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint>)Sig.Find(Signatures.MoveCommand);
            _game = new PerGame(() =>
            {
                // Everything cached across games must go: a new world frees the old one's fronts,
                // provinces and units, and a cached pointer into it is the crash the old build died of.
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
                _capitals = (0, []);
                _beachheads = [];
                _provinceCount = -1;
            });
            MainThread.Every(TimeSpan.FromSeconds(20), Backfill);
            if (_beachDefense) MainThread.Every(TimeSpan.FromSeconds(30), DefendBeachheads);
        }
        MainThread.Every(TimeSpan.FromMinutes(1), () =>
        {
            var (held, steered, pulled, concentrated, filled, reserved, defended, sortied, escorted, stationed, evacuated, cas) = (Interlocked.Exchange(ref _held, 0), Interlocked.Exchange(ref _steered, 0), Interlocked.Exchange(ref _pulled, 0), Interlocked.Exchange(ref _concentrated, 0), Interlocked.Exchange(ref _backfilled, 0), Interlocked.Exchange(ref _reserved, 0), Interlocked.Exchange(ref _defended, 0), Interlocked.Exchange(ref _sortied, 0), Interlocked.Exchange(ref _escorted, 0), Interlocked.Exchange(ref _stationed, 0), Interlocked.Exchange(ref _evacuated, 0), Interlocked.Exchange(ref _cas, 0));
            if (held + steered + pulled + concentrated + filled + reserved + defended + sortied + escorted + stationed + evacuated + cas > 0)
                Log.Info($"last minute: held {held} AI attack(s) until the army recovers, skipped {steered} local attack target(s) for better ones, lowered {pulled} lost air region(s), rescaled {concentrated} front request(s), filled {filled} hole(s) in AI lines, posted {reserved} reserve(s), answered {defended} landing(s), chose {sortied} engagement level(s), escorted {escorted} convoy run(s), left {stationed} fresh arrival(s) in place, emptied {evacuated} threatened airbase(s), focused {cas} CAS pass(es) on the axis");
        });
        if (!production) return;
        _balance = (delegate* unmanaged<nint, long*, nint, long*>)Sig.Find(Signatures.GetBalance);
        _need = (delegate* unmanaged<nint, long*, nint, nint, long*>)Sig.Find(Signatures.GetNeed);
        _isAir = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.IsAir);
        _isNaval = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.IsNaval);
        _applyTargets = Patches.Hook<ApplyTargetsFn>(Sig.Find(Signatures.ApplyFactoryTargets), (minister, lines, byArchetype, byRole) =>
        {
            Safely(() =>
            {
                if (SheepMod.Active) Rebalance(minister, lines - MilitaryLines);
            });
            _applyTargets!.OriginalFunction(minister, lines, byArchetype, byRole);
        });
    }

    /// <summary>Moves wanted factories (minister+696) from surplus archetypes to short ones, per category, keeping each total.</summary>
    private void Rebalance(nint minister, nint production)
    {
        var country = *(nint*)(production + CountryOfProduction);
        var logistics = country == 0 ? 0 : *(nint*)(country + LogisticsOfCountry);
        if (logistics == 0) return;
        var desired = Native.PdxArray<int>(minister + DesiredFactories);
        var best = Native.PdxArray(production + BestVariants);

        var pool = new int[2];
        var donors = new List<(int Index, int Category, int Given)>();
        var wanting = new List<(int Index, int Category, double Gap)>();
        for (var i = 0; i < Math.Min(desired.Length, best.Length); i++)
        {
            var variant = best[i];
            var type = variant == 0 ? 0 : *(nint*)(variant + TypeOfVariant);
            var archetype = type == 0 ? 0 : *(nint*)(type + ArchetypeOfType);
            if (archetype == 0 || _isNaval(archetype) != 0) continue;
            var category = _isAir(archetype) != 0 ? Air : Land;
            long balance, need;
            _balance(logistics, &balance, archetype);
            _need(logistics, &need, archetype, 0);
            var units = balance / (double)Native.FixedPoint;
            var needed = need / (double)Native.FixedPoint;
            if (units < 0)
                wanting.Add((i, category, -units * *(long*)(variant + CostOfVariant) / Native.FixedPoint)); // the shortage in production cost
            else if (needed <= 0 ? units > 0 && desired[i] > 0 : units > needed * _surplusPercent / 100 && desired[i] > 1)
            {
                var given = needed <= 0 ? desired[i] : desired[i] / 2; // nothing in the army uses it: all of them
                desired[i] -= given;
                pool[category] += given;
                donors.Add((i, category, given));
            }
        }

        foreach (var category in new[] { Air, Land })
        {
            var takers = wanting.Where(s => s.Category == category).OrderByDescending(s => s.Gap).ToList();
            if (takers.Count == 0)
            {
                foreach (var d in donors.Where(d => d.Category == category)) desired[d.Index] += d.Given; // nobody short: as vanilla wanted
                continue;
            }
            var left = pool[category];
            var total = takers.Sum(t => t.Gap);
            foreach (var t in takers)
            {
                var share = t == takers[^1] ? left : Math.Min(left, (int)Math.Ceiling(pool[category] * t.Gap / total));
                desired[t.Index] += share;
                left -= share;
            }
        }
    }

    // ---- Offensive readiness: an "attack on" an AI army isn't ready for is dropped; the AI asks again every hour ----

    private const int GroupOfCommand = 0x28, OrderOfCommand = 0x30, ExecuteOn = 0x34, AlsoArmies = 0x35; // COrderExecuteCommand
    private const int TypeOfOrder = 0x30, Executing = 0x248, Invasion = 3, Paradrop = 4;                   // COrder
    private const int UnitOfMemberSlot = 4, StrengthRatioSlot = 34, OrgRatioSlot = 39;                     // member -> CArmy; CArmy ratios (1e5)
    private const int SupplyOfUnit = 0x40;                                                                 // CArmy: supply ratio (1e5)
    private const long WeakOrg = 35 * Native.FixedPoint / 100;
    private const int MaxWeakPercent = 30;

    [Function(CallingConventions.Microsoft)] public delegate byte PostFn(nint cmd, nint tag);

    private IHook<PostFn>? _post;
    private delegate* unmanaged<nint, nint> _resolve;
    private delegate* unmanaged<nint, int, nint> _findOrder;
    private nint _executeVTable;
    private long _minOrg, _minStrength, _minSupply;
    private static bool _frontShortening, _pocketClosing;
    private int _held;

    private void LoadReadiness()
    {
        _resolve = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.ResolveRef);
        _allied = (delegate* unmanaged<nint, int*, byte, byte, byte>)Sig.Find(Signatures.SameOrAllied);
        _findOrder = (delegate* unmanaged<nint, int, nint>)Sig.Find(Signatures.FindOrder);
        _executeVTable = Sig.VTable(Signatures.ExecuteCommand);
        _post = Patches.Hook<PostFn>(Sig.Find(Signatures.PostAICommand), (cmd, tag) =>
        {
            if (*(nint*)cmd != _executeVTable || *(byte*)(cmd + ExecuteOn) == 0 || *(int*)tag == Game.ControlledTag || !SheepMod.Active || Ready(cmd, tag))
                return _post!.OriginalFunction(cmd, tag);
            ((delegate* unmanaged<nint, int, void>)(*(nint**)cmd)[0])(cmd, 1); // deleted, as the game does with an invalid command
            Interlocked.Increment(ref _held);
            return 0;
        });
    }

    /// <summary>Whether the army (and an army group's armies) of an "attack on" command is fit to attack. Read only: AI threads.</summary>
    private bool Ready(nint cmd, nint tag)
    {
        var reference = (int*)(cmd + GroupOfCommand);
        if (reference[0] == 0 && reference[1] == 0) return true;
        var group = _resolve((nint)reference);
        if (group == 0) return true;
        var id = *(int*)(cmd + OrderOfCommand);
        if (id != 0)
        {
            var order = _findOrder(group, id);
            if (order == 0 || *(byte*)(order + Executing) != 0 || *(int*)(order + TypeOfOrder) is Invasion or Paradrop) return true; // running, or at sea
        }
        List<nint> groups = [group];
        if (new OrdersGroup(group).IsArmyGroup) groups.AddRange(new OrdersGroup(group).Armies.ToArray());

        long count = 0, org = 0, strength = 0, supply = 0, weak = 0;
        foreach (var g in groups)
        foreach (var member in Native.PdxArray(g + OrdersGroup.MembersOffset))
        {
            var unit = ((delegate* unmanaged<nint, nint>)(*(nint**)member)[UnitOfMemberSlot])(member);
            long o, s;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[OrgRatioSlot])(unit, &o);
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[StrengthRatioSlot])(unit, &s);
            count++;
            org += o;
            strength += s;
            supply += *(long*)(unit + SupplyOfUnit);
            if (o < WeakOrg) weak++;
        }
        return count == 0 || org / count >= _minOrg && strength / count >= _minStrength && supply / count >= _minSupply && weak * 100 <= count * MaxWeakPercent
            && Manned(*(int*)tag); // a thin line doesn't advance: attacking widens it, the holes get the spearhead cut off
    }

    // ---- Pocket closing: the micro attack candidate check, patched at its one call site ----

    private const int ControllerOfProvince = 0x188, DivisionsInProvince = 0x11C;

    private static delegate* unmanaged<nint, nint, nint, byte> _cantMove;
    private static delegate* unmanaged<nint, int*, byte> _atWar;
    private static delegate* unmanaged<nint, int*, byte, byte, byte> _allied;
    private static delegate* unmanaged<nint, int> _fortLevel;
    private static int _steered, _pocketFailed;
    private static Logger? _log;

    private void LoadPockets()
    {
        var site = Sig.Find(Signatures.MicroAttackCandidate) + 10;
        var cantMove = Sig.Find(Signatures.CantMoveInto);
        if (site + 5 + *(int*)(site + 1) != cantMove) throw new InvalidOperationException("micro attack candidate check: the call moved");
        _cantMove = (delegate* unmanaged<nint, nint, nint, byte>)cantMove;
        _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        _allied = (delegate* unmanaged<nint, int*, byte, byte, byte>)Sig.Find(Signatures.SameOrAllied);
        _fortLevel = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FortLevel);
        var river = Sig.Find(Signatures.RiverPenalty);
        _riverSmall = river + 14 + *(int*)(river + 10); // mov rdx,[rip+disp] (7 bytes at +7)
        _riverLarge = river + 22 + *(int*)(river + 18); // cmovnz rdx,[rip+disp] (8 bytes at +14)
        _log = Log;
        if (*(ushort*)(site + 5) != 0xC084) throw new InvalidOperationException("micro attack candidate check: no `test al,al` after the call");
        // `call CantMoveInto; test al,al` (7 bytes: the hook's jump takes more than the call's 5) becomes a call of the filter
        // and the same test, for the `jnz skip` right after. rsp is 16-aligned at the call and its home space reserved.
        Patches.Warmup(ThunkOf(nameof(PocketFilter))); // its first call comes from an AI thread: compile it here first
        Patches.Asm(site, 7, "use64", $"mov rax, 0x{(nint)(delegate* unmanaged<nint, nint, nint, byte>)&PocketFilter:X}", "call rax", "test al, al");
    }

    /// <summary>
    /// Like the original check (nonzero = skip this target), but while some target of <paramref name="from"/> would cut off
    /// enemy divisions, every target that wouldn't is skipped too; the game's random pick then takes a pocket-closing one.
    /// Vanilla's own rejections stay. AI threads: read only.
    /// </summary>
    /// <summary>An UnmanagedCallersOnly thunk's metadata, for <see cref="Patches.Warmup"/>.</summary>
    private static System.Reflection.MethodInfo ThunkOf(string name) =>
        typeof(AiAssist).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    [UnmanagedCallersOnly]
    private static byte PocketFilter(nint us, nint from, nint to)
    {
        try
        {
            if (_cantMove(us, from, to) != 0) return 1;
            var tag = *(int*)us;
            var me = Game.Country(tag);
            if (me == 0 || !SheepMod.Active) return 0;
            if (_armorTerrain && BadForArmor(tag, from, to) || _roles && LineOnly(tag, from) && Divisions(to) > 0 && Pocket(me, tag, to) == 0 && HasBreakthrough(me, tag))
            {
                Interlocked.Increment(ref _steered);
                return 1;
            }
            if (!(_pocketClosing || _frontShortening || _targetValue || _axis)) return 0;
            var (pocket, shortest, valuable) = (0, int.MaxValue, 0.0);
            foreach (var id in Game.Neighbours(from))
            {
                var c = Game.Province(id);
                if (c == 0 || !Enemy(me, tag, c) || _cantMove(us, from, c) != 0 || _armorTerrain && BadForArmor(tag, from, c)) continue;
                if (Divisions(from) < Divisions(c) * (_fortLevel(c) / 2 + 1)) continue; // fails vanilla's strength test anyway
                if (_pocketClosing) pocket = Math.Max(pocket, Pocket(me, tag, c));
                if (_frontShortening) shortest = Math.Min(shortest, FrontChange(me, tag, c));
                if (_targetValue) valuable = Math.Max(valuable, TargetValue(c));
            }
            var skip = pocket > 0 ? Pocket(me, tag, to) == 0                                   // pocket targets only
                : _axis && !OnAxis(tag, to) ? true                                                   // nothing off the breakthrough axis
                : _targetValue && valuable >= MinTargetValue ? TargetValue(to) < valuable / 2         // worth at least half the best
                : _frontShortening && shortest < 0 && FrontChange(me, tag, to) > 0;              // nothing that lengthens the front
            if (skip) Interlocked.Increment(ref _steered);
            return skip ? (byte)1 : (byte)0;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _pocketFailed, 1) == 0) _log?.Error($"pocket filter: {e}");
            return _cantMove(us, from, to);
        }
    }

    /// <summary>Enemy divisions that taking <paramref name="target"/> cuts off: in enemy provinces next to it whose other land neighbours are all ours or allied.</summary>
    // ponytail: one-province pockets only; a flood fill bounded by NAI.POCKET_DISTANCE_MAX if bigger encirclements are missed.
    private static int Pocket(nint me, int tag, nint target)
    {
        var cut = 0;
        var targetId = *(int*)(target + 0xA4);
        foreach (var id in Game.Neighbours(target))
        {
            var e = Game.Province(id);
            if (e == 0 || Divisions(e) == 0 || !Enemy(me, tag, e)) continue;
            if (Game.Neighbours(e).All(n => n == targetId || Game.Province(n) is var q && (q == 0 || *(int*)(q + ControllerOfProvince) == 0 || Friendly(me, tag, q))))
                cut += Divisions(e);
        }
        return cut;
    }

    /// <summary>
    /// How the front's length changes when <paramref name="target"/> is taken: its enemy neighbours become new front, its
    /// neighbours of ours stop being front. Negative = shorter.
    /// </summary>
    private static int FrontChange(nint me, int tag, nint target)
    {
        var change = 0;
        foreach (var id in Game.Neighbours(target))
        {
            var n = Game.Province(id);
            if (n == 0 || *(int*)(n + ControllerOfProvince) == 0) continue; // sea
            if (Enemy(me, tag, n)) change++;
            else if (Friendly(me, tag, n)) change--;
        }
        return change;
    }

    private static int Divisions(nint province) => *(int*)(province + DivisionsInProvince);

    // ---- Armor and terrain: the divisions' own terrain attack (unit data: modded games too) plus the river crossing ----

    private const int UnitsInProvince = 0x110, StaticOfProvince = 0xB8, TerrainOfStatic = 0xA8, AdjacenciesOfStatic = 0x70;
    private const int IndexOfTerrain = 0x88, CategoryOfTerrain = 0x8C;
    private const int CommanderOfUnit = 0x1D8, StatsOfUnit = 0x138;
    private const int Hardness = 0xB8, Armor = 0x298, RiverAttack = 0x390, TerrainModifiers = 0x3F8, TerrainModifierSize = 0x28, AttackOfModifier = 0x10;
    private const int Mountain = 0x8, Urban = 0x10, Marsh = 0x2000, Jungle = 0x10000;
    private const int River = 2, LargeRiver = 3;
    private const long Armored = 40_000, Crippling = -25_000; // hardness 0.4; -25 % attack

    private static nint _riverSmall, _riverLarge; // the RIVER_CROSSING_PENALTY(_LARGE) defines
    private static bool _armorTerrain;

    /// <summary>
    /// Mostly armored or motorized divisions of ours in <paramref name="from"/> (hardness 0.4+ or armor), whose average
    /// attack into <paramref name="to"/>'s terrain, river crossing included, is -25 % or worse, or that would cross a river
    /// into mountains, marsh, jungle or a city.
    /// </summary>
    private static bool BadForArmor(int tag, nint from, nint to)
    {
        var terrain = *(nint*)(*(nint*)(to + StaticOfProvince) + TerrainOfStatic);
        var index = terrain == 0 ? 0 : *(int*)(terrain + IndexOfTerrain);
        if (index <= 0) return false;
        var river = Adjacency(from, *(int*)(to + 0xA4));
        var crossing = river == LargeRiver ? *(long*)_riverLarge : river == River ? *(long*)_riverSmall : 0;
        int ours = 0, mobile = 0;
        long attack = 0;
        foreach (var unit in Native.PdxArray(from + UnitsInProvince))
        {
            if (*(int*)(unit + CommanderOfUnit) != tag) continue;
            ours++;
            var stats = *(nint*)(unit + StatsOfUnit);
            if (stats == 0 || *(long*)(stats + Hardness) < Armored && *(long*)(stats + Armor) <= 0) continue;
            var modifiers = *(nint*)(stats + TerrainModifiers);
            if (modifiers == 0) continue;
            mobile++;
            attack += *(long*)(modifiers + (long)TerrainModifierSize * (index - 1) + AttackOfModifier) + (river >= River ? crossing + *(long*)(stats + RiverAttack) : 0);
        }
        if (mobile == 0 || mobile * 2 < ours) return false;
        return attack / mobile <= Crippling || river >= River && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Marsh | Jungle | Urban)) != 0;
    }

    // ---- Roles: line infantry holds, breakthrough divisions push ----

    private const int Defense = 0xA8, Breakthrough = 0xB0, UnitsOfCountry = 0x290, KindOfUnit = 8;
    private const long RolesCacheMs = 60_000;
    private static bool _roles;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (long Until, bool Has)> Breakthroughs = new();

    /// <summary>Armored, or more breakthrough than defense: made to attack.</summary>
    private static bool IsBreakthrough(nint unit)
    {
        var stats = *(nint*)(unit + StatsOfUnit);
        return stats != 0 && (*(long*)(stats + Hardness) >= Armored || *(long*)(stats + Armor) > 0 || *(long*)(stats + Breakthrough) > *(long*)(stats + Defense));
    }

    /// <summary>Our divisions in the province are all line infantry.</summary>
    private static bool LineOnly(int tag, nint province)
    {
        var any = false;
        foreach (var unit in Native.PdxArray(province + UnitsInProvince))
        {
            if (*(int*)(unit + CommanderOfUnit) != tag) continue;
            if (IsBreakthrough(unit)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>The country has breakthrough divisions at all (cached a minute): an all-infantry army still attacks with infantry.</summary>
    private static bool HasBreakthrough(nint country, int tag)
    {
        var now = Environment.TickCount64;
        if (Breakthroughs.TryGetValue(tag, out var cached) && now < cached.Until) return cached.Has;
        var has = false;
        foreach (var unit in Native.PdxArray(country + UnitsOfCountry))
            if (*(int*)(unit + KindOfUnit) == 0 && IsBreakthrough(unit)) { has = true; break; }
        Breakthroughs[tag] = (now + RolesCacheMs, has);
        return has;
    }

    // ---- Target value: victory points, capitals, supply hubs, airfields, resources, and how well it is held ----

    private const int VictoryPoints = 0x38, StateOfProvince = 0xC0, BuildingsOfProvince = 0x190;
    private const int ProvincesOfState = 0x18, IdOfState = 0x58, BestVictoryPoints = 0x85C, BuildingsOfState = 0x120, ResourcesOfState = 0x30;
    private const int ResourceEntries = 0xF8, SupplyNodeType = 0x3A0, AirBaseTypes = 0x298, CapitalOfCountry = 0x1018;
    private const int NavalBaseType = 0x400;
    private const int EntrenchOfUnit = 0x458, MaxEntrenchOfUnit = 0x460; // CArmy: dig-in now and at most (1e5)
    private const int MinTargetValue = 30;
    private const long CapitalsCacheMs = 60_000;

    private static bool _targetValue, _entrenchment;
    private static nint _buildingDb; // address of the building database global
    private static delegate* unmanaged<nint, nint, nint> _findBuilding;
    private static (long Until, HashSet<int> States) _capitals = (0, []);

    private void LoadTargetValue()
    {
        _findBuilding = (delegate* unmanaged<nint, nint, nint>)Sig.Find(Signatures.FindBuilding);
        var at = Sig.Find(Signatures.BuildingDbCall);
        var getter = at + 5 + *(int*)(at + 1);
        _buildingDb = getter + 18 + *(int*)(getter + 14);
    }

    /// <summary>
    /// What taking the enemy province is worth: its victory points, a capital, a supply hub, the state's airfields and
    /// resources (in full on the state's main province), plus empty or worn defenders; a dug-in stack counts against it.
    /// </summary>
    private static double TargetValue(nint target)
    {
        var db = *(nint*)_buildingDb;
        var state = *(nint*)(target + StateOfProvince);
        if (db == 0 || state == 0) return 0;
        var index = *(int*)(state + BestVictoryPoints);
        var main = index >= 0 && index < *(int*)(state + ProvincesOfState + 0xC) && ((nint*)*(nint*)(state + ProvincesOfState))[index] == target;

        double value = 3 * Math.Min(*(int*)(target + VictoryPoints), 30);
        if (Capitals().Contains(*(int*)(state + IdOfState))) value += main ? 60 : 15;
        var hub = _findBuilding(target + BuildingsOfProvince, *(nint*)(db + SupplyNodeType));
        if (hub != 0 && *(short*)(hub + 0x40) > 0) value += 20;

        var air = 0;
        var types = (nint*)*(nint*)(db + AirBaseTypes);
        for (int i = 0, n = *(int*)(db + AirBaseTypes + 0xC); i < n; i++)
            if (_findBuilding(state + BuildingsOfState, types[i]) is var b && b != 0) air += *(short*)(b + 0x42);
        long resources = 0;
        var definition = *(nint*)(state + ResourcesOfState);
        if (definition != 0)
            for (int i = 0, n = *(int*)(definition + ResourceEntries + 0xC); i < n; i++)
                resources += *(long*)(*(nint*)(definition + ResourceEntries) + 16 * i);
        value += (main ? 1 : 0.25) * (2 * air + Math.Min(resources / Native.FixedPoint, 40));

        // How it is held.
        var defenders = Native.PdxArray(target + UnitsInProvince);
        if (defenders.Length == 0) return value + 25;
        long org = 0, strength = 0;
        var dugIn = 0;
        foreach (var unit in defenders)
        {
            long o, s;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[OrgRatioSlot])(unit, &o);
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[StrengthRatioSlot])(unit, &s);
            org += o;
            strength += s;
            if (_entrenchment && *(long*)(unit + MaxEntrenchOfUnit) > 0 && *(long*)(unit + EntrenchOfUnit) * 100 >= 60 * *(long*)(unit + MaxEntrenchOfUnit)) dugIn++;
        }
        if (org / defenders.Length < 30_000 || strength / defenders.Length < 50_000) return value + 10;
        return value - 5 * defenders.Length * (1 + _fortLevel(target) / 2.0) - 4 * dugIn;
    }

    /// <summary>Every country's capital state id (cached a minute).</summary>
    private static HashSet<int> Capitals()
    {
        var cached = _capitals;
        if (Environment.TickCount64 < cached.Until) return cached.States;
        var states = new HashSet<int>();
        foreach (var country in Game.Countries)
            if (country != 0) states.Add(*(int*)(country + CapitalOfCountry));
        _capitals = (Environment.TickCount64 + CapitalsCacheMs, states);
        return states;
    }

    /// <summary>The adjacency type from one province to a neighbour: 0 normal, 1 strait, 2 river, 3 large river, 4 impassable.</summary>
    private static int Adjacency(nint from, int to)
    {
        var info = *(nint*)(from + StaticOfProvince);
        var records = *(nint*)(info + AdjacenciesOfStatic);
        for (int i = 0, n = *(int*)(info + AdjacenciesOfStatic + 0xC); i < n; i++)
            if (*(int*)(records + i * 0x30 + 8) == to) return *(byte*)(records + i * 0x30 + 0x10);
        return 0;
    }

    // ---- Air retreat: regions the AI is losing badly get a lower score, so its own allocator moves the planes ----

    private const int RegionEntrySize = 0x4E8, RegionOfEntry = 0x00, KindOfEntry = 0x08, ScoreOfEntry = 0x10, IgnoredRegion = 5;
    private const int SuperiorityRecords = 0xE0, SuperiorityRecordSize = 0xA0, OurSuperiority = 0x00, EnemySuperiority = 0x08;
    private const int CombatData = 0x170, OwnWings = 0x08, OtherWings = 0x20, WingDataSize = 0x98, HourOfWingData = 0x0C, DamageTaken = 0x38;
    private const int SuperiorityIdOfRegion = 0x60, CombatIdOfRegion = 0x58, HourOfGameState = 0x468;
    private const int ProvinceOfBase = 0x70, BaseEntrySize = 24, FreeOfBase = 8; // CAirBase's cached province; the module's base list entries
    private const int WindowHours = 168, MinLosses = 20;
    private const long MinShare = 35_000, PullOutShare = 15_000; // our share of the region's air superiority
    private const int LossesPerKillPercent = 150;

    [Function(CallingConventions.Microsoft)]
    public delegate void PrioritiesFn(nint aiObj, nint country, nint scratch, nint buffer, nint entries, nint bases, nint totals, nint buffer2);

    private static PrioritiesFn? _updatePriorities; // the original through its relocated prologue: the entry is an asm stub
    private static AiAssist? _self; // the loaded plugin: the thunk is static, the retreat logic is instance code
    private static int _thunkFailed;
    private int _holderOffset, _countOffset, _arrayOffset;
    private int _pulled;
    private static int _evacuated;
    private static bool _airRetreat, _airbaseEvac;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Hour, bool Threat)> BaseThreat = new(); // base province -> enemy close, for an hour

    private void LoadAirRetreat()
    {
        var at = Sig.FindAll(Signatures.StrategicAir, 2)[0];
        (_holderOffset, _countOffset, _arrayOffset) = (*(int*)(at + 3), *(byte*)(at + 14), *(byte*)(at + 36));
        _ = Sig.Find(Signatures.RegionAirData) + Sig.Find(Signatures.RegionCombatData); // the offsets below are still where they were
        // A managed detour on these 8 arguments crashed the game: 4 stack arguments make Reloaded's reverse stub enter
        // managed code misaligned (CreateArrow's and CollectTacticalTargets' crash class, dump confirmed). The entry is
        // plain assembly instead: park the stack arguments, call the thunk on an aligned stack with only register
        // arguments (the safe shape), and return to the game. The thunk runs the original through a cave — the first 38
        // bytes are position-independent, the 39th is rip-relative — then the retreat logic, as before.
        _self = this;
        var fn = Sig.Find(Signatures.UpdateRegionPriorities);
        var cave = new byte[50]; // the function's first 38 bytes, then `mov rax, fn+38; jmp rax`
        new ReadOnlySpan<byte>((void*)fn, 38).CopyTo(cave);
        cave[38] = 0x48; cave[39] = 0xB8; cave[48] = 0xFF; cave[49] = 0xE0;
        BitConverter.GetBytes(fn + 38).CopyTo(cave, 40);
        _updatePriorities = Patches.Function<PrioritiesFn>(Patches.Cave(cave));
        Patches.Warmup(ThunkOf(nameof(UpdatePriorities)));
        Patches.Asm(fn, 38,
            "use64",
            // All eight arguments parked in a frame of this thread's own stack, its address as the
            // thunk's one argument. A global parking slot races: AI passes of several countries run
            // in parallel, one thread's parked arguments overwritten before it reads them means it
            // calls the original with garbage — an access violation the runtime dies of. The parked
            // values sit above the 32 bytes the callee may use as its own scratch.
            "sub rsp, 0x78",
            "mov [rsp+0x28], rcx",
            "mov [rsp+0x30], rdx",
            "mov [rsp+0x38], r8",
            "mov [rsp+0x40], r9",
            "mov rax, [rsp+0xA0]", // entries: the 5th argument
            "mov [rsp+0x48], rax",
            "mov rax, [rsp+0xA8]", // bases: the 6th argument
            "mov [rsp+0x50], rax",
            "mov rax, [rsp+0xB0]", // totals: the 7th argument
            "mov [rsp+0x58], rax",
            "mov rax, [rsp+0xB8]", // buffer2: the 8th argument
            "mov [rsp+0x60], rax",
            "lea rcx, [rsp+0x28]",
            $"mov r10, 0x{(nint)(delegate* unmanaged<nint, void>)&UpdatePriorities:X}",
            "call r10",
            "add rsp, 0x78",
            "jmp qword [rsp]"); // to the game's return address
    }

    [UnmanagedCallersOnly]
    private static void UpdatePriorities(nint slot)
    {
        var s = (nint*)slot;
        _updatePriorities!(s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7]);
        try
        {
            if (!SheepMod.Active) return;
            _self!.AirRetreat(s[1], s[4]);
            if (_airbaseEvac) EvacuateAirbases(s[1], s[5]);
        }
        catch (Exception e) { if (Interlocked.Exchange(ref _thunkFailed, 1) == 0) CasLog.Error($"air hook: {e}"); }
    }

    /// <summary>
    /// Airbases about to be overrun stop taking wings: their free capacity goes far negative in the module's base list,
    /// so vanilla's own picker skips them and rebases the wings sitting there. Read only: AI worker threads.
    /// </summary>
    private static void EvacuateAirbases(nint country, nint bases)
    {
        var tag = *(int*)(country + 8);
        if (tag == 0 || tag == Game.ControlledTag) return;
        var hour = *(int*)(Game.State + HourOfGameState);
        var data = *(nint*)bases;
        for (int i = 0, n = *(int*)(bases + 0xC); i < n; i++)
        {
            var entry = data + BaseEntrySize * i; // {CAirBase*, free capacity, slot}
            if (*(int*)(entry + FreeOfBase) <= 0) continue; // full or already emptied
            var province = *(nint*)(*(nint*)entry + ProvinceOfBase);
            if (province == 0) continue;
            var id = *(int*)(province + 0xA4);
            if (!BaseThreat.TryGetValue(id, out var cached) || hour - cached.Hour is < 0 or >= 60) // a game hour, one pass of most AIs
                BaseThreat[id] = cached = (hour, Threatened(country, tag, province));
            if (!cached.Threat) continue;
            *(int*)(entry + FreeOfBase) = -1_000_000;
            Interlocked.Increment(ref _evacuated);
        }
    }

    /// <summary>Enemy divisions within two provinces: the base's ground is about to fall.</summary>
    private static bool Threatened(nint me, int tag, nint province)
    {
        var seen = new HashSet<int> { *(int*)(province + 0xA4) };
        var ring = new List<nint> { province };
        for (var depth = 0; depth < 2; depth++)
        {
            var next = new List<nint>();
            foreach (var p in ring)
            {
                var info = *(nint*)(p + StaticOfProvince);
                var records = *(nint*)(info + AdjacenciesOfStatic);
                for (int i = 0, n = *(int*)(info + AdjacenciesOfStatic + 0xC); i < n; i++)
                {
                    var q = Game.Province(*(int*)(records + i * 0x30 + 8));
                    if (q == 0 || *(int*)(q + ControllerOfProvince) == 0 || !seen.Add(*(int*)(q + 0xA4))) continue; // sea, or walked already
                    if (*(int*)(q + DivisionsInProvince) > 0 && Enemy(me, tag, q)) return true;
                    next.Add(q);
                }
            }
            ring = next;
        }
        return false;
    }

    /// <summary>Scales down the score of regions where we hold under 35 % of air superiority, lost 20+ planes in a week and 1.5+ per kill; under 15 %: 0.</summary>
    private void AirRetreat(nint country, nint entries)
    {
        var tag = *(int*)(country + 8);
        var holder = tag == Game.ControlledTag ? 0 : *(nint*)(Game.State + _holderOffset);
        if (holder == 0 || *(int*)(holder + _countOffset) == 0) return;
        var air = *(nint*)(*(nint*)(holder + _arrayOffset) + 8 * Game.TagIndex(tag));
        if (air == 0) return;
        var since = *(int*)(Game.State + HourOfGameState) - WindowHours;
        var records = Native.PdxArray<byte>(air + SuperiorityRecords); // only its size and data are used
        var combat = Native.PdxArray(air + CombatData);
        var data = *(nint*)entries;
        for (int i = 0, n = *(int*)(entries + 0xC); i < n; i++)
        {
            var entry = data + (nint)i * RegionEntrySize;
            var region = *(nint*)(entry + RegionOfEntry);
            var score = *(long*)(entry + ScoreOfEntry);
            if (region == 0 || score <= 0 || *(int*)(entry + KindOfEntry) == IgnoredRegion) continue;
            var id = *(int*)(region + SuperiorityIdOfRegion);
            if (id <= 0 || id >= records.Length) continue;
            var record = *(nint*)(air + SuperiorityRecords) + (nint)id * SuperiorityRecordSize;
            long ours = *(long*)(record + OurSuperiority), enemy = *(long*)(record + EnemySuperiority);
            if (enemy <= 0) continue;
            var share = ours * Native.FixedPoint / (ours + enemy);
            if (share >= MinShare) continue;
            var cid = *(int*)(region + CombatIdOfRegion);
            var stats = cid > 0 && cid < combat.Length ? combat[cid] : 0;
            if (stats == 0) continue;
            long lost = PlanesLost(stats + OwnWings, since), kills = PlanesLost(stats + OtherWings, since);
            if (lost < MinLosses || lost * 100 < kills * LossesPerKillPercent) continue;
            *(long*)(entry + ScoreOfEntry) = share < PullOutShare ? 0 : score * share / MinShare;
            Interlocked.Increment(ref _pulled);
        }
    }

    /// <summary>Planes shot down (by planes, AA or trains) in the wing records since the hour, newest first.</summary>
    private static long PlanesLost(nint wings, int since)
    {
        long planes = 0;
        var records = *(nint*)wings;
        for (var j = *(int*)(wings + 0xC) - 1; j >= 0; j--)
        {
            var record = records + (nint)j * WingDataSize;
            if (*(int*)(record + HourOfWingData) < since) break;
            var damage = *(nint*)(record + DamageTaken);
            for (int k = 0, m = *(int*)(record + DamageTaken + 0xC); k < m; k++)
                if (*(int*)(damage + k * 0x18 + 8) is 4 or 5 or 10) planes += *(long*)(damage + k * 0x18 + 0x10);
        }
        return planes / Native.FixedPoint;
    }

    private static bool Enemy(nint me, int tag, nint province)
    {
        var controller = *(int*)(province + ControllerOfProvince);
        if (controller <= 0 || controller == tag) return false; // 0: sea
        var other = stackalloc int[] { controller, Game.TagIndex(controller) };
        return _atWar(me, other) != 0;
    }

    private static bool Friendly(nint me, int tag, nint province)
    {
        var controller = *(int*)(province + ControllerOfProvince);
        if (controller == tag) return true;
        var other = stackalloc int[] { controller, Game.TagIndex(controller) };
        return _allied(me, other, 0, 0) != 0;
    }

    // ---- CAS concentration: battles on the day's breakthrough axis get the planes first ----

    private const int MissionBitsOfAirMission = 0x14, WingOfAirMission = 0x28, PoolOfWing = 0x38, CountryOfPool = 0x20;
    private const int CasMission = 2, TargetSize = 40, ProvinceOfTarget = 0x10; // STacticalTarget: inline, province id within

    [Function(CallingConventions.Microsoft)] public delegate nint TacticalFn(nint mission, nint targets, int chanceBase, int seed, byte includeDivisions, byte firstOnly);

    private static TacticalFn? _collectTactical; // the collector through its relocated prologue: the entry is an asm stub
    private static readonly Logger CasLog = new("AiAssist");
    private static int _casFailed;
    private static bool _casConcentration;
    private static int _cas;

    /// <summary>
    /// A managed hook on these 6 arguments crashed the game: two stack arguments make Reloaded's reverse stub enter
    /// managed code misaligned (CreateArrow's class, dump confirmed). The entry is plain assembly instead: park the
    /// stack arguments, call <see cref="CollectTactical"/> on an aligned stack with only register arguments (the safe
    /// shape), and return to the game with its result. The thunk runs the collector through a cave — its moved first
    /// instructions, then a jump back past the stub — and reorders the targets it filled.
    /// </summary>
    private void LoadCas()
    {
        var at = Sig.Find(Signatures.CollectTacticalTargets);
        var cave = new byte[30]; // the collector's first 18 bytes (no relative references), then `mov rax, at+18; jmp rax`
        new ReadOnlySpan<byte>((void*)at, 18).CopyTo(cave);
        cave[18] = 0x48; cave[19] = 0xB8; cave[28] = 0xFF; cave[29] = 0xE0;
        BitConverter.GetBytes(at + 18).CopyTo(cave, 20);
        _collectTactical = Patches.Function<TacticalFn>(Patches.Cave(cave));
        Patches.Warmup(ThunkOf(nameof(CollectTactical))); // its first call comes from an AI thread: compile it here first
        Patches.Asm(at, 18,
            "use64",
            // All six arguments parked in a frame of this thread's own stack (see LoadAirRetreat: a
            // global parking slot races between threads), above the callee's own 32-byte scratch.
            "sub rsp, 0x58",
            "mov [rsp+0x28], rcx",
            "mov [rsp+0x30], rdx",
            "mov [rsp+0x38], r8",
            "mov [rsp+0x40], r9",
            "mov rax, [rsp+0x80]", // includeDivisions: the 5th argument
            "mov [rsp+0x48], rax",
            "mov rax, [rsp+0x88]", // firstOnly: the 6th argument
            "mov [rsp+0x50], rax",
            "lea rcx, [rsp+0x28]",
            $"mov r10, 0x{(nint)(delegate* unmanaged<nint, nint>)&CollectTactical:X}",
            "call r10",
            "add rsp, 0x58",
            "jmp qword [rsp]"); // to the game's return address, rax = the collector's result
    }

    [UnmanagedCallersOnly]
    private static nint CollectTactical(nint slot)
    {
        var s = (nint*)slot;
        var result = _collectTactical!(s[0], s[1], (int)s[2], (int)s[3], (byte)s[4], (byte)s[5]);
        try
        {
            if (SheepMod.Active && _casConcentration && (*(int*)(s[0] + MissionBitsOfAirMission) & CasMission) != 0)
                ConcentrateCas(s[0], s[1]);
        }
        catch (Exception e) { if (Interlocked.Exchange(ref _casFailed, 1) == 0) CasLog.Error($"CAS hook: {e}"); }
        return result;
    }

    /// <summary>
    /// The plane budget is spent in the collector's array order, so battles on the axis move to the front of it (stable:
    /// between two axis battles the game's own order decides). Read only: main thread, per wing and hour.
    /// </summary>
    private static void ConcentrateCas(nint mission, nint targets)
    {
        var wing = *(nint*)(mission + WingOfAirMission);
        var pool = wing == 0 ? 0 : *(nint*)(wing + PoolOfWing);
        var country = pool == 0 ? 0 : *(nint*)(pool + CountryOfPool);
        if (country == 0) return;
        var tag = *(int*)(country + 8);
        if (tag == 0 || tag == Game.ControlledTag) return;
        var hour = *(int*)(Game.State + HourOfGameState);
        if (!Axes.TryGetValue(tag, out var axis) || hour - axis.Hour is < 0 or >= 2 * AxisRetryHours) return; // stale: no axis today
        var data = (byte*)*(nint*)targets;
        int n = *(int*)(targets + 0xC);
        if (data == null || n < 2) return;
        var onAxis = new bool[n];
        var count = 0;
        for (var i = 0; i < n; i++)
            if (axis.Core.Contains(*(int*)(data + (nint)i * TargetSize + ProvinceOfTarget))) { onAxis[i] = true; count++; }
        if (count == 0 || count == n) return;
        var copy = new byte[(nint)n * TargetSize];
        fixed (byte* dest = copy)
        {
            var k = 0;
            for (var i = 0; i < n; i++) if (onAxis[i]) Buffer.MemoryCopy(data + (nint)i * TargetSize, dest + (nint)k++ * TargetSize, TargetSize, TargetSize);
            for (var i = 0; i < n; i++) if (!onAxis[i]) Buffer.MemoryCopy(data + (nint)i * TargetSize, dest + (nint)k++ * TargetSize, TargetSize, TargetSize);
            Buffer.MemoryCopy(dest, data, copy.Length, copy.Length);
        }
        Interlocked.Increment(ref _cas);
    }

    // ---- Leaders: the most important fronts choose first; infantry, doctrine and status traits count ----

    private const int FrontsOfGeneral = 0x48, HoldingFront = 0x104, TraitsOfLeader = 0xDC8, TypeOfLeader = 0xE7C, CombatsOfUnit = 0x1B4;

    [Function(CallingConventions.Microsoft)] public delegate void GeneralFn(nint general);
    [Function(CallingConventions.Microsoft)] public delegate int ScoreLeaderFn(nint leader, nint group, nint operation, nint front, byte defensive);

    private IHook<GeneralFn>? _updateLeaders;
    private IHook<ScoreLeaderFn>? _scoreLeader;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, string> TraitNames = new();

    private void LoadLeaders()
    {
        _updateLeaders = Patches.Hook<GeneralFn>(Sig.Find(Signatures.UpdateLeaders), general =>
        {
            var fronts = general + FrontsOfGeneral;
            nint[]? saved = null;
            Safely(() =>
            {
                if (!SheepMod.Active) return;
                var data = (nint*)*(nint*)fronts;
                var order = Native.PdxArray(fronts).ToArray();
                saved = order;
                var ranked = order.OrderByDescending(Importance).ToArray(); // stable: ties keep the game's order
                for (var i = 0; i < ranked.Length; i++) data[i] = ranked[i];
            });
            try { _updateLeaders!.OriginalFunction(general); }
            finally
            {
                // The game's order back (it only reads the list here; if it changed it anyway, leave it as it is).
                if (saved != null && *(int*)(fronts + 0xC) == saved.Length)
                    for (var i = 0; i < saved.Length; i++) ((nint*)*(nint*)fronts)[i] = saved[i];
            }
        });
        _scoreLeader = Patches.Hook<ScoreLeaderFn>(Sig.Find(Signatures.ScoreLeader), (leader, group, operation, front, defensive) =>
        {
            var score = _scoreLeader!.OriginalFunction(leader, group, operation, front, defensive);
            if (score <= 0 || front == 0 || !SheepMod.Active) return score;
            var fit = 1.0;
            Safely(() => fit = Fit(leader, front, defensive != 0));
            return (int)Math.Min(int.MaxValue, score * Math.Max(fit, 0.5));
        });
    }

    /// <summary>How much a front matters: its divisions, more when they fight, more when it attacks.</summary>
    private static double Importance(nint front)
    {
        var divisions = Native.PdxArray(front);
        if (divisions.Length == 0) return 0;
        var fighting = 0;
        foreach (var unit in divisions)
            if (*(int*)(unit + CombatsOfUnit) > 0) fighting++;
        return divisions.Length * (1 + 2.0 * fighting / divisions.Length) * (*(int*)(front + HoldingFront) != 0 ? 1 : 1.5);
    }

    /// <summary>Trait fit the game's score leaves out: infantry traits by the front's infantry share, field marshal doctrine by its posture, bad status.</summary>
    private static double Fit(nint leader, nint front, bool holding)
    {
        var divisions = Native.PdxArray(front);
        var armored = 0;
        foreach (var unit in divisions)
            if (*(nint*)(unit + StatsOfUnit) is var stats && stats != 0 && (*(long*)(stats + Hardness) >= Armored || *(long*)(stats + Armor) > 0)) armored++;
        var infantry = divisions.Length == 0 ? 0 : 1 - (double)armored / divisions.Length;
        var marshal = *(int*)(leader + TypeOfLeader) == 0;
        var fit = 1.0;
        foreach (var trait in Native.PdxArray(leader + TraitsOfLeader))
        {
            var name = TraitNames.GetOrAdd(trait, t => Native.Utf8(Native.MsvcString(t + 0x10)));
            fit += name switch
            {
                "infantry_leader" or "infantry_expert" or "ambusher" => 0.25 * infantry,
                "offensive_doctrine" or "aggressive_assaulter" or "fast_planner" when marshal => holding ? -0.05 : 0.2,
                "defensive_doctrine" or "unyielding_defender" or "thorough_planner" when marshal => holding ? 0.2 : -0.05,
                "sick" or "wounded" or "disgruntled" or "substance_abuser" or "substance_addict" => -0.15,
                _ => 0,
            };
        }
        return fit;
    }

    // ---- Navy: convoys under attack get the fuel budget; task forces pick their fights by the strength they see ----

    private const int HandlesOfNavy = 0x50;                                            // naval AI: task force handles, 32 bytes each (+0xC count)
    private const int StationOfForce = 0x1F0, RegionOfStation = 0xC8;                  // CTaskForce -> station -> CNavalRegion
    private const int OwnerOfForce = 0x1D8, MissionOfForce = 0x374, AggressionOfForce = 0x394;
    private const int ShipsOfForce = 0x348, SpottedOfRegion = 0xF8, StrengthOfShip = 0x6F8; // CPdxArrays; CShip strength (1e5)
    private const int ConvoyThreats = 0x698, CountriesOfThreats = 8, TagOfThreats = 0x10, ThreatsOfCountry = 0x80; // gamestate -> per country
    private const int ConvoyDefenseFuel = 0x337538, ThreatPerSunkConvoy = 0x337F70;    // NAI defines, from the image base
    private const int Patrol = 1, Strike = 2, Raiding = 3;                             // EMissionType

    [Function(CallingConventions.Microsoft)] public delegate void NavyFn(nint navalAI);
    [Function(CallingConventions.Microsoft)] public delegate nint EngagementFn(nint cmd, nint force, int level);

    private static bool _navalSortie, _navalEscort;
    private IHook<NavyFn>? _navyAllocator;
    private IHook<EngagementFn>? _engagement;
    private nint _convoyRatio, _threatPerConvoy;
    private long _convoyBaseline;
    private int _convoyBumps, _sortied, _escorted;

    private void LoadNavy()
    {
        _resolve = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.ResolveRef);
        _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        var image = Sig.Find(Signatures.NavalAllocator) - 0x1A65AB0; // the allocator's own address gives the image base
        _convoyRatio = image + ConvoyDefenseFuel;
        _threatPerConvoy = image + ThreatPerSunkConvoy;
        _navyAllocator = Patches.Hook<NavyFn>(Sig.Find(Signatures.NavalAllocator), navalAI =>
        {
            var boosted = false;
            Safely(() => boosted = SheepMod.Active && _navalEscort && ConvoyPressure(navalAI));
            if (boosted && Interlocked.Increment(ref _convoyBumps) == 1)
                *(long*)_convoyRatio = ConvoyBaseline() * 2; // another country's worker may read the doubled ratio: once, too many escorts, never too few
            try { _navyAllocator!.OriginalFunction(navalAI); }
            finally
            {
                if (boosted && Interlocked.Decrement(ref _convoyBumps) == 0 && _convoyBaseline != 0)
                    *(long*)_convoyRatio = _convoyBaseline;
            }
        });
        _engagement = Patches.Hook<EngagementFn>(Sig.Find(Signatures.NavyEngagement), (cmd, force, level) =>
        {
            Safely(() =>
            {
                if (SheepMod.Active && _navalSortie) level = Engagement(force, level);
            });
            return _engagement!.OriginalFunction(cmd, force, level);
        });
    }

    /// <summary>The NAI define as it was loaded, captured on the first bump; every restore writes exactly this back.</summary>
    private long ConvoyBaseline()
    {
        var baseline = _convoyBaseline;
        if (baseline == 0) _convoyBaseline = baseline = *(long*)_convoyRatio;
        return baseline;
    }

    /// <summary>Whether the country is losing convoys fast: its worst naval region's threat, against the escort proposer's own gates. Read only: AI threads.</summary>
    private bool ConvoyPressure(nint navalAI)
    {
        var tag = NavyTag(navalAI);
        if (tag == 0 || tag == Game.ControlledTag) return false;
        var container = *(nint*)(Game.State + ConvoyThreats);
        var countries = container == 0 ? 0 : *(nint*)(container + CountriesOfThreats);
        if (countries == 0) return false;
        var data = *(nint*)(countries + 8 * Game.TagIndex(tag));
        if (data == 0 || *(int*)(data + TagOfThreats) != tag) return false;
        var threats = Native.PdxArray<int>(data + ThreatsOfCountry);
        var worst = 0;
        foreach (var threat in threats) worst = Math.Max(worst, threat);
        // The proposer only runs above 50, and calls a region under 4 sunks' worth of threat fine: below either, more escorts change nothing.
        if (worst <= 50 || worst < 4L * *(int*)_threatPerConvoy) return false;
        Interlocked.Increment(ref _escorted);
        return true;
    }

    /// <summary>The country whose naval AI this is, read off its first resolvable task force handle.</summary>
    private int NavyTag(nint navalAI)
    {
        var handles = *(nint*)(navalAI + HandlesOfNavy);
        for (int i = 0, n = *(int*)(navalAI + HandlesOfNavy + 0xC); i < n; i++)
        {
            var handle = handles + 32 * i;
            if (*(int*)handle == 0 && *(int*)(handle + 4) == 0) continue;
            var resolved = _resolve(handle);
            if (resolved == 0 || resolved == 16) continue;
            var tag = *(int*)(resolved - 16 + OwnerOfForce);
            if (tag is >= 0x414141 and <= 0x5A5A5A) return tag; // three letters, like "GER"
        }
        return 0;
    }

    /// <summary>
    /// The engagement level (0 never .. 4 always) a task force should have, by what it and the enemy it can see in its
    /// region are worth: twice the enemy it presses, half it slips away, a pair of ships only shadows. Read only: AI threads.
    /// </summary>
    private int Engagement(nint force, int level)
    {
        var tag = *(int*)(force + OwnerOfForce);
        if (tag == 0 || tag == Game.ControlledTag) return level; // the player's own button
        if (*(int*)(force + MissionOfForce) is not (Patrol or Strike or Raiding)) return level;
        var ships = Native.PdxArray<nint>(force + ShipsOfForce);
        if (ships.Length <= 2) return 0; // a pair on patrol or raiding shadows the enemy, it doesn't fight it
        long ours = 0;
        foreach (var ship in ships) ours += *(long*)(ship + StrengthOfShip);
        var station = *(nint*)(force + StationOfForce);
        var region = station == 0 ? 0 : *(nint*)(station + RegionOfStation);
        if (region == 0 || ours <= 0) return level;
        var me = Game.Country(tag);
        if (me == 0) return level;
        long theirs = 0;
        var pair = stackalloc int[2];
        foreach (var other in Native.PdxArray<nint>(region + SpottedOfRegion))
        {
            var owner = *(int*)(other + OwnerOfForce);
            if (owner == 0 || owner == tag) continue;
            pair[0] = owner;
            pair[1] = Game.TagIndex(owner);
            if (_atWar(me, pair) == 0) continue;
            foreach (var ship in Native.PdxArray<nint>(other + ShipsOfForce)) theirs += *(long*)(ship + StrengthOfShip);
        }
        var vanilla = level;
        if (theirs > 0 && ours >= theirs + theirs / 5) level = 4; // 1.2x over what we see: press it
        else if (ours * 2 <= theirs) level = 1;                  // half of it: slip away
        if (level != vanilla) Interlocked.Increment(ref _sortied);
        return level;
    }

    // ---- Concentration: quiet and weak-enemy fronts ask for fewer divisions, fronts facing a strong enemy for more ----

    private const int MinWanted = 0x90, DesiredWanted = 0x94, MaxWanted = 0x98, FixedRequest = 0x9C, CountryOfFront = 0xE0, FrontOfAiFront = 0xE8;
    private const int SectionOfAiFront = 0xF0, AreaDefense = 0x104, InvasionTarget = 0x120, AttackingFront = 0x218 + 0x84, OtherRequest = 0x1DC;
    private const int FactionMemberOfCountry = 0xF88, FactionOfMember = 0x290; // CCountry -> member status -> its CFaction (CIsInFactionWith compares the pointers)
    private const int SectionsOfFront = 0x58, IdOfSection = 8, PairsOfSection = 0x48, CombatsInProvince = 0x17C; // province: combats count
    private const double QuietFactor = 0.25, HotBoost = 1.25;

    [Function(CallingConventions.Microsoft)] public delegate nint GeneralRetFn(nint general);

    private IHook<GeneralRetFn>? _unitRequests;
    private int _concentrated;
    private static int _stationed;
    private static bool _concentration, _holdLine, _defensePriority, _axis, _reserve, _beachDefense, _stationing;
    private const int FactorHours = 24, CalmHours = 168;
    private const int AxisRetryHours = 24, ThinSectorWeight = 8;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, double Factor)> Factors = new(); // AI front -> its factor, for a day
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> LastBattle = new();                 // AI front -> hour of its last battle
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Hour, HashSet<int> Core)> Axes = new(); // tag -> its breakthrough axis, for a day

    private void LoadConcentration()
    {
        _resolve = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.ResolveRef);
        _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        _allied = (delegate* unmanaged<nint, int*, byte, byte, byte>)Sig.Find(Signatures.SameOrAllied);
        _unitRequests = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateFrontUnitRequests), general =>
        {
            var result = _unitRequests!.OriginalFunction(general);
            Safely(() =>
            {
                if (SheepMod.Active) Concentrate(general);
            });
            return result;
        });
        if (!_holdLine && !_reserve && !_stationing) return;
        // The regrouping drains fronts through here (unassign, then one mass move a turn), which no front request can block:
        // the last defender of a province, and the wall around a pocket, are kept out of its lists instead. Vanilla would
        // reassign them and staff the new front with divisions from far away, while the replacements walk the pocket breaks out.
        _collect = Patches.Hook<CollectFn>(Sig.Find(Signatures.CollectReassignableUnits), (context, list) =>
        {
            var result = _collect!.OriginalFunction(context, list);
            Safely(() =>
            {
                if (!SheepMod.Active) return;
                var data = *(nint*)list;
                int n = *(int*)(list + 0xC), kept = 0;
                for (var i = 0; i < n; i++)
                {
                    var entry = data + 16 * i; // {CArmy*, front slot*}
                    var unit = *(nint*)entry;
                    if (Pinned(unit) || _stationing && FreshlyArrived(unit)) continue;
                    if (kept != i) Buffer.MemoryCopy((void*)entry, (void*)(data + 16 * kept), 16, 16);
                    kept++;
                }
                *(int*)(list + 0xC) = kept;
            });
            return result;
        });
        if (!_holdLine && !_reserve) return;
        // FrontLine owns the only ApplyArmiesBalancing hook and applies this through it: a second hook stacks detours, which crashes Reloaded.
        Patches.KeepFromBalancing += unit => SheepMod.Active && Pinned(unit);
    }

    /// <summary>Rescales each land front's requests by the enemy actually facing it, before the theatre's divisions are shared out.</summary>
    private void Concentrate(nint general)
    {
        var other = stackalloc int[2]; // {tag, tag index} of a province's controller
        var spots = new Dictionary<int, (nint Province, int Weight)>(); // tag -> the thinnest enemy sector on its line, for the axis
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            var country = *(nint*)(front + CountryOfFront);
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag == 0 || tag == Game.ControlledTag) continue;
            var reference = (int*)(front + FrontOfAiFront);
            if (reference[0] == 0 && reference[1] == 0) continue;
            var frontLine = _resolve((nint)reference);
            if (frontLine == 0) continue;
            nint section = 0;
            foreach (var s in Native.PdxArray(frontLine + SectionsOfFront))
                if (*(int*)(s + IdOfSection) == *(int*)(front + SectionOfAiFront)) { section = s; break; }
            if (section == 0) continue;

            // Factions of the countries at war with us: a member's border may go hot any day, so it keeps its garrison.
            var warFactions = new HashSet<nint>();
            foreach (var c in Game.Countries)
            {
                var t = c == 0 ? 0 : *(int*)(c + 8);
                if (t <= 0 || t == tag) continue;
                (other[0], other[1]) = (t, Game.TagIndex(t));
                if (_atWar(country, other) != 0) warFactions.Add(FactionOf(c));
            }
            // Both sides of the section: enemy provinces (held by a country at war with us) and their divisions; our provinces
            // facing enemy divisions (each needs a defender), and whether any of ours is fighting.
            var enemyHeld = new Dictionary<nint, bool>();
            var contact = new HashSet<nint>();    // ours facing any enemy-held province
            var threatened = new HashSet<nint>(); // ours facing enemy divisions: a hole there lets them through
            var fighting = false;
            var aligned = false;                  // across sits a country of a warring country's faction (not at war with us itself)
            var enemy = 0;
            var pairs = *(nint*)(section + PairsOfSection);
            for (int i = 0, n = *(int*)(section + PairsOfSection + 0xC); i < n; i++)
            {
                nint ours = *(nint*)(pairs + 16 * i), theirs = *(nint*)(pairs + 16 * i + 8);
                if (ours != 0 && *(int*)(ours + CombatsInProvince) > 0) fighting = true;
                if (theirs == 0) continue;
                if (!enemyHeld.TryGetValue(theirs, out var held))
                {
                    var controller = *(int*)(theirs + ControllerOfProvince);
                    (other[0], other[1]) = (controller, controller > 0 ? Game.TagIndex(controller) : 0);
                    held = enemyHeld[theirs] = controller > 0 && controller != tag && _atWar(country, other) != 0;
                    if (held) enemy += *(int*)(theirs + DivisionsInProvince);
                    else if (controller > 0 && controller != tag && warFactions.Contains(FactionOf(Game.Country(controller)))) aligned = true;
                }
                if (held && ours != 0)
                {
                    contact.Add(ours);
                    if (*(int*)(theirs + DivisionsInProvince) > 0) threatened.Add(ours);
                }
            }
            var hostile = enemyHeld.ContainsValue(true);
            var hour = *(int*)(Game.State + HourOfGameState);
            if (threatened.Count > 0) _line[front] = (hour, tag, threatened); // the attack gate and the backfill read this
            if (_axis && hostile) // the day's breakthrough axis: the enemy province on this line that is easiest to take
                foreach (var our in threatened)
                foreach (var id in Game.Neighbours(our))
                {
                    var p = Game.Province(id);
                    if (p == 0 || !Enemy(country, tag, p)) continue;
                    var weight = SpotWeight(p, our);
                    if (!spots.TryGetValue(tag, out var s) || weight < s.Weight) spots[tag] = (p, weight);
                }
            if (aligned || *(int*)(front + AreaDefense) != 0 || *(nint*)(front + InvasionTarget) != 0) continue; // a warring country's faction border, garrisons and invasions: as the game asks
            if (hostile && (*(byte*)(front + FixedRequest) != 0 || *(byte*)(front + OtherRequest) != 0)) continue; // fixed shares on a front that is at war; quiet ones are thinned
            var enemyProvinces = enemyHeld.Count(e => e.Value);
            int min = *(int*)(front + MinWanted), desired = *(int*)(front + DesiredWanted), max = *(int*)(front + MaxWanted);
            // Requests are redone every hour; a factor that followed every enemy move would march divisions back and forth.
            // So a factor holds for a day, and a front gives divisions away only a week after its last battle.
            if (fighting) LastBattle[front] = hour;
            var factor = 1.0;
            if (_concentration)
            {
                if (Factors.TryGetValue(front, out var kept) && hour - kept.Hour is >= 0 and < FactorHours) factor = kept.Factor; // (negative: another save)
                else Factors[front] = (hour, factor = !hostile ? QuietFactor : enemy > min ? HotBoost : 1);
                if (fighting || LastBattle.TryGetValue(front, out var battle) && hour - battle is >= 0 and < CalmHours) factor = Math.Max(factor, 1);
            }
            // Hold the line: at war, every province of ours on the line keeps a defender, whatever else (a supply
            // crisis, another front) the game weighs; it would ship them off and leave the line empty while fighting.
            var floor = _holdLine && hostile ? contact.Count : 0;
            if (factor == 1 && min >= floor) continue;
            min = (int)Math.Ceiling(min * factor);
            desired = (int)Math.Ceiling(desired * factor);
            if (factor < 1) max = (int)Math.Ceiling(max * factor); // the rest goes to the fronts that need it
            min = Math.Max(min, Math.Max(floor, enemyProvinces > 0 && factor != 1 ? 1 : 0));
            desired = Math.Max(desired, min);
            max = Math.Max(max, desired);
            (*(int*)(front + MinWanted), *(int*)(front + DesiredWanted), *(int*)(front + MaxWanted)) = (min, desired, max);
            Interlocked.Increment(ref _concentrated);
        }
        var now = *(int*)(Game.State + HourOfGameState);
        foreach (var (tag, (province, weight)) in spots)
        {
            if (weight > ThinSectorWeight) continue;                                  // no thin sector anywhere: no axis today
            if (Axes.TryGetValue(tag, out var axis) && now - axis.Hour is >= 0 and < AxisRetryHours) continue; // held a day: divisions don't zig-zag
            Axes[tag] = (now, Rings(*(int*)(province + 0xA4)));
        }
    }

    /// <summary>How hard the enemy province is to take from ours: its divisions and forts, more for harsh terrain or a river in front of it.</summary>
    private static int SpotWeight(nint target, nint from)
    {
        var weight = 2 * Divisions(target) + _fortLevel(target);
        var terrain = *(nint*)(*(nint*)(target + StaticOfProvince) + TerrainOfStatic);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban | Marsh | Jungle)) != 0) weight += 3;
        if (Adjacency(from, *(int*)(target + 0xA4)) >= River) weight += 2;
        return weight;
    }

    /// <summary>The breakthrough axis: its target province and its ring; local attacks stay on these.</summary>
    private static HashSet<int> Rings(int target)
    {
        var core = new HashSet<int> { target };
        var ring = new List<int> { target };
        for (var depth = 0; depth < 2; depth++) // the target and its neighbours
        {
            var next = new List<int>();
            foreach (var id in ring)
            foreach (var n in Game.Neighbours(Game.Province(id)))
                if (core.Add(n)) next.Add(n);
            ring = next;
        }
        return core;
    }

    /// <summary>Whether a local attack target is on the tag's breakthrough axis (always true when there is none).</summary>
    private static bool OnAxis(int tag, nint to)
    {
        if (!Axes.TryGetValue(tag, out var axis)) return true;
        if (*(int*)(Game.State + HourOfGameState) - axis.Hour >= AxisRetryHours) return true; // stale: the next Concentrate replaces it
        return axis.Core.Contains(*(int*)(to + 0xA4));
    }

    // ---- Hold the line: pinned defenders, holes refilled, attacks wait on a thinly manned line ----

    private const int LocationOfUnit = 0x1F0, OwnerOfUnit = 0x1E0, Path = 0x200, Retreating = 0x24C, Busy = 0x2F8;
    private const int ArrivalOfUnit = 0x160, FreshHours = 168;
    private const int LineFreshHours = 48, MaxBackfills = 2, BackfillRadius = 10, MinBackfillOrg = 60;
    private const long SentMs = 120_000, GapMs = 600_000; // a helper walks without new orders; a hole isn't refilled at once
    private const int KeyBackfillRadius = 20, KeyHole = 60;
    private const long KeyGapMs = 120_000;
    private const int ReserveMax = 6, ReservePerLine = 4, ReserveMoves = 2, ReserveRadius = 6;
    private const int BeachheadMax = 8, BeachheadRadius = 6, MaxLandingSends = 6;
    private const long LandingMs = 300_000, LandingScanMs = 30_000;

    [Function(CallingConventions.Microsoft)] public delegate byte CollectFn(nint context, nint list);
    private IHook<CollectFn>? _collect;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, int Tag, HashSet<nint> Provinces)> _line = new(); // AI front -> its threatened provinces, refreshed hourly
    private readonly Dictionary<nint, long> _sent = []; // unit -> until: a division sent at a hole is left alone while it walks
    private readonly Dictionary<int, long> _gap = [];   // province id -> until: a hole a helper is walking to
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, byte> _posted = new(); // units standing on reserve posts: the theatre shuffle leaves them
    private List<HashSet<int>> _beachheads = [];
    private long _beachScanned;
    private static int _provinceCount = -1; // probe result: the game has no province enumeration
    private PerGame _game = null!;
    private delegate* unmanaged<nint, nint, int, byte, int, byte, byte, byte, byte, byte, nint> _move;
    private int _backfilled, _reserved, _defended;

    /// <summary>
    /// A division that took up its post only days ago: the theatre shuffle would march it somewhere else and back
    /// (ping-pong). While it walks, its arrival field is a schedule, not a stamp; a stale schedule reads as long ago.
    /// </summary>
    private static bool FreshlyArrived(nint unit)
    {
        if (*(int*)(unit + Path + 0xC) != 0) return false;
        var age = *(int*)(Game.State + HourOfGameState) - *(int*)(unit + ArrivalOfUnit);
        if (age is < 0 or >= FreshHours) return false;
        Interlocked.Increment(ref _stationed);
        return true;
    }

    /// <summary>The AI's regrouping can't take this division: it is the last one of its country in a province enemy divisions
    /// can walk into, or it stands on the wall of a pocket (an enemy province with divisions whose neighbours are all ours).</summary>
    private static bool Pinned(nint unit)
    {
        if (unit == 0) return false;
        var tag = *(int*)(unit + CommanderOfUnit);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        if (_posted.ContainsKey(unit)) return true; // standing on a reserve post: the theatre shuffle must leave it
        var country = Game.Country(tag);
        var location = *(nint*)(unit + LocationOfUnit);
        if (country == 0 || location == 0) return false;
        var alone = true;
        foreach (var other in Native.PdxArray(location + UnitsInProvince))
            if (other != unit && *(int*)(other + CommanderOfUnit) == tag) alone = false;
        foreach (var id in Game.Neighbours(location))
        {
            var p = Game.Province(id);
            if (p == 0 || *(int*)(p + DivisionsInProvince) == 0 || !Enemy(country, tag, p)) continue;
            if (alone || Game.Neighbours(p).All(n => Game.Province(n) is var q && (q == 0 || *(int*)(q + ControllerOfProvince) == 0 || !Enemy(country, tag, q))))
                return true; // nothing else holds the province, or a cut-off pocket sits against it
        }
        return false;
    }

    /// <summary>The country's tracked threatened line (fresh for two days), or null.</summary>
    private HashSet<nint>? LineOf(int tag, int hour)
    {
        HashSet<nint>? line = null;
        foreach (var e in _line)
            if (e.Value.Tag == tag && hour - e.Value.Hour is >= 0 and < LineFreshHours)
                (line ??= []).UnionWith(e.Value.Provinces);
        return line;
    }

    /// <summary>
    /// The country may start attacks: its divisions standing on the threatened line outnumber the line's provinces by a
    /// margin, or no line is tracked. Attacking on a barely manned line widens it, the holes open, and the spearhead gets
    /// cut off. Attacks already running are never touched.
    /// </summary>
    private bool Manned(int tag)
    {
        var line = LineOf(tag, *(int*)(Game.State + HourOfGameState));
        if (line == null || line.Count == 0) return true;
        var country = Game.Country(tag);
        var divisions = 0;
        foreach (var province in line)
            divisions += Standing(country, tag, province);
        return line.Count * 6 <= divisions * 5; // ~1.2 divisions per threatened province
    }

    /// <summary>Divisions of the tag or its allies standing in the province.</summary>
    private static int Standing(nint country, int tag, nint province)
    {
        var divisions = 0;
        var other = stackalloc int[2]; // {tag, tag index} of a division's commander
        foreach (var unit in Native.PdxArray(province + UnitsInProvince))
        {
            var commander = *(int*)(unit + CommanderOfUnit);
            if (commander == tag) { divisions++; continue; }
            (other[0], other[1]) = (commander, Game.TagIndex(commander));
            if (commander > 0 && _allied(country, other, 0, 0) != 0) divisions++;
        }
        return divisions;
    }

    /// <summary>
    /// Free divisions walk back into empty provinces of the threatened line, the ones worth holding first; a country with divisions to
    /// spare also keeps reserve posts a province behind its line.
    /// </summary>
    private void Backfill()
    {
        _game.Check();
        if (!SheepMod.Active || Game.State == 0) return; // 0 while the world is still loading: Game.Countries guards it too
        var now = Environment.TickCount64;
        foreach (var (unit, until) in _sent.ToArray()) if (now >= until) _sent.Remove(unit);
        foreach (var (province, until) in _gap.ToArray()) if (now >= until) _gap.Remove(province);
        var hour = *(int*)(Game.State + HourOfGameState);
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            var line = LineOf(tag, hour);
            if (line == null)
            {
                if (_reserve) Unpin(tag); // its front is gone: its reserve stops being one
                continue;
            }
            var sent = 0;
            foreach (var province in line.Where(p => !_gap.ContainsKey(*(int*)(p + 0xA4)) && Standing(country, tag, p) == 0)
                                         .OrderByDescending(p => _defensePriority ? Key(country, tag, p) : 0))
            {
                if (sent == MaxBackfills) break;
                var id = *(int*)(province + 0xA4);
                var key = _defensePriority && Key(country, tag, province) >= KeyHole; // a port, a city on a river: worth walking further for
                var helper = Helper(tag, province, line, key ? KeyBackfillRadius : BackfillRadius);
                if (helper == 0) continue; // nothing free nearby: the attack gate keeps the front still instead
                Move(helper, id);
                _sent[helper] = now + SentMs;
                _gap[id] = now + (key ? KeyGapMs : GapMs);
                sent++;
                Interlocked.Increment(ref _backfilled);
            }
            if (_reserve && Manned(tag)) Reserve(country, tag, line, now);
        }
    }

    /// <summary>
    /// A country with divisions to spare posts reserves a province behind its line: the posts covering the most line provinces
    /// (key ground first) each get a division, which stays until the front moves.
    /// </summary>
    private void Reserve(nint country, int tag, HashSet<nint> line, long now)
    {
        if (line.Count < ReservePerLine) return; // a short line is its own reserve
        var posts = new Dictionary<nint, int>();
        foreach (var p in line)
        foreach (var id in Game.Neighbours(p))
        {
            var q = Game.Province(id);
            if (q == 0 || posts.ContainsKey(q) || line.Contains(q) || *(int*)(q + ControllerOfProvince) != tag) continue;
            if (Game.Neighbours(q).Any(n => Game.Province(n) is var r && r != 0 && Enemy(country, tag, r))) continue; // on the front itself
            posts[q] = 0;
        }
        foreach (var post in posts.Keys.ToList())
            posts[post] = Game.Neighbours(post).Count(n => Game.Province(n) is var q && q != 0 && line.Contains(q));
        var want = Math.Min(ReserveMax, line.Count / ReservePerLine);
        var sent = 0;
        foreach (var (post, covers) in posts.OrderByDescending(p => p.Value).ThenBy(p => Key(country, tag, p.Key)).Take(want))
        {
            if (sent == ReserveMoves || covers == 0) break;
            var id = *(int*)(post + 0xA4);
            if (Standing(country, tag, post) > 0 || _gap.ContainsKey(id)) continue;
            var helper = Helper(tag, post, line, ReserveRadius);
            if (helper == 0) continue;
            Move(helper, id);
            _posted[helper] = 1;
            _sent[helper] = now + SentMs;
            _gap[id] = now + GapMs;
            sent++;
            Interlocked.Increment(ref _reserved);
        }
    }

    /// <summary>The tag's tracked line is gone: its reserve divisions stop being reserves.</summary>
    private static void Unpin(int tag)
    {
        foreach (var unit in _posted.Where(p => *(int*)(p.Key + CommanderOfUnit) == tag).Select(p => p.Key).ToList()) _posted.TryRemove(unit, out _);
    }

    /// <summary>What holding this province of ours is worth: a port (the fleet and supply), victory points, a river in front of it, city or mountain ground.</summary>
    private static int Key(nint country, int tag, nint province)
    {
        var db = *(nint*)_buildingDb;
        var type = db == 0 ? 0 : *(nint*)(db + NavalBaseType);
        var port = type == 0 ? 0 : _findBuilding(province + BuildingsOfProvince, type);
        if (port != 0 && *(short*)(port + 0x40) > 0) return 1000 + *(short*)(port + 0x40);
        var key = 3 * Math.Min(*(int*)(province + VictoryPoints), 30);
        var terrain = *(nint*)(*(nint*)(province + StaticOfProvince) + TerrainOfStatic);
        if (terrain != 0 && (*(int*)(terrain + CategoryOfTerrain) & (Mountain | Urban)) != 0) key += 25;
        foreach (var id in Game.Neighbours(province))
        {
            var p = Game.Province(id);
            if (p == 0 || !Enemy(country, tag, p) || Adjacency(province, id) < River) continue;
            key += 40; // the enemy has to cross a river to take it
            break;
        }
        return key;
    }

    /// <summary>The nearest free, rested division of the tag within radius provinces of ours, not standing on the line itself, or 0.</summary>
    private nint Helper(int tag, nint target, HashSet<nint> line, int radius = BackfillRadius)
    {
        var seen = new HashSet<int> { *(int*)(target + 0xA4) };
        var frontier = new List<nint> { target };
        for (var depth = 1; depth <= radius && frontier.Count > 0; depth++)
        {
            var next = new List<nint>();
            foreach (var province in frontier)
            foreach (var id in Game.Neighbours(province))
            {
                if (!seen.Add(id)) continue;
                var p = Game.Province(id);
                if (p == 0 || *(int*)(p + ControllerOfProvince) != tag) continue;
                if (!line.Contains(p))
                    foreach (var unit in Native.PdxArray(p + UnitsInProvince))
                        if (Spare(unit, tag)) return unit;
                next.Add(p);
            }
            frontier = next;
        }
        return 0;
    }

    /// <summary>A land division of the tag that can move now: rested, idle, and not one we sent already.</summary>
    private bool Spare(nint unit, int tag)
    {
        if (*(int*)(unit + KindOfUnit) != 0 || *(int*)(unit + CommanderOfUnit) != tag || *(int*)(unit + OwnerOfUnit) != tag) return false;
        if (*(int*)(unit + CombatsOfUnit) != 0 || *(byte*)(unit + Retreating) != 0 || *(nint*)(unit + Busy) != 0) return false;
        if (*(int*)(unit + Path + 0xC) != 0 || _sent.ContainsKey(unit)) return false;
        long org;
        ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[OrgRatioSlot])(unit, &org);
        return org >= (long)MinBackfillOrg * Native.FixedPoint / 100;
    }

    /// <summary>A plain move of a free division into an empty line province, posted the way the AI posts its own moves.</summary>
    private void Move(nint unit, int province)
    {
        var cmd = Allocator.AllocZeroed(0x88);
        _move(cmd, unit, province, 1, 2, 0, 0, 0, 0, 0);
        Commands.TryPost(cmd, true);
    }

    // ---- Beach defence: landings are walled in and attacked at once, not after the front collapses ----

    /// <summary>
    /// Every half minute: for each tracked landing on AI ground, the invaded country walls the beachhead in (a division into each of
    /// its empty landward neighbours) and sends attackers into it. Free divisions next to the beachhead only; the front is left to the AI.
    /// </summary>
    private void DefendBeachheads()
    {
        _game.Check();
        if (!SheepMod.Active || Game.State == 0) return; // 0 while the world is still loading: Game.Countries guards it too
        var now = Environment.TickCount64;
        if (now - _beachScanned >= LandingScanMs)
        {
            _beachheads = Landings();
            _beachScanned = now;
        }
        if (_beachheads.Count == 0) return;
        var hour = *(int*)(Game.State + HourOfGameState);
        var player = Game.ControlledTag;
        int* other = stackalloc int[2]; // {tag, tag index} of a beachhead holder
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : *(int*)(country + 8);
            if (tag <= 0 || tag == player) continue;
            var line = LineOf(tag, hour);
            if (line == null) continue; // no tracked line: the next Concentrate starts one
            var sent = 0;
            foreach (var beachhead in _beachheads)
            {
                var holder = 0;
                foreach (var id in beachhead) { holder = *(int*)(Game.Province(id) + ControllerOfProvince); break; }
                if (holder <= 0 || holder == tag) continue;
                (other[0], other[1]) = (holder, Game.TagIndex(holder));
                if (_atWar(country, other) == 0) continue; // a friendly or neutral landing, not ours to answer
                foreach (var id in beachhead)
                {
                    var p = Game.Province(id);
                    if (p == 0 || *(int*)(p + ControllerOfProvince) != holder) continue;
                    // Wall it in: an empty land neighbour of ours the landing could pour through gets a defender.
                    foreach (var n in Game.Neighbours(p))
                    {
                        var q = Game.Province(n);
                        if (q == 0 || _gap.ContainsKey(n) || *(int*)(q + ControllerOfProvince) != tag || Standing(country, tag, q) > 0) continue;
                        if (beachhead.Contains(n)) continue;
                        var helper = Helper(tag, q, line, ReserveRadius);
                        if (helper == 0) continue;
                        Move(helper, n);
                        _sent[helper] = now + SentMs;
                        _gap[n] = now + LandingMs;
                        if (++sent >= MaxLandingSends) goto nextCountry;
                    }
                    // Crush it: attack into the beachhead itself; the move turns into combat on arrival.
                    if (!_gap.ContainsKey(id))
                    {
                        var helper = Helper(tag, p, line, BeachheadRadius);
                        if (helper != 0)
                        {
                            Move(helper, id);
                            _sent[helper] = now + SentMs;
                            _gap[id] = now + LandingMs;
                            if (++sent >= MaxLandingSends) goto nextCountry;
                        }
                    }
                }
            }
        nextCountry: ;
            Interlocked.Add(ref _defended, sent);
        }
    }

    /// <summary>
    /// Enemy coastal provinces with divisions on them, whose land side touches a country at war with their holder: flood-filled
    /// into beachheads by holder (one landing may spread over several provinces), capped — bigger is a home front, not a landing.
    /// </summary>
    // ponytail: same-holder components only — a mixed allied landing answers as several small beachheads, each handled fine.
    private static List<HashSet<int>> Landings()
    {
        var landings = new List<HashSet<int>>();
        var seen = new HashSet<int>();
        int* other = stackalloc int[2]; // {tag, tag index} of a province's controller
        for (int id = 1, misses = 0; misses < 512 && (_provinceCount < 0 || id <= _provinceCount); id++)
        {
            var p = Game.Province(id);
            if (p == 0) { misses++; continue; }
            misses = 0;
            _provinceCount = id; // the run of valid ids up to here
            var holder = *(int*)(p + ControllerOfProvince);
            if (holder <= 0 || seen.Contains(id) || *(int*)(p + DivisionsInProvince) == 0 || !Coastal(p)) continue;
            var holderCountry = Game.Country(holder);
            if (holderCountry == 0) continue;
            var contested = false; // a land neighbour held by a country at war with the holder
            foreach (var n in Game.Neighbours(p))
            {
                var controller = Game.Province(n) is var q && q != 0 ? *(int*)(q + ControllerOfProvince) : 0;
                if (controller <= 0 || controller == holder) continue;
                (other[0], other[1]) = (controller, Game.TagIndex(controller));
                if (_atWar(holderCountry, other) != 0) { contested = true; break; }
            }
            if (!contested) continue;
            var beachhead = new HashSet<int> { id };
            var frontier = new List<int> { id };
            while (frontier.Count > 0 && beachhead.Count < BeachheadMax)
            {
                var next = new List<int>();
                foreach (var b in frontier)
                foreach (var n in Game.Neighbours(Game.Province(b)))
                    if (Game.Province(n) is var q && q != 0 && *(int*)(q + ControllerOfProvince) == holder && beachhead.Add(n)) next.Add(n);
                frontier = next;
            }
            foreach (var b in beachhead) seen.Add(b); // its home coast too: don't rescan it every half minute
            if (beachhead.Count < BeachheadMax) landings.Add(beachhead);
        }
        return landings;
    }

    /// <summary>Whether the province borders the sea (an adjacency to a province with no controller).</summary>
    private static bool Coastal(nint p)
    {
        var info = *(nint*)(p + StaticOfProvince);
        var records = *(nint*)(info + AdjacenciesOfStatic);
        for (int i = 0, n = *(int*)(info + AdjacenciesOfStatic + 0xC); i < n; i++)
        {
            var q = Game.Province(*(int*)(records + i * 0x30 + 8));
            if (q != 0 && *(int*)(q + ControllerOfProvince) == 0) return true;
        }
        return false;
    }

    /// <summary>The country's faction, or 0 when it is in none (CIsInFactionWith compares these pointers).</summary>
    private static nint FactionOf(nint country)
    {
        var member = country == 0 ? 0 : *(nint*)(country + FactionMemberOfCountry);
        return member == 0 ? 0 : *(nint*)(member + FactionOfMember);
    }

    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0) Log.Error($"AI hook: {e}");
        }
    }
}
