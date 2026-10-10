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
}

/// <summary>
/// Shift+click a resource's icon in the trade view to switch auto trade on or off for it ("Auto" shows under it). While on,
/// a shortage of that resource is imported with the trade view's own command (so multiplayer stays in sync), from our
/// subjects first, then our faction, then the country that can sell the most.
/// </summary>
[Plugin("com.green1052.autotrade", "Auto Trade", "1.0.0", GameVersion = "1.19.3")]
public unsafe class AutoTrade : Plugin
{
    // Layouts for HOI4 1.19.3. Amounts are fixed point (1e5) unless noted.
    private const int ResourcesOfCountry = 0x11F8, StatesOfCountry = 0x484, TagOfCountry = 8;
    private const int Influence = 0xF0;                         // CCountry: CPdxArray<long> trade influence, by importer TagIndex
    private const int DiplomacyOfCountry = 0xF88, Overlord = 0x188, Faction = 0x290; // diplomacy block: overlord tag (0 = none), faction*
    private const int WindowOfItem = 0x10, ResourceOfItem = 0x20; // CTradeResourceInfoItem
    private const int Exporter = 44, Amount = 56;               // CCreateTradeCommand: exporter tag, total amount in whole units (0 cancels)
    private const long Fixed = 100_000;
    private const long PendingMs = 10_000;                      // a sent deal shows in the balance at once in single player, a round trip later in multiplayer
    private static readonly byte[] Label = "Auto "u8.ToArray();

    [Function(CallingConventions.Microsoft)] public delegate void ItemFn(nint item);
    [Function(CallingConventions.Microsoft)] public delegate void UpdateFn(nint item, nint unused);

    private IHook<ItemFn>? _iconClick;
    private IHook<UpdateFn>? _update;
    private delegate* unmanaged<nint, nint, int> _balance;
    private delegate* unmanaged<nint, long*, nint, long*, long*, nint, void> _maxExport;
    private delegate* unmanaged<long*, nint, long*, long*, int, void> _amountForFactories;

    private readonly HashSet<nint> _auto = new();                          // resources with auto trade on
    private readonly Dictionary<nint, (int Balance, long Until)> _sent = new(); // by resource: balance when a deal was sent
    private (nint GameState, int Tag) _session;

    public override void Load()
    {
        var iconClick = Sig.Find(Signatures.ResourceIconClick);
        var update = Sig.Find(Signatures.ResourceItemUpdate);
        _balance = (delegate* unmanaged<nint, nint, int>)Sig.Find(Signatures.Balance);
        _maxExport = (delegate* unmanaged<nint, long*, nint, long*, long*, nint, void>)Sig.Find(Signatures.MaxExport);
        _amountForFactories = (delegate* unmanaged<long*, nint, long*, long*, int, void>)Sig.Find(Signatures.AmountForFactories);

        _iconClick = Patches.Hook<ItemFn>(iconClick, item =>
        {
            if (!Gui.ShiftHeld)
            {
                _iconClick!.OriginalFunction(item);
                return;
            }
            var resource = *(nint*)(item + ResourceOfItem);
            var on = _auto.Add(resource) || !_auto.Remove(resource);
            Log.Info($"auto trade {(on ? "on" : "off")} for a resource, {_auto.Count} on");
            _update!.OriginalFunction(item, 0); // redraw now
            Labels(item);
        });
        _update = Patches.Hook<UpdateFn>(update, (item, unused) =>
        {
            _update!.OriginalFunction(item, unused);
            Labels(item);
        });

        MainThread.Every(TimeSpan.FromSeconds(1), Pump);
    }

    /// <summary>"Auto " in front of the column's neededType text (empty but for oil) while auto trade is on; idempotent.</summary>
    private void Labels(nint item)
    {
        var box = Gui.FindTextBox(*(nint*)(item + WindowOfItem), "neededType");
        if (box == 0) return;
        var text = Gui.Text(box);
        var labelled = text.StartsWith(Label);
        var on = _auto.Contains(*(nint*)(item + ResourceOfItem));
        if (on && !labelled) Gui.SetText(box, [.. Label, .. text]);
        else if (!on && labelled) Gui.SetText(box, text[Label.Length..].ToArray());
    }

    private void Pump()
    {
        var tag = Game.PlayerTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        if (_session != (Game.State, tag)) // another game or country
        {
            _session = (Game.State, tag);
            _auto.Clear();
            _sent.Clear();
        }
        var resources = *(nint*)(country + ResourcesOfCountry);
        if (resources == 0) return;
        var now = Environment.TickCount64;
        foreach (var resource in _auto)
        {
            var balance = _balance(resources, resource);
            if (_sent.TryGetValue(resource, out var sent) && sent.Balance == balance && now < sent.Until) continue; // not in yet
            _sent.Remove(resource);
            if (balance < 0) Import(tag, resource, -balance, balance, now);
        }
    }

    /// <summary>Buys <paramref name="needed"/> units more of <paramref name="resource"/> from the best seller that has room.</summary>
    private void Import(int tag, nint resource, int needed, int balance, long now)
    {
        var faction = *(nint*)(*(nint*)(Game.Country(tag) + DiplomacyOfCountry) + Faction);
        var importer = stackalloc long[1];
        *(int*)importer = tag;
        ((int*)importer)[1] = Game.TagIndex(tag);

        var exporterIdx = stackalloc long[1];
        (int Tag, (int Relation, long Room, long Influence) Rank, long Room, long Existing, long Step) best = default;
        foreach (var exporter in Game.Countries)
        {
            var exporterTag = exporter == 0 ? 0 : *(int*)(exporter + TagOfCountry);
            if (exporterTag == 0 || exporterTag == tag || *(int*)(exporter + StatesOfCountry) <= 0) continue;
            var exporterResources = *(nint*)(exporter + ResourcesOfCountry);
            if (exporterResources == 0) continue;

            long max, existing;
            _maxExport(exporterResources, importer, resource, &max, &existing, 0);
            var room = (max - existing) / Fixed;
            if (room <= 0) continue;
            *(int*)exporterIdx = exporterTag;
            ((int*)exporterIdx)[1] = Game.TagIndex(exporterTag);
            long step;
            _amountForFactories(&step, resource, importer, exporterIdx, 1);
            step = Math.Max(1, step / Fixed);
            if (room < step) continue;

            var influences = Native.PdxArray(exporter + Influence); // longs, read as nints (same size)
            var index = ((int*)importer)[1];
            var influence = index < influences.Length ? influences[index] : 0;
            // Our subjects first, then our faction, then whoever can sell the most; trade influence breaks ties.
            var diplomacy = *(nint*)(exporter + DiplomacyOfCountry);
            var relation = *(int*)(diplomacy + Overlord) == tag ? 2 : faction != 0 && *(nint*)(diplomacy + Faction) == faction ? 1 : 0;
            var rank = (relation, room, (long)influence);
            if (best.Tag == 0 || rank.CompareTo(best.Rank) > 0) best = (exporterTag, rank, room, existing / Fixed, step);
        }
        if (best.Tag == 0) return; // nobody can sell (or no free civilian factories)

        var add = Math.Min((needed + best.Step - 1) / best.Step * best.Step, best.Room / best.Step * best.Step);
        var cmd = Commands.New(Signatures.TradeCommand, 0x40, tag, resource);
        *(int*)(cmd + Exporter) = best.Tag;
        *(int*)(cmd + Amount) = (int)(best.Existing + add);
        if (!Commands.TryPost(cmd)) return;
        _sent[resource] = (balance, now + PendingMs);
        Log.Info($"importing {add} more of a resource (short by {needed})");
    }
}
