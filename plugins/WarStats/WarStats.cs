using System.Globalization;
using System.Linq;
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

    /// <summary>CLogistics::GetBalance(logistics, long* out, CEquipmentType* archetype): stockpile - need - net lend-lease (fixed point).</summary>
    [Signature] public const string GetBalance = "48 89 5C 24 ? 48 89 74 24 ? 57 48 83 EC 20 49 8B D8 48 8B F2 48 8B F9 E8 ? ? ? ? 45 33 C9";
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
    private const int ProductionOfCountry = 0xF68, LogisticsOfCountry = 0xF98, MilitaryLines = 88; // AutoProduction's layouts
    private const int VariantOfLine = 136, TypeOfVariant = 1008, ArchetypeOfType = 1240;
    private const int RegionsOfGameState = 0x2E0, ForcesOfRegion = 0xF8; // strategic regions; the task forces spotted in one (AiAssist's layouts)
    private const int OwnerOfForce = 0x1D8, ShipsOfForce = 0x348, StrengthOfShip = 0x6F8;
    private const int Days = 30;                    // days of equipment stockpile history kept

    [Function(CallingConventions.Microsoft)] private delegate nint FindFn(nint window, nint name, byte logIfMissing);

    private delegate* unmanaged<nint, long*, nint, long*> _balance;
    private IHook<FindFn>? _find;
    private nint _window, _text, _date;
    private byte[] _day = [];                       // the date part of the topbar's date text, last seen
    private readonly Dictionary<nint, List<long>> _history = []; // archetype -> stockpile per day (fixed point)
    private readonly Dictionary<nint, string> _names = [];       // archetype -> its loc key
    private bool _shown, _capturing, _reportedMissing;

    public override void Load()
    {
        var enabled = Config.Bind("Window", "Enabled", true, "The war statistics button under the topbar's flag.");
        _balance = (delegate* unmanaged<nint, long*, nint, long*>)Sig.Find(Signatures.GetBalance);
        _find = Patches.Hook<FindFn>(*(nint*)(Sig.VTable(Signatures.ContainerWindow) + FindTextBoxSlot * 8),
            (window, name, log) =>
            {
                if (_window == 0 && !_capturing) Capture(window, enabled);
                return _find!.OriginalFunction(window, name, log);
            });
        MainThread.Every(TimeSpan.FromSeconds(1), Tick);
    }

    /// <summary>Casualties are cumulative (read live); the equipment flow needs a sample a day, off the topbar's date text.</summary>
    private void Tick()
    {
        if (_window == 0 || Game.ControlledTag == 0)
        { // the menu: the topbar is gone, and the game rebuilds it (our hook catches it) when a game starts
            if (_window != 0) { _window = 0; _shown = false; _day = []; _history.Clear(); }
            return;
        }
        var text = Gui.Text(_date);
        var afterTime = text.IndexOf((byte)' ') + 1; // "7:00 29, September 1939": the date is everything past the time
        var day = text[Math.Min(afterTime, text.Length)..];
        if (!day.SequenceEqual(_day))
        {
            _day = [.. day];
            Sample();
        }
        if (_shown) Refresh();
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
            _date = Gui.FindTextBox(Gui.Find(topbar, WindowSlot, "time_control_window"), "DateText");
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
        var enemies = new HashSet<int>();
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
                if (a == me) enemies.Add(b);
                if (b == me) enemies.Add(a);
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
        var naval = Naval(enemies);
        if (naval.Length > 0)
            text.Append('\n').Append(Loc.Get("com.green1052.warstats.naval", "Naval strength by region (spotted)")).Append('\n').Append(naval);
        if (_history.Count > 0)
        {
            text.Append('\n').Append(Loc.Get("com.green1052.warstats.equipment", "Equipment stockpile")).Append(" (").Append(Days).Append(')').Append('\n');
            var index = 0;
            foreach (var (archetype, run) in _history)
                text.Append("  ").Append(Name(archetype, index++)).Append(' ', 2).Append(Fmt(run[^1] / Native.FixedPoint))
                    .Append("  ").Append(Bar(run)).Append('\n');
        }
        Gui.SetText(_text, Gui.Literal(text.ToString()));
    }

    /// <summary>Each sea region where our fleets and an enemy's meet: the strength of each side's task forces spotted
    /// there. Only what is spotted shows, like the game's own map information.</summary>
    private static StringBuilder Naval(HashSet<int> enemies)
    {
        var text = new StringBuilder();
        var regions = Game.State == 0 ? 0 : *(nint*)(Game.State + RegionsOfGameState);
        if (regions == 0 || enemies.Count == 0) return text;
        for (int id = 1, n = *(int*)(Game.State + RegionsOfGameState + 0xC); id < n; id++)
        {
            var region = ((nint*)regions)[id];
            if (region == 0) continue;
            long ours = 0, theirs = 0;
            foreach (var force in Native.PdxArray(region + ForcesOfRegion))
            {
                var mine = *(int*)(force + OwnerOfForce) == Game.ControlledTag;
                if (!mine && !enemies.Contains(*(int*)(force + OwnerOfForce))) continue;
                foreach (var ship in Native.PdxArray(force + ShipsOfForce))
                {
                    if (mine) ours += *(long*)(ship + StrengthOfShip);
                    else theirs += *(long*)(ship + StrengthOfShip);
                }
            }
            if (ours + theirs <= 0) continue;
            text.Append("  ").Append(Loc.Get($"STRATEGIC_REGION_{id}", $"region {id}")).Append(' ')
                .Append(Fmt(ours / Native.FixedPoint)).Append(" vs ").Append(Fmt(theirs / Native.FixedPoint)).Append('\n');
        }
        return text;
    }

    /// <summary>One stockpile sample per equipment archetype the country produces lines for.</summary>
    private void Sample()
    {
        var country = Game.Country(Game.ControlledTag);
        if (country == 0) { _history.Clear(); return; } // back to the menu: a new game's history starts over
        var production = *(nint*)(country + ProductionOfCountry);
        var logistics = *(nint*)(country + LogisticsOfCountry);
        if (production == 0 || logistics == 0) return;
        var seen = new HashSet<nint>();
        foreach (var line in Native.PdxArray(production + MilitaryLines).ToArray())
        {
            var variant = *(nint*)(line + VariantOfLine);
            var type = variant == 0 ? 0 : *(nint*)(variant + TypeOfVariant);
            var archetype = type == 0 ? 0 : *(nint*)(type + ArchetypeOfType);
            if (archetype == 0 || !seen.Add(archetype)) continue;
            long balance;
            _balance(logistics, &balance, archetype);
            var run = _history.TryGetValue(archetype, out var list) ? list : _history[archetype] = [];
            run.Add(balance);
            if (run.Count > Days) run.RemoveAt(0);
        }
        foreach (var gone in _history.Keys.Where(k => !seen.Contains(k)).ToArray()) _history.Remove(gone);
    }

    /// <summary>The archetype's loc key: the std::string it holds (equipment ids are [a-z0-9_]); else a placeholder.</summary>
    private string Name(nint archetype, int index)
    {
        if (_names.TryGetValue(archetype, out var known)) return known;
        var guessed = TryName(archetype + 0x440) ?? TryName(archetype + 0x468) ?? $"equipment {index}";
        if (guessed.StartsWith("equipment "))
            Log.Info($"equipment name field not found at 0x{(archetype + 0x440):X} or 0x{(archetype + 0x468):X}: "
                + $"size0 {*(long*)(archetype + 0x450)}, size1 {*(long*)(archetype + 0x478)}");
        var name = Loc.Get(guessed, guessed);
        _names[archetype] = name;
        return name;
    }

    /// <summary>The std::string at <paramref name="at"/> when it plausibly is one holding an equipment id, else null.</summary>
    private static string? TryName(nint at)
    {
        var size = *(long*)(at + 0x10);
        var cap = *(long*)(at + 0x18);
        if (size is < 1 or > 0x100 || cap < 15 || cap < size) return null; // a sane std::string: SSO or a heap buffer
        var bytes = Native.MsvcString(at);
        foreach (var b in bytes) // an equipment id: [a-z0-9_]
            if (b < '0' || b > 'z' || (b > '9' && b < 'a' && b != '_')) return null;
        return bytes.IsEmpty ? null : Native.Utf8(bytes);
    }

    /// <summary>The run as a text sparkline: '.' when out of stock, then rising through ":-=+*#".</summary>
    private static string Bar(List<long> run)
    {
        const string ramp = ".:-=+*#";
        var min = run.Min();
        var max = run.Max();
        var bar = new char[run.Count];
        for (var i = 0; i < run.Count; i++)
            bar[i] = run[i] <= 0 ? '.' : ramp[Math.Clamp((int)((run[i] - min) * (ramp.Length - 1) / Math.Max(1, max - min)), 1, ramp.Length - 1)];
        return new string(bar);
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
