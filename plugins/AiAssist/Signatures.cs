using HOI4ModLoader;

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
