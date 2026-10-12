using HOI4ModLoader;

namespace AiAssist;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>PostAICommand(command, int* postingTag) -> bool: every AI command, from every AI path; an invalid one is deleted and 0 returned.</summary>
    [Signature] public const string PostAiCommand = "40 53 48 83 EC 40 80 3D ? ? ? ? 00 48 8B D9 0F 84 ? ? ? ? 80 3D ? ? ? ? 00 75 ? 48 8B CA E8";

    /// <summary>
    /// CAIGeneral::UpdateAreaDefense(general): gives every army on its area defence fronts (front+0x104 set, armies at +0x60)
    /// an area defence order (type 5) and its provinces. Called from 0x141096DC0, the result unused.
    /// </summary>
    [Signature] public const string UpdateAreaDefense = "48 89 4C 24 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC E8 00 00 00 0F 29 B4 24 ? ? ? ? 4C 8B B1";

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

    [VTable] public const string DeleteOrderCommand = "COrderDeleteCommand";
    [VTable] public const string AssignOrderCommand = "COrderAssignCommand";
    [VTable] public const string UnassignOrderCommand = "COrderUnassignCommand";
    [VTable] public const string ParadropTargetCommand = "COrderSetParadropTargetCommand"; // 0x38: {group CRef @0x28, order id @0x30, province @0x34} (ctor 0x14183AF60)

    /// <summary>River crossing in land combat: `mov r8,[rcx+138h]; mov rdx,[rip+RIVER_CROSSING_PENALTY]; cmovnz rdx,[rip+..._LARGE]`.</summary>
    [Signature] public const string RiverPenalty = "4C 8B 81 38 01 00 00 48 8B 15 ? ? ? ? 48 0F 45 15 ? ? ? ?";

    /// <summary>
    /// AIAir: score every strategic region for the country's air force (aiObj, CCountry*, scratch, buffer, CPdxArray&lt;entry 0x4E8&gt;*
    /// entries, bases, long totals[18], buffer); entry+0x10 is the score, the share of planes it gets (&lt;= 0: none).
    /// </summary>
    [Signature] public const string UpdateRegionPriorities = "48 89 5C 24 ? 4C 89 4C 24 ? 4C 89 44 24 ? 48 89 54 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8B EC 48 83 EC 70 80 3D ? ? ? ? 00 0F 84";

    /// <summary>
    /// CAIGeneral::UpdateFrontUnitRequests(general): every front of the theatre sets its Min/Desired/Max divisions
    /// (CAIFront+0x90/+0x94/+0x98); the theatre's divisions are shared out by them right after.
    /// </summary>
    [Signature] public const string UpdateFrontUnitRequests = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 30 48 8B F1 48 8B 49 08 48 8B 01 FF 50 08 48 8B C8 E8 ? ? ? ? 48 8B C8 48 8B D8 48 8B 90 88 0F 00 00 83 BA 04 01 00 00 00";

    /// <summary>
    /// CArmyReinforcementRequests vtable slot 1 (requests, long* score, CEquipmentType* archetype) -> score*: how much a division
    /// is owed this archetype, (4e7*(template prio+1) + 1e8*theatre group prio) * (1 - have/need). The daily stockpile
    /// distribution fills divisions highest score first. requests+0x20 = CArmy. Game simulation thread.
    /// </summary>
    [Signature] public const string ReinforceScore = "48 89 5C 24 10 55 56 57 41 56 41 57 48 83 EC 30 33 DB 4D 8B F0 4C 8B FA 48 8B E9 39 59 34 74";

    /// <summary>CArmyUpgradesRequests vtable slot 1 (requests, long* score, archetype) -> score*: the same for newer-equipment upgrades (0: nothing to upgrade).</summary>
    [Signature] public const string UpgradeScore = "48 89 5C 24 08 48 89 6C 24 10 56 48 83 EC 20 48 8B F2 48 8B E9 48 8D 54 24 48 E8 ? ? ? ? 33 DB 48 39 5C 24 48 75";

    /// <summary>
    /// CAIInteriorMinister: turn its factories wanted per equipment archetype (minister+696, int each) into production line
    /// changes (minister, CPdxArray&lt;CProductionLine*&gt;* production+88, current per archetype, current per naval role).
    /// Every 2nd day, on the country's AI worker thread.
    /// </summary>
    [Signature] public const string ApplyFactoryTargets = "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 55 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 90 00 00 00 4D 8B F9";

    /// <summary>
    /// CProductionLine::GetEfficiency(line, long* out) -> out: average efficiency of its active factories (line+0x18, capped at
    /// vfunc+0x58), Q15 percent (10 % = 10 &lt;&lt; 15). Vanilla AI calls it on worker threads (production line switch check).
    /// </summary>
    [Signature] public const string LineEfficiency = "40 53 48 83 EC 20 44 8B 41 18 48 8B DA E8 ? ? ? ? 48 8B C3 48 83 C4 20 5B C3";

    /// <summary>CEquipmentType::IsNaval(type) -> bool.</summary>
    [Signature] public const string IsNaval = "40 53 48 83 EC 20 48 8B D9 48 8B 89 ? ? ? ? 48 3B CB 74 ? E8 ? ? ? ? 84 C0 74 ? B0 01 48 83 C4 20 5B C3 48 8B 83 ? ? ? ? 48 8B 8B ? ? ? ? 48 85 C0 75 ? 48 8B 83 ? ? ? ? 48 8B 80 ? ? ? ? 48 0B C8 E8 ? ? ? ? 48 B9 C1 03 40 00 80 00 00 00";

    /// <summary>
    /// NAIAirEvaluation, sea neighbours of land regions: `mov rax,[gs+2E0h]` regions, `cmp [region+0A0h],50000` water share,
    /// `movsxd r14,[region+0BCh]; mov rax,[region+0B0h]` neighbour regions, `movsxd r8,[neighbour+60h]` region id (pins those offsets).
    /// </summary>
    [Signature] public const string RegionNeighbours = "45 3B A5 EC 02 00 00 7D ? 49 8B 85 E0 02 00 00 48 8B 2C F8 48 8B 44 24 ? EB ? 33 ED 48 81 BD A0 00 00 00 50 C3 00 00 0F 8D ? ? ? ? 4C 63 B5 BC 00 00 00 33 DB 4D 85 F6 0F 8E ? ? ? ? 4C 8B AC 24 ? ? ? ? 4C 8B A4 24 ? ? ? ? 0F 1F 44 00 00 48 8B 85 B0 00 00 00 4C 8B 0C D8 4D 63 41 60";

    /// <summary>Strategic bombing region scorer: `add [entry+68h]` escorts, `add [entry+80h]` strategic bombing, `add [entry+0B0h]` logistics strike: plane wishes by mission bit (pins them).</summary>
    [Signature] public const string StrBombWishes = "B9 1A 00 00 00 4C 01 4B 68 4C 01 83 80 00 00 00 48 01 93 B0 00 00 00 E8";

    /// <summary>
    /// CAirMission::CollectTacticalTargets(mission, CPdxArray&lt;STacticalTarget&gt;* targets, ...): the battles a close air
    /// support mission sees, in region province-list order — the order the plane budget is spent in. Main thread.
    /// </summary>
    [Signature] public const string CollectTacticalTargets = "4C 8B DC 49 89 4B 08 56 57 41 55 48 81 EC C0 00 00 00 83 79 44 00 41 8B F9 49 63 F0 4C 8B EA 0F 84 ? ? ? ? 48 8B 41 30 48 83 B8 98 00 00 00 00";

    /// <summary>
    /// The per-candidate body of CAIFront::CalcNavalInvasionTarget's parallel_for (closure, CProvince* candidate): skips excluded ids and the
    /// current target (sets found), scores the rest (distance, area strategies, failed ports, no-port factor) and appends province and score
    /// under the closure's SRWLOCK. TBB workers of a country's AI update; its only caller is that tbb body.
    /// </summary>
    [Signature] public const string InvasionTargetCandidate = "48 89 5C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 81 EC A0 00 00 00 48 8B F2 4C 8B F9 48 8B 01 44 0F B6 00 90 45 84 C0 0F 85";

    /// <summary>
    /// CInvasionSupportObjective (naval AI goal) vtable slot 1, IsValid(objective) -> bool: one of its invasion orders (+0x38, 24 B refs, count +0x44)
    /// still exists and no region of its route (+0x10, CPdxArray&lt;CStrategicRegion*&gt;, filled at creation by 0x1420141F0 from the order's
    /// naval path) is closed to it; +8 CCountry*. The naval goal trackers ask every live objective each update, on the country's AI worker thread.
    /// </summary>
    [Signature] public const string InvasionSupportValid = "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 41 56 48 83 EC 20 33 F6 48 8B F9 8B DE 39 59 44 7E ? 48 63 C3 48 8D 14 40 48 8B 47 38 48 8D 0C D0 E8 ? ? ? ? 48 85 C0 75 ? FF C3 3B 5F 44 7C ? 32 C0 48 8B 5C 24 30 48 8B 74 24 38 48 8B 7C 24 40 48 83 C4 20 41 5E C3 83 FB FF 74 ? 39 77 1C 7E ? 4C 8B F6 66 66 0F 1F 84 00 00 00 00 00 48 8B 47 10 48 8B 4F 08";

    /// <summary>The building database's coastal_bunker CBuildingType* (db) (thread-safe magic-static key; the null type when undefined).</summary>
    [Signature] public const string CoastalBunkerType = "40 53 48 83 EC 30 48 8B D9 8B 15 ? ? ? ? 65 48 8B 04 25 58 00 00 00 B9 64 08 00 00 48 8B 04 D0 8B 04 01 39 05 ? ? ? ? 7F ? 4C 8D 43 58 48 8D 4B 40 48 8D 15 ? ? ? ? 48 83 C4 30 5B E9 ? ? ? ? 48 8D 0D ? ? ? ? E8 ? ? ? ? 83 3D ? ? ? ? FF 75 ? 48 8D 05 ? ? ? ? 48 89 44 24 20 48 C7 44 24 28 0E 00 00 00";

    /// <summary>
    /// The force concentration "active" test: `cmp dword [rcx+218h],0; jle; cmp dword [rcx+22Ch],0; jz; cmp qword [rcx+238h],0; jz`.
    /// Pins CAIFront+0x218 (its embedded CAIForceConcentration: +0 divisions given, +0x14 units, +0x20 army). Never called.
    /// </summary>
    [Signature] public const string ConcentrationActive = "83 B9 18 02 00 00 00 48 8B D9 0F 8E ? ? ? ? 83 B9 2C 02 00 00 00 0F 84 ? ? ? ? 48 83 B9 38 02 00 00 00";

    /// <summary>CAIForceConcentration::StoreTarget: `mov rdi,[rsi+48h]; mov rax,[rsi+60h]; mov r14,[rax]` (target, CPdxArray&lt;CProvince*&gt; path, path[0] = source). Never called.</summary>
    [Signature] public const string ConcentrationPath = "48 8B 7E 48 48 8B 46 60 4C 8B 30";

    /// <summary>CAIForceConcentration::SetExecute: `movzx edi,byte [rcx+84h]; cmp byte [rdx+248h],0; setnz al` (attack on vs the order executing). Never called.</summary>
    [Signature] public const string ConcentrationAttacking = "0F B6 B9 84 00 00 00 80 BA 48 02 00 00 00 0F 95 C0";

    /// <summary>
    /// COrderSetParadropSourceCommand ctor (mem[0x50], COrdersGroup*, int source province, int order id (0: a new paradrop order),
    /// CPdxArray&lt;CArmy*&gt;* paratroopers). Only the map UI builds it: the vanilla AI never makes paradrop orders.
    /// </summary>
    [Signature] public const string ParadropSourceCommand = "48 89 5C 24 10 48 89 6C 24 20 48 89 4C 24 08 56 57 41 55 41 56 41 57 48 83 EC 30 41 8B D9 41 8B F8 4C 8B EA 48 8B F1 C6 41 08 00 C7 41 0C FF FF FF FF";

    /// <summary>IsParadropSource(CProvince*, COrdersGroup*) -> bool: our airbase province holding NAir.MIN_PLANE_COUNT_PARADROP transports (`lea rsi,[rdx+3Ch]`: group tag).</summary>
    [Signature] public const string IsParadropSource = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8D 72 3C 48 8B DA 48 8B D6 45 33 C9 45 33 C0 48 8B F9 E8 ? ? ? ? 3B 05 ? ? ? ? 0F 8C ? ? ? ?";

    /// <summary>IsParadropTarget(COrder*, CProvince*) -> bool: land, and in range of the order's source airbase (reads province+0xB8 unchecked: land provinces only).</summary>
    [Signature] public const string IsParadropTarget = "40 53 55 57 48 83 EC 30 48 8B 82 B8 00 00 00 48 8B DA 48 8B E9 F6 80 D2 00 00 00 01 0F 84 ? ? ? ? 8B B9 B8 00 00 00";

    /// <summary>
    /// COrder check (order, std::string* reason or null) -> bool: invasion/paradrop prepared, route air superiority (cached at +0x11A
    /// with a null reason), transport planes for the divisions standing at the source (none there: passes).
    /// </summary>
    [Signature] public const string OrderReady = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 8B 41 30 48 8B F1 83 F8 03 75 ? E8 ? ? ? ? EB ? 83 F8 04 75 ? E8 ? ? ? ?";

    /// <summary>
    /// The air AI's per-wing order (0x1412CAFF0; wing, CAirBase* base, CStrategicRegion* region, int mission index or -1, bool
    /// cancelTransfer) -> bool: flies the wing to the base with the mission, or sets the region and mission there with the
    /// AI's commands. A wing on training at its base only gets training cleared. Called for every wing the plan uses, by the
    /// mission share-out (0x1412C3F20, region and mission) and the rebasing (0x1412CDB20, -1), from CAIMilitaryMinister's
    /// daily and hourly updates: the country's AI worker thread.
    /// </summary>
    /// <summary>
    /// CAIGeneral's daily army training pass (CCountry*, bool haltAll, CPdxArray&lt;CAIFront*&gt;* fronts): stops every exercise when
    /// haltAll (surrender progress, 10 %+ of divisions fighting, war preparation), else decides per army of its fronts.
    /// AI worker thread.
    /// </summary>
    [Signature] public const string UpdateArmyTraining = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 54 41 55 41 56 41 57 48 83 EC 30 4C 8B E1 4D 8B 38 49 63 40 0C";

    /// <summary>COrdersGroup: any of its divisions below the training cap (TRAINING_MAX_LEVEL, regular) (group) -> bool.</summary>
    [Signature] public const string BelowMaxLevel = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 80 79 39 00 75 ? 48 8B 59 50 48 63 41";

    /// <summary>
    /// Naval AI: the task forces no objective took (CPdxArray&lt;slot*&gt;* slots, int* tag; slot+0 the task force's CRef): each gets
    /// hold, is sent to the nearest base if at sea, and merged into a fleet. Country's AI worker thread.
    /// </summary>
    [Signature] public const string UnassignedTaskForces = "48 89 54 24 ? 48 89 4C 24 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 48 01 00 00 0F 29 B4 24 ? ? ? ? 4C 8B E9";

    /// <summary>CNavalMissionSetTypeCommand ctor (mem[0x48], CTaskForce*, int mission); +0x44 stop training at max.</summary>
    [Signature] public const string SetNavalMission = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 41 8B D8 48 8B F2 48 8B F9 C6 41 ? ? C7 41 ? ? ? ? ? 33 C9 89 4F ? C7 47 ? ? ? ? ? 66 89 4F ? 48 89 4F ? 48 8D 05 ? ? ? ? 48 89 07 48 8D 4F ? E8 ? ? ? ? 90 89 5F ? 66 C7 47";

    /// <summary>CNavalMissionMoveCommand ctor (mem[0x48], CTaskForce*, int sea province, bool).</summary>
    [Signature] public const string NavalMove = "48 89 4C 24 ? 53 55 56 57 41 56 48 83 EC 30 41 0F B6 D9 41 8B F8";

    /// <summary>CTaskForce: a ship can still gain experience from training (under the training cap) (tf) -> bool.</summary>
    [Signature] public const string TaskForceCanTrain = "48 89 5C 24 18 57 48 83 EC 20 48 8B F9 48 8D 4C 24 30 E8 ? ? ? ? 48 8B 9F 48 03 00 00 48 63 87 54 03 00 00 48 8D 3C C3 48 3B DF 74 22 66 90";

    /// <summary>CTaskForce: its ships' shore bombardment (tf, long* out) -> out.</summary>
    [Signature] public const string TaskForceShoreBombardment = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 C7 02 00 00 00 00 48 8B FA 48 8B 99";

    /// <summary>
    /// CMoveShipsCommand ctor (mem[0x58], CPdxArray&lt;CShip*&gt;* ships, CTaskForce* toNavy, bool createNew, int province, bool move):
    /// with no task force and createNew, the ships (all of one task force) become a new task force of its fleet, where it is.
    /// </summary>
    [Signature] public const string MoveShips = "48 89 5C 24 18 48 89 4C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 40 0F 29 74 24 30 41 0F B6 D9 49 8B F8 4C 8B FA 4C 8B F1 C6 41 08 00 C7 41 0C FF FF FF FF 45 33 ED 44 89 69 10";

    /// <summary>CShip: its shore bombardment (ship, long* out) -> out (a task force's is the sum over its ships).</summary>
    [Signature] public const string ShipShoreBombardment = "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 41 56 41 57 48 83 EC 30 4C 8B 81 28 07 00 00 48 8D B9 80 00 00 00";

    /// <summary>
    /// Naval AI allocator's first filter (CPdxArray&lt;slot*&gt;* list) -> removed: drops reserve-fleet and dead task forces from
    /// the list the objectives then take from (slot+0 the task force's CRef, +0x18 its objective). Country's AI worker thread.
    /// </summary>
    [Signature] public const string FilterTaskForces = "41 56 48 83 EC 30 48 89 5C 24 ? 4C 8B F1 48 89 6C 24 ? 48 89 74 24 ? 33 F6 48 89 7C 24 ? 4C 89 7C 24 ? 39 71 0C";

    /// <summary>
    /// AI leader score (0x14194B960), the candidate who commands another group: `cmp rbp,rax; je; call InCombat(group); test al,al;
    /// jnz reject(-100); sub esi,200`. Every AI leader choice (FindBestLeader 0x14194C710) scores through it.
    /// </summary>
    [Signature] public const string LeaderStealBranch = "48 3B E8 74 ? 48 8B C8 E8 ? ? ? ? 84 C0 75 ? 81 EE C8 00 00 00";

    [Signature] public const string AssignWing = "48 89 5C 24 ? 48 89 6C 24 ? 56 57 41 54 41 56 41 57 48 83 EC 30 45 8B F9";

    /// <summary>
    /// CStratAirSetMissionCommand ctor (mem[0x48], CAirWing*, int mission bits, bool enable, bool clearFirst): +0x40 bits, +0x44
    /// enable, +0x45 clear, +0x46 stop training at max XP. The AI's own training pass posts (wing, 0x800, 1, 1) with +0x46 set.
    /// </summary>
    [Signature] public const string SetMissionCommand = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 54 24 ? 48 89 4C 24 ? 56 57 41 56 48 83 EC 20 41 0F B6 F9 41 8B D8 48 8B F2 4C 8B F1 C6 41 08 00 C7 41 0C FF FF FF FF 33 C9 41 89 4E 10 41 C7 46 14 00 00 FF FF 66 41 89 4E 18 49 89 4E 1C 48 8D 05 ? ? ? ? 49 89 06 49 8D 4E 28 E8 ? ? ? ? 90 41 89 5E 40 41 88 7E 44 0F B6 44 24";

    /// <summary>CAirWing::CanTrain(wing) -> bool: may fly training (`mov eax,[rcx+30h]; shr eax,0Bh`: mission 0x800 allowed), XP under NAir.AIR_WING_XP_TRAINING_MAX, not in a raid, not a carrier's in port.</summary>
    [Signature] public const string CanTrain = "40 53 48 83 EC 20 8B 41 30 48 8B D9 C1 E8 0B";

    /// <summary>CAirWing::GetAirBase(wing) -> CAirBase*: where it is, or is flying to (ArmySupport's pattern too).</summary>
    [Signature] public const string AirBaseOfWing = "40 53 48 83 EC 20 48 8B D9 48 83 C1 48 83 39 00 75 ? 83 79 ? ? 74 ? E8 ? ? ? ? 48 85 C0 75 ? 48 8B 4B ? 48 83 C1 18 83 39 00 75 ? 83 79 ? ? 75 ? 33 C0 48 83 C4 20 5B C3";

    /// <summary>CAirWing::GetExperienceLevel(wing) -> int: how many of NAir.AIR_WING_XP_LEVELS its XP (`mov rdx,[rcx+218h]`) reached; 0 green.</summary>
    [Signature] public const string WingXpLevel = "4C 8B 05 ? ? ? ? 33 C0 48 8B 91 18 02 00 00";

    /// <summary>
    /// The diplomacy's "at war with anyone" test (diplomacy, int mode; 0 as the game asks) -> bool: true when `[rcx+0A4h]` is set,
    /// else when a relation is a war. CCountry::IsAtWar (0x140700520) is `mov rcx,[rcx+0F88h]; jmp` here; air missions call it
    /// with 0 to tell war from peace.
    /// </summary>
    [Signature] public const string AtWarAny = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 83 B9 A4 00 00 00 00 8B FA";
}
