using System.Collections.Concurrent;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Air training: green wings the AI would send where nothing happens train instead ----

    private const int MissionsOfWing = 0xA0, DestinationOfWing = 0x48, AttachedArmyOfWing = 0x9B8; // CAirWing: mission bits, transfer target CRef, army
    private const int PoolsOfAir = 0x58, WingsOfPool = 0x28;                                        // CStrategicAir: wing pools; their wings
    private const int DiplomacyOfCountry = 0xF88;
    private const int TrainingMission = 0x800, SetMissionSize = 0x48, StopAtMaxOfSetMission = 0x46;
    private const int AirSuperiorityIndex = 0, InterceptionIndex = 2; // mission indices (bit = 1 << index)
    private const int TrainedLevel = 2;                                // AIR_WING_XP_LEVELS reached; 2 = training's cap (300)
    private const int MaxTrainingPercent = 50;                         // of a country's wings training at once

    [Function(CallingConventions.Microsoft)] public delegate byte AssignWingFn(nint wing, nint airbase, nint region, int mission, byte cancelTransfer);

    private IHook<AssignWingFn>? _assignWing;
    private static delegate* unmanaged<nint, nint, int, byte, byte, nint> _setMission;
    private static delegate* unmanaged<nint, byte> _canTrain;
    private static delegate* unmanaged<nint, nint> _baseOf;
    private static delegate* unmanaged<nint, int> _xpLevel;
    private static delegate* unmanaged<nint, int, byte> _atWarAny;
    private static delegate* unmanaged<nint, int*, byte> _postAi;
    private static readonly ConcurrentDictionary<int, (int Day, int Base, int Count)> TrainingPosted = new(); // today: wings training at its first look, posted since
    private static int _training, _keptBase;
    private const int BackAfterHours = 7 * 24;
    private static readonly ConcurrentDictionary<nint, (nint From, int Hour)> Moved = new(); // wing -> base it last left, hour

    private void LoadAirTraining()
    {
        _ = Sig.Find(AirRegions.RegionAirDataSig) + Sig.FindAll(Game.StrategicAirSig, 2)[0]; // the functions the superiority offsets come from
        _setMission = (delegate* unmanaged<nint, nint, int, byte, byte, nint>)Sig.Find(Signatures.SetMissionCommand);
        _canTrain = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.CanTrain);
        _baseOf = (delegate* unmanaged<nint, nint>)Sig.Find(Signatures.AirBaseOfWing);
        _xpLevel = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.WingXpLevel);
        _atWarAny = (delegate* unmanaged<nint, int, byte>)Sig.Find(Signatures.AtWarAny);
        _postAi = (delegate* unmanaged<nint, int*, byte>)Sig.Find(Signatures.PostAiCommand);
        Allocator.Resolve(); // the heap functions are found here, not on AI threads
        _assignWing = Patches.Hook<AssignWingFn>(Sig.Find(Signatures.AssignWing), (wing, airbase, region, mission, cancelTransfer) =>
        {
            // No closure (Safely's): every wing the AI assigns, on its threads.
            var train = false;
            var keep = false;
            if (SheepMod.Active)
            {
                try { train = Train(wing, airbase, region, mission); }
                catch (Exception e) { Failed(e); }
                if (!train)
                    try { keep = MovingBack(wing, airbase); }
                    catch (Exception e) { Failed(e); }
            }
            return train || keep ? (byte)1 : _assignWing!.OriginalFunction(wing, airbase, region, mission, cancelTransfer);
        });
        MainThread.Every(TimeSpan.FromSeconds(1), TrainIdleWings);
    }

    private const int StateOfAirBase = 0x68; // CAirBase: its state; none for a carrier's

    // 1.19.4 hands back base pointers that are no longer readable (it crashed the game); such a wing counts as a carrier's and is left alone.
    private static bool OnCarrier(nint airbase) =>
        airbase != 0 && (!Native.Readable(airbase + StateOfAirBase) || *(nint*)(airbase + StateOfAirBase) == 0);

    private const int IdleWingsHours = 24;
    private static int _idleWingsAt;

    /// <summary>
    /// Daily: green AI wings with no mission go on training, within half of a country's wings. Wings the AI sends
    /// somewhere go through <see cref="Train"/>. Main thread.
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
            var room = TrainingRoom(tag, day); // once per country: it walks every wing
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
    /// Whether the wing the AI is sending on <paramref name="mission"/> in <paramref name="region"/> trains instead (posts the
    /// training mission unless already on it): a green AI wing that can train, at peace, or at war a fighter staying at its
    /// base on patrol over a quiet land region; at most half the country's wings. AI worker thread.
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
        // At war a wing being moved goes (its base may be lost); at peace the AI moves idle wings for nothing.
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
        TrainingPosted.AddOrUpdate(tag, static (_, d) => (d, 0, 1), static (_, sent, d) => sent.Day == d ? (d, sent.Base, sent.Count + 1) : (d, 0, 1), day);
        Interlocked.Increment(ref _training);
    }

    /// <summary>
    /// No enemy air superiority in the region, by the country's air records, and none of its divisions fighting there
    /// (its battles need air cover even with no enemy planes).
    /// </summary>
    private static bool Quiet(int tag, nint region)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return false;
        var country = Game.Country(tag);
        foreach (var unit in country == 0 ? [] : Game.Units(country))
            if (unit != 0 && Division.IsLand(unit) && Division.Fighting(unit) && Division.Location(unit) is var at && at != 0 && Province.StrategicRegion(at) == region)
                return false;
        return AirRegions.Superiority(air, region) is { Enemy: <= 0 };
    }

    /// <summary>
    /// Refuses a move back to the base the wing left under a week ago (the AI flips wings between two bases daily); the wing
    /// keeps its mission where it is. Other moves are remembered. AI worker thread.
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
    /// How many more of the country's wings may train under MaxTrainingPercent. Counts wings with the training bit or those
    /// training at the day's first look plus those posted since, whichever is more (a posted mission shows in the bits once
    /// run, and an AI pass posts many before any does).
    /// </summary>
    private static int TrainingRoom(int tag, int day)
    {
        var air = Game.StrategicAir(tag);
        if (air == 0) return 0;
        int wings = 0, training = 0;
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
        if (!TrainingPosted.TryGetValue(tag, out var sent) || sent.Day != day) TrainingPosted[tag] = sent = (day, training, 0);
        return wings * MaxTrainingPercent / 100 - Math.Max(training, sent.Base + sent.Count);
    }
}
