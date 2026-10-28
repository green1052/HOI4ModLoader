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

    [VTable] public const string FocusCommand = "CSetNationalFocusCommand";
}

/// <summary>
/// National focus queue. Shift+click a focus to queue or unqueue it. When the focus slot is idle, the first entry the game
/// accepts starts with the UI's own command, so multiplayer stays in sync. Queued entries show their place before the focus'
/// name. Entries that can no longer start are dropped.
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
    private const int WindowOfItem = 0x10;                                // focus tree items: their window
    private const long SentTimeoutMs = 30_000;                            // a sent command not running by then failed: drop the entry

    [Function(CallingConventions.Microsoft)] public delegate void FocusClickFn(nint view, nint focus, byte a3, nint a4);
    [Function(CallingConventions.Microsoft)] public delegate void NodeStatesFn(nint view, float dt, nint cache);

    private IHook<FocusClickFn>? _focusClick;
    private IHook<NodeStatesFn>? _focusNodeStates;
    private delegate* unmanaged<nint, nint, byte> _focusCompleted;
    private readonly HashSet<nint> _numberedFocusItems = []; // focus items whose name has our badge
    private readonly Queue _focus = new("focus", Signatures.FocusCommand, 0x38);
    private PerGame _game = null!;

    public override void Load()
    {
        var focusClick = Sig.Find(Signatures.FocusClick);
        var focusNodeStates = Sig.Find(Signatures.FocusUpdateNodeStates);
        _focusCompleted = (delegate* unmanaged<nint, nint, byte>)Sig.Find(Signatures.FocusCompleted);
        _game = new PerGame(() => // another game or country: queued pointers no longer apply
        {
            _focus.Clear();
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

        MainThread.Every(TimeSpan.FromMilliseconds(250), Pump);
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

        var focus = *(nint*)(country + FocusOfCountry);
        if (focus == 0) return;
        var current = *(nint*)(focus + FocusInProgress);
        if (current != 0) _focus.Running(0, current);
        else StartFirst(_focus, 0, tag);

        // After the running check above, which takes an entry off when it starts.
        Drop(_focus, f => Unreachable(focus, f));
    }

    /// <summary>Unqueues the entries that can no longer start.</summary>
    private void Drop(Queue queue, Func<nint, bool> stale)
    {
        var dropped = queue.Items.RemoveAll(i => stale(i));
        if (dropped > 0) Log.Info($"{queue.Name}: dropped {dropped} that can no longer start, {queue.Items.Count} left");
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
    private void StartFirst(Queue queue, int slot, int tag)
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
