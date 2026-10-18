using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// Auto construction target on each building button of the construction tab; appended so it draws over the button.
[assembly: GuiPatch("interface/countryconstructionsview.gui", "gui/auto_construction_target.gui", After = @"(?s)name = ""start_construction_entry"".*?(?=\n\t\})")]

namespace AutoConstruction;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>Construction tab, click on a building button (item): selects the building for the construction map mode.</summary>
    [Signature] public const string BuildingClick = "40 53 48 83 EC 30 80 3D ? ? ? ? ? 48 8B D9 74 ? 48 8B 0D";

    /// <summary>CCountryConstructionView::UpdateBuildingItems(view): (re)fills the building buttons, every frame while the tab is open.</summary>
    [Signature] public const string UpdateBuildingItems = "48 89 5C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC D0 01 00 00 0F 29 B4 24 ? ? ? ? 4C 8B E9";

    /// <summary>BuildingContainer::GetFreeLevels(container, type) -> int: max level minus planned (built and queued), shared slots included.</summary>
    [Signature] public const string FreeLevels = "48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC 20 4C 63 82";

    /// <summary>CCountry::CanBuildInState(country, state, type, bool) -> bool: what the construction map mode allows.</summary>
    [Signature] public const string CanBuildInState = "48 8B 42 18 4D 8B D0 4C 8B C1 48 8B 08 48 8B 81 B8 00 00 00 F6 80 D2 00 00 00 01";

    /// <summary>CCountry::CanBuildInProvince(country, province, type, bool) -> bool.</summary>
    [Signature] public const string CanBuildInProvince = "48 8B 82 B8 00 00 00 4D 8B D0 4C 8B C1 F6 80 D2 00 00 00 01";

    /// <summary>CState::GetFreeSharedSlots(state) -> int: building slots left for factories, dockyards and the like.</summary>
    [Signature] public const string FreeSharedSlots = "48 89 5C 24 ? 57 48 83 EC 20 33 D2 48 8B D9 E8";

    /// <summary>Lexer: token -> std::string* (a building type's name).</summary>
    [Signature] public const string TokenToString = "40 53 48 83 EC 30 48 63 D9 80 3D";

    /// <summary>
    /// CProductionStatus::IsConstructionSaturated(status) -> bool: the queue takes every free civilian factory. Reads the
    /// define MAX_CIV_FACTORIES_PER_LINE (`mov r9d, [rip+disp32]` at +0x41).
    /// </summary>
    [Signature] public const string ConstructionSaturated = "48 B8 09 E1 D1 C6 11 6B F1 29 48 F7 A9 78 03 00 00 4C 8B C2 49 C1 F8 0E 49 8B C0 48 C1 E8 3F 4C 03 C0 48 8B 41 70 44 2B 81 B0 03 00 00 44 2B 81 98 03 00 00 48 63 49 7C";

    [VTable] public const string ConstructionCommand = "CAddConstructionCommand";
    [VTable] public const string BuildingReference = "CBuildingReference";
    [VTable] public const string BuildingLine = "CBuildingProductionLine";
}

/// <summary>
/// Construction targets per building. In the construction tab, Shift+click a building to raise its target by 1
/// (Ctrl+Shift: 5), Ctrl+click to lower it; the button shows how many are left to build. While built plus queued is under
/// the target, more is queued with the construction tab's own command, so multiplayer stays in sync. Best states first:
/// cores, then higher infrastructure (lower for infrastructure itself), then more free slots. Also runs AutoSupply.cs and
/// AutoProduction.cs.
/// </summary>
[Plugin("com.green1052.autoconstruction", "Auto Construction", "1.0.0", GameVersion = "1.19.3")]
public unsafe partial class AutoConstruction : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int StatesOfCountry = 0x460, ProductionOfCountry = 0xF68;
    // CProductionStatus: civilian factories (fixed point), those used by construction lines, those used elsewhere (trade, consumer goods).
    private const int CivilianFactories = 0x378, DamagedFactories = 0x390, ConstructionFactories = 0x398, OtherFactories = 0x3B0;
    private const int CoresOfState = 176;
    private const int TokenOfType = 8, ProvinceLevel = 700;      // CBuildingType: name token; > 0 = built per province (naval base, bunker...)
    private const int ContainerOfBuilding = 472, TypeOfBuilding = 480; // CBuilding: its container, its type
    private const int Lines = 112, FactoriesOfLine = 0x18, LevelsOfLine = 72, BuildingOfLine = 112, RepairLine = 140; // production status, CBuildingProductionLine
    private const int Items = 1440, WindowOfItem = 16, TypeOfItem = 32; // construction view building buttons
    private const int Location = 56, Building = 60, Levels = 72, Insertion = 76; // CAddConstructionCommand
    private const byte AtEnd = 1;
    private const long PendingMs = 30_000;
    private const int BigStep = 5;                               // Ctrl+Shift+click
    private const string TargetBox = "hoimod_auto";

    [Function(CallingConventions.Microsoft)] public delegate nint ClickFn(nint item);
    [Function(CallingConventions.Microsoft)] public delegate void ViewFn(nint view);

    private IHook<ClickFn>? _click;
    private IHook<ViewFn>? _update;
    private delegate* unmanaged<nint, nint, int> _freeLevels;
    private delegate* unmanaged<nint, nint, nint, byte, byte> _canBuildInState, _canBuildInProvince;
    private delegate* unmanaged<nint, int> _freeSharedSlots;
    private delegate* unmanaged<int, nint> _tokenToString;
    private int* _maxFactoriesPerLine;

    // Wanted count. Building types are made once at startup, so a pointer names the same building in every game loaded.
    private readonly Dictionary<(int Tag, nint Type), int> _targets = [];
    private readonly Dictionary<nint, (int Count, long Until)> _sent = [];   // by type: count expected once the sent commands land
    private readonly HashSet<nint> _failed = [];                             // types whose last try found nowhere to build (logged once)
    private PerGame _game = null!;
    private bool _reportedBox;
    private int _ahead;

    public override void Load()
    {
        _ahead = Math.Max(0, Config.Bind("Construction", "QueueAhead", 2,
            "Lines kept waiting in the queue beyond what idle civilian factories work on, so a finished line is replaced at once."));
        var supply = Config.Bind("Supply", "AutoSupply", true,
            "Where your divisions are low on supply, upgrade a saturated hub's railway, connect a cut-off hub, or build a new hub.");
        var production = Config.Bind("Production", "AutoProduction", true,
            "Move military factories, one at a time, from lines with a big equipment surplus to lines that are short.");
        // Bound even when off: unbound keys are pruned from the file after Load.
        _surplusPercent = Math.Max(0, Config.Bind("Production", "SurplusPercent", 50, "A line gives up factories when its surplus is over this share (%) of what the army needs."));
        _coverDays = Math.Max(0, Config.Bind("Production", "CoverDays", 60, "A short line gets factories only if its own output won't cover the shortage within this many days. A moved factory restarts at base efficiency."));
        _movesPerDay = Math.Max(1, Config.Bind("Production", "MovesPerTurn", 2, "Most factories moved per game day, so lines keep their efficiency."));

        _ = Sig.Find(Buildings.FindSig);
        _freeLevels = (delegate* unmanaged<nint, nint, int>)Sig.Find(Signatures.FreeLevels);
        _canBuildInState = (delegate* unmanaged<nint, nint, nint, byte, byte>)Sig.Find(Signatures.CanBuildInState);
        _canBuildInProvince = (delegate* unmanaged<nint, nint, nint, byte, byte>)Sig.Find(Signatures.CanBuildInProvince);
        _freeSharedSlots = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FreeSharedSlots);
        _tokenToString = (delegate* unmanaged<int, nint>)Sig.Find(Signatures.TokenToString);
        var saturated = Sig.Find(Signatures.ConstructionSaturated);
        if (*(uint*)(saturated + 0x41) << 8 != 0x0D8B4400) throw new InvalidOperationException("MAX_CIV_FACTORIES_PER_LINE not found");
        _maxFactoriesPerLine = (int*)(saturated + 0x48 + *(int*)(saturated + 0x44));
        RestoreTargets();
        _game = new PerGame(() =>
        {
            _sent.Clear();
            _failed.Clear();
            _supplySent.Clear();
            _starvingSince.Clear();
            _hubUpgraded.Clear();
            (_supplyDay, _lastHub) = (-1, int.MinValue);
            _pending.Clear();
            _day = -1;
        });

        _click = Patches.Hook<ClickFn>(Sig.Find(Signatures.BuildingClick), item =>
        {
            var key = (Tag: Game.ControlledTag, Type: *(nint*)(item + TypeOfItem));
            var type = key.Type;
            _game.Check();
            // A plain click, or Ctrl+click without a target to lower, is the game's.
            if (type == 0 || !Gui.ShiftHeld && !(Gui.CtrlHeld && _targets.ContainsKey(key))) return _click!.OriginalFunction(item);
            var country = Game.Country(key.Tag);
            if (country == 0) return _click!.OriginalFunction(item);
            var count = Count(country, type);
            // Targets count from what the country has (built and queued): +1 means one more than now.
            var from = Math.Max(count, _targets.GetValueOrDefault(key, count));
            var target = Math.Max(0, from + (!Gui.ShiftHeld ? -1 : Gui.CtrlHeld ? BigStep : 1));
            if (target <= count && !Gui.ShiftHeld) _targets.Remove(key); // lowered to what's there: off
            else _targets[key] = target;
            SaveTargets();
            _failed.Remove(type);
            Log.Info($"{Name(type)}: target {target}, now {count} (built and queued)");
            ShowTarget(item);
            return 0;
        });
        _update = Patches.Hook<ViewFn>(Sig.Find(Signatures.UpdateBuildingItems), view =>
        {
            _update!.OriginalFunction(view);
            Log.Guard("target labels", () =>
            {
                foreach (var item in Native.PdxArray(view + Items)) ShowTarget(item);
            });
        });

        MainThread.Every(TimeSpan.FromSeconds(1), Pump);
        if (supply) LoadSupply();
        if (production) LoadProduction();
    }

    // Targets survive a plugin reload within one game process: building type pointers hold until it exits.
    private static readonly string TargetsFile = Path.Combine(Loader.ModsDir, "cache", "autoconstruction_targets.txt");

    private void SaveTargets()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TargetsFile)!);
            File.WriteAllLines(TargetsFile, [Environment.ProcessId.ToString(), .. _targets.Select(t => $"{t.Key.Tag} {t.Key.Type:X} {t.Value}")]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"targets not saved: {e.Message}"); }
    }

    private void RestoreTargets()
    {
        try
        {
            if (!File.Exists(TargetsFile)) return;
            var lines = File.ReadAllLines(TargetsFile);
            if (lines.Length == 0 || lines[0] != Environment.ProcessId.ToString()) return; // an earlier game run's pointers
            foreach (var line in lines.Skip(1))
                if (line.Split(' ') is [var tag, var type, var target] && int.TryParse(tag, out var t) && int.TryParse(target, out var n)
                    && long.TryParse(type, System.Globalization.NumberStyles.HexNumber, null, out var p)) _targets[(t, (nint)p)] = n;
            if (_targets.Count > 0) Log.Info($"kept {_targets.Count} construction target(s) across the reload");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Name(nint type) => Native.Utf8(Native.MsvcString(_tokenToString(*(int*)(type + TokenOfType))));

    /// <summary>
    /// Shows "+N" in the button's target box: levels still to build for its target; empty without a target. Queued levels
    /// count as missing until finished, or the click (which queues at once) would show "+0" right away.
    /// </summary>
    private void ShowTarget(nint item)
    {
        var type = *(nint*)(item + TypeOfItem);
        if (type == 0) return;
        var box = Gui.FindTextBox(*(nint*)(item + WindowOfItem), TargetBox);
        if (!_reportedBox)
        {
            _reportedBox = true;
            if (box == 0) Log.Info($"no '{TargetBox}' element: targets work but don't show (see GameFiles in this log)");
        }
        if (box == 0) return;
        var country = Game.Country(Game.ControlledTag);
        var text = _targets.TryGetValue((Game.ControlledTag, type), out var target) && country != 0 ? $"+{Math.Max(0, target - Count(country, type, queued: false))}" : "";
        if (Native.Utf8(Gui.Text(box)) != text) Gui.SetText(box, text);
    }

    private void Pump()
    {
        if (!Game.HasStarted) return; // menus, lobby, loading: no commands
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var now = Environment.TickCount64;
        var room = Staffable(country) + Math.Max(0, _ahead - Waiting(country));
        foreach (var ((owner, type), target) in _targets)
        {
            if (owner != tag) continue;
            var count = Count(country, type);
            if (_sent.TryGetValue(type, out var sent) && count < sent.Count && now < sent.Until) continue; // not all in the queue yet
            _sent.Remove(type);
            if (count >= target || room == 0) continue; // the rest once factories free up
            var queued = Queue(country, tag, type, Math.Min(target - count, room));
            room -= queued;
            if (queued > 0)
            {
                _sent[type] = (count + queued, now + PendingMs);
                _failed.Remove(type);
            }
            else if (_failed.Add(type)) Log.Info($"{Name(type)}: {count} of {target}, but no state can take more (or the game refused the command)");
        }
    }

    /// <summary>
    /// New construction lines the idle civilian factories can staff, at MAX_CIV_FACTORIES_PER_LINE a line (rounded up).
    /// The game staffs lines in queue order, so more lines would only wait.
    /// </summary>
    private int Staffable(nint country)
    {
        var production = *(nint*)(country + ProductionOfCountry);
        if (production == 0 || *_maxFactoriesPerLine <= 0) return 0;
        // Damaged factories are subtracted as the construction tab's "available" does: under repair they build nothing.
        var idle = (int)((*(long*)(production + CivilianFactories) - *(long*)(production + DamagedFactories)) / Native.FixedPoint)
                   - *(int*)(production + OtherFactories) - *(int*)(production + ConstructionFactories);
        return idle <= 0 ? 0 : (idle + *_maxFactoriesPerLine - 1) / *_maxFactoriesPerLine;
    }

    /// <summary>Construction lines (not repairs) with no civilian factory on them yet.</summary>
    private static int Waiting(nint country)
    {
        var production = *(nint*)(country + ProductionOfCountry);
        if (production == 0) return 0;
        var lineVTable = Sig.VTable(Signatures.BuildingLine);
        var waiting = 0;
        foreach (var line in Native.PdxArray(production + Lines))
            if (*(nint*)line == lineVTable && *(byte*)(line + RepairLine) == 0 && *(int*)(line + FactoriesOfLine) == 0) waiting++;
        return waiting;
    }

    /// <summary>Levels of <paramref name="type"/> built in the country's states, plus those queued (not repairs) if <paramref name="queued"/>.</summary>
    private int Count(nint country, nint type, bool queued = true)
    {
        var count = 0;
        var perProvince = *(int*)(type + ProvinceLevel) > 0;
        foreach (var state in Native.PdxArray(country + StatesOfCountry))
        {
            if (!perProvince) count += Buildings.Level(State.Buildings(state), type);
            else foreach (var province in State.Provinces(state)) count += Buildings.Level(Province.Buildings(province), type);
        }
        if (!queued) return count;
        foreach (var (line, _) in ConstructionLines(country, type))
            if (*(byte*)(line + RepairLine) == 0) count += *(int*)(line + LevelsOfLine);
        return count;
    }

    /// <summary>The country's construction lines (repairs too) of <paramref name="type"/>, with the CBuilding each works on.</summary>
    private static List<(nint Line, nint Building)> ConstructionLines(nint country, nint type)
    {
        var lines = new List<(nint, nint)>();
        var production = *(nint*)(country + ProductionOfCountry);
        if (production == 0) return lines;
        var lineVTable = Sig.VTable(Signatures.BuildingLine);
        foreach (var line in Native.PdxArray(production + Lines))
        {
            var building = *(nint*)line == lineVTable ? *(nint*)(line + BuildingOfLine) : 0;
            if (building != 0 && *(nint*)(building + TypeOfBuilding) == type) lines.Add((line, building));
        }
        return lines;
    }

    /// <summary>
    /// Queues up to <paramref name="wanted"/> levels, one per state (province for province buildings), best first. Places
    /// already building this type go last: separate lines build in parallel, one line's levels in turn. Returns the levels queued.
    /// </summary>
    private int Queue(nint country, int tag, nint type, int wanted)
    {
        var perProvince = *(int*)(type + ProvinceLevel) > 0;
        // Containers with a construction line of this type.
        var building = ConstructionLines(country, type).Select(line => *(nint*)(line.Building + ContainerOfBuilding)).ToHashSet();

        var infrastructureType = Buildings.Type(Buildings.InfrastructureSlot); // 0 before the game made its building database
        var places = new List<(nint Container, int Id, int Free, (int, int, int, int) Rank)>();
        foreach (var state in Native.PdxArray(country + StatesOfCountry))
        {
            if (_canBuildInState(country, state, type, 1) == 0) continue;
            var infrastructure = infrastructureType == 0 ? 0 : Buildings.Level(State.Buildings(state), infrastructureType);
            var rank = (Native.PdxArray<int>(state + CoresOfState).Contains(tag) ? 1 : 0,
                type == infrastructureType ? -infrastructure : infrastructure, // faster construction; infrastructure itself where it's lacking
                _freeSharedSlots(state), -State.Id(state));
            if (!perProvince) places.Add((State.Buildings(state), State.Id(state), _freeLevels(State.Buildings(state), type), rank));
            else
                foreach (var province in State.Provinces(state))
                    if (_canBuildInProvince(country, province, type, 1) != 0)
                        places.Add((Province.Buildings(province), Game.Id(province), _freeLevels(Province.Buildings(province), type), rank));
        }
        var order = places.Where(p => p.Free > 0)
            .OrderBy(p => building.Contains(p.Container)).ThenByDescending(p => p.Rank).ToList();

        var queued = order.Take(wanted).Count(place => PostConstruction(tag, place.Id, type));
        if (queued > 0) Log.Info($"{Name(type)}: queued 1 level in each of {queued} {(perProvince ? "provinces" : "states")}");
        return queued;
    }

    /// <summary>Queues one level of <paramref name="type"/> at the end of the construction queue, in a state or province (by id).</summary>
    private static bool PostConstruction(int tag, int location, nint type)
    {
        var cmd = Commands.New(Signatures.ConstructionCommand, 0x50, tag, Sig.VTable(Signatures.BuildingReference));
        *(int*)(cmd + Location) = location;
        *(int*)(cmd + Building) = *(int*)(type + TokenOfType);
        *(int*)(cmd + Levels) = 1;
        *(byte*)(cmd + Insertion) = AtEnd;
        return Commands.TryPost(cmd);
    }
}
