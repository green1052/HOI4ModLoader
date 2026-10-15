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
    private static int _training, _keptBase;
    private const int BackAfterHours = 7 * 24;
    private static readonly ConcurrentDictionary<nint, (nint From, int Hour)> Moved = new(); // wing: the base it last left, when

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
            var keep = false;
            Safely(() => train = Train(wing, airbase, region, mission));
            if (!train) Safely(() => keep = MovingBack(wing, airbase));
            return train || keep ? (byte)1 : _assignWing!.OriginalFunction(wing, airbase, region, mission, cancelTransfer);
        });
        MainThread.Every(TimeSpan.FromSeconds(1), TrainIdleWings);
    }

    private const int StateOfAirBase = 0x68; // CAirBase: its state; none for a carrier's

    private static bool OnCarrier(nint airbase) => airbase != 0 && *(nint*)(airbase + StateOfAirBase) == 0;

    private const int IdleWingsHours = 24;
    private static int _idleWingsAt;

    /// <summary>
    /// Daily: green AI wings with no mission at all (the air AI found none for them: Germany's naval bombers at war sat
    /// waiting) go on training, within the half of a country's wings. Wings the AI sends somewhere go through
    /// <see cref="Train"/> instead. Main thread.
    /// </summary>
    private static void TrainIdleWings()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _idleWingsAt, IdleWingsHours)) return;
        var (day, player) = (Game.Hour / 24, Game.ControlledTag);
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            var air = tag <= 0 || tag == player ? 0 : Game.StrategicAir(tag);
            if (air == 0) continue;
            var room = TrainingRoom(tag, day); // counted once a country: per wing it walked every wing again
            foreach (var pool in Native.PdxArray(air + PoolsOfAir).ToArray())
            {
                if (pool == 0) continue;
                foreach (var wing in Native.PdxArray(pool + WingsOfPool).ToArray())
                {
                    if (wing == 0 || *(int*)(wing + MissionsOfWing) != 0 || *(long*)(wing + DestinationOfWing) != 0 || *(nint*)(wing + AttachedArmyOfWing) != 0) continue;
                    if (OnCarrier(_baseOf(wing))) continue; // a carrier's wings fly with it
                    if (room <= 0) break;
                    if (_canTrain(wing) == 0 || _xpLevel(wing) >= TrainedLevel) continue;
                    var cmd = TrainingCommand(wing);
                    if (!Commands.CanExecute(cmd))
                    {
                        Commands.Free(cmd);
                        continue;
                    }
                    if (!Commands.Post(cmd, true)) continue;
                    CountTraining(tag, day);
                    room--;
                }
            }
        }
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
        if (mission < 0 || region == 0 || *(nint*)(wing + AttachedArmyOfWing) != 0) return false;
        if (_canTrain(wing) == 0 || _xpLevel(wing) >= TrainedLevel || OnCarrier(_baseOf(wing))) return false; // a carrier's wings fly its missions
        var tag = WingOwner(wing);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        var country = Game.Country(tag);
        var diplomacy = country == 0 ? 0 : *(nint*)(country + DiplomacyOfCountry);
        if (diplomacy == 0) return false;
        var atWar = _atWarAny(diplomacy, 0) != 0;
        // At war only a wing staying at its base trains (one being moved goes: its base may be lost); at peace any: the AI
        // shuffles idle wings between bases with nothing to do, and they sat waiting (Italy after Ethiopia).
        if (atWar && (*(long*)(wing + DestinationOfWing) != 0 || airbase == 0 || _baseOf(wing) != airbase)) return false;
        if (atWar && !(mission is AirSuperiorityIndex or InterceptionIndex && LandRegion(region) && Quiet(tag, region))) return false;
        if ((*(int*)(wing + MissionsOfWing) & TrainingMission) != 0) return true; // already training: kept
        var day = Game.Hour / 24;
        if (TrainingRoom(tag, day) <= 0 || !Post(TrainingCommand(wing), tag)) return false;
        CountTraining(tag, day);
        return true;
    }

    /// <summary>The training mission command for the wing, as the AI's own training pass sends it (stop at max XP).</summary>
    private static nint TrainingCommand(nint wing)
    {
        var cmd = Allocator.AllocZeroed(SetMissionSize);
        _setMission(cmd, wing, TrainingMission, 1, 1);
        *(byte*)(cmd + StopAtMaxOfSetMission) = 1;
        return cmd;
    }

    /// <summary>A training mission posted today: counted until it shows in the wing's bits (<see cref="TrainingRoom"/>).</summary>
    private static void CountTraining(int tag, int day)
    {
        TrainingPosted.AddOrUpdate(tag, (day, 1), (_, sent) => sent.Day == day ? (day, sent.Count + 1) : (day, 1));
        Interlocked.Increment(ref _training);
    }

    /// <summary>
    /// No enemy air superiority in the region, by the country's own air records, and none of its divisions fighting there:
    /// over its own battles air superiority still counts (the enemy fights under it) even with no enemy planes, as over
    /// Italy's offensive in Ethiopia.
    /// </summary>
    private static bool Quiet(int tag, nint region)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return false;
        var country = Game.Country(tag);
        foreach (var unit in country == 0 ? [] : Game.Units(country))
            if (unit != 0 && Division.IsLand(unit) && Division.Fighting(unit) && Division.Location(unit) is var at && at != 0 && *(nint*)(at + RegionOfProvince) == region)
                return false;
        var id = *(int*)(region + SuperiorityIdOfRegion);
        if (id <= 0 || id >= Native.PdxArray<byte>(air + SuperiorityRecords).Length) return false;
        var record = *(nint*)(air + SuperiorityRecords) + (nint)id * SuperiorityRecordSize;
        return *(long*)(record + EnemySuperiority) <= 0;
    }

    /// <summary>
    /// Whether the AI sends the wing back to the base it left less than a week ago: refused, the wing keeps its mission where it
    /// is. The air AI's daily pass flips wings between two bases (each move frees room at the base left, which then scores
    /// best), so they spent their days in transit. Otherwise a move is remembered. AI worker thread.
    /// </summary>
    private static bool MovingBack(nint wing, nint airbase)
    {
        var from = airbase == 0 ? 0 : _baseOf(wing);
        if (from == 0 || from == airbase || OnCarrier(from) || OnCarrier(airbase)) return false; // carriers move: their wings go where they are
        var tag = WingOwner(wing);
        if (tag <= 0 || tag == Game.ControlledTag) return false;
        var hour = Game.Hour;
        if (Moved.Count > 4096) foreach (var (w, m) in Moved) if (hour - m.Hour is < 0 or >= BackAfterHours) Moved.TryRemove(w, out _); // wings come and go
        if (Moved.TryGetValue(wing, out var last) && last.From == airbase && hour - last.Hour is >= 0 and < BackAfterHours)
        {
            Interlocked.Increment(ref _keptBase);
            return true;
        }
        Moved[wing] = (from, hour);
        return false;
    }

    /// <summary>
    /// How many more of the country's wings may go on training under MaxTrainingPercent: those with the training bit, or the
    /// ones posted today if more (a posted mission shows in the bits once run; adding both counted it twice).
    /// </summary>
    private static int TrainingRoom(int tag, int day)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return 0;
        int wings = 0, training = 0, posted = TrainingPosted.TryGetValue(tag, out var sent) && sent.Day == day ? sent.Count : 0;
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
        return wings * MaxTrainingPercent / 100 - Math.Max(training, posted);
    }
}
