using HOI4ModLoader;

namespace AutoConstruction;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class ProductionSignatures
{
    /// <summary>CLogistics::GetBalance(logistics, long* out, CEquipmentType* archetype): stockpile - need - net lend-lease (fixed point); &lt; 0 = short.</summary>
    [Signature] public const string GetBalance = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 49 8B D8 48 8B F2 48 8B F9 E8 ? ? ? ? 45 33 C9";

    /// <summary>CLogistics::GetNeed(logistics, long* out, CEquipmentType* archetype, null): missing in fielded divisions and reserves (fixed point).</summary>
    [Signature] public const string GetNeed = "48 8B C4 4C 89 48 ? 4C 89 40 ? 48 89 50 ? 48 89 48 ? 56";

    [VTable] public const string MilitaryLine = "CMilitaryProductionLine";
    [VTable] public const string AddFactoriesCommand = "CAddProductionLineFactoriesCommand";
}

/// <summary>
/// Auto production: moves military factories the way a player tweaks the production screen, from a line whose equipment
/// has a big surplus to a line whose equipment is short, one factory at a time, with the production screen's own command
/// (multiplayer works). Paced by game days (MovesPerTurn a day), so a pause adds no moves; the day's unused ones can still
/// go while paused. Land and air lines; ships are left alone. No line is created or emptied.
/// </summary>
public unsafe partial class AutoConstruction
{
    // Layouts for HOI4 1.19.3.
    private const int LogisticsOfCountry = 0xF98;
    private const int HandleOfLine = 8, CostOfLine = 40, OutputOfLine = 48, RequestedOfLine = 232, MaxFactoriesSlot = 11;
    private const int CostOfVariant = 976;
    private const int LineOfCommand = 40, DeltaOfCommand = 48;
    private const int MinSurplus = 500;           // units: a small need's percentage alone would move factories over a handful of guns
    private const long ProductionPendingMs = 20_000; // a line we changed is left alone meanwhile (multiplayer: a round trip)

    private delegate* unmanaged<nint, long*, nint, long*> _balance;
    private delegate* unmanaged<nint, long*, nint, nint, long*> _need;
    private readonly Dictionary<nint, long> _pending = []; // line -> until
    private int _surplusPercent, _coverDays, _movesPerDay, _day = -1, _movesToday;

    private void LoadProduction()
    {
        _balance = (delegate* unmanaged<nint, long*, nint, long*>)Sig.Find(ProductionSignatures.GetBalance);
        _need = (delegate* unmanaged<nint, long*, nint, nint, long*>)Sig.Find(ProductionSignatures.GetNeed);
        MainThread.Every(TimeSpan.FromSeconds(1), ProductionPump);
    }

    private void ProductionPump()
    {
        if (!Game.HasStarted) return; // menus, lobby, loading: no commands
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var day = Game.Hour / 24;
        if (day != _day) (_day, _movesToday) = (day, 0);
        if (_movesToday >= _movesPerDay) return;
        var production = *(nint*)(country + ProductionOfCountry);
        var logistics = *(nint*)(country + LogisticsOfCountry);
        if (production == 0 || logistics == 0) return;
        var now = Environment.TickCount64;
        foreach (var (line, until) in _pending)
            if (now >= until) _pending.Remove(line); // removing while enumerating a Dictionary is allowed

        // Per equipment archetype: one balance, shared by every line making it.
        var donors = new List<(nint Line, double Surplus)>();
        var receivers = new List<(nint Line, double Gap)>();
        var lineVTable = Sig.VTable(ProductionSignatures.MilitaryLine);
        foreach (var line in Native.PdxArray(production + ProductionLine.MilitaryLinesOffset))
        {
            if (*(nint*)line != lineVTable || _pending.ContainsKey(line)) continue;
            var variant = ProductionLine.Variant(line);
            var archetype = ProductionLine.Archetype(variant);
            if (archetype == 0) continue;
            long balance, need;
            _balance(logistics, &balance, archetype);
            _need(logistics, &need, archetype, 0);
            var requested = *(int*)(line + RequestedOfLine);
            var units = balance / (double)Native.FixedPoint;
            var needed = need / (double)Native.FixedPoint;
            if (units < 0 && requested < MaxFactories(line) && DaysToCover(line, -units) > _coverDays)
                receivers.Add((line, -units * *(long*)(variant + CostOfVariant) / Native.FixedPoint)); // short, in production cost
            else if (needed > 0 && units > Math.Max(MinSurplus, needed * _surplusPercent / 100) && requested > 1) // never emptied
                donors.Add((line, units / needed));
        }
        if (donors.Count == 0 || receivers.Count == 0) return;

        // Biggest gap gets a factory from the biggest surplus, as a player would.
        var moves = 0;
        using var from = donors.OrderByDescending(d => d.Surplus).Select(d => d.Line).GetEnumerator();
        foreach (var (to, _) in receivers.OrderByDescending(r => r.Gap))
        {
            if (_movesToday == _movesPerDay || !from.MoveNext()) break;
            // Refused either way: counted against the day and the lines left alone for a while, so a refusal isn't retried
            // (or a factory taken and given back) every second. Refused at the receiver: the factory goes back.
            if (!AddFactories(tag, from.Current, -1)) _pending[from.Current] = now + ProductionPendingMs;
            else
            {
                if (AddFactories(tag, to, +1)) moves++;
                else AddFactories(tag, from.Current, +1);
                _pending[from.Current] = _pending[to] = now + ProductionPendingMs;
            }
            _movesToday++;
        }
        if (moves > 0) Log.Info($"moved {moves} factor{(moves == 1 ? "y" : "ies")} from surplus lines to short ones");
    }

    private static int MaxFactories(nint line) => ((delegate* unmanaged<nint, int>)(*(nint**)line)[MaxFactoriesSlot])(line);

    /// <summary>Days the line's own output (production per day over cost per unit) needs for <paramref name="units"/>.</summary>
    private static double DaysToCover(nint line, double units)
    {
        var output = *(long*)(line + OutputOfLine);
        return output <= 0 ? double.PositiveInfinity : units * *(long*)(line + CostOfLine) / output;
    }

    private static bool AddFactories(int tag, nint line, int delta)
    {
        var cmd = Commands.New(ProductionSignatures.AddFactoriesCommand, 0x38, tag, 0);
        *(long*)(cmd + LineOfCommand) = *(long*)(line + HandleOfLine); // this command has the line's handle where others have the tag
        *(int*)(cmd + DeltaOfCommand) = delta;
        return Commands.TryPost(cmd);
    }
}
