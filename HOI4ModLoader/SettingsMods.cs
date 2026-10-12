using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// The ingame settings window's 4 tabs become 5 (narrower) and the Mods page goes in after its Back button.
[assembly: HOI4ModLoader.GuiPatch("interface/settings.gui", "gui/settings_mods.gui",
    Replace = @"(?s)(name = ""menu_settings_ingame"".*?)buttonType = \{\s*name = ""settings_tab_1"".*?name = ""settings_tab_4"".*?\n\t\t\t\}(.*?shortcut = ""ESCAPE""\s*\})")]

namespace HOI4ModLoader;

/// <summary>
/// A "Mods" tab in the game's settings window: a checkbox per plugin to switch it on or off (hover for what it does), and
/// an arrow to its settings (its config entries: checkboxes for true/false, arrows for numbers, a list to pick from for text with choices; pages of 8, with arrows by
/// the title when there are more). Changes are written to the
/// plugin's config and every plugin reloads. The tab comes from the GuiPatch above (built from the
/// installed game's interface\settings.gui at startup); without it nothing changes.
/// </summary>
internal static unsafe class SettingsMods
{
    /// <summary>CSettingsScreen::Init(screen, window): stores the window and binds every button and checkbox (once per menu).</summary>
    [Signature] public const string InitSig = "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 18 4C 89 70 20 55 48 8D 68 A1 48 81 EC 90 00 00 00 0F 29 70 E8 4C 8B D2 48 8B F1 48 89 91 98 5E 00 00";

    /// <summary>CSettingsScreen::UpdateTabButtons(screen): marks the tab button of the shown container selected, the others not.</summary>
    [Signature] public const string UpdateTabsSig = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 48 8B F1 48 8D 1D ? ? ? ? 48 8D 2D ? ? ? ? 66 66 66 0F 1F 84 00 00 00 00 00 48 8B 8E 98 5E 00 00";

    /// <summary>CSettingsScreen::OnOpen(screen): shows the window and re-shows the shown tab, if it is one of the four vanilla ones.</summary>
    [Signature] public const string OpenSig = "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 55 48 8B EC 48 83 EC 40 48 8B F1 48 8B 89 98 5E 00 00 48 83 C1 30 48 8B 01 FF 50 78";

    /// <summary>CCheckBox::UpdateSprite(checkbox): shows the frame of its state.</summary>
    [Signature] public const string CheckBoxSpriteSig = "40 53 48 83 EC 40 80 B9 BB 01 00 00 00 48 8B D9 48 89 74 24 58 75 22 48 8B 89 C0 00 00 00 48 8B 01 FF 90 60 02 00 00";

    /// <summary>Checkbox observer: { vtable, owner, on toggle(owner), on checked(owner), on unchecked(owner), ... } (64 bytes).</summary>
    [VTable] public const string CheckBoxGlue = "?$CCheckBoxObserverGlue@VCSettingsScreen@@";

    /// <summary>Button observer: { vtable, 20 std::function slots (64 bytes each), slot 0 = click }.</summary>
    [VTable] public const string ButtonGlue = "CButtonObserverGlue";

    /// <summary>std::function impl of a bound member call: _Do_call jumps to [impl+8] with rcx = [impl+16].</summary>
    [VTable] public const string MemberCall = "?$_Func_impl_no_alloc@V?$_Binder@U_Unforced@std@@AEAP8CSettingsScreen@@EAAXXZPEAV3@@std@@X$$V@std";

    private const int RootOfScreen = 0x5E98, ShownTab = 0x5E60; // CSettingsScreen: its window, the shown tab container
    private const int ButtonSlot = 13, CheckBoxSlot = 21, WindowSlot = 55;
    private const int ShowSlot = 15, HideSlot = 16, WidgetOfWindow = 48; // a container's GUI object, its Show()/Hide()
    private const int SetFrameSlot = 22;                              // button: SetFrame(1 selected, 2 not)
    private const int CheckState = 176, Observers = 128, AddObserverSlot = 1; // CCheckBox: state (bit 0 = checked), observer list
    private const int PluginRows = 18, SettingRows = 8;

    [Function(CallingConventions.Microsoft)] public delegate nint InitFn(nint screen, nint window);
    [Function(CallingConventions.Microsoft)] public delegate nint TabsFn(nint screen);
    [Function(CallingConventions.Microsoft)] public delegate nint OpenFn(nint screen);

    private static IHook<InitFn>? _init;
    private static IHook<TabsFn>? _updateTabs;
    private static IHook<OpenFn>? _open;
    private static delegate* unmanaged<nint, void> _checkBoxSprite;
    private static readonly Logger Log = new("Settings");
    private static bool _reportedMissing;

    /// <summary>The widgets of the last built Mods page (the settings screen is built once per menu).</summary>
    private static nint _screen, _page, _list, _settings, _tab, _title, _listTitle, _prev, _next, _pageText;
    private static readonly nint[] PluginBoxes = new nint[PluginRows], PluginTitles = new nint[PluginRows], PluginArrows = new nint[PluginRows];
    private static readonly nint[] Rows = new nint[SettingRows];

    /// <summary>The plugin whose settings are shown, and those settings (section, key, value, description).</summary>
    private static string? _plugin;
    private static List<(string Section, string Key, string Value, string Description)> _entries = [];
    private static int _first; // the entry in the first row (pages of SettingRows)

    /// <summary>A text setting whose choices the rows list instead (its list button), the one ticked; the first choice shown.</summary>
    private static (string Section, string Key, string Description, IReadOnlyList<string> Choices)? _pick;
    private static int _pickFirst;

    internal static void Install()
    {
        // Everything resolved before the first hook: a broken signature leaves no half-installed tab (the loader's hooks stay).
        _checkBoxSprite = (delegate* unmanaged<nint, void>)Sig.Find(CheckBoxSpriteSig);
        _ = Sig.VTable(CheckBoxGlue) + Sig.VTable(ButtonGlue) + Sig.VTable(MemberCall);
        var (init, open, updateTabs) = (Sig.Find(InitSig), Sig.Find(OpenSig), Sig.Find(UpdateTabsSig));

        _init = Patches.Hook<InitFn>(init, (screen, window) =>
        {
            var result = _init!.OriginalFunction(screen, window);
            Guard(() => Build(screen));
            return result;
        });
        _open = Patches.Hook<OpenFn>(open, screen =>
        {
            var result = _open!.OriginalFunction(screen);
            if (screen == _screen && _page != 0 && *(nint*)(screen + ShownTab) == _page) Guard(ShowPage); // reopened on the Mods tab
            return result;
        });
        _updateTabs = Patches.Hook<TabsFn>(updateTabs, screen =>
        {
            var result = _updateTabs!.OriginalFunction(screen);
            if (screen == _screen && _tab != 0) // the vanilla table knows 4 tabs: ours is selected when its page is shown
                Guard(() => ((delegate* unmanaged<nint, int, void>)(*(nint**)_tab)[SetFrameSlot])(_tab, *(nint*)(screen + ShownTab) == _page ? 1 : 2));
            return result;
        });
    }

    private static void Build(nint screen)
    {
        var root = *(nint*)(screen + RootOfScreen);
        if (root == 0 || Gui.FindTextBox(root, "hoimod_ui") == 0)
        {
            if (!_reportedMissing) Log.Info("no Mods tab: the settings window isn't HOI4ModLoader\\ui's (see GameFiles in this log)");
            _reportedMissing = true;
            return;
        }
        _screen = screen;
        _page = Gui.Find(root, WindowSlot, "menu_settings_MODS");
        _list = Gui.Find(_page, WindowSlot, "hoimod_mods_list");
        _settings = Gui.Find(_page, WindowSlot, "hoimod_mods_settings");
        _title = Gui.FindTextBox(_settings, "hoimod_settings_title");
        _listTitle = Gui.FindTextBox(_list, "hoimod_mods_title");
        _tab = Gui.Find(root, ButtonSlot, "hoimod_mods_tab");
        var tabText = Gui.FindTextBox(root, "hoimod_mods_tab_text");
        if (tabText != 0) Gui.SetText(tabText, Loc.Get("HOIMOD_MODS_TAB", "Mods"));
        Visible(_page, false);

        for (var row = 0; row < PluginRows; row++)
        {
            PluginBoxes[row] = Gui.Find(_list, CheckBoxSlot, $"hoimod_plugin_{row}");
            PluginTitles[row] = Gui.FindTextBox(_list, $"hoimod_plugin_{row}_title");
            PluginArrows[row] = Gui.Find(_list, ButtonSlot, $"hoimod_plugin_{row}_settings");
            BindCheckBox(PluginBoxes[row], &OnPluginToggle, row);
            BindButton(PluginArrows[row], &OnOpenSettings, row);
            var plugin = row;
            Gui.SetTooltip(PluginTitles[row], () => plugin < Loader.Known.Count ? PluginTooltip(Loader.Known[plugin]) : "");
        }
        for (var row = 0; row < SettingRows; row++)
        {
            var container = Rows[row] = Gui.Find(_settings, WindowSlot, $"hoimod_setting_{row}");
            BindButton(Gui.Find(container, ButtonSlot, "hoimod_setting_decrease"), &OnDecrease, row);
            BindButton(Gui.Find(container, ButtonSlot, "hoimod_setting_increase"), &OnIncrease, row);
            BindCheckBox(Gui.Find(container, CheckBoxSlot, "hoimod_setting_check"), &OnSettingToggle, row);
            BindButton(Gui.Find(container, ButtonSlot, "hoimod_setting_pick"), &OnPick, row);
            var setting = row;
            Gui.SetTooltip(Gui.FindTextBox(container, "hoimod_setting_label"),
                () => _pick == null && _first + setting < _entries.Count ? SettingTooltip(_entries[_first + setting]) : "");
        }
        BindButton(Gui.Find(_settings, ButtonSlot, "hoimod_settings_back"), &OnBack, 0);
        _prev = Gui.Find(_settings, ButtonSlot, "hoimod_settings_prev");
        _next = Gui.Find(_settings, ButtonSlot, "hoimod_settings_next");
        _pageText = Gui.FindTextBox(_settings, "hoimod_settings_page");
        BindButton(_prev, &OnTurnPage, -1);
        BindButton(_next, &OnTurnPage, +1);
        BindButton(_tab, &OnTab, 0);
    }

    [UnmanagedCallersOnly]
    private static void OnTab(nint unused) => Guard(() =>
    {
        if (*(nint*)(_screen + ShownTab) != _page) ShowPage();
    });

    /// <summary>Shows the Mods page (its plugin list) in place of the shown tab.</summary>
    private static void ShowPage()
    {
        var shown = *(nint*)(_screen + ShownTab);
        if (shown != 0 && shown != _page) Visible(shown, false);
        Visible(_page, true);
        *(nint*)(_screen + ShownTab) = _page; // a vanilla tab's click hides it again
        ShowList();
        _updateTabs!.OriginalFunction(_screen);
        ((delegate* unmanaged<nint, int, void>)(*(nint**)_tab)[SetFrameSlot])(_tab, 1);
    }

    // ---- plugin list ----

    private static void ShowList()
    {
        _plugin = null;
        _pick = null;
        Visible(_settings, false);
        Visible(_list, true);
        var plugins = Loader.Known;
        if (_listTitle != 0)
            Gui.SetText(_listTitle, Loc.Format("HOIMOD_TITLE", "HOI4ModLoader {0}  ({1} of {2} plugins active)", Loader.Version,
                plugins.Count(p => Loader.IsLoaded(p.Id) || Loader.IsWaiting(p)), plugins.Count));
        for (var row = 0; row < PluginRows; row++)
        {
            var show = row < plugins.Count;
            Shown(PluginBoxes[row], show);
            Shown(PluginTitles[row], show);
            Shown(PluginArrows[row], show && Settings(plugins[row].Id).Count > 0);
            if (!show) continue;
            Gui.SetText(PluginTitles[row], Gui.Literal(Loader.Name(plugins[row])));
            Check(PluginBoxes[row], Loader.IsEnabled(plugins[row].Id));
        }
        if (plugins.Count > PluginRows) Log.Error($"{plugins.Count} plugins, but the Mods tab has {PluginRows} rows");
    }

    [UnmanagedCallersOnly]
    private static void OnPluginToggle(nint row) => Guard(() =>
    {
        var plugins = Loader.Known;
        if (row >= plugins.Count) return;
        var on = (*(int*)(PluginBoxes[row] + CheckState) & 1) != 0;
        var id = plugins[(int)row].Id;
        if (Loader.IsEnabled(id) == on) return; // called once per observer slot of a click
        Loader.SetEnabled(id, on);
        Log.Info($"{id} {(on ? "enabled" : "disabled")}: reloading plugins");
        Loader.RequestReload();
    });

    /// <summary>What a plugin does and whether it runs, for its name's tooltip.</summary>
    private static string PluginTooltip(PluginAttribute plugin)
    {
        var text = new StringBuilder(Loader.Name(plugin)).Append(' ').Append(plugin.Version);
        var description = Loc.Get($"{plugin.Id}.desc", "");
        if (description.Length > 0) text.Append('\n').Append(description);
        text.Append("\n\n").Append(
            Loader.IsLoaded(plugin.Id) ? Loc.Get("HOIMOD_STATUS_RUNNING", "Running.") :
            !Loader.IsEnabled(plugin.Id) ? Loc.Get("HOIMOD_STATUS_OFF", "Off.") :
            plugin.AffectsGameState && Loader.Multiplayer ? Loc.Get("HOIMOD_STATUS_OFF_MP", "Enabled: it changes game rules, so it rests in multiplayer and starts again in single player.") :
            Loc.Get("HOIMOD_STATUS_FAILED", "Failed to load: see HOI4ModLoader/LogOutput.log."));
        if (plugin.AffectsGameState && !Loader.Multiplayer) text.Append(' ').Append(Loc.Get("HOIMOD_SP_ONLY", "Single player only: it changes game rules."));
        return text.ToString();
    }

    // ---- one plugin's settings ----

    /// <summary>A setting's name, value and what it does (its translation, else the config file's comment).</summary>
    private static string SettingTooltip((string Section, string Key, string Value, string Description) entry) =>
        $"{Loc.Get($"{_plugin}.{entry.Key}", entry.Key)} = {entry.Value}\n{Loc.Get($"{_plugin}.{entry.Key}.desc", entry.Description)}";

    /// <summary>A plugin's editable config entries: everything but [General] (Enabled is the list's checkbox).</summary>
    private static List<(string Section, string Key, string Value, string Description)> Settings(string id) =>
        [.. Loader.Config(id).Entries().Where(e => e.Section != "General")];

    [UnmanagedCallersOnly]
    private static void OnOpenSettings(nint row) => Guard(() =>
    {
        var plugins = Loader.Known;
        if (row >= plugins.Count) return;
        _plugin = plugins[(int)row].Id;
        _first = 0;
        Visible(_list, false);
        Visible(_settings, true);
        Gui.SetText(_title, Gui.Literal(Loader.Name(plugins[(int)row])));
        ShowSettings();
    });

    [UnmanagedCallersOnly]
    private static void OnBack(nint unused) => Guard(() =>
    {
        if (_pick == null) ShowList();
        else ShowSettings(); // from a setting's choices back to the settings
    });

    [UnmanagedCallersOnly]
    private static void OnTurnPage(nint direction) => Guard(() =>
    {
        if (_pick is { } pick)
        {
            var choice = _pickFirst + (int)direction * SettingRows;
            if (choice < 0 || choice >= pick.Choices.Count) return;
            _pickFirst = choice;
            ShowChoices();
            return;
        }
        var first = _first + (int)direction * SettingRows;
        if (_plugin == null || first < 0 || first >= _entries.Count) return;
        _first = first;
        ShowSettings();
    });

    private static void ShowSettings()
    {
        _pick = null;
        _entries = Settings(_plugin!);
        if (_first >= _entries.Count) _first = 0;
        Gui.SetText(_title, Gui.Literal(Loader.Name(Loader.Known.First(p => p.Id == _plugin))));
        ShowPages(_first, _entries.Count);
        for (var row = 0; row < SettingRows; row++)
        {
            var container = Rows[row];
            var index = _first + row;
            Visible(container, index < _entries.Count);
            if (index >= _entries.Count) continue;
            var (section, key, value, _) = _entries[index];
            Gui.SetText(Gui.FindTextBox(container, "hoimod_setting_label"), Gui.Literal(Loc.Get($"{_plugin}.{key}", key)));
            var isBool = bool.TryParse(value, out var on);
            var listed = !isBool && Loader.Config(_plugin!).ChoicesOf(section, key) != null;
            var stepped = !isBool && !listed && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            var check = Gui.Find(container, CheckBoxSlot, "hoimod_setting_check");
            Shown(check, isBool);
            if (isBool) Check(check, on);
            Shown(Gui.Find(container, ButtonSlot, "hoimod_setting_decrease"), stepped);
            Shown(Gui.Find(container, ButtonSlot, "hoimod_setting_increase"), stepped);
            Shown(Gui.Find(container, ButtonSlot, "hoimod_setting_pick"), listed);
            var shownValue = Gui.FindTextBox(container, "hoimod_setting_value");
            Shown(shownValue, !isBool);
            if (!isBool) Gui.SetText(shownValue, Display(value)); // the tooltip has it whole
        }
    }

    /// <summary>The page arrows and "n/m" for <paramref name="count"/> rows from <paramref name="first"/> on.</summary>
    private static void ShowPages(int first, int count)
    {
        var pages = (count + SettingRows - 1) / SettingRows;
        Shown(_prev, pages > 1);
        Shown(_next, pages > 1);
        Shown(_pageText, pages > 1);
        if (pages > 1) Gui.SetText(_pageText, $"{first / SettingRows + 1}/{pages}");
    }

    private static string Display(string value) => value == "" ? Loc.Get("HOIMOD_SETTING_NONE", "(none)") : Gui.Literal(Fit(value));

    /// <summary>A text setting's list button: its choices in the rows, a page at a time, starting at the current one's page.</summary>
    [UnmanagedCallersOnly]
    private static void OnPick(nint row) => Guard(() =>
    {
        if (_plugin == null || _pick != null || _first + row >= _entries.Count) return;
        var (section, key, value, description) = _entries[_first + (int)row];
        if (Loader.Config(_plugin).ChoicesOf(section, key) is not { Count: > 0 } choices) return;
        _pick = (section, key, description, choices);
        _pickFirst = Math.Max(0, choices.ToList().FindIndex(c => c.Equals(value, StringComparison.OrdinalIgnoreCase))) / SettingRows * SettingRows;
        ShowChoices();
    });

    /// <summary>The picked setting's choices: one a row, the current one ticked; ticking another sets it.</summary>
    private static void ShowChoices()
    {
        var (section, key, _, choices) = _pick!.Value;
        var current = _entries.FirstOrDefault(e => e.Section == section && e.Key == key).Value ?? "";
        Gui.SetText(_title, Gui.Literal(Loc.Get($"{_plugin}.{key}", key)));
        ShowPages(_pickFirst, choices.Count);
        for (var row = 0; row < SettingRows; row++)
        {
            var container = Rows[row];
            var index = _pickFirst + row;
            Visible(container, index < choices.Count);
            if (index >= choices.Count) continue;
            Gui.SetText(Gui.FindTextBox(container, "hoimod_setting_label"), Display(choices[index]));
            var check = Gui.Find(container, CheckBoxSlot, "hoimod_setting_check");
            Shown(check, true);
            Check(check, choices[index].Equals(current, StringComparison.OrdinalIgnoreCase));
            Shown(Gui.Find(container, ButtonSlot, "hoimod_setting_decrease"), false);
            Shown(Gui.Find(container, ButtonSlot, "hoimod_setting_increase"), false);
            Shown(Gui.Find(container, ButtonSlot, "hoimod_setting_pick"), false);
            Shown(Gui.FindTextBox(container, "hoimod_setting_value"), false);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnDecrease(nint row) => Guard(() => Step(row, -1));

    [UnmanagedCallersOnly]
    private static void OnIncrease(nint row) => Guard(() => Step(row, +1));

    /// <summary>Changes a number by a step that fits its size (1 for 0..99, 10 for 100..999, ...); Shift: 10 steps.</summary>
    private static void Step(nint row, int direction)
    {
        if (_plugin == null || _first + row >= _entries.Count) return;
        var (section, key, value, description) = _entries[_first + (int)row];
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            var step = (long)Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, Math.Abs(whole)))) - 1));
            Save(section, key, (whole + direction * step * (Gui.ShiftHeld ? 10 : 1)).ToString(CultureInfo.InvariantCulture), description);
        }
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            var step = number == 0 ? 0.1 : Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(number))) - 1);
            Save(section, key, Math.Round(number + direction * step * (Gui.ShiftHeld ? 10 : 1), 6).ToString(CultureInfo.InvariantCulture), description);
        }
    }

    /// <summary>A value cut to fit its box (about 22 Latin letters; a Hangul or CJK one counts double).</summary>
    internal static string Fit(string value)
    {
        var width = 0;
        for (var i = 0; i < value.Length; i++)
        {
            width += value[i] >= 0x1100 ? 2 : 1;
            if (width > 22) return value[..Math.Max(1, i - 1)] + "..";
        }
        return value;
    }

    [UnmanagedCallersOnly]
    private static void OnSettingToggle(nint row) => Guard(() =>
    {
        if (_pick is { } pick) // a choice ticked: it is the setting's value now (ShowSettings, and so the later calls of this click, see the settings again)
        {
            if (_pickFirst + row < pick.Choices.Count) Save(pick.Section, pick.Key, pick.Choices[_pickFirst + (int)row], pick.Description);
            return;
        }
        if (_plugin == null || _first + row >= _entries.Count) return;
        var (section, key, value, description) = _entries[_first + (int)row];
        var on = (*(int*)(Gui.Find(Rows[row], CheckBoxSlot, "hoimod_setting_check") + CheckState) & 1) != 0;
        if (!bool.TryParse(value, out var was) || was == on) return; // called once per observer slot of a click
        Save(section, key, on ? "True" : "False", description);
    });

    private static void Save(string section, string key, string value, string description)
    {
        Loader.Config(_plugin!).Set(section, key, value, description);
        Log.Info($"{_plugin}: {section}.{key} = {value}, reloading plugins");
        ShowSettings();
        Loader.RequestReload();
    }

    // ---- helpers ----

    private static void Visible(nint window, bool show) =>
        ((delegate* unmanaged<nint, void>)(*(nint**)(window + WidgetOfWindow))[show ? ShowSlot : HideSlot])(window + WidgetOfWindow);

    private static void Shown(nint widget, bool show)
    {
        if (widget == 0 || Gui.IsHidden(widget) == !show) return;
        if (show) Gui.Show(widget);
        else Gui.Hide(widget);
    }

    private static void Check(nint box, bool on)
    {
        *(int*)(box + CheckState) = on ? 1 : 0;
        _checkBoxSprite(box);
    }

    private static void BindCheckBox(nint box, delegate* unmanaged<nint, void> handler, nint cookie)
    {
        var glue = Allocator.AllocZeroed(64);
        *(nint*)glue = Sig.VTable(CheckBoxGlue);
        *(nint*)(glue + 8) = cookie;
        *(nint*)(glue + 16) = *(nint*)(glue + 24) = *(nint*)(glue + 32) = (nint)handler; // toggled, checked, unchecked
        AddObserver(box, glue);
    }

    private static void BindButton(nint button, delegate* unmanaged<nint, void> handler, nint cookie)
    {
        var glue = Allocator.AllocZeroed(8 + 20 * 64);
        *(nint*)glue = Sig.VTable(ButtonGlue);
        *(nint*)(glue + 8) = Sig.VTable(MemberCall); // slot 0 (click): an inline std::function calling handler(cookie)
        *(nint*)(glue + 16) = (nint)handler;
        *(nint*)(glue + 24) = cookie;
        *(nint*)(glue + 64) = glue + 8;
        AddObserver(button, glue);
    }

    private static void AddObserver(nint widget, nint glue)
    {
        var list = widget + Observers;
        ((delegate* unmanaged<nint, nint, void>)(*(nint**)list)[AddObserverSlot])(list, glue);
    }

    private static void Guard(Action action) => Log.Guard("Mods tab", action); // never throw into the game
}
