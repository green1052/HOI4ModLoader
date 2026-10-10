using System.Globalization;
using System.Text;
using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// The war statistics window: a button under the topbar's flag opens a window listing every war's casualties and score.
// The window itself comes from the GuiPatch below (built from the installed game's interface\topbar.gui at startup).
[assembly: GuiPatch("interface/topbar.gui", "gui/warstats.gui",
    After = @"(?s)name\s*=\s*""reopen_ingame_lobby_label"".*?\n\t\t\}")]

namespace WarStats;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>The class of every .gui container window: the implementation of its Find-text-box vtable slot is hooked
    /// to catch the topbar's pointer, the first time the game looks a topbar text box up by name.</summary>
    [VTable] public const string ContainerWindow = "CContainerWindow";
}

/// <summary>
/// A ledger of every war: casualties both sides have taken and the war score, in a window opened from the topbar.
/// Read-only, so multiplayer lockstep holds and other players need nothing.
/// </summary>
[Plugin("com.green1052.warstats", "War Statistics", "1.0.0", GameVersion = "1.19.3")]
public unsafe class WarStats : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int DiplomacyOfCountry = 0xF88;   // CCountry: CDiplomacyStatus*
    private const int RelationsOfDiplomacy = 0x20;  // status: CPdxArray<CRelation*>
    private const int KindOfRelation = 8;           // CRelation: 14346 = at war
    private const int TagAOfRelation = 0x10, TagBOfRelation = 0x14;
    private const int LossesAOfRelation = 0x50, LossesBOfRelation = 0x58; // each side's cumulative dead, raw men
    private const int BreakdownAOfRelation = 0x70, BreakdownBOfRelation = 0xE0; // CWarScoreBreakdown, per side
    private const int FindTextBoxSlot = 15;         // CContainerWindow vtable: FindInstantTextBox(window, name, logIfMissing)
    private const int ButtonSlot = 13, WindowSlot = 55;
    private const int NameOfWindow = 0x15D0;        // container: descriptor whose +0x48 std::string is the .gui name
    private const int War = 14346;

    [Function(CallingConventions.Microsoft)] private delegate nint FindFn(nint window, nint name, byte logIfMissing);

    private IHook<FindFn>? _find;
    private nint _window, _text;
    private bool _shown, _capturing, _reportedMissing;

    public override void Load()
    {
        var enabled = Config.Bind("Window", "Enabled", true, "The war statistics button under the topbar's flag.");
        _find = Patches.Hook<FindFn>(*(nint*)(Sig.VTable(Signatures.ContainerWindow) + FindTextBoxSlot * 8),
            (window, name, log) =>
            {
                if (_window == 0 && !_capturing) Capture(window, enabled);
                return _find!.OriginalFunction(window, name, log);
            });
        MainThread.Every(TimeSpan.FromSeconds(1), () => { if (_shown) Refresh(); }); // casualties are cumulative: live reading needs no history
    }

    /// <summary>The game looks its topbar text boxes up by name: the first such lookup hands us the topbar itself.</summary>
    private void Capture(nint topbar, bool enabled)
    {
        var descriptor = *(nint*)(topbar + NameOfWindow);
        if (descriptor == 0 || !Native.MsvcString(descriptor + 0x48).SequenceEqual("topbar"u8)) return;
        _capturing = true;
        try // our own lookups below re-enter the hook
        {
            var button = Gui.Find(topbar, ButtonSlot, "warstats_button");
            _window = Gui.Find(topbar, WindowSlot, "warstats_window");
            _text = Gui.FindTextBox(_window, "warstats_text");
            if (button == 0 || _window == 0 || _text == 0)
            {
                _window = 0;
                if (!_reportedMissing)
                {
                    Log.Info("no war statistics window: topbar.gui isn't HOI4ModLoader\\ui's (see GameFiles in this log)");
                    _reportedMissing = true;
                }
                return;
            }
            if (!enabled) Gui.Hide(button);
            Gui.ShowWindow(_window, false);
            Gui.OnClick(button, Toggle);
        }
        finally { _capturing = false; }
    }

    private void Toggle()
    {
        _shown = !_shown;
        Gui.ShowWindow(_window, _shown);
        if (_shown) Refresh();
    }

    private void Refresh()
    {
        var me = Game.ControlledTag;
        var seen = new HashSet<long>();
        var wars = new List<(string Title, long Mine, long Theirs, long Score)>();
        foreach (var country in Game.Countries)
        {
            var diplomacy = country == 0 ? 0 : *(nint*)(country + DiplomacyOfCountry);
            if (diplomacy == 0) continue;
            foreach (var relation in Native.PdxArray(diplomacy + RelationsOfDiplomacy).ToArray())
            {
                if (*(int*)(relation + KindOfRelation) != War) continue;
                var a = *(int*)(relation + TagAOfRelation);
                var b = *(int*)(relation + TagBOfRelation);
                if (!seen.Add((long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b))) continue; // both sides carry the war
                var la = *(long*)(relation + LossesAOfRelation);
                var lb = *(long*)(relation + LossesBOfRelation);
                var score = Score(*(nint*)(relation + a == me ? BreakdownAOfRelation : BreakdownBOfRelation));
                if (b == me) { (a, b) = (b, a); (la, lb) = (lb, la); } // our side first
                wars.Add(($"{Name(a)} vs {Name(b)}", la, lb, score));
            }
        }
        var text = new StringBuilder();
        foreach (var war in wars.OrderByDescending(w => w.Mine + w.Theirs))
            text.Append(war.Title).Append('\n')
                .Append("  ").Append(Loc.Get("com.green1052.warstats.casualties", "casualties")).Append(' ')
                .Append(Fmt(war.Mine)).Append(" / ").Append(Fmt(war.Theirs))
                .Append("    ").Append(Loc.Get("com.green1052.warstats.score", "war score")).Append(' ').Append(war.Score)
                .Append('\n');
        if (text.Length == 0) text.Append(Loc.Get("com.green1052.warstats.none", "No wars."));
        Gui.SetText(_text, Gui.Literal(text.ToString()));
    }

    /// <summary>The weighted points of one side's war score breakdown (land, occupation, bombing, sinkings, ...).</summary>
    private static long Score(nint breakdown)
    {
        if (breakdown == 0) return 0;
        var total = 0L;
        for (var at = 0x10; at <= 0x58; at += 8) total += *(long*)(breakdown + at);
        return total;
    }

    /// <summary>The country's name; its localisation key is its tag ("GER" etc.).</summary>
    private static string Name(int tag) => Loc.Get(Tag(tag), Tag(tag));

    private static string Tag(int tag) =>
        new([(char)(tag >> 16), (char)(tag >> 8 & 0xFF), (char)(tag & 0xFF)]);

    private static string Fmt(long n) => n >= 1_000_000 ? $"{n / 1e6:0.#}M" : n >= 10_000 ? $"{n / 1e3:0.#}K" : n.ToString("N0", CultureInfo.InvariantCulture);
}
