using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Queues;

/// <summary>Byte patterns and class names (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
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

    /// <summary>Intelligence agency upgrade button (button): button+UpgradeOfButton is the upgrade.</summary>
    [Signature] public const string AgencyClick = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 30 48 8B F9 E8 ? ? ? ? 84 C0";

    /// <summary>Intelligence agency upgrade entry refresh (entry): fills entry+WindowOfEntry's texts for entry+UpgradeOfButton.</summary>
    [Signature] public const string AgencyEntryRefresh = "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 38 01 00 00 0F 29 B4 24 ? ? ? ? 4C 8B F1";

    /// <summary>Agency upgrades window: refresh every entry (window). Only runs when the window opens or the game changes something.</summary>
    [Signature] public const string AgencyWindowRefresh = "48 89 6C 24 ? 56 48 83 EC 20 48 8B 41 ? 48 8B B0";

    /// <summary>CSession::PostCommand(session, command, force) (clausewitz session.cpp).</summary>
    [Signature] public const string PostCommand = "48 89 5C 24 ? 57 48 81 EC 30 01 00 00 48 8B FA 48 8B D9 45 84 C0";

    /// <summary>Sends a command the way the UI does: `mov rcx,[rip+SessionHolder]; mov rax,[rcx]; call [rax+88h]` gets the session.</summary>
    [Signature] public const string SessionHolder = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 8B FA 48 8B F1 48 8B 0D ? ? ? ? 48 8B 01 FF 90 88 00 00 00 48 8B D8 B9 48 00 00 00 E8";

    /// <summary>
    /// Country by tag: `mov rdi,[rip+GameState]; ...; call TagIndex; ...; mov rax,[rdi+Countries]; mov rax,[rax+rcx*8]`.
    /// Twice: the const and non-const getter, both identical.
    /// </summary>
    [Signature(2)] public const string CountryLookup = "83 3B 00 48 8B 3D ?? ?? ?? ?? 75 04 33 C0 EB 08 48 8B CB E8 ?? ?? ?? ?? 48 8B 5C 24 48 48 63 C8 48 8B 87 ?? ?? ?? ?? 48 8B 04 C8";

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
    const int PlayerTag = 0x520, ObserverTag = 0x524;      // gamestate: int country tags
    const int ResearchOfCountry = 0xF60;                     // CCountry -> research
    const int Slots = 0xA0;                                  // research: CPdxArray<CResearchSlot*>
    const int TechOfSlot = 0x18, IndexOfSlot = 0x40;          // slot: CTechnologyStatus* in progress (null = idle), slot number
    const int TechOfStatus = 0x160;                          // CTechnologyStatus -> CTechnology
    const int FocusOfCountry = 0x1380, FocusInProgress = 0x10; // CCountry -> focus progress -> CNationalFocus* (null = idle)
    const int AgencyOfCountry = 0xFC0, AgencyCreated = 0xC0, AgencyUpgradeInProgress = 0xD0;
    const int StatusOfEvent = 8, UpgradeOfButton = 40, WindowOfEntry = 16;
    const int ButtonOfEntry = 0x40, AgencyOfEntry = 4216;    // agency upgrade entry: the clickable button, the CIntelligenceAgency
    const int Repetitions = 96, MaxRepetitions = 540;         // agency: CPdxArray<{upgrade*, int done}>; upgrade: allowed count
    const int Hidden = 117, HiddenFlag = 0x10, ShowSlot = 15; // GUI object: flags byte, Show() vtable slot
    const string NumberBox = "hoimod_queue"; // added to the GUI by the Queues UI mod
    const int CommandTag = 40, CommandTarget = 48, ResearchSlot = 56, ResearchBonus = 60;
    const int VK_SHIFT = 0x10;
    const long CooldownMs = 1500; // a posted command takes a tick or two to execute

    [Function(CallingConventions.Microsoft)] public delegate nint TechClickFn(nint view, nint ev);
    [Function(CallingConventions.Microsoft)] public delegate void FocusClickFn(nint view, nint focus, byte a3);
    [Function(CallingConventions.Microsoft)] public delegate nint AgencyClickFn(nint button);
    [Function(CallingConventions.Microsoft)] public delegate void EntryFn(nint entry);
    [Function(CallingConventions.Microsoft)] public delegate void WindowFn(nint window);
    [Function(CallingConventions.Microsoft)] public delegate int PeekMessageFn(nint msg, nint hwnd, uint min, uint max, uint remove);

    IHook<TechClickFn>? _techClick, _xorTechClick;
    bool _reportedNumberBox;
    IHook<FocusClickFn>? _focusClick;
    IHook<AgencyClickFn>? _agencyClick;
    IHook<EntryFn>? _agencyEntryRefresh;
    IHook<WindowFn>? _agencyWindowRefresh;
    nint _agencyWindow; // last refreshed agency upgrades window, valid while it's open (i.e. whenever one of its entries is clicked)
    IHook<PeekMessageFn>? _peekMessage;

    delegate* unmanaged<nint, nint, byte, byte> _post;
    delegate* unmanaged<nint, int> _tagIndex;
    nint* _sessionHolder, _gameState;
    int _countries, _mainThread;
    nint _researchVt, _focusVt, _agencyVt;

    readonly Queue _research = new("research"), _focus = new("focus"), _agency = new("agency upgrade");
    long _nextPump;
    (nint GameState, int Tag) _session;

    public override void Load()
    {
        var techClick = Sig.Find(Signatures.TechClick);
        var xorTechClick = Sig.Find(Signatures.XorTechClick);
        var focusClick = Sig.Find(Signatures.FocusClick);
        var agencyClick = Sig.Find(Signatures.AgencyClick);
        var agencyEntryRefresh = Sig.Find(Signatures.AgencyEntryRefresh);
        var agencyWindowRefresh = Sig.Find(Signatures.AgencyWindowRefresh);
        var holder = Sig.Find(Signatures.SessionHolder) + 0x15;  // mov rcx, [rip+disp32]
        var lookup = Sig.FindAll(Signatures.CountryLookup, 2)[0];
        _post = (delegate* unmanaged<nint, nint, byte, byte>)Sig.Find(Signatures.PostCommand);
        _sessionHolder = (nint*)(holder + 7 + *(int*)(holder + 3));
        _gameState = (nint*)(lookup + 10 + *(int*)(lookup + 6));
        _tagIndex = (delegate* unmanaged<nint, int>)(lookup + 24 + *(int*)(lookup + 20));
        _countries = *(int*)(lookup + 35);
        _researchVt = Sig.VTable(Signatures.ResearchCommand);
        _focusVt = Sig.VTable(Signatures.FocusCommand);
        _agencyVt = Sig.VTable(Signatures.AgencyCommand);
        _mainThread = Environment.CurrentManagedThreadId; // plugins load on the game's main (UI) thread

        _techClick = Patches.Hook<TechClickFn>(techClick, (view, ev) => ShiftQueuesTech(ev) ? 0 : _techClick!.OriginalFunction(view, ev));
        _xorTechClick = Patches.Hook<TechClickFn>(xorTechClick, (view, ev) => ShiftQueuesTech(ev) ? 0 : _xorTechClick!.OriginalFunction(view, ev));
        _focusClick = Patches.Hook<FocusClickFn>(focusClick, (view, focus, a3) =>
        {
            if (focus != 0 && Shift()) Toggle(_focus, focus);
            else _focusClick!.OriginalFunction(view, focus, a3);
        });
        _agencyClick = Patches.Hook<AgencyClickFn>(agencyClick, button =>
        {
            if (!Shift()) return _agencyClick!.OriginalFunction(button);
            Toggle(_agency, *(nint*)(button + UpgradeOfButton));
            if (_agencyWindow != 0) _agencyWindowRefresh!.OriginalFunction(_agencyWindow); // renumber now, not on next reopen
            return 0;
        });

        _agencyEntryRefresh = Patches.Hook<EntryFn>(agencyEntryRefresh, entry =>
        {
            _agencyEntryRefresh!.OriginalFunction(entry);
            ShowPosition(*(nint*)(entry + WindowOfEntry), _agency, *(nint*)(entry + UpgradeOfButton));
            KeepClickable(entry);
        });

        _agencyWindowRefresh = Patches.Hook<WindowFn>(agencyWindowRefresh, window =>
        {
            _agencyWindow = window;
            _agencyWindowRefresh!.OriginalFunction(window);
        });

        // The main loop pumps window messages every frame: a safe place to send commands from the UI thread.
        var peek = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "PeekMessageW");
        _peekMessage = Patches.Hook<PeekMessageFn>(peek, (msg, hwnd, min, max, remove) =>
        {
            var result = _peekMessage!.OriginalFunction(msg, hwnd, min, max, remove);
            if (Environment.CurrentManagedThreadId == _mainThread && Environment.TickCount64 >= _nextPump)
            {
                _nextPump = Environment.TickCount64 + 250;
                Pump();
            }
            return result;
        });
    }

    bool ShiftQueuesTech(nint ev)
    {
        var status = *(nint*)(ev + StatusOfEvent);
        if (status == 0 || !Shift()) return false;
        Toggle(_research, *(nint*)(status + TechOfStatus));
        return true;
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
        var upgrade = *(nint*)(entry + UpgradeOfButton);
        var button = *(nint*)(entry + ButtonOfEntry);
        if (agency == 0 || upgrade == 0 || button == 0 || (*(byte*)(button + Hidden) & HiddenFlag) == 0) return;
        if (upgrade == *(nint*)(agency + AgencyUpgradeInProgress) || TimesDone(agency, upgrade) >= *(int*)(upgrade + MaxRepetitions)) return;
        *(byte*)(button + Hidden) &= unchecked((byte)~HiddenFlag);
        ((delegate* unmanaged<nint, void>)(*(nint**)button)[ShowSlot])(button);
    }

    static int TimesDone(nint agency, nint upgrade)
    {
        var pairs = *(nint*)(agency + Repetitions);
        for (int i = 0, n = *(int*)(agency + Repetitions + 0xC); i < n; i++)
            if (*(nint*)(pairs + 16 * i) == upgrade) return *(int*)(pairs + 16 * i + 8);
        return 0;
    }

    static bool Shift() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

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

    void Pump()
    {
        var gs = *_gameState;
        if (gs == 0) return;
        int tag = *(int*)(gs + PlayerTag) > 0 ? *(int*)(gs + PlayerTag) : *(int*)(gs + ObserverTag);
        if (tag <= 0) return;
        if (_session != (gs, tag)) // another game or country: queued pointers no longer apply
        {
            _session = (gs, tag);
            _research.Items.Clear();
            _focus.Items.Clear();
            _agency.Items.Clear();
        }
        var country = *(nint*)(*(nint*)(gs + _countries) + 8L * _tagIndex((nint)(&tag)));
        if (country == 0) return;

        var research = *(nint*)(country + ResearchOfCountry);
        if (research != 0 && Ready(_research))
            foreach (var slot in Native.PdxArray(research + Slots))
            {
                var current = *(nint*)(slot + TechOfSlot);
                if (current != 0) _research.Items.Remove(*(nint*)(current + TechOfStatus)); // started by hand
                else StartFirst(_research, _researchVt, 0x40, tag, cmd =>
                {
                    *(int*)(cmd + ResearchSlot) = *(int*)(slot + IndexOfSlot);
                    *(byte*)(cmd + ResearchBonus) = 0;
                });
            }

        var focus = *(nint*)(country + FocusOfCountry);
        if (focus != 0 && Ready(_focus))
        {
            var current = *(nint*)(focus + FocusInProgress);
            if (current != 0) _focus.Items.Remove(current);
            else StartFirst(_focus, _focusVt, 0x38, tag, null);
        }

        var agency = *(nint*)(country + AgencyOfCountry);
        if (agency != 0 && Ready(_agency) && *(byte*)(agency + AgencyCreated) != 0 && *(nint*)(agency + AgencyUpgradeInProgress) == 0)
            StartFirst(_agency, _agencyVt, 0x38, tag, null);
    }

    static bool Ready(Queue q) => q.Items.Count > 0 && Environment.TickCount64 >= q.Cooldown;

    /// <summary>Sends the first queued entry the game would accept (its own CanExecute), and drops it from the queue.</summary>
    void StartFirst(Queue queue, nint vtable, int size, int tag, Action<nint>? fill)
    {
        foreach (var item in queue.Items)
        {
            var cmd = NewCommand(vtable, size, tag, item);
            fill?.Invoke(cmd);
            var canExecute = (delegate* unmanaged<nint, byte>)(*(nint**)cmd)[9];
            if (canExecute(cmd) == 0)
            {
                NativeMemory.Free((void*)cmd);
                continue;
            }
            queue.Items.Remove(item);
            queue.Cooldown = Environment.TickCount64 + CooldownMs;
            var holder = *_sessionHolder;
            var session = ((delegate* unmanaged<nint, nint>)(*(nint**)holder)[17])(holder); // +88h
            _post(session, cmd, 0); // the session owns (and eventually frees) the command
            Log.Info($"{queue.Name}: started, {queue.Items.Count} left");
            return;
        }
    }

    /// <summary>
    /// What the game's command constructors do (CCommand base, then tag + target). The memory comes from the CRT heap,
    /// the same process heap hoi4.exe's statically linked CRT frees commands from.
    /// </summary>
    static nint NewCommand(nint vtable, int size, int tag, nint target)
    {
        var cmd = (nint)NativeMemory.AllocZeroed((nuint)size);
        *(nint*)cmd = vtable;
        *(int*)(cmd + 12) = -1;
        *(int*)(cmd + 20) = -65536;
        *(int*)(cmd + CommandTag) = tag;
        *(nint*)(cmd + CommandTarget) = target;
        return cmd;
    }

    sealed class Queue(string name)
    {
        public string Name { get; } = name;
        public List<nint> Items { get; } = new();
        public long Cooldown;
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);
}
