using HOI4ModLoader;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>In the AI's ace assignment: `mov r14,[rbx+Aces]; movsxd rax,[rbx+Aces+0Ch]`; Aces is the country's CPdxArray&lt;CAce*&gt;.</summary>
    [Signature] public const string CountryAces = "4C 8B B3 D8 12 00 00 48 63 83 E4 12 00 00 4D 8D 2C C6";

    /// <summary>CAce::GetWing(ace) -> CAirWing*, or 0.</summary>
    [Signature] public const string WingOfAce = "48 83 EC 28 48 81 C1 E8 00 00 00";

    /// <summary>CAirWing::GetAce(wing) -> CAce*, or 0.</summary>
    [Signature] public const string AceOfWing = "48 81 C1 E8 02 00 00 83 39 00 75 ? 83 79 ? ? 75";

    /// <summary>CAce::CanAssignTo(ace, wing, std::string* reasonOrNull) -> bool: alive and flies the wing's planes. Starts `cmp byte [rcx+Alive],0`.</summary>
    [Signature] public const string CanAssignAce = "40 53 48 83 EC 40 80 B9 58 01 00 00 00 49 8B D8 4C 8B C1";

    /// <summary>Puts an ace on a wing, off its old one. With no ace it takes the wing's ace off. No country field.</summary>
    [VTable] public const string AssignAceCommand = "CAssignAceCommand";
}

/// <summary>
/// Auto aces, once a game day. Fallen aces are taken off their wings; vanilla leaves them there, blocking the slot.
/// Every country's in single player, the player's in multiplayer. The player's idle aces go to wings without a living
/// ace, wings on a mission first, then the biggest. Vanilla commands, so multiplayer works.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int AceSize = 0x38, AceRef = 0x08, WingRef = 0x18, AceOfCommand = 0x28, WingOfCommand = 0x30;

    private delegate* unmanaged<nint, nint> _wingOfAce, _aceOfWing;
    private delegate* unmanaged<nint, nint, nint, byte> _canAssign;
    private int _aces, _alive, _aceDay = -1;

    private void LoadAces()
    {
        _aces = *(int*)(Sig.Find(Signatures.CountryAces) + 3);
        _wingOfAce = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.WingOfAce);
        _aceOfWing = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.AceOfWing);
        var canAssign = Sig.Find(Signatures.CanAssignAce);
        _alive = *(int*)(canAssign + 8);
        _canAssign = (delegate* unmanaged<nint, nint, nint, byte>)canAssign;
        _ = Sig.VTable(Signatures.AssignAceCommand) + Sig.FindAll(Game.StrategicAirSig, 2)[0];
        MainThread.Every(TimeSpan.FromSeconds(1), AutoAces);
    }

    private void AutoAces()
    {
        if (!Game.HasStarted) return;
        var day = Game.Hour / 24;
        if (day == _aceDay) return;
        _aceDay = day;
        var player = Game.ControlledTag;
        foreach (var country in Game.Countries)
        {
            var tag = Game.Tag(country);
            var ours = tag == player;
            if (!ours && Multiplayer) continue; // other players' aces are theirs
            var idle = new List<nint>();
            foreach (var ace in Native.PdxArray(country + _aces))
            {
                if (ace == 0) continue;
                var wing = _wingOfAce(ace);
                if (*(byte*)(ace + _alive) == 0)
                {
                    if (wing != 0 && PostAce(0, wing, !ours)) Log.Info($"#{tag}: a fallen ace is taken off its air wing");
                }
                else if (wing == 0 && ours) idle.Add(ace);
            }
            if (idle.Count > 0) AssignAces(tag, idle);
        }
    }

    /// <summary>The idle aces onto wings without a living ace: wings on a mission first, then the biggest.</summary>
    private void AssignAces(int tag, List<nint> idle)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return;
        var free = new List<nint>();
        foreach (var pool in Native.PdxArray(air + PoolsOfAir))
        foreach (var wing in Native.PdxArray(pool + WingsOfPool))
        {
            var ace = _aceOfWing(wing);
            if (ace == 0 || *(byte*)(ace + _alive) == 0) free.Add(wing);
        }
        free = [.. free.OrderByDescending(w => *(uint*)(w + MissionMask) is not (0 or Exercise)).ThenByDescending(w => *(int*)(w + Planes))];
        foreach (var ace in idle)
        {
            var wing = free.FirstOrDefault(w => _canAssign(ace, w, 0) != 0);
            if (wing == 0) continue;
            free.Remove(wing);
            if (PostAce(ace, wing, false)) Log.Info($"an ace joins an air wing of {*(int*)(wing + Planes)} planes");
        }
    }

    private static bool PostAce(nint ace, nint wing, bool force)
    {
        var cmd = Commands.New(Signatures.AssignAceCommand, AceSize, 0, 0); // no ace: an all-zero ref, which GetAce reads as none
        if (ace != 0) *(long*)(cmd + AceOfCommand) = *(long*)(ace + AceRef);
        *(long*)(cmd + WingOfCommand) = *(long*)(wing + WingRef);
        return Commands.TryPost(cmd, force);
    }
}
