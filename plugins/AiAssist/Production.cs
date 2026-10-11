using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // Layouts for HOI4 1.19.3.
    private const int MilitaryLines = 88, CountryOfProduction = 656, BestVariants = 208; // CCountryProduction
    private const int LogisticsOfCountry = 0xF98, DesiredFactories = 696;                // CAIInteriorMinister: int per archetype
    private const int TypeOfVariant = 1008, ArchetypeOfType = 1240, CostOfVariant = 976;
    private const int Air = 0, Land = 1;

    [Function(CallingConventions.Microsoft)] public delegate void ApplyTargetsFn(nint minister, nint lines, nint currentByArchetype, nint currentByRole);

    private IHook<ApplyTargetsFn>? _applyTargets;
    private delegate* unmanaged<nint, long*, nint, long*> _balance;
    private delegate* unmanaged<nint, long*, nint, nint, long*> _need;
    private delegate* unmanaged<nint, byte> _isAir, _isNaval;
    private int _surplusPercent;

    /// <summary>Moves wanted factories (minister+696) from surplus archetypes to short ones, per category, keeping each total.</summary>
    private void Rebalance(nint minister, nint production)
    {
        var country = *(nint*)(production + CountryOfProduction);
        var logistics = country == 0 ? 0 : *(nint*)(country + LogisticsOfCountry);
        if (logistics == 0) return;
        var desired = Native.PdxArray<int>(minister + DesiredFactories);
        var best = Native.PdxArray(production + BestVariants);

        var pool = new int[2];
        var donors = new List<(int Index, int Category, int Given)>();
        var wanting = new List<(int Index, int Category, double Gap)>();
        for (var i = 0; i < Math.Min(desired.Length, best.Length); i++)
        {
            var variant = best[i];
            var type = variant == 0 ? 0 : *(nint*)(variant + TypeOfVariant);
            var archetype = type == 0 ? 0 : *(nint*)(type + ArchetypeOfType);
            if (archetype == 0 || _isNaval(archetype) != 0) continue;
            var category = _isAir(archetype) != 0 ? Air : Land;
            long balance, need;
            _balance(logistics, &balance, archetype);
            _need(logistics, &need, archetype, 0);
            var units = balance / (double)Native.FixedPoint;
            var needed = need / (double)Native.FixedPoint;
            if (units < 0)
                wanting.Add((i, category, -units * *(long*)(variant + CostOfVariant) / Native.FixedPoint)); // the shortage in production cost
            else if (needed <= 0 ? units > 0 && desired[i] > 0 : units > needed * _surplusPercent / 100 && desired[i] > 1)
            {
                var given = needed <= 0 ? desired[i] : desired[i] / 2; // nothing in the army uses it: all of them
                desired[i] -= given;
                pool[category] += given;
                donors.Add((i, category, given));
            }
        }

        foreach (var category in new[] { Air, Land })
        {
            var takers = wanting.Where(s => s.Category == category).OrderByDescending(s => s.Gap).ToList();
            if (takers.Count == 0)
            {
                foreach (var d in donors.Where(d => d.Category == category)) desired[d.Index] += d.Given; // nobody short: as vanilla wanted
                continue;
            }
            var left = pool[category];
            var total = takers.Sum(t => t.Gap);
            foreach (var t in takers)
            {
                var share = t == takers[^1] ? left : Math.Min(left, (int)Math.Ceiling(pool[category] * t.Gap / total));
                desired[t.Index] += share;
                left -= share;
            }
        }
    }
}
