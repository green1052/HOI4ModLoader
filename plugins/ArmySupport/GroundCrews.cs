using HOI4ModLoader;

namespace ArmySupport;

/// <summary>
/// Auto ground crews: More Ground Crews (+10 % air efficiency in a region for command power, given back when turned off) goes on
/// where the player's wings fight hardest, two regions at most, and off again where they stopped or command power runs under
/// the reserve. Only the regions it turned on are turned off; one the player turns off by hand is left alone for the game.
/// The player's own commands: multiplayer works.
/// </summary>
public partial class ArmySupport
{
    private const int CrewsHours = 6, CrewsFightHours = 48, CrewsQuietHours = 72, CrewsMinPlanes = 10, CrewsMax = 2;

    private readonly HashSet<int> _crewsOurs = [], _crewsHandsOff = [];
    private int _crewsAt, _crewsWorld = -1;
    private long _crewsReserve;

    private void LoadGroundCrews(int reserve)
    {
        _crewsReserve = reserve * Native.FixedPoint;
        _ = Sig.Find(GroundCrews.CanExecuteSig) + Sig.VTable(GroundCrews.CommandClass) + Sig.FindAll(Game.StrategicAirSig, 2)[0]
            + Sig.Find(AirRegions.RegionAirDataSig) + Sig.Find(AirRegions.RegionCombatDataSig);
        MainThread.Every(TimeSpan.FromSeconds(1), AutoGroundCrews);
    }

    /// <summary>Every six game hours (never while paused).</summary>
    private void AutoGroundCrews()
    {
        var tag = Game.HasStarted ? Game.ControlledTag : 0;
        if (tag == 0) return;
        var hour = Game.Hour;
        if (_crewsWorld != Game.Generation) // another game or country: what was ours isn't
        {
            (_crewsWorld, _crewsAt) = (Game.Generation, hour - CrewsHours);
            _crewsOurs.Clear();
            _crewsHandsOff.Clear();
        }
        if ((uint)(hour - _crewsAt) < CrewsHours) return; // a load to an earlier date runs at once
        _crewsAt = hour;
        var country = Game.Country(tag);
        if (country == 0 || Game.StrategicAir(tag) == 0) return;
        var (on, off) = GroundCrews.Manage(country, _crewsReserve, CrewsMax, hour - CrewsFightHours, hour - CrewsQuietHours, CrewsMinPlanes, false,
            _crewsOurs, _crewsHandsOff);
        if (on.Count > 0) Log.Info($"more ground crews on in air region(s) {string.Join(", ", on)}");
        if (off.Count > 0) Log.Info($"more ground crews off in air region(s) {string.Join(", ", off)}");
    }
}
