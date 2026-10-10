using HOI4ModLoader;

namespace AutoProduction;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>CLogistics::GetBalance(logistics, long* out, CEquipmentType* archetype): stockpile - need - net lend-lease (fixed point); &lt; 0 = short.</summary>
    [Signature] public const string GetBalance = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 49 8B D8 48 8B F2 48 8B F9 E8 ? ? ? ? 45 33 C9";

    /// <summary>CLogistics::GetNeed(logistics, long* out, CEquipmentType* archetype, null): missing in fielded divisions and reserves (fixed point).</summary>
    [Signature] public const string GetNeed = "48 8B C4 4C 89 48 ? 4C 89 40 ? 48 89 50 ? 48 89 48 ? 56";

    [VTable] public const string MilitaryLine = "CMilitaryProductionLine";
    [VTable] public const string AddFactoriesCommand = "CAddProductionLineFactoriesCommand";
}

/// <summary>
/// Moves military factories the way a player tweaks the production screen: from a line whose equipment has a big surplus
/// to a line whose equipment is short, one factory at a time, with the production screen's own command (multiplayer works).
/// Land and air lines; ships are left alone. No line is created or emptied.
/// </summary>
[Plugin("com.green1052.autoproduction", "Auto Production", "1.0.0", GameVersion = "1.19.3")]
public unsafe class AutoProduction : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int ProductionOfCountry = 0xF68, LogisticsOfCountry = 0xF98, MilitaryLines = 88;
    private const int HandleOfLine = 8, CostOfLine = 40, OutputOfLine = 48, VariantOfLine = 136, RequestedOfLine = 232, MaxFactoriesSlot = 11;
    private const int TypeOfVariant = 1008, ArchetypeOfType = 1240, CostOfVariant = 976;
    private const int LineOfCommand = 40, DeltaOfCommand = 48;
    private const long PendingMs = 20_000; // a line we changed is left alone meanwhile (multiplayer: a round trip)

    private delegate* unmanaged<nint, long*, nint, long*> _balance;
    private delegate* unmanaged<nint, long*, nint, nint, long*> _need;
    private readonly Dictionary<nint, long> _pending = []; // line -> until
    private PerGame _game = null!;
    private int _surplusPercent, _minSurplus, _minFactories, _coverDays, _movesPerTurn;

    public override void Load()
    {
        _surplusPercent = Math.Max(0, Config.Bind("Production", "SurplusPercent", 50, "A line gives factories away when its equipment's surplus is above this much (%) of what the army needs."));
        _minSurplus = Math.Max(0, Config.Bind("Production", "MinSurplus", 500, "And above this many units."));
        _minFactories = Math.Max(1, Config.Bind("Production", "MinFactories", 1, "Never take a line below this many factories."));
        _coverDays = Math.Max(0, Config.Bind("Production", "CoverDays", 60, "A short line only gets factories if its own output won't cover the shortage within this many days: a moved factory starts over at the base efficiency."));
        _movesPerTurn = Math.Max(1, Config.Bind("Production", "MovesPerTurn", 2, "Factories moved at most every 10 seconds, so lines keep their efficiency."));
        _balance = (delegate* unmanaged<nint, long*, nint, long*>)Sig.Find(Signatures.GetBalance);
        _need = (delegate* unmanaged<nint, long*, nint, nint, long*>)Sig.Find(Signatures.GetNeed);
        _game = new PerGame(_pending.Clear);
        MainThread.Every(TimeSpan.FromSeconds(10), Pump);
    }

    private void Pump()
    {
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var production = *(nint*)(country + ProductionOfCountry);
        var logistics = *(nint*)(country + LogisticsOfCountry);
        if (production == 0 || logistics == 0) return;
        var now = Environment.TickCount64;
        foreach (var (line, until) in _pending.ToArray())
            if (now >= until) _pending.Remove(line);

        // Per equipment archetype: one balance, shared by every line making it.
        var donors = new List<(nint Line, double Surplus)>();
        var receivers = new List<(nint Line, double Gap)>();
        var lineVTable = Sig.VTable(Signatures.MilitaryLine);
        foreach (var line in Native.PdxArray(production + MilitaryLines))
        {
            if (*(nint*)line != lineVTable || _pending.ContainsKey(line)) continue;
            var variant = *(nint*)(line + VariantOfLine);
            var type = variant == 0 ? 0 : *(nint*)(variant + TypeOfVariant);
            var archetype = type == 0 ? 0 : *(nint*)(type + ArchetypeOfType);
            if (archetype == 0) continue;
            long balance, need;
            _balance(logistics, &balance, archetype);
            _need(logistics, &need, archetype, 0);
            var requested = *(int*)(line + RequestedOfLine);
            var units = balance / (double)Native.FixedPoint;
            var needed = need / (double)Native.FixedPoint;
            if (units < 0 && requested < MaxFactories(line) && DaysToCover(line, -units) > _coverDays)
                receivers.Add((line, -units * *(long*)(variant + CostOfVariant) / Native.FixedPoint)); // short, in production cost
            else if (needed > 0 && units > Math.Max(_minSurplus, needed * _surplusPercent / 100) && requested > _minFactories)
                donors.Add((line, units / needed));
        }
        if (donors.Count == 0 || receivers.Count == 0) return;

        // Biggest gap gets a factory from the biggest surplus, as a player would.
        var moves = 0;
        using var from = donors.OrderByDescending(d => d.Surplus).Select(d => d.Line).GetEnumerator();
        foreach (var (to, _) in receivers.OrderByDescending(r => r.Gap))
        {
            if (moves == _movesPerTurn || !from.MoveNext()) break;
            if (!AddFactories(tag, from.Current, -1)) continue;
            if (!AddFactories(tag, to, +1)) AddFactories(tag, from.Current, +1); // give it back
            _pending[from.Current] = _pending[to] = now + PendingMs;
            moves++;
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
        var cmd = Commands.New(Signatures.AddFactoriesCommand, 0x38, tag, 0);
        *(long*)(cmd + LineOfCommand) = *(long*)(line + HandleOfLine); // this command has the line's handle where others have the tag
        *(int*)(cmd + DeltaOfCommand) = delta;
        return Commands.TryPost(cmd);
    }
}
