using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Production lines: small factory cuts don't drain lines that have built up efficiency ----

    private const int BestVariants = 208;                                          // CCountryProduction: best variant per archetype
    private const int DesiredFactories = 696;                                       // CAIInteriorMinister: int per archetype
    private const int ActiveFactoriesOfLine = 0x18, IndexOfArchetype = 1336;       // CProductionLine; CEquipmentType
    private const long StickyEfficiency = 35L << 15;                                // average line efficiency 35 %; GetEfficiency is Q15 percent
    private const int StickyBand = 25, MaxArchetypes = 256;                         // cuts up to 25 % (at least 1) are held

    [Function(CallingConventions.Microsoft)] public delegate void ApplyTargetsFn(nint minister, nint lines, nint currentByArchetype, nint currentByRole);

    private IHook<ApplyTargetsFn>? _applyTargets;
    private static delegate* unmanaged<nint, long*, long*> _efficiency;
    private static delegate* unmanaged<nint, byte> _isNaval;
    private static int _stickyHours, _stuck;
    // minister -> the game hour each archetype's cut was first held (0: none). Only that country's AI thread touches its array.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, int[]> HoldSince = new();

    private void LoadProductionLines(int days)
    {
        _stickyHours = 24 * days;
        _efficiency = (delegate* unmanaged<nint, long*, long*>)Sig.Find(Signatures.LineEfficiency);
        _isNaval = (delegate* unmanaged<nint, byte>)Sig.Find(Signatures.IsNaval);
        _applyTargets = Patches.Hook<ApplyTargetsFn>(Sig.Find(Signatures.ApplyFactoryTargets), (minister, lines, byArchetype, byRole) =>
        {
            Safely(() => Stick(minister, lines, byArchetype));
            _applyTargets!.OriginalFunction(minister, lines, byArchetype, byRole);
        });
    }

    /// <summary>
    /// Vanilla cuts an equipment's factories off its last lines whatever their efficiency and spends them on other equipment,
    /// where they start over at 10 %. A small cut off efficient lines is held for up to StickyDays (it is often undone soon);
    /// the factories it would have freed come off the smallest increases instead (air or land alike: factories are fungible).
    /// Big cuts, full stops and cuts vanilla still asks for after the hold go through. Ships are left alone.
    /// </summary>
    private static void Stick(nint minister, nint lines, nint byArchetype)
    {
        var desired = Native.PdxArray<int>(minister + DesiredFactories);
        var current = Native.PdxArray<int>(byArchetype);
        var best = Native.PdxArray(lines - ProductionLine.MilitaryLinesOffset + BestVariants);
        var n = Math.Min(Math.Min(desired.Length, current.Length), best.Length);
        if (n is <= 0 or > MaxArchetypes) return;
        Span<long> weighted = stackalloc long[n];
        Span<int> active = stackalloc int[n];
        weighted.Clear();
        active.Clear();
        foreach (var line in Native.PdxArray(lines))
        {
            var archetype = line == 0 ? 0 : ProductionLine.Archetype(ProductionLine.Variant(line));
            var factories = archetype == 0 ? 0 : *(int*)(line + ActiveFactoriesOfLine);
            if (factories <= 0) continue;
            var i = *(int*)(archetype + IndexOfArchetype);
            if ((uint)i >= (uint)n) continue;
            long efficiency;
            _efficiency(line, &efficiency);
            weighted[i] += efficiency * factories;
            active[i] += factories;
        }

        var since = HoldSince.GetOrAdd(minister, static (_, size) => new int[size], n);
        if (since.Length != n) HoldSince[minister] = since = new int[n];
        var hour = Game.Hour;
        var kept = 0;
        for (var i = 0; i < n; i++)
        {
            var cut = current[i] - desired[i];
            if (cut <= 0 || desired[i] <= 0 || active[i] == 0 // a full stop is a big change
                || weighted[i] / active[i] < StickyEfficiency || cut > Math.Max(1, current[i] * StickyBand / 100))
            {
                since[i] = 0;
                continue;
            }
            if (since[i] == 0 || hour < since[i]) since[i] = hour;
            else if (hour - since[i] >= _stickyHours)
            {
                since[i] = 0; // vanilla kept asking: it meant it
                continue;
            }
            desired[i] = current[i];
            kept += cut;
        }
        if (kept == 0) return;
        Interlocked.Increment(ref _stuck);
        while (kept > 0) // ponytail: O(n^2) over ~60-100 archetypes, every 2nd day
        {
            var pick = -1;
            for (var i = 0; i < n; i++)
                if (desired[i] > current[i] && (pick < 0 || desired[i] - current[i] < desired[pick] - current[pick]) && Military(best[i])) pick = i;
            if (pick < 0) break; // nothing grows: the kept factories just stay where they are efficient
            var take = Math.Min(kept, desired[pick] - current[pick]);
            desired[pick] -= take;
            kept -= take;
        }
    }

    private static bool Military(nint variant)
    {
        var archetype = ProductionLine.Archetype(variant);
        return archetype != 0 && _isNaval(archetype) == 0;
    }
}
