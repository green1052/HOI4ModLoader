using System.Collections.Concurrent;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Air training: green wings the air AI would send where nothing happens fly the training mission instead ----

    private const int MissionsOfWing = 0xA0, DestinationOfWing = 0x48, AttachedArmyOfWing = 0x9B8; // CAirWing: mission bits, transfer target CRef, army
    private const int PoolsOfAir = 0x58, WingsOfPool = 0x28;                                        // CStrategicAir: wing pools; their wings
    private const int DiplomacyOfCountry = 0xF88;
    private const int TrainingMission = 0x800, SetMissionSize = 0x48, StopAtMaxOfSetMission = 0x46;
    private const int AirSuperiorityIndex = 0, InterceptionIndex = 2; // mission indices (bit = 1 << index)
    private const int TrainedLevel = 2;                                // AIR_WING_XP_LEVELS reached: 2 = past the second (300, training's cap)
    private const int MaxTrainingPercent = 50;                         // of a country's wings on training at once

    [Function(CallingConventions.Microsoft)] public delegate byte AssignWingFn(nint wing, nint airbase, nint region, int mission, byte cancelTransfer);

    private IHook<AssignWingFn>? _assignWing;
    private static delegate* unmanaged<nint, nint, int, byte, byte, nint> _setMission;
    private static delegate* unmanaged<nint, byte> _canTrain;
    private static delegate* unmanaged<nint, nint> _baseOf;
    private static delegate* unmanaged<nint, int> _xpLevel;
    private static delegate* unmanaged<nint, int, byte> _atWarAny;
    private static delegate* unmanaged<nint, int*, byte> _postAi;
    private static readonly ConcurrentDictionary<int, (int Day, int Count)> TrainingPosted = new(); // not yet in the wings' bits
    private static int _training;

    private void LoadAirTraining()
    {
        _ = Sig.Find(AirRegions.RegionAirDataSig) + Sig.FindAll(Game.StrategicAirSig, 2)[0]; // the superiority records Quiet reads
        _setMission = (delegate* unmanaged<nint, nint, int, byte, byte, nint>)Sig.Find(Signatures.SetMissionCommand);
        _canTrain = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.CanTrain);
        _baseOf = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.AirBaseOfWing);
        _xpLevel = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.WingXpLevel);
        _atWarAny = (delegate* unmanaged<nint, int, byte>)Sig.Find(Signatures.AtWarAny);
        _postAi = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.PostAiCommand);
        Allocator.Free(Allocator.AllocZeroed(SetMissionSize)); // resolves the heap functions here, not racing on AI threads
        _assignWing = Patches.Hook<AssignWingFn>(Sig.Find(Signatures.AssignWing), (wing, airbase, region, mission, cancelTransfer) =>
        {
            var train = false;
            Safely(() => train = Train(wing, airbase, region, mission));
            return train ? (byte)1 : _assignWing!.OriginalFunction(wing, airbase, region, mission, cancelTransfer);
        });
    }

    /// <summary>
    /// Whether the wing the air AI is sending on <paramref name="mission"/> in <paramref name="region"/> trains instead (posting
    /// the training mission unless it flies it already): an AI wing staying at its base that can still gain from training and
    /// hasn't passed the second experience level, at peace or (at war) a fighter patrol over a land region no enemy plane flies
    /// in, with at most half of the country's wings training. A wing the AI moves to another base goes (its base may be lost);
    /// past the level the AI's own order clears training. AI worker thread.
    /// </summary>
    private static bool Train(nint wing, nint airbase, nint region, int mission)
    {
        if (mission < 0 || region == 0 || *(long*)(wing + DestinationOfWing) != 0 || *(nint*)(wing + AttachedArmyOfWing) != 0) return false;
        if (airbase == 0 || _baseOf(wing) != airbase) return false;
        if (_canTrain(wing) == 0 || _xpLevel(wing) >= TrainedLevel) return false;
        var tag = WingOwner(wing);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        var country = Game.Country(tag);
        var diplomacy = country == 0 ? 0 : *(nint*)(country + DiplomacyOfCountry);
        if (diplomacy == 0) return false;
        if (_atWarAny(diplomacy, 0) != 0 && !(mission is AirSuperiorityIndex or InterceptionIndex && LandRegion(region) && Quiet(tag, region))) return false;
        if ((*(int*)(wing + MissionsOfWing) & TrainingMission) != 0) return true; // already training: kept
        var day = Game.Hour / 24;
        if (!RoomToTrain(tag, day)) return false;
        var cmd = Allocator.AllocZeroed(SetMissionSize);
        _setMission(cmd, wing, TrainingMission, 1, 1); // as the AI's own training pass sends it
        *(byte*)(cmd + StopAtMaxOfSetMission) = 1;
        if (!Commands.CanExecute(cmd))
        {
            Commands.Free(cmd);
            return false;
        }
        if (_postAi(cmd, &tag) == 0) return false; // deleted by PostAICommand
        TrainingPosted.AddOrUpdate(tag, (day, 1), (_, sent) => sent.Day == day ? (day, sent.Count + 1) : (day, 1));
        Interlocked.Increment(ref _training);
        return true;
    }

    /// <summary>No enemy air superiority in the region, by the country's own air records.</summary>
    private static bool Quiet(int tag, nint region)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return false;
        var id = *(int*)(region + SuperiorityIdOfRegion);
        if (id <= 0 || id >= Native.PdxArray<byte>(air + SuperiorityRecords).Length) return false;
        var record = *(nint*)(air + SuperiorityRecords) + (nint)id * SuperiorityRecordSize;
        return *(long*)(record + EnemySuperiority) <= 0;
    }

    /// <summary>Fewer than half of the country's wings train, counting training posted today (it shows in the bits once run).</summary>
    private static bool RoomToTrain(int tag, int day)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return false;
        int wings = 0, training = TrainingPosted.TryGetValue(tag, out var sent) && sent.Day == day ? sent.Count : 0;
        foreach (var pool in Native.PdxArray(air + PoolsOfAir))
        {
            if (pool == 0) continue;
            foreach (var w in Native.PdxArray(pool + WingsOfPool))
            {
                if (w == 0) continue;
                wings++;
                if ((*(int*)(w + MissionsOfWing) & TrainingMission) != 0) training++;
            }
        }
        return (training + 1) * 100 <= wings * MaxTrainingPercent;
    }
}
