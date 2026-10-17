using HOI4ModLoader;

namespace AiAssist;

public partial class AiAssist
{
    // ---- Ground crews: spare command power goes to More Ground Crews (+10 % air efficiency) where the AI's wings fight ----
    //
    // Vanilla's AI never uses them. On costs 20 command power and lowers the cap by as much; off gives it back.
    // The reserve keeps command power for generals' traits.

    private const int GroundCrewsHours = 48, FightHours = 72, QuietHours = 7 * 24, MinAirCombat = 10, MaxGroundCrews = 2;
    private const long GroundCrewsReserve = 50 * Native.FixedPoint; // command power left after the cost
    private static int _groundCrewsAt = Never, _crewsOn, _crewsOff;

    private void LoadGroundCrews()
    {
        _ = Sig.Find(GroundCrews.CanExecuteSig) + Sig.VTable(GroundCrews.CommandClass) + Sig.Find(AirRegions.RegionAirDataSig) + Sig.Find(AirRegions.RegionCombatDataSig);
        MainThread.Every(TimeSpan.FromSeconds(1), ManageGroundCrews);
    }

    /// <summary>Every two game days, per AI country: ground crews on where its wings fought hardest in the last three days (two regions at most); off where they stopped for a week or command power runs low.</summary>
    private void ManageGroundCrews()
    {
        if (!Game.HasStarted || !SheepMod.Active || !Due(ref _groundCrewsAt, GroundCrewsHours)) return;
        var (hour, player) = (Game.Hour, Game.ControlledTag);
        foreach (var country in Game.Countries)
        {
            var tag = country == 0 ? 0 : Game.Tag(country);
            if (tag <= 0 || tag == player || Game.StrategicAir(tag) == 0) continue;
            var (on, off) = GroundCrews.Manage(country, GroundCrewsReserve, MaxGroundCrews, hour - FightHours, hour - QuietHours, MinAirCombat, true);
            Interlocked.Add(ref _crewsOn, on.Count);
            Interlocked.Add(ref _crewsOff, off.Count);
        }
    }
}
