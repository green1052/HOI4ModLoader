using HOI4ModLoader;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>CAirBase: provinces of a strategic region in range (CPdxArray* outOrNull, region, base, long range, int maxCount) -> int: their count.</summary>
    [Signature] public const string ProvincesInRange = "48 89 5C 24 ? 55 56 57 41 56 41 57 48 83 EC 50 41 83 78";

    /// <summary>CAirWing::GetRange(wing, long* out) -> out (map pixels, fixed point).</summary>
    [Signature] public const string GetRange = "48 89 5C 24 ? 48 89 6C 24 ? 56 57 41 56 48 83 EC 20 4C 8B 05";

    /// <summary>CAirBase::GetFreeCapacityByMaxSize(base) -> int: capacity left, counting every pool there and every wing on its way.</summary>
    [Signature] public const string FreeCapacity = "48 89 5C 24 ? 56 57 41 56 48 83 EC 20 4C 89 7C 24";

    /// <summary>CAirWing::GetMaxSize(wing) -> int: its full plane count.</summary>
    [Signature] public const string MaxSize = "40 53 48 83 EC 30 80 3D ? ? ? ? ? 48 89 74 24";

    /// <summary>CAirWing::GetAirBase(wing) -> CAirBase*: where it is, or is flying to.</summary>
    /// <summary>CAirWing::GetAirBase (vtable slot 2): the base the wing is at or flying to. CRef&lt;CAirBase&gt; at [wing+0x38]+0x18.</summary>
    [Signature] public const string AirBaseOfWing = "48 89 5C 24 08 57 48 83 EC 20 48 8B D9 48 8B 0D ? ? ? ? 48 8B 01 FF 90 B8 00 00 00 48 8B 4B 38 48 8B F8 48 83 C1 18 83 39 00 75 0A 83 79 04 00 75 04 33 C0 EB 05 E8 ? ? ? ? 45 33 C0";

    /// <summary>Squared map distance from a strategic region to a state (region, CState*) -> int.</summary>
    [Signature] public const string RegionToState = "48 83 EC 28 4C 8B 42 ? 48 8B 51 ? 48 8D 4C 24 ? 41 8B 80 ? ? ? ? 2B 82 E0 00 00 00";

    /// <summary>
    /// CStratAirTransferCommand ctor (mem[0x48], wing, base, bool returnHome, CStrategicRegion* region, int missionMask):
    /// flies the wing over and resumes the mission, as the game's own rebasing does.
    /// </summary>
    [Signature] public const string TransferCommand = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 41 0F B6 E9 49 8B F0 48 8B D9";
}

/// <summary>
/// Auto rebase: a wing on a mission covering less than Coverage of its region flies to the land airbase with room that
/// covers more (closest on a tie), keeping its mission. Uses the game's transfer command, so multiplayer works. Wings
/// attached to an army (the game rebases those) and carriers are skipped.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int BasesOfAir = 0x10, PoolsOfAir = 0x58, WingsOfPool = 0x28;
    private const int MissionMask = 0xA0, MissionRegion = 0xC0, Coverage = 0xE0, AttachedArmy = 0x9B8;
    private const int StateOfBase = 0x68;
    private const uint Exercise = 0x800;

    private delegate* unmanaged<nint, nint, nint, long, int, int> _inRange;
    private delegate* unmanaged<nint, long*, long*> _range;
    private delegate* unmanaged<nint, int> _freeCapacity, _maxSize;
    private delegate* unmanaged<nint, nint> _baseOf;
    private delegate* unmanaged<nint, nint, int> _regionToState;
    private delegate* unmanaged<nint, nint, nint, byte, nint, int, nint> _transfer;
    private long _coverage;

    private void LoadRebase(int coverage)
    {
        _coverage = coverage * Native.FixedPoint / 100;
        ResolveAir();
        MainThread.Every(TimeSpan.FromSeconds(10), Rebase);
    }

    /// <summary>The air functions rebasing and paradrop transports share.</summary>
    private void ResolveAir()
    {
        if (_transfer != null) return;
        _ = Sig.FindAll(Game.StrategicAirSig, 2);
        _inRange = (delegate* unmanaged<nint, nint, nint, long, int, int>)Sig.Find(Signatures.ProvincesInRange);
        _range = (delegate* unmanaged<nint, long*, long*>)Sig.Find(Signatures.GetRange);
        _freeCapacity = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FreeCapacity);
        _maxSize = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.MaxSize);
        _baseOf = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.AirBaseOfWing);
        _regionToState = (delegate* unmanaged<nint, nint, int>)Sig.Find(Signatures.RegionToState);
        _transfer = (delegate* unmanaged<nint, nint, nint, byte, nint, int, nint>)Sig.Find(Signatures.TransferCommand);
    }

    private void Rebase()
    {
        var air = Game.HasStarted ? Game.StrategicAir(Game.ControlledTag) : 0;
        if (air == 0) return;
        var free = new Dictionary<nint, int>();
        foreach (var b in Native.PdxArray(air + BasesOfAir))
            if (*(nint*)(b + StateOfBase) != 0) free[b] = _freeCapacity(b); // land bases, as the game rebases
        var moved = 0;
        foreach (var pool in Native.PdxArray(air + PoolsOfAir))
        foreach (var wing in Native.PdxArray(pool + WingsOfPool))
        {
            var region = *(nint*)(wing + MissionRegion);
            var mask = *(uint*)(wing + MissionMask);
            if (region == 0 || mask == 0 || (mask & Exercise) != 0 || *(nint*)(wing + AttachedArmy) != 0 || *(long*)(wing + Destination) != 0) continue;
            if (*(long*)(wing + Coverage) >= _coverage) continue;
            var current = _baseOf(wing);
            if (current == 0 || *(nint*)(current + StateOfBase) == 0) continue; // on a carrier
            long rangeOut;
            var range = *_range(wing, &rangeOut);
            var reached = _inRange(0, region, current, range, 0);
            var size = _maxSize(wing);
            var best = free.Where(b => b.Key != current && b.Value >= size)
                .Select(b => (Base: b.Key, Reached: _inRange(0, region, b.Key, range, 0), Distance: _regionToState(region, *(nint*)(b.Key + StateOfBase))))
                .Where(b => b.Reached > reached) // strictly better: no back and forth
                .OrderByDescending(b => b.Reached).ThenBy(b => b.Distance)
                .FirstOrDefault();
            if (best.Base == 0) continue;
            var cmd = Allocator.AllocZeroed(0x48);
            _transfer(cmd, wing, best.Base, 0, region, (int)mask); // keeps the mission on arrival
            if (!Commands.TryPost(cmd)) continue;
            free[best.Base] -= size;
            moved++;
        }
        if (moved > 0) Log.Info($"rebased {moved} air wing(s) closer to their mission regions");
    }
}
