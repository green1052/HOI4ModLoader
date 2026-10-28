using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Battle odds: an AI offensive the game's combat sums say it loses doesn't start ----

    // CSubUnitDefinition at CArmy+0x138: the division's current stats, fixed point 1e5.
    private const int DefenseOfStats = 0xA8, BreakthroughOfStats = 0xB0, HardnessOfStats = 0xB8, SoftAttackOfStats = 0xC0,
                      HardAttackOfStats = 0xC8, MaxOrgOfStats = 0x280, ArmorOfStats = 0x298, PiercingOfStats = 0x2A0;

    // NDefines globals by RVA (hoi4.exe 1.19.4, uniform +0xAFCB8 shift of the 1.19.3 defines block, verified on the ground-crews
    // cost define) that CCombatLand reads. Read on use: Lua fills them after plugins load.
    // An implausible value falls back to vanilla's.
    // shortcut: if 1.19.4 inserted defines mid-block these five are wrong; zeros fall back to vanilla odds, so verify in IDA then
    private const int BaseAvoidRva = 0x33E57E0, NoDefAvoidRva = 0x33E57F0, FortPenaltyRva = 0x33E6418, RiverRva = 0x33E5F98, LargeRiverRva = 0x33E6048;
    private static double BaseAvoid => Define(BaseAvoidRva, 1, 100, 90);
    private static double NoDefAvoid => Define(NoDefAvoidRva, 1, 100, 60);
    private static double FortPenalty => Define(FortPenaltyRva, -1, -0.001, -0.15);
    private static double River => Define(RiverRva, -1, -0.001, -0.3);
    private static double LargeRiver => Define(LargeRiverRva, -1, -0.001, -0.6);
    private const double ArmorPenalty = 0.5; // damage factor when piercing is under the target's armor
    private const int LosingOddsPercent = 100; // an attack is lost when breaking them takes longer than this % of breaking us
    private static readonly nint Image = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;

    private static double Define(int rva, double min, double max, double vanilla)
    {
        var value = *(long*)(Image + rva) / (double)Native.FixedPoint;
        return value >= min && value <= max ? value : vanilla;
    }

    /// <summary>
    /// Why the order's divisions shouldn't attack, or null if they may. Each manned enemy province next to them is one attack:
    /// it is lost if their organisation outlasts ours, as land combat deals it (<see cref="HoursToBreak"/>; forts and rivers
    /// cut our attack). Held when more attacks are lost than won. Unlike power, this counts quality. AI threads, read only.
    /// </summary>
    private static string? PredictedLoss(ReadOnlySpan<nint> members, int tag)
    {
        var country = Game.Country(tag);
        if (country == 0) return null;
        var attackers = new Dictionary<nint, List<nint>>(); // enemy province -> our divisions next to it
        foreach (var member in members)
        {
            var unit = member - MemberOfUnit;
            var at = Division.Location(unit);
            if (at == 0 || Division.Stats(unit) == 0) continue;
            foreach (var id in Game.Neighbours(at))
                if (Game.Province(id) is var p && p != 0 && Province.Divisions(p) > 0 && Province.Enemy(country, tag, p))
                {
                    if (!attackers.TryGetValue(p, out var list)) attackers[p] = list = [];
                    list.Add(unit);
                }
        }
        int lost = 0, won = 0;
        foreach (var (target, ours) in attackers)
        {
            var theirs = new List<nint>();
            foreach (var unit in Province.Units(target))
                if (unit != 0 && Division.IsLand(unit) && Division.Stats(unit) != 0 && Game.AtWar(country, Division.Commander(unit))) theirs.Add(unit);
            if (theirs.Count == 0) continue;
            var from = Division.Location(ours[0]);
            var modifier = 1 + Province.FortLevel(target) * FortPenalty
                + (Game.AdjacencyType(from, Game.Id(target)) switch { Province.River => River, Province.LargeRiver => LargeRiver, _ => 0 });
            var hoursTheirs = HoursToBreak(ours, theirs, Math.Max(0.1, modifier), defenderHolds: true);
            var hoursOurs = HoursToBreak(theirs, ours, 1, defenderHolds: false);
            if (hoursTheirs * 100 > hoursOurs * LosingOddsPercent) lost++;
            else won++;
        }
        if (lost == 0 || lost <= won) return null;
        return $"the battle odds say it loses {lost} of its {lost + won} attack(s)";
    }

    /// <summary>
    /// Hours until <paramref name="targets"/>' organisation runs out under <paramref name="hitters"/>' attacks. Attacks up to
    /// the targets' defense (<paramref name="defenderHolds"/>) or breakthrough hit at the base avoid chance, the rest at the
    /// no-defense one; half damage when piercing is under armor.
    /// </summary>
    private static double HoursToBreak(List<nint> hitters, List<nint> targets, double attackModifier, bool defenderHolds)
    {
        double hardness = 0, armor = 0, piercing = 0, soak = 0, org = 0;
        foreach (var t in targets)
        {
            var s = Division.Stats(t);
            hardness += *(long*)(s + HardnessOfStats) / (double)Native.FixedPoint;
            armor += *(long*)(s + ArmorOfStats) / (double)Native.FixedPoint;
            soak += 0.1 * *(long*)(s + (defenderHolds ? DefenseOfStats : BreakthroughOfStats)) / Native.FixedPoint;
            org += Division.Organisation(t) / (double)Native.FixedPoint * *(long*)(s + MaxOrgOfStats) / Native.FixedPoint;
        }
        hardness /= targets.Count;
        armor /= targets.Count;
        double attacks = 0, strength = 0;
        foreach (var h in hitters)
        {
            var s = Division.Stats(h);
            attacks += 0.1 * (*(long*)(s + SoftAttackOfStats) * (1 - hardness) + *(long*)(s + HardAttackOfStats) * hardness) / Native.FixedPoint;
            piercing += *(long*)(s + PiercingOfStats) / (double)Native.FixedPoint;
            strength += Division.Strength(h) / (double)Native.FixedPoint;
        }
        attacks *= attackModifier;
        piercing /= hitters.Count;
        var hits = Math.Min(attacks, soak) * (1 - BaseAvoid / 100) + Math.Max(0, attacks - soak) * (1 - NoDefAvoid / 100);
        var damage = hits * (strength / hitters.Count) * (piercing >= armor ? 1 : ArmorPenalty);
        return damage <= 0 ? double.PositiveInfinity : org / damage;
    }
}
