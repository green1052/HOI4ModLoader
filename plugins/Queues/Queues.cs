using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Queues;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CCountryTechTreeView click on a tech / on an XOR tech (view, event): event+8 is the CTechnologyStatus. Normally opens
    /// the technology info window, whose Research button then starts it.
    /// </summary>
    [Signature] public const string TechClick = "48 89 5C 24 ? 57 48 83 EC 20 80 3D ? ? ? ? ? 48 8B F9";
    [Signature] public const string XorTechClick = "40 55 56 48 83 EC 28 80 3D";

    /// <summary>
    /// National focus tree: open the detail popup of a clicked focus (view, CNationalFocus*, bool). Hooked instead of the
    /// popup's select button, which is disabled while another focus is in progress.
    /// </summary>
    [Signature] public const string FocusClick = "48 85 D2 0F 84 ? ? ? ? 48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 41 56 41 57 48 83 EC 30";

    /// <summary>Intelligence agency upgrade entry click (entry): entry+UpgradeOfEntry is the upgrade.</summary>
    [Signature] public const string AgencyClick = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 30 48 8B F9 E8 ? ? ? ? 84 C0";

    /// <summary>Intelligence agency upgrade entry refresh (entry): fills entry+WindowOfEntry's texts and buttons.</summary>
    [Signature] public const string AgencyEntryRefresh = "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 38 01 00 00 0F 29 B4 24 ? ? ? ? 4C 8B F1";

    /// <summary>Agency upgrades window: refresh every entry (window). Only runs when the window opens or the game changes something.</summary>
    [Signature] public const string AgencyWindowRefresh = "48 89 6C 24 ? 56 48 83 EC 20 48 8B 41 ? 48 8B B0";

    [VTable] public const string ResearchCommand = "CSetResearchCommand";
    [VTable] public const string FocusCommand = "CSetNationalFocusCommand";
    [VTable] public const string AgencyCommand = "CIntelligenceAgencyUpgradeCommand";
}

/// <summary>
/// Research, national focus and intelligence agency upgrade queues. Shift+click a tech, focus or agency upgrade to
/// queue/unqueue it; whenever a research slot, the focus or the agency is idle, the first queued entry the game accepts
/// is started with the same command the UI sends (so multiplayer stays in sync).
/// </summary>
[Plugin("com.green1052.queues", "Queues", "1.0.0", GameVersion = "1.19.3")]
public unsafe class Queues : Plugin
{
    // Layouts for HOI4 1.19.3.
    const int ResearchOfCountry = 0xF60, Slots = 0xA0;            // CCountry -> research -> CPdxArray<CResearchSlot*>
    const int TechOfSlot = 0x18, IndexOfSlot = 0x40;              // slot: CTechnologyStatus* in progress (null = idle), slot number
    const int TechOfStatus = 0x160;                              // CTechnologyStatus -> CTechnology
    const int FocusOfCountry = 0x1380, FocusInProgress = 0x10;    // CCountry -> focus progress -> CNationalFocus* (null = idle)
    const int AgencyOfCountry = 0xFC0, AgencyCreated = 0xC0, AgencyUpgradeInProgress = 0xD0;
    const int Repetitions = 96, MaxRepetitions = 540;             // agency: CPdxArray<{upgrade*, int done}>; upgrade: allowed count
    const int StatusOfEvent = 8;                                  // tech tree click event -> CTechnologyStatus
    const int WindowOfEntry = 16, UpgradeOfEntry = 40, ButtonOfEntry = 0x40, AgencyOfEntry = 4216; // agency upgrade entry
    const int ResearchSlot = 56, ResearchBonus = 60;             // CSetResearchCommand fields after the target
    const string NumberBox = "hoimod_queue";                      // added to the GUI by the Queues UI mod
    const long CooldownMs = 1500;                                 // a posted command takes a tick or two to execute

    [Function(CallingConventions.Microsoft)] public delegate nint TechClickFn(nint view, nint ev);
    [Function(CallingConventions.Microsoft)] public delegate void FocusClickFn(nint view, nint focus, byte a3);
    [Function(CallingConventions.Microsoft)] public delegate nint AgencyClickFn(nint entry);
    [Function(CallingConventions.Microsoft)] public delegate void EntryFn(nint entry);
    [Function(CallingConventions.Microsoft)] public delegate void WindowFn(nint window);

    IHook<TechClickFn>? _techClick, _xorTechClick;
    IHook<FocusClickFn>? _focusClick;
    IHook<AgencyClickFn>? _agencyClick;
    IHook<EntryFn>? _agencyEntryRefresh;
    IHook<WindowFn>? _agencyWindowRefresh;
    nint _agencyWindow; // last refreshed agency upgrades window, valid while it's open (i.e. whenever one of its entries is clicked)
    bool _reportedNumberBox;

    readonly Queue _research = new("research", Signatures.ResearchCommand, 0x40);
    readonly Queue _focus = new("focus", Signatures.FocusCommand, 0x38);
    readonly Queue _agency = new("agency upgrade", Signatures.AgencyCommand, 0x38);
    (nint GameState, int Tag) _session;

    public override void Load()
    {
        var techClick = Sig.Find(Signatures.TechClick);
        var xorTechClick = Sig.Find(Signatures.XorTechClick);
        var focusClick = Sig.Find(Signatures.FocusClick);
        var agencyClick = Sig.Find(Signatures.AgencyClick);
        var agencyEntryRefresh = Sig.Find(Signatures.AgencyEntryRefresh);
        var agencyWindowRefresh = Sig.Find(Signatures.AgencyWindowRefresh);

        _techClick = Patches.Hook<TechClickFn>(techClick, (view, ev) => ShiftQueuesTech(ev) ? 0 : _techClick!.OriginalFunction(view, ev));
        _xorTechClick = Patches.Hook<TechClickFn>(xorTechClick, (view, ev) => ShiftQueuesTech(ev) ? 0 : _xorTechClick!.OriginalFunction(view, ev));
        _focusClick = Patches.Hook<FocusClickFn>(focusClick, (view, focus, a3) =>
        {
            if (focus != 0 && Gui.ShiftHeld) Toggle(_focus, focus);
            else _focusClick!.OriginalFunction(view, focus, a3);
        });
        _agencyClick = Patches.Hook<AgencyClickFn>(agencyClick, entry =>
        {
            if (!Gui.ShiftHeld) return _agencyClick!.OriginalFunction(entry);
            Toggle(_agency, *(nint*)(entry + UpgradeOfEntry));
            if (_agencyWindow != 0) _agencyWindowRefresh!.OriginalFunction(_agencyWindow); // renumber now, not on next reopen
            return 0;
        });
        _agencyEntryRefresh = Patches.Hook<EntryFn>(agencyEntryRefresh, entry =>
        {
            _agencyEntryRefresh!.OriginalFunction(entry);
            ShowPosition(*(nint*)(entry + WindowOfEntry), _agency, *(nint*)(entry + UpgradeOfEntry));
            KeepClickable(entry);
        });
        _agencyWindowRefresh = Patches.Hook<WindowFn>(agencyWindowRefresh, window =>
        {
            _agencyWindow = window;
            _agencyWindowRefresh!.OriginalFunction(window);
        });

        MainThread.Every(TimeSpan.FromMilliseconds(250), Pump);
    }

    bool ShiftQueuesTech(nint ev)
    {
        var status = *(nint*)(ev + StatusOfEvent);
        if (status == 0 || !Gui.ShiftHeld) return false;
        Toggle(_research, *(nint*)(status + TechOfStatus));
        return true;
    }

    void Toggle(Queue queue, nint item)
    {
        if (item == 0) return;
        if (queue.Items.Remove(item)) Log.Info($"{queue.Name}: unqueued, {queue.Items.Count} left");
        else
        {
            queue.Items.Add(item);
            Log.Info($"{queue.Name}: queued #{queue.Items.Count}");
        }
    }

    /// <summary>Writes the 1-based queue position of <paramref name="item"/> into the window's number box (empty if not queued).</summary>
    void ShowPosition(nint window, Queue queue, nint item)
    {
        var box = window == 0 ? 0 : Gui.FindChild(window, NumberBox);
        if (!_reportedNumberBox)
        {
            _reportedNumberBox = true;
            Log.Info(box != 0 ? "queue numbers: on" : $"queue numbers: no '{NumberBox}' element (is the Queues UI mod enabled?)");
        }
        if (box == 0) return;
        var index = queue.Items.IndexOf(item);
        Gui.SetText(box, index < 0 ? "" : (index + 1).ToString());
    }

    /// <summary>
    /// While another upgrade runs, the game hides every other upgrade's button (showing an icon in its place), so it can't be
    /// clicked, let alone shift+clicked. Keep the button of any upgrade that isn't running or finished: a plain click still
    /// does nothing, because the game's own command check rejects starting a second upgrade.
    /// </summary>
    static void KeepClickable(nint entry)
    {
        var agency = *(nint*)(entry + AgencyOfEntry);
        var upgrade = *(nint*)(entry + UpgradeOfEntry);
        var button = *(nint*)(entry + ButtonOfEntry);
        if (agency == 0 || upgrade == 0 || button == 0 || !Gui.IsHidden(button)) return;
        if (upgrade == *(nint*)(agency + AgencyUpgradeInProgress) || TimesDone(agency, upgrade) >= *(int*)(upgrade + MaxRepetitions)) return;
        Gui.Show(button);
    }

    static int TimesDone(nint agency, nint upgrade)
    {
        var pairs = *(nint*)(agency + Repetitions);
        for (int i = 0, n = *(int*)(agency + Repetitions + 0xC); i < n; i++)
            if (*(nint*)(pairs + 16 * i) == upgrade) return *(int*)(pairs + 16 * i + 8);
        return 0;
    }

    void Pump()
    {
        var tag = Game.PlayerTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        if (_session != (Game.State, tag)) // another game or country: queued pointers no longer apply
        {
            _session = (Game.State, tag);
            _research.Items.Clear();
            _focus.Items.Clear();
            _agency.Items.Clear();
        }

        var research = *(nint*)(country + ResearchOfCountry);
        if (research != 0 && _research.Ready)
            foreach (var slot in Native.PdxArray(research + Slots))
            {
                var current = *(nint*)(slot + TechOfSlot);
                if (current != 0) _research.Items.Remove(*(nint*)(current + TechOfStatus)); // started by hand
                else StartFirst(_research, tag, cmd =>
                {
                    *(int*)(cmd + ResearchSlot) = *(int*)(slot + IndexOfSlot);
                    *(byte*)(cmd + ResearchBonus) = 0;
                });
            }

        var focus = *(nint*)(country + FocusOfCountry);
        if (focus != 0 && _focus.Ready)
        {
            var current = *(nint*)(focus + FocusInProgress);
            if (current != 0) _focus.Items.Remove(current);
            else StartFirst(_focus, tag);
        }

        var agency = *(nint*)(country + AgencyOfCountry);
        if (agency != 0 && _agency.Ready && *(byte*)(agency + AgencyCreated) != 0 && *(nint*)(agency + AgencyUpgradeInProgress) == 0)
            StartFirst(_agency, tag);
    }

    /// <summary>Sends the first queued entry the game would accept (its own CanExecute), and drops it from the queue.</summary>
    void StartFirst(Queue queue, int tag, Action<nint>? fill = null)
    {
        foreach (var item in queue.Items)
        {
            var cmd = Commands.New(queue.Command, queue.CommandSize, tag, item);
            fill?.Invoke(cmd);
            if (!Commands.TryPost(cmd)) continue;
            queue.Items.Remove(item);
            queue.Cooldown = Environment.TickCount64 + CooldownMs;
            Log.Info($"{queue.Name}: started, {queue.Items.Count} left");
            return;
        }
    }

    sealed class Queue(string name, string command, int commandSize)
    {
        public string Name { get; } = name;
        public string Command { get; } = command;
        public int CommandSize { get; } = commandSize;
        public List<nint> Items { get; } = new();
        public long Cooldown;
        public bool Ready => Items.Count > 0 && Environment.TickCount64 >= Cooldown;
    }
}
