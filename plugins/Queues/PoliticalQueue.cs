using System.Text;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Queues;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class PoliticalSignatures
{
    /// <summary>Idea picker entry, click lambda (std::function impl): impl+8 is the CPoliticalSelectableIdeaItem.</summary>
    [Signature] public const string IdeaClick = "48 8B 41 ? 80 78 ? ? 74 ? 80 78";

    /// <summary>CPoliticalSelectableIdeaItem::Update(item, uint* tag, CIdea* slotIdea): re-sets the entry's name and button.</summary>
    [Signature] public const string IdeaUpdate = "48 89 5C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 10 01 00 00 49 8B D8";

    /// <summary>CPoliticalIdeasList::RefreshItems(list): updates every entry of the idea picker.</summary>
    [Signature] public const string IdeaListRefresh = "48 89 5C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC 40 02 00 00 0F 29 B4 24 ? ? ? ? 4C 8B F1";

    /// <summary>Advisor picker, pick lambda (impl, CCharacterStatus*): impl+16 slot occupant, impl+32 std::string* slot.</summary>
    [Signature] public const string AdvisorPick = "48 89 5C 24 ? 57 48 83 EC 20 80 79 ? ? 48 8B DA 48 8B F9 74";

    /// <summary>
    /// CCountryCharacterSelectionItem click lambda (impl): impl+8 is the item. Runs the pick (AdvisorPick), then marks the
    /// item clicked (highlight) for the selector to close.
    /// </summary>
    [Signature] public const string AdvisorItemClick = "40 53 48 83 EC 20 48 8B 59 08 48 8B 8B A0 06 00 00 48 85 C9 74 ? 48 89 5C 24 30 48 8B 01 48 8D 54 24 30 FF 50 10 48 83 7B 10 00";

    /// <summary>CCountryCharacterSelectionItem::SetUnselectable(item, bool): disables the entry's button (can't afford, not allowed).</summary>
    [Signature] public const string AdvisorUnselectable = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 40 0F B6 FA 48 8B F1 48 8B 59 10 48 83 C1 20 E8";

    /// <summary>CCountryCharacterSelectionItem::Update(item, CCharacterStatus*, bool).</summary>
    [Signature] public const string AdvisorUpdate = "44 88 44 24 ? 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24";

    /// <summary>Decisions view: click on a decision (item) / on a targeted decision (item); both post at once.</summary>
    [Signature] public const string DecisionClick = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 20 48 8B D9 48 8B 3D ? ? ? ? 48 8B 07 48 8B CF FF 90 ? ? ? ? 48 8B E8";
    [Signature] public const string TargetedDecisionClick = "48 89 5C 24 ? 48 89 6C 24 ? 56 57 41 56 48 83 EC 20 48 8B D9 48 8B 35 ? ? ? ? 48 8B 06";

    /// <summary>Decisions view entries: SetDecision(item, CDecision*, uint* tag) / SetTarget(item, target, uint* tag); set the name.</summary>
    [Signature] public const string DecisionSet = "48 89 5C 24 ? 48 89 6C 24 ? 56 57 41 56 48 83 EC 50 48 8B 99";
    [Signature] public const string TargetedDecisionSet = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 41 56 41 57 48 83 EC 50 48 89 91";

    [VTable] public const string AddIdea = "CAddIdeaCommand";
    [VTable] public const string ReplaceIdea = "CReplaceIdeaCommand";
    [VTable] public const string AddAdvisor = "CAddAdvisorCommand";
    [VTable] public const string ReplaceAdvisor = "CReplaceAdvisorCommand";
    [VTable] public const string SelectDecision = "CSelectDecisionCommand";
    [VTable] public const string SelectTargetedDecision = "CSelectTargetedDecisionCommand";
}

/// <summary>
/// Political power queue. Shift+click an idea (law, designer, national spirit) in its slot's picker, an advisor (political
/// advisor, theorist, chief, high command) in the advisor picker, or a decision, to queue/unqueue it; queued entries show
/// "N. " in front of their name. The first queued entry the game accepts (its own command check, which includes the cost)
/// is taken with the same command the UI sends, one at a time, so it happens as soon as there's enough political power.
/// </summary>
[Plugin("com.green1052.politicalqueue", "Political Power Queue", "1.0.0", GameVersion = "1.19.3", Description = "Shift+click advisors, ideas, laws and decisions to queue them; they are taken when you have the political power.")]
public unsafe class PoliticalQueue : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int PoliticsOfCountry = 0xF90, ActiveIdeas = 80, PoliticalPower = 0xE0;
    private const int SlotOfIdea = 2720, IdOfCharacter = 8;
    private const int IdeaItemOfImpl = 8, WindowOfIdeaItem = 40, IdeaOfItem = 48, IdeaActive = 56, IdeaName = 72, IdeaButton = 0x70;
    private const int ListOfIdeasWindow = 1416;
    private const int OccupantOfImpl = 16, SlotOfImpl = 32, CharacterItemOffset = 1400; // the pick lambda gets item + 1400
    private const int CharacterOfItem = 1400, SlotOfItem = 1560, CharacterName = 1592;
    private const int DecisionOfItem = 4016, DecisionName = 3984, TargetOfItem = 4024, TargetedDecisionName = 3976;
    private const int CharacterButton = 0x28, DecisionButton = 0xA58;
    private const int TargetTag = 32, TargetExtra = 36;
    private const int EnableSlot = 0x288, SetClickableSlot = 0x168; // CButton vtable: Enable(), SetClickable(bool)
    private const int ButtonState = 0xB0, Disabled = 3, ButtonFlags = 0xE4, Clickable = 0x08;
    private const long WaitMs = 5_000;                    // after a post: until political power changes, at most this long

    private enum Kind { Idea, Advisor, Decision, TargetedDecision }

    /// <summary>A queued entry. Pointers are game database objects, valid for the whole game.</summary>
    private sealed record Entry(Kind Kind, nint Item, string Slot = "", int Target = 0, int Extra = 0)
    {
        public nint Occupant { get; init; } // advisors: who held the slot when queued
    }

    [Function(CallingConventions.Microsoft)] public delegate nint OneFn(nint a);
    [Function(CallingConventions.Microsoft)] public delegate nint TwoFn(nint a, nint b);
    [Function(CallingConventions.Microsoft)] public delegate nint ThreeFn(nint a, nint b, nint c);

    private IHook<OneFn>? _ideaClick, _decisionClick, _targetedDecisionClick, _advisorItemClick;
    private IHook<TwoFn>? _advisorPick;
    private IHook<ThreeFn>? _ideaUpdate, _advisorUpdate, _decisionSet, _targetedDecisionSet;
    private IHook<TwoFn>? _advisorUnselectable;
    private readonly HashSet<nint> _forced = new(); // entries whose button we made clickable for Shift+click: a plain click does nothing
    private delegate* unmanaged<nint, void> _ideaListRefresh;

    private readonly List<Entry> _queue = new();
    private (long PoliticalPower, long Until)? _waiting;
    private (nint GameState, int Tag) _session;

    public override void Load()
    {
        _ideaListRefresh = (delegate* unmanaged<nint, void>)Sig.Find(PoliticalSignatures.IdeaListRefresh);

        _ideaClick = Patches.Hook<OneFn>(Sig.Find(PoliticalSignatures.IdeaClick), impl =>
        {
            var item = *(nint*)(impl + IdeaItemOfImpl);
            if (!Gui.ShiftHeld && _forced.Contains(item)) return 0;
            if (!Gui.ShiftHeld || item == 0 || *(byte*)(item + IdeaActive) != 0) return _ideaClick!.OriginalFunction(impl);
            Toggle(new Entry(Kind.Idea, *(nint*)(item + IdeaOfItem)));
            var list = *(nint*)(*(nint*)(item + WindowOfIdeaItem) + ListOfIdeasWindow);
            if (list != 0) _ideaListRefresh(list); // renumber now
            return 0;
        });
        _ideaUpdate = Patches.Hook<ThreeFn>(Sig.Find(PoliticalSignatures.IdeaUpdate), (item, tag, slotIdea) =>
        {
            var result = _ideaUpdate!.OriginalFunction(item, tag, slotIdea);
            Label(*(nint*)(item + IdeaName), new Entry(Kind.Idea, *(nint*)(item + IdeaOfItem)));
            Force(item, *(nint*)(item + IdeaButton)); // disabled while unaffordable or not allowed
            return result;
        });

        _advisorPick = Patches.Hook<TwoFn>(Sig.Find(PoliticalSignatures.AdvisorPick), (impl, status) =>
        {
            var slot = *(nint*)(impl + SlotOfImpl);
            if (!Gui.ShiftHeld || status == 0 || slot == 0) return _advisorPick!.OriginalFunction(impl, status);
            Toggle(new Entry(Kind.Advisor, *(nint*)status, SlotName(slot)) { Occupant = *(nint*)(impl + OccupantOfImpl) });
            LabelAdvisor(status - CharacterItemOffset);
            return 0;
        });
        // A plain click on an advisor the game blocked does nothing, as if it were still disabled: stop it before the item
        // highlights itself (stopping it at the pick leaves the highlight on and the selector open).
        _advisorItemClick = Patches.Hook<OneFn>(Sig.Find(PoliticalSignatures.AdvisorItemClick), impl =>
            !Gui.ShiftHeld && _forced.Contains(*(nint*)(impl + IdeaItemOfImpl)) ? 0 : _advisorItemClick!.OriginalFunction(impl));
        _advisorUpdate = Patches.Hook<ThreeFn>(Sig.Find(PoliticalSignatures.AdvisorUpdate), (item, status, flag) =>
        {
            var result = _advisorUpdate!.OriginalFunction(item, status, flag);
            _forced.Remove(item); // a refresh runs Update first, then SetUnselectable or SetSelectable
            LabelAdvisor(item);
            return result;
        });
        _advisorUnselectable = Patches.Hook<TwoFn>(Sig.Find(PoliticalSignatures.AdvisorUnselectable), (item, iconEnabled) =>
        {
            var result = _advisorUnselectable!.OriginalFunction(item, iconEnabled);
            Force(item, *(nint*)(item + CharacterButton));
            return result;
        });

        _decisionClick = Patches.Hook<OneFn>(Sig.Find(PoliticalSignatures.DecisionClick), item =>
        {
            var decision = *(nint*)(item + DecisionOfItem);
            if (!Gui.ShiftHeld && _forced.Contains(item)) return 0;
            if (!Gui.ShiftHeld || decision == 0) return _decisionClick!.OriginalFunction(item);
            Toggle(new Entry(Kind.Decision, decision));
            Label(*(nint*)(item + DecisionName), new Entry(Kind.Decision, decision));
            return 0;
        });
        _targetedDecisionClick = Patches.Hook<OneFn>(Sig.Find(PoliticalSignatures.TargetedDecisionClick), item =>
        {
            var entry = TargetedEntry(*(nint*)(item + TargetOfItem));
            if (!Gui.ShiftHeld && _forced.Contains(item)) return 0;
            if (!Gui.ShiftHeld || entry == null) return _targetedDecisionClick!.OriginalFunction(item);
            Toggle(entry);
            Label(*(nint*)(item + TargetedDecisionName), entry);
            return 0;
        });
        _decisionSet = Patches.Hook<ThreeFn>(Sig.Find(PoliticalSignatures.DecisionSet), (item, decision, tag) =>
        {
            var result = _decisionSet!.OriginalFunction(item, decision, tag);
            if (decision != 0) Label(*(nint*)(item + DecisionName), new Entry(Kind.Decision, decision));
            Force(item, *(nint*)(item + DecisionButton)); // unaffordable: enabled but not clickable; unavailable: disabled
            return result;
        });
        _targetedDecisionSet = Patches.Hook<ThreeFn>(Sig.Find(PoliticalSignatures.TargetedDecisionSet), (item, target, tag) =>
        {
            var result = _targetedDecisionSet!.OriginalFunction(item, target, tag);
            if (TargetedEntry(target) is { } entry) Label(*(nint*)(item + TargetedDecisionName), entry);
            Force(item, *(nint*)(item + DecisionButton));
            return result;
        });

        MainThread.Every(TimeSpan.FromMilliseconds(250), Pump);
    }

    /// <summary>
    /// The game drops clicks on a disabled or unclickable button before any handler runs, and it blocks exactly what a queue
    /// is for (can't afford yet). Make <paramref name="button"/> clickable, and remember <paramref name="item"/> so that a
    /// plain click on it still does nothing.
    /// </summary>
    private void Force(nint item, nint button)
    {
        if (button == 0) return;
        if (*(int*)(button + ButtonState) != Disabled && (*(byte*)(button + ButtonFlags) & Clickable) != 0)
        {
            _forced.Remove(item);
            return;
        }
        _forced.Add(item);
        var vtable = *(nint*)button;
        ((delegate* unmanaged<nint, void>)*(nint*)(vtable + EnableSlot))(button);
        ((delegate* unmanaged<nint, byte, void>)*(nint*)(vtable + SetClickableSlot))(button, 1);
    }

    private static string SlotName(nint str) => Encoding.Latin1.GetString(Native.MsvcString(str));

    /// <summary>A targeted decision entry: the target object's vtable slot 0 is its CDecision*; tag and extra identify the target.</summary>
    private static Entry? TargetedEntry(nint target)
    {
        if (target == 0) return null;
        var decision = ((delegate* unmanaged<nint, nint>)**(nint**)target)(target);
        return decision == 0 ? null : new Entry(Kind.TargetedDecision, decision, Target: *(int*)(target + TargetTag), Extra: *(int*)(target + TargetExtra));
    }

    private void LabelAdvisor(nint item)
    {
        var character = *(nint*)(item + CharacterOfItem);
        if (character != 0) Label(*(nint*)(item + CharacterName), new Entry(Kind.Advisor, character, SlotName(item + SlotOfItem)));
    }

    private void Toggle(Entry entry)
    {
        var index = _queue.FindIndex(e => Same(e, entry));
        if (index >= 0)
        {
            _queue.RemoveAt(index);
            Log.Info($"{entry.Kind}: unqueued, {_queue.Count} left");
        }
        else
        {
            _queue.Add(entry);
            Log.Info($"{entry.Kind}: queued #{_queue.Count}");
        }
    }

    private static bool Same(Entry a, Entry b) => a with { Occupant = 0 } == b with { Occupant = 0 };

    /// <summary>Puts "N. " in front of the name box's text if the entry is queued, and takes an old one off otherwise.</summary>
    private void Label(nint box, Entry entry)
    {
        if (box == 0) return;
        var text = Gui.Text(box);
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit((char)text[digits])) digits++;
        var name = digits > 0 && text[digits..].StartsWith(". "u8) ? text[(digits + 2)..] : text;
        var position = _queue.FindIndex(e => Same(e, entry)) + 1;
        byte[] shown = position > 0 ? [.. Encoding.ASCII.GetBytes($"{position}. "), .. name] : name.ToArray();
        if (!text.SequenceEqual(shown)) Gui.SetText(box, shown);
    }

    private void Pump()
    {
        var tag = Game.PlayerTag;
        var country = Game.Country(tag);
        var politics = country == 0 ? 0 : *(nint*)(country + PoliticsOfCountry);
        if (politics == 0) return;
        if (_session != (Game.State, tag)) // another game or country
        {
            _session = (Game.State, tag);
            _queue.Clear();
            _waiting = null;
            _forced.Clear();
        }

        // One at a time: the cost is paid when the command runs (a round trip later in multiplayer), so a second post could
        // pass the same check with the same political power.
        var power = *(long*)(politics + PoliticalPower);
        var now = Environment.TickCount64;
        if (_waiting is { } w && w.PoliticalPower == power && now < w.Until) return;
        _waiting = null;

        var active = Native.PdxArray(politics + ActiveIdeas).ToArray();
        _queue.RemoveAll(e => e.Kind == Kind.Idea && active.Contains(e.Item)); // taken (by us or by hand)
        foreach (var entry in _queue)
        {
            if (!Post(entry, tag, active)) continue;
            _queue.Remove(entry); // decisions can be repeatable: never post one twice
            _waiting = (power, now + WaitMs);
            Log.Info($"{entry.Kind}: taken, {_queue.Count} left");
            return;
        }
    }

    private static bool Post(Entry entry, int tag, ReadOnlySpan<nint> activeIdeas)
    {
        switch (entry.Kind)
        {
            case Kind.Idea:
            {
                var add = Commands.New(PoliticalSignatures.AddIdea, 0x40, tag, entry.Item);
                *(byte*)(add + 56) = 1; // pay the cost
                if (Commands.TryPost(add)) return true;
                var slot = *(nint*)(entry.Item + SlotOfIdea);
                foreach (var old in activeIdeas) // a full slot (laws always are): replace what's in it
                {
                    if (*(nint*)(old + SlotOfIdea) != slot) continue;
                    var replace = Commands.New(PoliticalSignatures.ReplaceIdea, 0x48, tag, old);
                    *(nint*)(replace + 56) = entry.Item;
                    *(byte*)(replace + 64) = 1;
                    if (Commands.TryPost(replace)) return true;
                }
                return false;
            }
            case Kind.Advisor:
                return PostAdvisor(PoliticalSignatures.AddAdvisor, 0x58, tag, 0, entry)
                    || entry.Occupant != 0 && PostAdvisor(PoliticalSignatures.ReplaceAdvisor, 0x60, tag, entry.Occupant, entry);
            case Kind.Decision:
                return Commands.TryPost(Commands.New(PoliticalSignatures.SelectDecision, 0x38, tag, entry.Item));
            default:
            {
                var cmd = Commands.New(PoliticalSignatures.SelectTargetedDecision, 0x40, tag, 0);
                *(int*)(cmd + 44) = entry.Target;
                *(int*)(cmd + 48) = entry.Extra;
                *(nint*)(cmd + 56) = entry.Item;
                return Commands.TryPost(cmd);
            }
        }
    }

    /// <summary>
    /// CAddAdvisorCommand: +40 character id, +48 pay cost, +56 std::string slot. CReplaceAdvisorCommand: +40 old id, +48 new
    /// id, +56 pay cost, +64 slot. Neither has the usual tag/target layout, so both are filled here.
    /// </summary>
    private static bool PostAdvisor(string className, int size, int tag, nint occupant, Entry entry)
    {
        var cmd = Commands.New(className, size, tag, 0);
        var slot = cmd + (occupant == 0 ? 56 : 64);
        *(long*)(cmd + 40) = *(long*)(entry.Item + IdOfCharacter);
        if (occupant == 0) *(byte*)(cmd + 48) = 1;
        else
        {
            *(long*)(cmd + 40) = *(long*)(occupant + IdOfCharacter);
            *(long*)(cmd + 48) = *(long*)(entry.Item + IdOfCharacter);
            *(byte*)(cmd + 56) = 1;
        }
        Native.SetMsvcString(slot, Encoding.Latin1.GetBytes(entry.Slot));
        if (Commands.CanExecute(cmd))
        {
            Commands.Post(cmd);
            return true;
        }
        Native.SetMsvcString(slot, ReadOnlySpan<byte>.Empty); // frees a heap buffer
        Commands.Free(cmd);
        return false;
    }
}
