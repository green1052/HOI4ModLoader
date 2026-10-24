using System.Text;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AutoTrade;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>CTradeResourceInfoItem: click on the resource icon (item): `mov rdx,[rcx+20h]; mov rcx,[rcx+0A88h]; jmp SelectResource`.</summary>
    [Signature] public const string ResourceIconClick = "48 8B 51 20 48 8B 89 88 0A 00 00 E9";

    /// <summary>CTradeResourceInfoItem::Update(item, unused): re-sets every text of a resource column in the trade view.</summary>
    [Signature] public const string ResourceItemUpdate = "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC C8 01 00 00 0F 29 B4 24 ? ? ? ? 4C 8B F1";

    /// <summary>CCountryResources::GetBalance(resources, CStrategicResource*) -> int: the trade view's balance (net of imports).</summary>
    [Signature] public const string Balance = "48 83 EC 28 4C 63 92 D8 00 00 00 4C 8D 99 38 02 00 00";

    /// <summary>
    /// CCountryResources::GetMaxExport(exporter resources, TagIdx* importer, resource, long* max, long* existing, std::string* tooltip):
    /// the trade offer slider's max, a total including the existing deal; 0 if they can't trade (war, embargo, no influence).
    /// </summary>
    [Signature] public const string MaxExport = "48 89 5C 24 ? 4C 89 4C 24 ? 4C 89 44 24 ? 48 89 4C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC 10 03 00 00";

    /// <summary>Trade::AmountForFactories(long* out, resource, TagIdx* importer, TagIdx* exporter, int factories): the slider's step.</summary>
    [Signature] public const string AmountForFactories = "40 53 56 57 41 55 41 56 48 83 EC 20 48 89 6C 24 ? 4C 8B F1 4C 89 64 24 ? 49 8B C9";

    [VTable] public const string TradeCommand = "CCreateTradeCommand";

    /// <summary>
    /// Lexer::TokenToString(int token) -> std::string*: a script token's text, under the token table's lock (any thread).
    /// A CStrategicResource's key ("oil", "steel") is the token at its +0x08.
    /// </summary>
    [Signature] public const string TokenToString = "40 53 48 83 EC 30 48 63 D9 80 3D ? ? ? ? 00 75 05 E8 ? ? ? ? 3B 1D ? ? ? ? 7E";

    /// <summary>
    /// AddFuelEffect::Execute: `mov rsi,[rax+1580h]` at +0x2B loads the country's CCountryFuelStatus (+0x08 fuel, +0x10 max
    /// fuel, int64 2^15 fixed point). The offset is read from its disp32 at +0x2E.
    /// </summary>
    [Signature] public const string FuelOfCountry = "40 53 56 57 48 83 EC 40 48 8B D9 48 8B FA 48 8D 4A 08 E8 ? ? ? ? 48 8D 4B 58 C6 44 24 20 00 45 33 C9 48 8D 54 24 60 4C 8B C7 48 8B B0 80 15 00 00";
}

/// <summary>
/// Shift+click a resource's icon in the trade view to toggle auto trade for it ("Auto" shows under it). While on, a
/// shortage is imported and a surplus of a whole import step cut, with the trade view's own command, so multiplayer stays
/// in sync. Bought from subjects first, then the faction, then whoever can sell the most; cut in reverse. Oil follows the
/// fuel stockpile instead: only unused oil becomes fuel, so its balance never shows a shortage. Ctrl+Alt+C draws our trade
/// routes (TradeOverlay.cs).
/// </summary>
[Plugin("com.green1052.autotrade", "Auto Trade", "1.0.0", GameVersion = "1.19.3")]
public unsafe partial class AutoTrade : Plugin
{
    // Layouts for HOI4 1.19.3. Amounts are fixed point (1e5) unless noted.
    private const int ResourcesOfCountry = 0x11F8, StatesOfCountry = 0x484; // int: states it controls (0: gone or exiled)
    private const int Influence = 0xF0;                         // CCountry: CPdxArray<long> trade influence, by importer TagIndex
    private const int DiplomacyOfCountry = 0xF88, Overlord = 0x188, Faction = 0x290; // diplomacy block: overlord tag (0 = none), faction*
    private const int WindowOfItem = 0x10, ResourceOfItem = 0x20; // CTradeResourceInfoItem
    private const int Exporter = 44, Amount = 56;               // CCreateTradeCommand: exporter tag, total amount in whole units (0 cancels)
    private const int PendingHours = 48;                        // after a deal changes, wait until the balance moves, two game days at most

    [Function(CallingConventions.Microsoft)] public delegate void ItemFn(nint item);
    [Function(CallingConventions.Microsoft)] public delegate void UpdateFn(nint item, nint unused);

    private IHook<ItemFn>? _iconClick;
    private IHook<UpdateFn>? _update;
    private delegate* unmanaged<nint, nint, int> _balance;
    private delegate* unmanaged<nint, long*, nint, long*, long*, nint, void> _maxExport;
    private delegate* unmanaged<long*, nint, long*, long*, int, void> _amountForFactories;
    private delegate* unmanaged<int, nint> _tokenToString;
    private const int KeyOfResource = 0x08;

    // By country tag. Resources are made once at startup, so a pointer names the same resource in every game loaded.
    private readonly HashSet<(int Tag, nint Resource)> _auto = [];
    private readonly Dictionary<nint, (int Balance, bool Up, int Until)> _sent = []; // by resource: balance when a deal was changed, game hour
    private PerGame _game = null!;
    private int _surplus, _fuelLow, _fuelHigh, _fuelOfCountry, _oilAt = int.MinValue;
    private long _oilFuel; // the fuel at the last oil decision, for its trend
    private const int OilEveryHours = 7 * 24;
    private bool _alliesFirst;
    private nint _oil;
    private byte[]? _autoPrefix; // "Auto " in the game's language, made once: Labels runs with every redraw of a column
    private const int FuelOfStatus = 0x08, MaxFuelOfStatus = 0x10;
    private const int ItemDatabaseRva = 0x332F088, OilOfDatabase = 0x68; // the strategic resources' database global (HOI4 1.19.3): its oil

    public override void Load()
    {
        _surplus = Math.Max(0, Config.Bind("Trade", "Surplus", 0, "Import until the balance is at least this much, as a buffer for new factories and lost deals. Imports are cut once the balance is a whole import step above it."));
        _alliesFirst = Config.Bind("Sellers", "AlliesFirst", true, "Buy from your subjects first, then your faction, then whoever can sell the most (trade influence breaks ties).");
        _fuelLow = Math.Clamp(Config.Bind("Fuel", "FuelLowPercent", 50, "With auto trade on oil: each game week, import one more step while fuel is under this share (%) of its maximum and not filling up on its own."), 0, 100);
        _fuelHigh = Math.Clamp(Config.Bind("Fuel", "FuelHighPercent", 90, "With auto trade on oil: each game week, cut oil imports by a step while fuel is at or over this share (%) of its maximum and not falling."), _fuelLow, 100);
        _fuelOfCountry = *(int*)(Sig.Find(Signatures.FuelOfCountry) + 0x2E);
        var database = *(nint*)(System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress + ItemDatabaseRva);
        _oil = database == 0 ? 0 : *(nint*)(database + OilOfDatabase);
        if (_oil == 0) Log.Info("oil: the resource database wasn't found, so oil trades by its balance like the rest");
        var iconClick = Sig.Find(Signatures.ResourceIconClick);
        var update = Sig.Find(Signatures.ResourceItemUpdate);
        _balance = (delegate* unmanaged<nint, nint, int>)Sig.Find(Signatures.Balance);
        _maxExport = (delegate* unmanaged<nint, long*, nint, long*, long*, nint, void>)Sig.Find(Signatures.MaxExport);
        _amountForFactories = (delegate* unmanaged<long*, nint, long*, long*, int, void>)Sig.Find(Signatures.AmountForFactories);
        _tokenToString = (delegate* unmanaged<int, nint>)Sig.Find(Signatures.TokenToString);
        RestoreAuto();
        _game = new PerGame(() =>
        {
            _sent.Clear();
            _oilAt = int.MinValue;
        });

        _iconClick = Patches.Hook<ItemFn>(iconClick, item =>
        {
            if (!Gui.ShiftHeld || Game.ControlledTag == 0)
            {
                _iconClick!.OriginalFunction(item);
                return;
            }
            var key = (Tag: Game.ControlledTag, Resource: *(nint*)(item + ResourceOfItem));
            var on = _auto.Add(key) || !_auto.Remove(key);
            SaveAuto();
            Log.Info($"auto trade {(on ? "on" : "off")} for {Name(key.Resource)}, {_auto.Count(a => a.Tag == key.Tag)} on");
            _update!.OriginalFunction(item, 0); // redraw now
            Labels(item);
        });
        _update = Patches.Hook<UpdateFn>(update, (item, unused) =>
        {
            _update!.OriginalFunction(item, unused);
            Log.Guard("labels", (this, item), static s => s.Item1.Labels(s.item));
        });

        MainThread.Every(TimeSpan.FromSeconds(1), Pump);
        LoadOverlay();
    }

    // The auto list survives a plugin reload within one game process: resource pointers hold until it exits.
    private static readonly string AutoFile = Path.Combine(Loader.ModsDir, "cache", "autotrade_auto.txt");

    private void SaveAuto()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoFile)!);
            File.WriteAllLines(AutoFile, [Environment.ProcessId.ToString(), .. _auto.Select(a => $"{a.Tag} {a.Resource:X}")]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"auto trade list not saved: {e.Message}"); }
    }

    private void RestoreAuto()
    {
        if (Loader.AtStartup) return; // a hot reload only: Windows recycles PIDs, so the check below alone can pass on an earlier run's file
        try
        {
            if (!File.Exists(AutoFile)) return;
            var lines = File.ReadAllLines(AutoFile);
            if (lines.Length == 0 || lines[0] != Environment.ProcessId.ToString()) return; // an earlier game run's pointers
            foreach (var line in lines.Skip(1))
                if (line.Split(' ') is [var tag, var resource] && int.TryParse(tag, out var t)
                    && long.TryParse(resource, System.Globalization.NumberStyles.HexNumber, null, out var r)) _auto.Add((t, (nint)r));
            if (_auto.Count > 0) Log.Info($"auto trade kept on for {_auto.Count} resource(s) across the reload");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The resource's script key ("oil", "steel"), for the log.</summary>
    private string Name(nint resource)
    {
        var text = Native.MsvcString(_tokenToString(*(int*)(resource + KeyOfResource)));
        return text.IsEmpty ? "a resource" : Encoding.ASCII.GetString(text);
    }

    /// <summary>"Auto " in front of the column's neededType text (empty but for oil) while auto trade is on; idempotent.</summary>
    private void Labels(nint item)
    {
        var box = Gui.FindTextBox(*(nint*)(item + WindowOfItem), "neededType");
        if (box == 0) return;
        _autoPrefix ??= Encoding.UTF8.GetBytes(Loc.Get("com.green1052.autotrade.auto", "Auto") + " ");
        Gui.SetPrefix(box, _autoPrefix, _auto.Contains((Game.ControlledTag, *(nint*)(item + ResourceOfItem))));
    }

    private void Pump()
    {
        if (!Game.HasStarted) return; // menus, lobby, loading: no commands
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var resources = *(nint*)(country + ResourcesOfCountry);
        if (resources == 0) return;
        var now = Game.Hour;
        foreach (var (owner, resource) in _auto)
        {
            if (owner != tag) continue;
            var balance = _balance(resources, resource);
            if (resource == _oil && _oil != 0)
            {
                Oil(country, tag, balance, now);
                continue;
            }
            if (_sent.TryGetValue(resource, out var sent) && now < sent.Until && (sent.Up ? balance <= sent.Balance : balance >= sent.Balance)) continue; // not in yet
            _sent.Remove(resource);
            if (balance < _surplus) Import(tag, resource, _surplus - balance, balance, now);
            else Reduce(tag, resource, balance - _surplus, balance, now);
        }
    }

    private const int RecoveringPercent = 2; // a week's fuel gain (% of the maximum) that counts as filling up on its own

    /// <summary>
    /// Oil by the fuel stockpile, once a game week: a step more while fuel is under FuelLowPercent of its maximum and gaining
    /// under RecoveringPercent a week, a step less while at or over FuelHighPercent and not falling. Weekly because the
    /// stockpile fills slowly, so daily steps would overshoot. The first week has no trend yet and goes by level only.
    /// </summary>
    private void Oil(nint country, int tag, int balance, int now)
    {
        var status = *(nint*)(country + _fuelOfCountry);
        if (status == 0) return;
        long fuel = *(long*)(status + FuelOfStatus), max = *(long*)(status + MaxFuelOfStatus);
        if (max <= 0) return;
        if (now - _oilAt is >= 0 and < OilEveryHours) return;
        var first = _oilAt == int.MinValue || now < _oilAt;
        var weekly = first ? 0 : (fuel - _oilFuel) * 100 / max; // % of the maximum gained (or lost) in the week
        (_oilAt, _oilFuel) = (now, fuel);
        var percent = fuel * 100 / max;
        var action = percent < _fuelLow && weekly < RecoveringPercent
                ? Import(tag, _oil, 1, balance, now) ? "importing a step more" : "no seller with room for more" // one seller step
            : percent >= _fuelHigh && weekly >= 0
                ? Reduce(tag, _oil, int.MaxValue / 2, balance, now, oneStep: true) ? "importing a step less" : "no imports to cut"
            : "holding";
        Log.Info($"oil: fuel at {percent}% of its maximum, {weekly:+0;-0;0}% in the last week: {action}");
    }

    /// <summary>Buys <paramref name="needed"/> units more of <paramref name="resource"/> from the best seller that has room.</summary>
    private bool Import(int tag, nint resource, int needed, int balance, int now)
    {
        var best = Sellers(tag, resource).Where(s => s.Room >= s.Step).OrderByDescending(s => (s.Relation, s.Room, s.Influence)).FirstOrDefault();
        if (best.Tag == 0) return false; // nobody can sell (or no free civilian factories)
        var add = Math.Min((needed + best.Step - 1) / best.Step * best.Step, best.Room / best.Step * best.Step);
        if (!Post(tag, resource, best.Tag, best.Existing + add, balance, true, now)) return false;
        Log.Info($"importing {add} more {Name(resource)} (short by {needed})");
        return true;
    }

    /// <summary>
    /// Cuts our imports of <paramref name="resource"/> by whole steps, at most <paramref name="excess"/> units, from the least
    /// preferred seller (the import order reversed): the same command with a smaller total; 0 cancels the deal.
    /// </summary>
    private bool Reduce(int tag, nint resource, int excess, int balance, int now, bool oneStep = false)
    {
        var deal = Sellers(tag, resource).Where(s => s.Existing > 0 && s.Step <= excess).OrderBy(s => (s.Relation, s.Influence)).FirstOrDefault();
        if (deal.Tag == 0) return false;
        var cut = Math.Min(deal.Existing, oneStep ? deal.Step : excess / deal.Step * deal.Step);
        if (!Post(tag, resource, deal.Tag, deal.Existing - cut, balance, false, now)) return false;
        Log.Info($"importing {cut} less {Name(resource)} ({excess} over)");
        return true;
    }

    /// <summary>
    /// Every country that can trade <paramref name="resource"/> with us: whole units it could still sell, our existing deal,
    /// one civilian factory's worth (the slider's step), and its rank (subject 2, faction 1, else 0; then trade influence).
    /// </summary>
    private List<(int Tag, int Relation, long Room, long Influence, long Existing, long Step)> Sellers(int tag, nint resource)
    {
        var ours = *(nint*)(Game.Country(tag) + DiplomacyOfCountry);
        var faction = ours == 0 ? 0 : *(nint*)(ours + Faction);
        var importer = Game.TagIdx(tag);
        var sellers = new List<(int, int, long, long, long, long)>();
        foreach (var exporter in Game.Countries)
        {
            var exporterTag = exporter == 0 ? 0 : Game.Tag(exporter);
            if (exporterTag == 0 || exporterTag == tag || *(int*)(exporter + StatesOfCountry) <= 0) continue;
            var exporterResources = *(nint*)(exporter + ResourcesOfCountry);
            var diplomacy = *(nint*)(exporter + DiplomacyOfCountry);
            if (exporterResources == 0 || diplomacy == 0) continue;

            long max, existing;
            _maxExport(exporterResources, &importer, resource, &max, &existing, 0);
            if (max <= 0 && existing <= 0) continue;
            var exporterIdx = Game.TagIdx(exporterTag);
            long step;
            _amountForFactories(&step, resource, &importer, &exporterIdx, 1);

            var influences = Native.PdxArray<long>(exporter + Influence);
            var index = (int)(importer >> 32);
            var relation = !_alliesFirst ? 0 : *(int*)(diplomacy + Overlord) == tag ? 2 : faction != 0 && *(nint*)(diplomacy + Faction) == faction ? 1 : 0;
            sellers.Add((exporterTag, relation, (max - existing) / Native.FixedPoint, index < influences.Length ? influences[index] : 0,
                existing / Native.FixedPoint, Math.Max(1, step / Native.FixedPoint)));
        }
        return sellers;
    }

    /// <summary>Sets our deal with <paramref name="exporter"/> to <paramref name="total"/> units, as the trade view's slider does.</summary>
    private bool Post(int tag, nint resource, int exporter, long total, int balance, bool up, int now)
    {
        var cmd = Commands.New(Signatures.TradeCommand, 0x40, tag, resource);
        *(int*)(cmd + Exporter) = exporter;
        *(int*)(cmd + Amount) = (int)total;
        if (!Commands.TryPost(cmd)) return false;
        _sent[resource] = (balance, up, now + PendingHours);
        return true;
    }
}
