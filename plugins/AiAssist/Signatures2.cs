using HOI4ModLoader;

namespace AiAssist;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3) for railway guns and supply hubs, checked by tests\Signatures.Tests.</summary>
public static class AiSignatures2
{
    /// <summary>
    /// CAIGeneral's railway gun assignment (0x14107E410, ai_general.cpp, from CAIGeneral::HourlyUpdate), its per-army loop over the
    /// theatre's fronts (r13 CAIFront, r14 index into its armies at +0x60): `mov rax,[r13+60h]; mov rcx,[rax+r14*8]; movsxd rdx,[rcx+5Ch]`
    /// (divisions), the count into [rbp-30h][r8*4] (what the armies are sorted by, largest first), then the score
    /// RAILWAY_GUN_ASSIGNMENT_SCORE_UNITCOUNT_MULTIPLIER x divisions (0 on an area defence front) into [r9+r8*8] at +0x5C,
    /// followed by `mov rax,[r13+60h]; mov [rbp+1150h],rax` (15 bytes from +0x5C, no branch target inside).
    /// </summary>
    [Signature] public const string RailwayGunScore = "49 8B 45 60 4A 8B 0C F0 48 63 51 5C 85 D2 0F 84 ? ? ? ? 03 DA 89 9D ? ? ? ? 4C 63 C6 48 8B 45 D0 42 89 14 80 41 83 BD 04 01 00 00 00 75 28 48 8B C2 48 0F AF 05 ? ? ? ? 48 69 C8 A0 86 01 00 49 8B C2 48 F7 E9 48 C1 FA 0E 48 8B C2 48 C1 E8 3F 48 03 D0 EB 03 48 8B D7 4B 89 14 C1 49 8B 45 60 48 89 85 50 11 00 00 41 3B F7";

    /// <summary>BuildingContainer::GetFreeLevels(container, type) -> int: max level minus planned (built and queued), shared slots included.</summary>
    [Signature] public const string FreeLevels = "48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 20 4C 63 82";

    /// <summary>CCountry::CanBuildInProvince(country, province, type, bool) -> bool.</summary>
    [Signature] public const string CanBuildInProvince = "48 8B 82 B8 00 00 00 4D 8B D0 4C 8B C1 F6 80 D2 00 00 00 01";

    [VTable] public const string ConstructionCommand = "CAddConstructionCommand";
    [VTable] public const string BuildingReference = "CBuildingReference";
    [VTable] public const string BuildingLine = "CBuildingProductionLine";
}
