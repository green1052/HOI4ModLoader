using HOI4ModLoader;

namespace AttachedCas;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CAS target selection (collects CAirMission::STacticalTarget combats in the mission regions), per combat:
    /// `mov rbp,[rsi+28h]; mov rcx,rbp; call CAirWing::GetAttachedOrdersGroup; test rax,rax; jz take; ...`. Unattached wings
    /// take the combat; attached ones fall through to two loops that keep it only if a division on either side is in their army.
    /// </summary>
    [Signature] public const string AttachedCheck = "48 8B 6E 28 48 8B CD E8 ? ? ? ? 48 85 C0 0F 84 ? ? ? ? 48 8B CD E8";
}

/// <summary>
/// Air wings attached to a general give close air support to every combat in their mission regions, like unattached wings,
/// instead of only to combats their own army's divisions fight in.
/// </summary>
[Plugin("com.green1052.attachedcas", "Attached CAS", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe class AttachedCas : Plugin
{
    public override void Load()
    {
        // `jz take` (0F 84 rel32) becomes `jmp take; nop` (E9 rel32+1, 90): every wing takes the combat.
        var jz = Sig.Find(Signatures.AttachedCheck) + 15;
        var rel = *(int*)(jz + 2);
        Patches.Bytes(jz, "0F 84" + Hex(rel), "E9" + Hex(rel + 1) + "90");
    }

    static string Hex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));
}
