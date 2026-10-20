using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// Queue number on intelligence agency upgrades; appended at the entry's end so it draws over the button.
[assembly: GuiPatch("interface/countryintelligenceagencyview.gui", "gui/agency_queue_number.gui", After = @"(?s)name = ""upgrade_entry"".*?(?=\n\t\})")]

namespace Queues;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CCountryTechTreeView click on a tech / on an XOR tech (view, event): event+8 is the CTechnologyStatus. Normally opens
    /// the tech's info window.
    /// </summary>
    [Signature] public const string TechClick = "48 89 5C 24 ? 57 48 83 EC 20 80 3D ? ? ? ? ? 48 8B F9";
    [Signature] public const string XorTechClick = "40 55 56 48 83 EC 28 80 3D";

    /// <summary>
    /// National focus tree: opens a clicked focus' popup (view, CNationalFocus*, bool). Hooked instead of the popup's select
    /// button, which is disabled while another focus runs.
    /// </summary>
    [Signature] public const string FocusClick = "48 85 D2 0F 84 ? ? ? ? 48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 41 56 41 57 48 83 EC 30";

    /// <summary>Intelligence agency upgrade entry click (entry): entry+UpgradeOfEntry is the upgrade.</summary>
    [Signature] public const string AgencyClick = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 30 48 8B F9 E8 ? ? ? ? 84 C0";

    /// <summary>Intelligence agency upgrade entry refresh (entry): fills entry+WindowOfEntry's texts and buttons.</summary>
    [Signature] public const string AgencyEntryRefresh = "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 38 01 00 00 0F 29 B4 24 ? ? ? ? 4C 8B F1";

    /// <summary>Agency upgrades window: refreshes every entry (window). Runs only when the window opens or something changes.</summary>
    [Signature] public const string AgencyWindowRefresh = "48 89 6C 24 ? 56 48 83 EC 20 48 8B 41 ? 48 8B B0";

    /// <summary>
    /// CTechnologyTreeTechItem::UpdateBonus(item): re-sets the item's "bonus" text (hidden and empty with no bonus) on every
    /// state change, so a number added after it never piles up.
    /// </summary>
    [Signature] public const string TechItemUpdateBonus = "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC 88 01 00 00 4C 8B E9";

    /// <summary>CCountryTechTreeView::UpdateAllItemStates(view): re-runs every displayed item's state, and so UpdateBonus.</summary>
    [Signature] public const string TechViewUpdateAllItems = "40 53 57 41 54 41 55 41 56 48 83 EC 30 48 8B F9 E8 ? ? ? ? 80 3D ? ? ? ? 00 4C 8B E0";

    /// <summary>
    /// CNationalFocusView::UpdateNodeStates(view, float dt, cache): updates every focus item, every frame while the tree is
    /// open. Item names are set only once, when the items are built.
    /// </summary>
    [Signature] public const string FocusUpdateNodeStates = "48 8B C4 48 89 58 ? 48 89 68 ? 56 57 41 54 41 56 41 57 48 83 EC 60 F3 0F 10 91 ? ? ? ? 45 32 FF";

    /// <summary>CNationalFocusProgress::IsCompleted(progress, CNationalFocus*) -> bool.</summary>
    [Signature] public const string FocusCompleted = "48 89 54 24 10 48 83 EC 28 48 83 C1 58 4C 8D 44 24 38 48 8D 54 24 30 E8";

    [VTable] public const string ResearchCommand = "CSetResearchCommand";
    [VTable] public const string FocusCommand = "CSetNationalFocusCommand";
    [VTable] public const string AgencyCommand = "CIntelligenceAgencyUpgradeCommand";
}

/// <summary>
/// Research, national focus and intelligence agency upgrade queues. Shift+click a tech, focus or upgrade to queue or
/// unqueue it. When a research slot, the focus or the agency is idle, the first entry the game accepts starts with the
/// UI's own command, so multiplayer stays in sync. Queued entries show their place: on a tech's bonus text, before a
/// focus' name, in the upgrade's number box (the GuiPatch above). Entries that can no longer start are dropped.
/// Also loads the political power queue (<see cref="PoliticalQueue"/>).
/// </summary>
[Plugin("com.green1052.queues", "Queues", "1.0.0", GameVersion = "1.19.3")]
public unsafe class Queues : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int ResearchOfCountry = 0xF60, Slots = 0xA0;            // CCountry -> research -> CPdxArray<CResearchSlot*>
    private const int TechOfSlot = 0x18, IndexOfSlot = 0x40;              // slot: CTechnologyStatus* in progress (null = idle), slot number
    private const int TechOfStatus = 0x160, LevelOfStatus = 0x174;       // CTechnologyStatus -> CTechnology, levels researched
    private const int Statuses = 0x88, IndexOfTech = 0x38, MaxLevel = 0x3DC; // research: CPdxArray<CTechnologyStatus*> by tech index - 1; CTechnology
    private const int FocusOfCountry = 0x1380, FocusInProgress = 0x10;    // CCountry -> focus progress -> CNationalFocus* (null = idle)
    private const int AgencyOfCountry = 0xFC0, AgencyCreated = 0xC0, AgencyUpgradeInProgress = 0xD0;
    private const int Repetitions = 96, MaxRepetitions = 540;             // agency: CPdxArray<{upgrade*, int done}>; upgrade: allowed count
    private const int StatusOfEvent = 8;                                  // tech tree click event -> CTechnologyStatus
    private const int WindowOfEntry = 16, UpgradeOfEntry = 40, ButtonOfEntry = 0x40, AgencyOfEntry = 4216; // agency upgrade entry
    private const int WindowOfItem = 0x10;                                // tech and focus tree items: their window
    private const int ViewOfTechItem = 0x30, StatusOfTechItem = 0xC8, StateOfTechItem = 0xD0; // CTechnologyTreeTechItem
    private const int TechResearching = 5;                               // tech item state
    private const int ViewedTagOfTechView = 0x1AA0;                       // int tag of the country shown, <= 0 = the player
    private const int FocusItems = 0x2058, FocusOfItem = 0x20;            // CNationalFocusView items; CNationalFocusTreeItem -> CNationalFocus
    private const int ViewedCountryOfFocusView = 0x20F8, TagOfCountry = 8; // CCountry* shown, null = the player
    private const int Prerequisites = 0x570, FocusesOfGroup = 8;          // CNationalFocus: CPdxArray of prerequisite blocks (AND), each a CPdxArray of focuses (OR)
    private const int MutuallyExclusive = 0x588;                          // CNationalFocus: CPdxArray of blocks like the prerequisites'
    private const int ResearchSlot = 56, ResearchBonus = 60;             // CSetResearchCommand fields after the target
    private const string NumberBox = "hoimod_queue";                      // added to the GUI by the GuiPatch above
    private const long SentTimeoutMs = 30_000;                            // a sent command not running by then failed: drop the entry

    [Function(CallingConventions.Microsoft)] public delegate nint TechClickFn(nint view, nint ev);
    [Function(CallingConventions.Microsoft)] public delegate void FocusClickFn(nint view, nint focus, byte a3);
    [Function(CallingConventions.Microsoft)] public delegate nint AgencyClickFn(nint entry);
    [Function(CallingConventions.Microsoft)] public delegate void EntryFn(nint entry);
    [Function(CallingConventions.Microsoft)] public delegate void WindowFn(nint window);
    [Function(CallingConventions.Microsoft)] public delegate void NodeStatesFn(nint view, float dt, nint cache);

    private IHook<TechClickFn>? _techClick, _xorTechClick;
    private IHook<FocusClickFn>? _focusClick;
    private IHook<AgencyClickFn>? _agencyClick;
    private IHook<EntryFn>? _agencyEntryRefresh;
    private IHook<WindowFn>? _agencyWindowRefresh;
    private IHook<EntryFn>? _techItemBonus;
    private IHook<NodeStatesFn>? _focusNodeStates;
    private delegate* unmanaged<nint, void> _techViewUpdateAllItems;
    private delegate* unmanaged<nint, nint, byte> _focusCompleted;
    private readonly HashSet<nint> _numberedFocusItems = []; // focus items whose name has our badge
    private nint _agencyWindow; // last refreshed agency upgrades window; still open whenever one of its entries is clicked
    private bool _reportedNumberBox;

    private readonly Queue _research = new("research", Signatures.ResearchCommand, 0x40);
    private readonly Queue _focus = new("focus", Signatures.FocusCommand, 0x38);
    private readonly Queue _agency = new("agency upgrade", Signatures.AgencyCommand, 0x38);
    private PerGame _game = null!;

    public override void Load()
    {
        var techClick = Sig.Find(Signatures.TechClick);
        var xorTechClick = Sig.Find(Signatures.XorTechClick);
        var focusClick = Sig.Find(Signatures.FocusClick);
        var agencyClick = Sig.Find(Signatures.AgencyClick);
        var agencyEntryRefresh = Sig.Find(Signatures.AgencyEntryRefresh);
        var agencyWindowRefresh = Sig.Find(Signatures.AgencyWindowRefresh);
        var techItemBonus = Sig.Find(Signatures.TechItemUpdateBonus);
        _techViewUpdateAllItems = (delegate* unmanaged<nint, void>)Sig.Find(Signatures.TechViewUpdateAllItems);
        var focusNodeStates = Sig.Find(Signatures.FocusUpdateNodeStates);
        _focusCompleted = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.FocusCompleted);
        _game = new PerGame(() => // another game or country: queued pointers no longer apply
        {
            _research.Clear();
            _focus.Clear();
            _agency.Clear();
            _numberedFocusItems.Clear();
        });

        _techClick = Patches.Hook<TechClickFn>(techClick, (view, ev) => ShiftQueuesTech(view, ev) ? 0 : _techClick!.OriginalFunction(view, ev));
        _xorTechClick = Patches.Hook<TechClickFn>(xorTechClick, (view, ev) => ShiftQueuesTech(view, ev) ? 0 : _xorTechClick!.OriginalFunction(view, ev));
        _techItemBonus = Patches.Hook<EntryFn>(techItemBonus, item =>
        {
            _techItemBonus!.OriginalFunction(item);
            Log.Guard("tech numbers", (this, item), static s => s.Item1.LabelTech(s.item));
        });
        _focusClick = Patches.Hook<FocusClickFn>(focusClick, (view, focus, a3) =>
        {
            _game.Check();
            var viewed = *(nint*)(view + ViewedCountryOfFocusView);
            var ours = viewed == 0 || *(int*)(viewed + TagOfCountry) == Game.ControlledTag; // another country's tree: never queued (it could never start)
            if (!ours || focus == 0 || !Gui.ShiftHeld) _focusClick!.OriginalFunction(view, focus, a3);
            else if (_focus.Items.Contains(focus)) UnqueueFocus(focus);
            else Toggle(_focus, focus);
        });
        _focusNodeStates = Patches.Hook<NodeStatesFn>(focusNodeStates, (view, dt, cache) =>
        {
            _focusNodeStates!.OriginalFunction(view, dt, cache);
            Log.Guard("focus numbers", (this, view), static s => s.Item1.LabelFocuses(s.view));
        });
        _agencyClick = Patches.Hook<AgencyClickFn>(agencyClick, entry =>
        {
            if (!Gui.ShiftHeld) return _agencyClick!.OriginalFunction(entry);
            _game.Check();
            AddLevel(*(nint*)(entry + AgencyOfEntry), *(nint*)(entry + UpgradeOfEntry));
            if (_agencyWindow != 0) _agencyWindowRefresh!.OriginalFunction(_agencyWindow); // renumber now, not on next reopen
            return 0;
        });
        _agencyEntryRefresh = Patches.Hook<EntryFn>(agencyEntryRefresh, entry =>
        {
            _agencyEntryRefresh!.OriginalFunction(entry);
            Log.Guard("agency numbers", (this, entry), static s =>
            {
                s.Item1.ShowPosition(*(nint*)(s.entry + WindowOfEntry), s.Item1._agency, *(nint*)(s.entry + UpgradeOfEntry));
                KeepClickable(s.entry);
            });
        });
        _agencyWindowRefresh = Patches.Hook<WindowFn>(agencyWindowRefresh, window =>
        {
            _agencyWindow = window;
            _agencyWindowRefresh!.OriginalFunction(window);
        });

        MainThread.Every(TimeSpan.FromMilliseconds(250), Pump);
        if (Config.Bind("Queues", "PoliticalPower", true, "Shift+click an idea, law, advisor or decision to take it once you have the political power."))
            new PoliticalQueue(Log).Load();
    }

    private bool ShiftQueuesTech(nint view, nint ev)
    {
        var status = *(nint*)(ev + StatusOfEvent);
        if (status == 0 || !Gui.ShiftHeld) return false;
        _game.Check();
        Toggle(_research, *(nint*)(status + TechOfStatus));
        _techViewUpdateAllItems(view); // renumber every item now
        return true;
    }

    /// <summary>Puts a queued tech's place on its freshly re-set "bonus" text, showing it if hidden.</summary>
    private void LabelTech(nint item)
    {
        var status = *(nint*)(item + StatusOfTechItem);
        var view = *(nint*)(item + ViewOfTechItem);
        if (status == 0 || view == 0) return;
        var viewed = *(int*)(view + ViewedTagOfTechView);
        if (viewed > 0 && viewed != Game.ControlledTag) return;
        var position = _research.Items.IndexOf(*(nint*)(status + TechOfStatus)) + 1;
        if (position == 0 || *(int*)(item + StateOfTechItem) == TechResearching) return;
        var box = Gui.FindTextBox(*(nint*)(item + WindowOfItem), "bonus"); // every item but secret weapons has one
        if (box == 0) return;
        Gui.Number(box, position);
        if (Gui.IsHidden(box)) Gui.Show(box);
    }

    /// <summary>
    /// Keeps the number in front of queued focuses' names and takes it off unqueued ones. Runs every frame while the tree is
    /// open, touching only the items that changed.
    /// </summary>
    private void LabelFocuses(nint view)
    {
        var viewed = *(nint*)(view + ViewedCountryOfFocusView);
        if (viewed != 0 && *(int*)(viewed + TagOfCountry) != Game.ControlledTag) return;
        foreach (var item in Native.PdxArray(view + FocusItems))
        {
            var position = _focus.Items.IndexOf(*(nint*)(item + FocusOfItem)) + 1;
            if (position == 0 && !_numberedFocusItems.Remove(item)) continue;
            var box = Gui.FindTextBox(*(nint*)(item + WindowOfItem), "name");
            if (box == 0) continue;
            Gui.Number(box, position);
            if (position > 0) _numberedFocusItems.Add(item);
        }
    }

    /// <summary>
    /// Unqueues <paramref name="focus"/> and every queued focus that needed it: a prerequisite block that held an unqueued
    /// focus and has no option left completed, running or queued.
    /// </summary>
    private void UnqueueFocus(nint focus)
    {
        var country = Game.Country(Game.ControlledTag);
        var progress = country == 0 ? 0 : *(nint*)(country + FocusOfCountry);
        var removed = new HashSet<nint> { focus };
        _focus.Items.Remove(focus);
        bool Satisfied(nint f) => _focus.Items.Contains(f) || progress != 0 && (*(nint*)(progress + FocusInProgress) == f || _focusCompleted(progress, f) != 0);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var queued in _focus.Items.ToList())
            {
                var lost = false;
                foreach (var block in Native.PdxArray(queued + Prerequisites))
                {
                    var options = Native.PdxArray(block + FocusesOfGroup);
                    var hit = false;
                    var met = false;
                    foreach (var option in options)
                    {
                        hit |= removed.Contains(option);
                        met |= Satisfied(option);
                    }
                    if (hit && !met) lost = true;
                }
                if (!lost) continue;
                _focus.Items.Remove(queued);
                removed.Add(queued);
                changed = true;
            }
        }
        Log.Info($"focus: unqueued {removed.Count} (with what needed it), {_focus.Items.Count} left");
    }

    private void Toggle(Queue queue, nint item)
    {
        if (item == 0) return;
        if (queue.Items.Remove(item)) Log.Info($"{queue.Name}: unqueued, {queue.Items.Count} left");
        else
        {
            queue.Items.Add(item);
            Log.Info($"{queue.Name}: queued #{queue.Items.Count}");
        }
    }

    /// <summary>
    /// Queues one more level of an agency upgrade, up to the levels left; past that, unqueues it all. A one-level upgrade
    /// toggles.
    /// </summary>
    private void AddLevel(nint agency, nint upgrade)
    {
        if (agency == 0 || upgrade == 0) return;
        var queued = _agency.Items.Count(i => i == upgrade);
        var left = LevelsLeft(agency, upgrade);
        if (queued < left)
        {
            _agency.Items.Add(upgrade);
            Log.Info($"agency upgrade: queued #{_agency.Items.Count} ({queued + 1} of {left} levels left)");
        }
        else if (queued > 0)
        {
            _agency.Items.RemoveAll(i => i == upgrade);
            Log.Info($"agency upgrade: unqueued, {_agency.Items.Count} left");
        }
    }

    /// <summary>Writes the 1-based queue position of <paramref name="item"/> into the window's number box (empty if not queued).</summary>
    private void ShowPosition(nint window, Queue queue, nint item)
    {
        var box = window == 0 ? 0 : Gui.FindTextBox(window, NumberBox);
        if (!_reportedNumberBox)
        {
            _reportedNumberBox = true;
            Log.Info(box != 0 ? "queue numbers: on" : $"queue numbers: no '{NumberBox}' element (see GameFiles in this log)");
        }
        if (box == 0) return;
        var index = queue.Items.IndexOf(item);
        var levels = queue.Items.Count(i => i == item);
        Gui.SetText(box, index < 0 ? "" : levels > 1 ? $"{index + 1}x{levels}" : (index + 1).ToString());
    }

    /// <summary>
    /// While an upgrade runs, the game hides the other upgrades' buttons, so they can't be shift+clicked. Shows them again
    /// for upgrades with levels left; a plain click still does nothing, as the game rejects a second upgrade.
    /// </summary>
    private static void KeepClickable(nint entry)
    {
        var agency = *(nint*)(entry + AgencyOfEntry);
        var upgrade = *(nint*)(entry + UpgradeOfEntry);
        var button = *(nint*)(entry + ButtonOfEntry);
        if (agency == 0 || upgrade == 0 || button == 0 || !Gui.IsHidden(button)) return;
        if (LevelsLeft(agency, upgrade) <= 0) return; // the running upgrade too, if it has levels after this one
        Gui.Show(button);
    }

    /// <summary>Levels of <paramref name="upgrade"/> not done nor running.</summary>
    private static int LevelsLeft(nint agency, nint upgrade) =>
        *(int*)(upgrade + MaxRepetitions) - TimesDone(agency, upgrade) - (*(nint*)(agency + AgencyUpgradeInProgress) == upgrade ? 1 : 0);

    private static int TimesDone(nint agency, nint upgrade)
    {
        foreach (var done in Native.PdxArray<Repetition>(agency + Repetitions))
            if (done.Upgrade == upgrade) return done.Times;
        return 0;
    }

    private readonly record struct Repetition(nint Upgrade, int Times); // 16 bytes, padded like the game's pair

    private void Pump()
    {
        if (!Game.HasStarted) return; // menus, lobby, loading: no commands
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;

        var research = *(nint*)(country + ResearchOfCountry);
        if (research != 0)
            foreach (var slot in Native.PdxArray(research + Slots))
            {
                var index = *(int*)(slot + IndexOfSlot);
                var current = *(nint*)(slot + TechOfSlot);
                if (current != 0) _research.Running(index, *(nint*)(current + TechOfStatus));
                else StartFirst(_research, index, tag, cmd =>
                {
                    *(int*)(cmd + ResearchSlot) = index;
                    *(byte*)(cmd + ResearchBonus) = 0;
                });
            }

        var focus = *(nint*)(country + FocusOfCountry);
        if (focus != 0)
        {
            var current = *(nint*)(focus + FocusInProgress);
            if (current != 0) _focus.Running(0, current);
            else StartFirst(_focus, 0, tag);
        }

        var agency = *(nint*)(country + AgencyOfCountry);
        if (agency != 0 && *(byte*)(agency + AgencyCreated) != 0)
        {
            var current = *(nint*)(agency + AgencyUpgradeInProgress);
            if (current != 0) _agency.Running(0, current);
            else StartFirst(_agency, 0, tag);
        }

        // After the running checks above, which take an entry off when it starts.
        Drop(_research, tech => research != 0 && Researched(research, tech));
        Drop(_focus, f => focus != 0 && Unreachable(focus, f));
        if (agency == 0) return;
        foreach (var upgrade in _agency.Items.Distinct().ToList()) // levels done by hand: keep no more than are left
            while (_agency.Items.Count(i => i == upgrade) > Math.Max(0, LevelsLeft(agency, upgrade))) _agency.Items.RemoveAt(_agency.Items.LastIndexOf(upgrade));
    }

    /// <summary>Unqueues the entries that can no longer start.</summary>
    private void Drop(Queue queue, Func<nint, bool> stale)
    {
        var dropped = queue.Items.RemoveAll(i => stale(i));
        if (dropped > 0) Log.Info($"{queue.Name}: dropped {dropped} that can no longer start, {queue.Items.Count} left");
    }

    /// <summary>Every level of <paramref name="tech"/> is researched; the first test of the game's CanResearch.</summary>
    private static bool Researched(nint research, nint tech)
    {
        var index = *(int*)(tech + IndexOfTech) - 1;
        var statuses = Native.PdxArray(research + Statuses);
        return index >= 0 && index < statuses.Length && statuses[index] != 0 && *(int*)(statuses[index] + LevelOfStatus) >= *(int*)(tech + MaxLevel);
    }

    /// <summary>
    /// The focus is taken, shut out by a taken mutually exclusive one, or has a prerequisite block whose options are all
    /// shut out. "Taken" is IsCompleted (done or running), as in the game's availability check (CNationalFocus vtable slot 9).
    /// </summary>
    private bool Unreachable(nint progress, nint focus)
    {
        if (_focusCompleted(progress, focus) != 0 || ShutOut(progress, focus)) return true;
        foreach (var block in Native.PdxArray(focus + Prerequisites))
        {
            var options = Native.PdxArray(block + FocusesOfGroup);
            var open = options.Length == 0;
            foreach (var option in options) open |= !ShutOut(progress, option);
            if (!open) return true;
        }
        return false;
    }

    private bool ShutOut(nint progress, nint focus)
    {
        foreach (var block in Native.PdxArray(focus + MutuallyExclusive))
            foreach (var other in Native.PdxArray(block + FocusesOfGroup))
                if (_focusCompleted(progress, other) != 0) return true;
        return false;
    }

    /// <summary>
    /// Sends the first entry the game accepts (its CanExecute) to the idle <paramref name="slot"/>. It stays queued until it
    /// shows up running: a multiplayer command takes a round trip, and another one sent to the slot meanwhile would replace it.
    /// </summary>
    private void StartFirst(Queue queue, int slot, int tag, Action<nint>? fill = null)
    {
        var now = Environment.TickCount64;
        queue.Idle(slot);
        if (queue.Sent.TryGetValue(slot, out var sent))
        {
            if (now < sent.Until) return;
            queue.Sent.Remove(slot); // taken but never started: stop waiting on it
            if (queue.Items.Remove(sent.Item)) Log.Info($"{queue.Name}: dropped an entry that never started");
        }
        foreach (var item in queue.Items)
        {
            if (queue.Sent.Any(s => s.Key != slot && s.Value.Item == item && now < s.Value.Until)) continue; // on its way to another slot
            var cmd = Commands.New(queue.Command, queue.CommandSize, tag, item);
            fill?.Invoke(cmd);
            if (!Commands.TryPost(cmd)) continue;
            queue.Sent[slot] = (item, now + SentTimeoutMs);
            Log.Info($"{queue.Name}: started #{queue.Items.IndexOf(item) + 1}");
            return;
        }
    }

    private sealed class Queue(string name, string command, int commandSize)
    {
        public string Name { get; } = name;
        public string Command { get; } = command;
        public int CommandSize { get; } = commandSize;
        public List<nint> Items { get; } = [];
        public Dictionary<int, (nint Item, long Until)> Sent { get; } = []; // by slot: sent, not seen running yet
        private readonly Dictionary<int, nint> _running = []; // by slot: what was running at the last pump

        /// <summary>
        /// <paramref name="item"/> runs in <paramref name="slot"/>, sent by us or started by the player. When it starts, one
        /// entry comes off the queue (repeatable agency upgrades are queued once per level).
        /// </summary>
        public void Running(int slot, nint item)
        {
            if (_running.TryGetValue(slot, out var was) && was == item) return;
            _running[slot] = item;
            Sent.Remove(slot);
            foreach (var other in Sent.Where(s => s.Value.Item == item).Select(s => s.Key).ToList()) Sent.Remove(other); // started elsewhere
            Items.Remove(item);
        }

        /// <summary><paramref name="slot"/> runs nothing.</summary>
        public void Idle(int slot) => _running.Remove(slot);

        public void Clear()
        {
            Items.Clear();
            Sent.Clear();
            _running.Clear();
        }
    }
}
