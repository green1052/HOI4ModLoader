using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Battle odds: an AI offensive whose attacks the game's own combat sums say it loses doesn't start ----

    // CSubUnitDefinition at CArmy+0x138 (the division's current values, equipment and doctrine in), fixed point 1e5.
    private const int DefenseOfStats = 0xA8, BreakthroughOfStats = 0xB0, HardnessOfStats = 0xB8, SoftAttackOfStats = 0xC0,
                      HardAttackOfStats = 0xC8, MaxOrgOfStats = 0x280, ArmorOfStats = 0x298, PiercingOfStats = 0x2A0;

    // NDefines globals by RVA in hoi4.exe 1.19.3 (CCombatLand reads them), read when used: the game fills them from its Lua
    // after the plugins load. Vanilla's value where one reads implausibly (zero before that, a mod's nonsense).
    private const int BaseAvoidRva = 0x3335B28, NoDefAvoidRva = 0x3335BD8, FortPenaltyRva = 0x3336760, RiverRva = 0x33362E0, LargeRiverRva = 0x3336390;
    private static double BaseAvoid => Define(BaseAvoidRva, 1, 100, 90);
    private static double NoDefAvoid => Define(NoDefAvoidRva, 1, 100, 60);
    private static double FortPenalty => Define(FortPenaltyRva, -1, -0.001, -0.15);
    private static double River => Define(RiverRva, -1, -0.001, -0.3);
    private static double LargeRiver => Define(LargeRiverRva, -1, -0.001, -0.6);
    private const double ArmorPenalty = 0.5; // damage dealt by divisions whose piercing is under the target's armor (the threshold table's low end)
    private const int LosingOddsPercent = 100; // attacks predicted lost outnumbering the won ones hold the offensive
    private static readonly nint Image = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;

    private static double Define(int rva, double min, double max, double vanilla)
    {
        var value = *(long*)(Image + rva) / (double)Native.FixedPoint;
        return value >= min && value <= max ? value : vanilla;
    }

    /// <summary>
    /// Why the order's divisions shouldn't attack: for each enemy province next to them with enemy divisions in it, the hours
    /// until each side's organisation runs out, the way the game's land combat deals it (attacks a hour from soft and hard
    /// attack by the other side's hardness, those up to its defense, or our breakthrough when it hits back, hit at the base
    /// avoid chance and the rest at the no-defense one, half damage under armor, forts and rivers off our attack); more of
    /// those lost than won and it holds. Null: it may go. Unlike power, this counts quality (Japan beats China's numbers).
    /// AI threads, read only.
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
    /// Hours until <paramref name="targets"/>' organisation runs out under <paramref name="hitters"/>' attacks: the targets
    /// soak up attacks with their defense (<paramref name="defenderHolds"/>) or their breakthrough (hitting back).
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
