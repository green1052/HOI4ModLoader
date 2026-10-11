using HOI4ModLoader;

namespace ChecksumPatcher;

/// <summary>Byte patterns, checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    public const string GateAnchor = "Active Mod Count: ";

    /// <summary>
    /// gameapplication.cpp: `achievements = strcmp(VANILLA_CHECKSUM, checksum) == 0` compiles to `test eax,eax; sete reg; call`.
    /// Anchored to the function that logs "Active Mod Count: " so only the real gate matches.
    /// </summary>
    [Signature(Anchor = GateAnchor)] public const string Gate = "85 C0 0F 94 ?? E8";
}

/// <summary>
/// Keeps achievements available when mods change the game checksum, by forcing the "checksum is vanilla" comparison to
/// succeed (Ironman works with mods already). Patches memory only; hoi4.exe on disk is untouched.
/// </summary>
[Plugin("com.green1052.checksumpatcher", "Checksum Patcher", "1.0.0")]
public class ChecksumPatcher : Plugin
{
    // test eax,eax -> xor eax,eax: the following sete always yields "unmodified".
    public override void Load() => Patches.Bytes(Sig.Find(Signatures.Gate, Signatures.GateAnchor), "85 C0", "31 C0");
}
