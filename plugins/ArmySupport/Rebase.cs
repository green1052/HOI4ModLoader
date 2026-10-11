using HOI4ModLoader;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>
    /// GetStrategicAirForCountry (twice, identical): `mov rbx,[rax+690h]; test rbx,rbx; jz; cmp dword [rbx+3Ch],0; ...;
    /// mov rax,[rbx+30h]; mov rax,[rax+rcx*8]`: the per-country air (+0x10 usable airbases, +0x58 wing pools) by tag index.
    /// </summary>
    [Signature(2)] public const string StrategicAir = "48 8B 98 90 06 00 00 48 85 DB 74 ? 83 7B ? ? 74 ? 83 7F ? ? 48 8D 4F ? 75 ? 33 C0 48 63 C8 48 8B 43 ? 48 8B 04 C8";

    /// <summary>CAirBase: provinces of a strategic region in range (CPdxArray* outOrNull, region, base, long range, int maxCount) -> int.</summary>
    [Signature] public const string ProvincesInRange = "48 89 5C 24 ? 55 56 57 41 56 41 57 48 83 EC 50 41 83 78";

    /// <summary>CAirWing::GetRange(wing, long* out) -> out (map pixels, fixed point).</summary>
    [Signature] public const string GetRange = "48 89 5C 24 ? 48 89 6C 24 ? 56 57 41 56 48 83 EC 20 4C 8B 05";

    /// <summary>CAirBase::GetFreeCapacityByMaxSize(base) -> int: capacity left, counting every pool there and every wing on its way.</summary>
    [Signature] public const string FreeCapacity = "48 89 5C 24 ? 56 57 41 56 48 83 EC 20 4C 89 7C 24";

    /// <summary>CAirWing::GetMaxSize(wing) -> int: its full plane count.</summary>
    [Signature] public const string MaxSize = "40 53 48 83 EC 30 80 3D ? ? ? ? ? 48 89 74 24";

    /// <summary>CAirWing::GetAirBase(wing) -> CAirBase*: where it is, or is flying to.</summary>
    [Signature] public const string AirBaseOfWing = "40 53 48 83 EC 20 48 8B D9 48 83 C1 48 83 39 00 75 ? 83 79 ? ? 74 ? E8 ? ? ? ? 48 85 C0 75 ? 48 8B 4B ? 48 83 C1 18 83 39 00 75 ? 83 79 ? ? 75 ? 33 C0 48 83 C4 20 5B C3";

    /// <summary>Squared map distance from a strategic region to a state (region, CState*) -> int.</summary>
    [Signature] public const string RegionToState = "48 83 EC 28 4C 8B 42 ? 48 8B 51 ? 48 8D 4C 24 ? 41 8B 80 ? ? ? ? 2B 82 E0 00 00 00";

    /// <summary>
    /// CStratAirTransferCommand ctor (mem[0x48], wing, base, bool returnHome, CStrategicRegion* region, int missionMask): flies
    /// the wing over and resumes the mission there, as the game's own rebasing does.
    /// </summary>
    [Signature] public const string TransferCommand = "48 89 5C 24 ? 48 89 4C 24 ? 55 56 57 48 83 EC 30 41 0F B6 E9 49 8B F0 48 8B D9";
}

/// <summary>
/// Auto rebase: a wing on a mission reaching less than Coverage of its region from its airbase is flown to the usable
/// airbase with room that reaches more of it (closest to the region on a tie), keeping its mission, with the game's own
/// transfer command (multiplayer works). The game already does this for wings attached to an army; those are left to it,
/// as are carriers.
/// </summary>
public unsafe partial class ArmySupport
{
    // Layouts for HOI4 1.19.3.
    private const int BasesOfAir = 0x10, PoolsOfAir = 0x58, WingsOfPool = 0x28;
    private const int MissionMask = 0xA0, MissionRegion = 0xC0, Coverage = 0xE0, AttachedArmy = 0x9B8;
    private const int StateOfBase = 0x68;
    private const uint Exercise = 0x800;

    private nint _strategicAir;
    private int _holderOffset, _countOffset, _arrayOffset;
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
        _strategicAir = Sig.FindAll(Signatures.StrategicAir, 2)[0];
        (_holderOffset, _countOffset, _arrayOffset) = (*(int*)(_strategicAir + 3), *(byte*)(_strategicAir + 14), *(byte*)(_strategicAir + 36));
        _inRange = (delegate* unmanaged<nint, nint, nint, long, int, int>)Sig.Find(Signatures.ProvincesInRange);
        _range = (delegate* unmanaged<nint, long*, long*>)Sig.Find(Signatures.GetRange);
        _freeCapacity = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FreeCapacity);
        _maxSize = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.MaxSize);
        _baseOf = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.AirBaseOfWing);
        _regionToState = (delegate* unmanaged<nint, nint, int>)Sig.Find(Signatures.RegionToState);
        _transfer = (delegate* unmanaged<nint, nint, nint, byte, nint, int, nint>)Sig.Find(Signatures.TransferCommand);
        MainThread.Every(TimeSpan.FromSeconds(10), Rebase);
    }

    private void Rebase()
    {
        var air = Game.HasStarted ? StrategicAir(Game.ControlledTag) : 0;
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

    private nint StrategicAir(int tag)
    {
        var holder = tag <= 0 || Game.State == 0 ? 0 : *(nint*)(Game.State + _holderOffset);
        if (holder == 0 || *(int*)(holder + _countOffset) == 0) return 0;
        return *(nint*)(*(nint*)(holder + _arrayOffset) + 8 * Game.TagIndex(tag));
    }
}
