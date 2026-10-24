using HOI4ModLoader;

namespace AiAssist;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3) for railway guns, supply hubs and railways, checked by tests\Signatures.Tests.</summary>
public static class AiSignatures2
{
    /// <summary>
    /// CAIGeneral's railway gun assignment (0x14107E410, from CAIGeneral::HourlyUpdate), its per-army loop over the theatre's
    /// fronts (r13 CAIFront, r14 index into its armies at +0x60). Reads the army's divisions (`movsxd rdx,[rcx+5Ch]`), stores
    /// them at [rbp-30h][r8*4] (the sort key, largest first), then the score RAILWAY_GUN_ASSIGNMENT_SCORE_UNITCOUNT_MULTIPLIER
    /// x divisions (0 on an area defence front) at [r9+r8*8] (+0x5C). The 15 bytes from +0x5C hold no branch target.
    /// </summary>
    [Signature] public const string RailwayGunScore = "49 8B 45 60 4A 8B 0C F0 48 63 51 5C 85 D2 0F 84 ? ? ? ? 03 DA 89 9D ? ? ? ? 4C 63 C6 48 8B 45 D0 42 89 14 80 41 83 BD 04 01 00 00 00 75 28 48 8B C2 48 0F AF 05 ? ? ? ? 48 69 C8 A0 86 01 00 49 8B C2 48 F7 E9 48 C1 FA 0E 48 8B C2 48 C1 E8 3F 48 03 D0 EB 03 48 8B D7 4B 89 14 C1 49 8B 45 60 48 89 85 50 11 00 00 41 3B F7";

    /// <summary>
    /// The AI's railway builder (0x1414E0740), used by its interior minister: (CPdxArray&lt;int&gt;* path, int* tag, int levelsToAdd,
    /// byte mode) -> bool. Path: adjacent province ids; the target level is the lowest (level + levelsToAdd) along it, at most 5.
    /// Posts a CBuildRailwayCommand for the tag.
    /// </summary>
    [Signature] public const string BuildRailway = "4C 8B DC 49 89 5B 10 45 88 4B 20 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 40 41 0F B6 F1 45 8B F8 4C 8B E2 4C 8B E9 80 3D";
}
