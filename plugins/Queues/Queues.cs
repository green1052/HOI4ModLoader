using System.Runtime.InteropServices;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Queues;

/// <summary>Byte patterns and class names (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>CCountryTechTreeView: start researching a tech (view, CTechnologyStatus*, bonus, ...).</summary>
    [Signature] public const string ResearchClick = "48 8B C4 4C 89 48 ? 44 88 40 ? 48 89 50 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D A8";

    /// <summary>CNationalFocusDetailView button handler (view, event): event+8 == 0 selects view+FocusOfView.</summary>
    [Signature] public const string FocusClick = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 30 48 8B F9 8B 4A";

    /// <summary>Intelligence agency upgrade button (button): button+UpgradeOfButton is the upgrade.</summary>
    [Signature] public const string AgencyClick = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 30 48 8B F9 E8 ? ? ? ? 84 C0";

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
    const int FocusOfView = 3920, UpgradeOfButton = 40;
    const int CommandTag = 40, CommandTarget = 48, ResearchSlot = 56, ResearchBonus = 60;
    const int VK_SHIFT = 0x10;
    const long CooldownMs = 1500; // a posted command takes a tick or two to execute

    [Function(CallingConventions.Microsoft)] public delegate void ResearchClickFn(nint view, nint techStatus, byte bonus, nint a4);
    [Function(CallingConventions.Microsoft)] public delegate void FocusClickFn(nint view, nint ev);
    [Function(CallingConventions.Microsoft)] public delegate nint AgencyClickFn(nint button);
    [Function(CallingConventions.Microsoft)] public delegate int PeekMessageFn(nint msg, nint hwnd, uint min, uint max, uint remove);

    IHook<ResearchClickFn>? _researchClick;
    IHook<FocusClickFn>? _focusClick;
    IHook<AgencyClickFn>? _agencyClick;
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
        var researchClick = Sig.Find(Signatures.ResearchClick);
        var focusClick = Sig.Find(Signatures.FocusClick);
        var agencyClick = Sig.Find(Signatures.AgencyClick);
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

        _researchClick = Patches.Hook<ResearchClickFn>(researchClick, (view, status, bonus, a4) =>
        {
            if (Shift()) Toggle(_research, *(nint*)(status + TechOfStatus));
            else _researchClick!.OriginalFunction(view, status, bonus, a4);
        });
        _focusClick = Patches.Hook<FocusClickFn>(focusClick, (view, ev) =>
        {
            if (*(int*)(ev + 8) == 0 && Shift()) Toggle(_focus, *(nint*)(view + FocusOfView));
            else _focusClick!.OriginalFunction(view, ev);
        });
        _agencyClick = Patches.Hook<AgencyClickFn>(agencyClick, button =>
        {
            if (!Shift()) return _agencyClick!.OriginalFunction(button);
            Toggle(_agency, *(nint*)(button + UpgradeOfButton));
            return 0;
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
