using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Queues;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.4), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// National focus tree: opens a clicked focus' popup (view, CNationalFocus*, bool, a4). Hooked instead of the popup's select
    /// button, which is disabled while another focus runs. 1.19.4 added a 4th (r9) argument.
    /// </summary>
    [Signature] public const string FocusClick = "48 85 D2 0F 84 ? ? ? ? 48 8B C4 48 89 58 08 48 89 68 18 48 89 70 20 57 41 56 41 57 48 81 EC B0 00 00 00";

    /// <summary>
    /// CNationalFocusView::UpdateNodeStates(view, float dt, cache): updates every focus item, every frame while the tree is
    /// open. Item names are set only once, when the items are built.
    /// </summary>
    [Signature] public const string FocusUpdateNodeStates = "48 89 4C 24 08 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC 98 05 00 00 4C 8B F9 33 C0 89 44 24 30 89 85 F8 04 00 00";

    /// <summary>CNationalFocusProgress::IsCompleted(progress, CNationalFocus*) -> bool.</summary>
    [Signature] public const string FocusCompleted = "48 89 54 24 10 48 83 EC 28 48 83 C1 58 4C 8D 44 24 38 48 8D 54 24 30 E8";

    /// <summary>
    /// Tech tree click handler (view, event): reads the clicked tech from the event. 1.19.4 has a single click path; the
    /// 1.19.3 "TechClick" twin no longer exists (no other handler in the cluster reads the event's tech).
    /// </summary>
    [Signature] public const string XorTechClick = "40 55 56 48 83 EC 28 80 3D";

    /// <summary>
    /// CCountryTechTreeView::UpdateAllItemStates(view): re-runs every displayed tech item's state, and so its bonus text.
    /// Hooked instead of the per-item bonus setter: after the original call every item is freshly labelled, so one loop
    /// over the items (view+0x2788, count view+0x27AC) re-stamps our queue numbers.
    /// </summary>
    [Signature] public const string TechViewUpdateAllItems = "40 53 57 41 54 41 55 41 56 48 83 EC 30 48 8B F9 E8 ? ? ? ? 80 3D ? ? ? ? 00 4C 8B E0";

    [VTable] public const string FocusCommand = "CSetNationalFocusCommand";
    [VTable] public const string ResearchCommand = "CSetResearchCommand";
}

/// <summary>
/// National focus and research queues. Shift+click a focus or a tech to queue or unqueue it. When a slot is idle, the first
/// entry the game accepts starts with the UI's own command, so multiplayer stays in sync. Queued entries show their place:
/// before a focus' name, on a tech's bonus text. Entries that can no longer start are dropped.
/// </summary>
[Plugin("com.green1052.queues", "Queues", "1.0.0", GameVersion = "1.19.4")]
public unsafe class Queues : Plugin
{
    // Layouts for HOI4 1.19.4.
    private const int FocusOfCountry = 0x1380, FocusInProgress = 0x10;    // CCountry -> focus progress -> CNationalFocus* (null = idle)
    private const int FocusItems = 0x3B80, FocusOfItem = 0x20;            // CNationalFocusView items; CNationalFocusTreeItem -> CNationalFocus
    // shortcut: 0x5F0 is the pointer passed as the popup's country; if wrong, shift-click only degrades to a normal click
    private const int ViewedCountryOfFocusView = 0x5F0, TagOfCountry = 8; // CCountry* shown, null = the player
    private const int Prerequisites = 0x570, FocusesOfGroup = 8;          // CNationalFocus: CPdxArray of prerequisite blocks (AND), each a CPdxArray of focuses (OR)
    private const int MutuallyExclusive = 0x588;                          // CNationalFocus: CPdxArray of blocks like the prerequisites'
    private const int WindowOfItem = 0x10;                                // focus tree and tech tree items: their window

    // shortcut: research offsets except StatusOfEvent/TechOfStatus/TechItems/TechItemCount are 1.19.3 values kept on an
    // unchanged research view; wrong ones only mis-number or fail to drop entries
    private const int ResearchOfCountry = 0xF60;                          // CCountry -> CResearch
    private const int Slots = 0xA0, TechOfSlot = 0x18, IndexOfSlot = 0x40; // CResearch: slots array; CResearchSlot -> tech status, its index
    private const int StatusOfEvent = 8, TechOfStatus = 0x160;            // click event -> CResearchStatus -> CTechnology*
    private const int Statuses = 0x88, IndexOfTech = 0x38, LevelOfStatus = 0x174, MaxLevel = 0x3DC; // CResearch status table; CTechnology
    private const int StatusOfTechItem = 0xC8, StateOfTechItem = 0xD0, TechResearching = 5; // CTechnologyTreeTechItem
    private const int ViewOfTechItem = 0x30, ViewedTagOfTechView = 0x1AA0; // item -> view; int tag shown, <= 0 = the player
    private const int TechItems = 0x2788, TechItemCount = 0x27AC;         // CCountryTechTreeView: item pointers, count
    private const int ResearchSlot = 56, ResearchBonus = 60;              // CSetResearchCommand fields after the target
    private const long SentTimeoutMs = 30_000;                            // a sent command not running by then failed: drop the entry

    [Function(CallingConventions.Microsoft)] public delegate void FocusClickFn(nint view, nint focus, byte a3, nint a4);
    [Function(CallingConventions.Microsoft)] public delegate void NodeStatesFn(nint view, float dt, nint cache);
    [Function(CallingConventions.Microsoft)] public delegate nint TechClickFn(nint view, nint ev);
    [Function(CallingConventions.Microsoft)] public delegate void EntryFn(nint view);

    private IHook<FocusClickFn>? _focusClick;
    private IHook<NodeStatesFn>? _focusNodeStates;
    private IHook<TechClickFn>? _xorTechClick;
    private IHook<EntryFn>? _techViewUpdateAllItems;
    private delegate* unmanaged<nint, nint, byte> _focusCompleted;
    private readonly HashSet<nint> _numberedFocusItems = []; // focus items whose name has our badge
    private readonly Queue _focus = new("focus", Signatures.FocusCommand, 0x38);
    private readonly Queue _research = new("research", Signatures.ResearchCommand, 0x40);
    private PerGame _game = null!;

    public override void Load()
    {
        var focusClick = Sig.Find(Signatures.FocusClick);
        var focusNodeStates = Sig.Find(Signatures.FocusUpdateNodeStates);
        _focusCompleted = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.FocusCompleted);
        _game = new PerGame(() => // another game or country: queued pointers no longer apply
        {
            _focus.Clear();
            _research.Clear();
            _numberedFocusItems.Clear();
        });

        _focusClick = Patches.Hook<FocusClickFn>(focusClick, (view, focus, a3, a4) =>
        {
            _game.Check();
            var viewed = *(nint*)(view + ViewedCountryOfFocusView);
            var ours = viewed == 0 || *(int*)(viewed + TagOfCountry) == Game.ControlledTag; // another country's tree: never queued (it could never start)
            if (!ours || focus == 0 || !Gui.ShiftHeld) _focusClick!.OriginalFunction(view, focus, a3, a4);
            else if (_focus.Items.Contains(focus)) UnqueueFocus(focus);
            else Toggle(_focus, focus);
        });
        _focusNodeStates = Patches.Hook<NodeStatesFn>(focusNodeStates, (view, dt, cache) =>
        {
            _focusNodeStates!.OriginalFunction(view, dt, cache);
            Log.Guard("focus numbers", (this, view), static s => s.Item1.LabelFocuses(s.view));
        });
        _xorTechClick = Patches.Hook<TechClickFn>(Sig.Find(Signatures.XorTechClick), (view, ev) =>
            ShiftQueuesTech(view, ev) ? 0 : _xorTechClick!.OriginalFunction(view, ev));
        _techViewUpdateAllItems = Patches.Hook<EntryFn>(Sig.Find(Signatures.TechViewUpdateAllItems), view =>
        {
            _techViewUpdateAllItems!.OriginalFunction(view);
            Log.Guard("tech numbers", (this, view), static s => s.Item1.LabelTechs(s.view));
        });

        MainThread.Every(TimeSpan.FromMilliseconds(250), Pump);
    }

    private bool ShiftQueuesTech(nint view, nint ev)
    {
        var status = *(nint*)(ev + StatusOfEvent);
        if (status == 0 || !Gui.ShiftHeld) return false;
        _game.Check();
        Toggle(_research, *(nint*)(status + TechOfStatus));
        _techViewUpdateAllItems!.OriginalFunction(view); // refresh the tree; LabelTechs follows in the hook
        return true;
    }

    /// <summary>Puts a queued tech's place on its freshly re-set "bonus" text, showing it if hidden.</summary>
    private void LabelTechs(nint view)
    {
        var items = *(nint*)(view + TechItems);
        var count = *(int*)(view + TechItemCount);
        for (var i = 0; i < count && items != 0; i++) LabelTech(*(nint*)(items + 8 * i));
    }

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
        if (focus == 0) return;
        var currentFocus = *(nint*)(focus + FocusInProgress);
        if (currentFocus != 0) _focus.Running(0, currentFocus);
        else StartFirst(_focus, 0, tag);

        // After the running checks above, which take an entry off when it starts.
        Drop(_research, tech => research != 0 && Researched(research, tech));
        Drop(_focus, f => Unreachable(focus, f));
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
    /// Sends the first entry the game accepts (its CanExecute) to the idle slot. It stays queued until it shows up running:
    /// a multiplayer command takes a round trip, and another one sent to the slot meanwhile would replace it.
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
        /// entry comes off the queue.
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
