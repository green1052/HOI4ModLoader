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
            "At war, the AI keeps a division in every province of a front that borders the enemy (enemy divisions or not); it no longer ships them all off (supply crisis, another front) and leaves the line empty while fighting.");
        _concentration = Config.Bind("AI", "Concentration", true,
            "The AI keeps only a thin screen on fronts with no enemy at war (neutral or friendly borders) and sends the rest to fronts facing more enemy divisions than they hold; fronts at war are never thinned.");
        _targetValue = Config.Bind("AI", "TargetValue", true,
            "The AI's local attacks go for valuable provinces first: victory points, capitals, supply hubs, airfields, resources, empty or worn-out defences (after pockets, before front shortening).");
        var leaders = Config.Bind("AI", "Leaders", true,
            "The AI gives its best field marshals and generals to its biggest, fighting, attacking fronts first, and counts the traits it ignores: infantry traits for infantry armies, offensive or defensive doctrine for attacking or holding fronts, bad status.");
        var airRetreat = Config.Bind("AI", "AirRetreat", true,
            "The AI pulls its planes out of regions where it is losing the air war badly (low air superiority, heavy losses, few kills) and uses them where it can win.");

        if (readiness) LoadReadiness();
        if (_targetValue) LoadTargetValue();
        if (_pocketClosing || _frontShortening || _armorTerrain || _roles || _targetValue) LoadPockets();
        if (airRetreat) LoadAirRetreat();
        if (leaders) LoadLeaders();
        if (_concentration || _holdLine) LoadConcentration();
        MainThread.Every(TimeSpan.FromMinutes(1), () =>
        {
            var (held, steered, pulled, concentrated) = (Interlocked.Exchange(ref _held, 0), Interlocked.Exchange(ref _steered, 0), Interlocked.Exchange(ref _pulled, 0), Interlocked.Exchange(ref _concentrated, 0));
            if (held + steered + pulled + concentrated > 0)
                Log.Info($"last minute: held {held} AI attack(s) until the army recovers, skipped {steered} local attack target(s) for better ones, lowered {pulled} lost air region(s), rescaled {concentrated} front request(s)");
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
        _findOrder = (delegate* unmanaged<nint, int, nint>)Sig.Find(Signatures.FindOrder);
        _executeVTable = Sig.VTable(Signatures.ExecuteCommand);
        _post = Patches.Hook<PostFn>(Sig.Find(Signatures.PostAICommand), (cmd, tag) =>
        {
            if (*(nint*)cmd != _executeVTable || *(byte*)(cmd + ExecuteOn) == 0 || *(int*)tag == Game.ControlledTag || !SheepMod.Active || Ready(cmd))
                return _post!.OriginalFunction(cmd, tag);
            ((delegate* unmanaged<nint, int, void>)(*(nint**)cmd)[0])(cmd, 1); // deleted, as the game does with an invalid command
            Interlocked.Increment(ref _held);
            return 0;
        });
    }

    /// <summary>Whether the army (and an army group's armies) of an "attack on" command is fit to attack. Read only: AI threads.</summary>
    private bool Ready(nint cmd)
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
        return count == 0 || org / count >= _minOrg && strength / count >= _minStrength && supply / count >= _minSupply && weak * 100 <= count * MaxWeakPercent;
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
        Patches.Asm(site, 7, "use64", $"mov rax, 0x{(nint)(delegate* unmanaged<nint, nint, nint, byte>)&PocketFilter:X}", "call rax", "test al, al");
    }

    /// <summary>
    /// Like the original check (nonzero = skip this target), but while some target of <paramref name="from"/> would cut off
    /// enemy divisions, every target that wouldn't is skipped too; the game's random pick then takes a pocket-closing one.
    /// Vanilla's own rejections stay. AI threads: read only.
    /// </summary>
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
            if (!_pocketClosing && !_frontShortening && !_targetValue) return 0;
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
    private const int MinTargetValue = 30;
    private const long CapitalsCacheMs = 60_000;

    private static bool _targetValue;
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
        foreach (var unit in defenders)
        {
            long o, s;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[OrgRatioSlot])(unit, &o);
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)unit)[StrengthRatioSlot])(unit, &s);
            org += o;
            strength += s;
        }
        if (org / defenders.Length < 30_000 || strength / defenders.Length < 50_000) return value + 10;
        return value - 5 * defenders.Length * (1 + _fortLevel(target) / 2.0);
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
    private const int WindowHours = 168, MinLosses = 20;
    private const long MinShare = 35_000, PullOutShare = 15_000; // our share of the region's air superiority
    private const int LossesPerKillPercent = 150;

    [Function(CallingConventions.Microsoft)]
    public delegate void PrioritiesFn(nint aiObj, nint country, nint scratch, nint buffer, nint entries, nint bases, nint totals, nint buffer2);

    private IHook<PrioritiesFn>? _priorities;
    private int _holderOffset, _countOffset, _arrayOffset;
    private int _pulled;

    private void LoadAirRetreat()
    {
        var at = Sig.FindAll(Signatures.StrategicAir, 2)[0];
        (_holderOffset, _countOffset, _arrayOffset) = (*(int*)(at + 3), *(byte*)(at + 14), *(byte*)(at + 36));
        _ = Sig.Find(Signatures.RegionAirData) + Sig.Find(Signatures.RegionCombatData); // the offsets below are still where they were
        _priorities = Patches.Hook<PrioritiesFn>(Sig.Find(Signatures.UpdateRegionPriorities), (aiObj, country, scratch, buffer, entries, bases, totals, buffer2) =>
        {
            _priorities!.OriginalFunction(aiObj, country, scratch, buffer, entries, bases, totals, buffer2);
            Safely(() =>
            {
                if (SheepMod.Active) AirRetreat(country, entries);
            });
        });
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

    // ---- Concentration: quiet and weak-enemy fronts ask for fewer divisions, fronts facing a strong enemy for more ----

    private const int MinWanted = 0x90, DesiredWanted = 0x94, MaxWanted = 0x98, FixedRequest = 0x9C, CountryOfFront = 0xE0, FrontOfAiFront = 0xE8;
    private const int SectionOfAiFront = 0xF0, AreaDefense = 0x104, InvasionTarget = 0x120, AttackingFront = 0x218 + 0x84, OtherRequest = 0x1DC;
    private const int SectionsOfFront = 0x58, IdOfSection = 8, PairsOfSection = 0x48, CombatsInProvince = 0x17C; // province: combats count
    private const double QuietFactor = 0.25, HotBoost = 1.25;

    [Function(CallingConventions.Microsoft)] public delegate nint GeneralRetFn(nint general);

    private IHook<GeneralRetFn>? _unitRequests;
    private int _concentrated;
    private static bool _concentration, _holdLine;
    private const int FactorHours = 24, CalmHours = 168;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Hour, double Factor)> Factors = new(); // AI front -> its factor, for a day
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int> LastBattle = new();                 // AI front -> hour of its last battle

    private void LoadConcentration()
    {
        _resolve = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.ResolveRef);
        _atWar = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.IsAtWarWith);
        _unitRequests = Patches.Hook<GeneralRetFn>(Sig.Find(Signatures.UpdateFrontUnitRequests), general =>
        {
            var result = _unitRequests!.OriginalFunction(general);
            Safely(() =>
            {
                if (SheepMod.Active) Concentrate(general);
            });
            return result;
        });
    }

    /// <summary>Rescales each land front's requests by the enemy actually facing it, before the theatre's divisions are shared out.</summary>
    private void Concentrate(nint general)
    {
        var other = stackalloc int[2]; // {tag, tag index} of a province's controller
        foreach (var front in Native.PdxArray(general + FrontsOfGeneral))
        {
            if (*(byte*)(front + FixedRequest) != 0 || *(int*)(front + AreaDefense) != 0 || *(nint*)(front + InvasionTarget) != 0 || *(byte*)(front + OtherRequest) != 0)
                continue; // area defense, invasions and fixed shares: as the game asks
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

            // Both sides of the section: enemy provinces (held by a country at war with us) and their divisions; our provinces
            // facing enemy divisions (each needs a defender), and whether any of ours is fighting.
            var enemyHeld = new Dictionary<nint, bool>();
            var contact = new HashSet<nint>();    // ours facing any enemy-held province
            var fighting = false;
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
                }
                if (held && ours != 0) contact.Add(ours);
            }
            var hostile = enemyHeld.ContainsValue(true);
            var enemyProvinces = enemyHeld.Count(e => e.Value);
            int min = *(int*)(front + MinWanted), desired = *(int*)(front + DesiredWanted), max = *(int*)(front + MaxWanted);
            // Requests are redone every hour; a factor that followed every enemy move would march divisions back and forth.
            // So a factor holds for a day, and a front gives divisions away only a week after its last battle.
            var hour = *(int*)(Game.State + HourOfGameState);
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
