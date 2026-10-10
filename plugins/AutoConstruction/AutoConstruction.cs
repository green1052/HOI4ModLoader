using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AutoConstruction;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>Construction tab, click on a building button (item): selects the building for the construction map mode.</summary>
    [Signature] public const string BuildingClick = "40 53 48 83 EC 30 80 3D ? ? ? ? ? 48 8B D9 74 ? 48 8B 0D";

    /// <summary>CCountryConstructionView::UpdateBuildingItems(view): (re)fills the building buttons, every frame while the tab is open.</summary>
    [Signature] public const string UpdateBuildingItems = "48 89 5C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC D0 01 00 00 0F 29 B4 24 ? ? ? ? 4C 8B E9";

    /// <summary>BuildingContainer::Find(container, CBuildingType*) -> CBuilding* or null.</summary>
    [Signature] public const string FindBuilding = "80 7A 20 00 74 ? 83 79 2C 00 74 ? 48 63 92 E0 02 00 00 48 8B 41 20";

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

    [VTable] public const string ConstructionCommand = "CAddConstructionCommand";
    [VTable] public const string BuildingReference = "CBuildingReference";
    [VTable] public const string BuildingLine = "CBuildingProductionLine";
}

/// <summary>
/// Auto construction targets. In the construction tab, Shift+click a building to raise its target by 1 (Ctrl+Shift: 5),
/// Ctrl+click to lower it; the target shows on the button (Auto Construction UI mod). While a building's count (built
/// plus queued) is under its target, more is queued with the construction tab's own command (so multiplayer stays in
/// sync), in the best state first: core states, then higher infrastructure, then more free building slots.
/// </summary>
[Plugin("com.green1052.autoconstruction", "Auto Construction", "1.0.0", GameVersion = "1.19.3")]
public unsafe class AutoConstruction : Plugin
{
    // Layouts for HOI4 1.19.3.
    private const int StatesOfCountry = 0x460, ProductionOfCountry = 0xF68;
    private const int ProvincesOfState = 24, IdOfState = 88, CoresOfState = 176, BuildingsOfState = 288;
    private const int IdOfProvince = 164, BuildingsOfProvince = 400;
    private const int TokenOfType = 8, ProvinceLevel = 700;      // CBuildingType: name token; > 0 = built per province (naval base, bunker...)
    private const int LevelOfBuilding = 64, ContainerOfBuilding = 472, TypeOfBuilding = 480; // CBuilding: int16 level, its container
    private const int Lines = 112, LevelsOfLine = 72, BuildingOfLine = 112, RepairLine = 140; // production status, CBuildingProductionLine
    private const int Items = 1440, WindowOfItem = 16, TypeOfItem = 32; // construction view building buttons
    private const int Location = 56, Building = 60, Levels = 72, Insertion = 76; // CAddConstructionCommand
    private const byte AtEnd = 1;
    private const long PendingMs = 30_000;
    private const string TargetBox = "hoimod_auto";

    [Function(CallingConventions.Microsoft)] public delegate nint ClickFn(nint item);
    [Function(CallingConventions.Microsoft)] public delegate void ViewFn(nint view);

    private IHook<ClickFn>? _click;
    private IHook<ViewFn>? _update;
    private delegate* unmanaged<nint, nint, nint> _find;
    private delegate* unmanaged<nint, nint, int> _freeLevels;
    private delegate* unmanaged<nint, nint, nint, byte, byte> _canBuildInState, _canBuildInProvince;
    private delegate* unmanaged<nint, int> _freeSharedSlots;
    private delegate* unmanaged<int, nint> _tokenToString;

    private readonly Dictionary<nint, int> _targets = [];                    // CBuildingType* -> wanted count
    private readonly Dictionary<nint, (int Count, long Until)> _sent = [];   // by type: count expected once the sent commands land
    private readonly HashSet<nint> _failed = [];                             // types whose last try found nowhere to build (logged once)
    private nint _infrastructure;                                               // its CBuildingType*, once seen on a button
    private PerGame _game = null!;
    private bool _reportedBox;

    public override void Load()
    {
        _find = (delegate* unmanaged<nint, nint, nint>)Sig.Find(Signatures.FindBuilding);
        _freeLevels = (delegate* unmanaged<nint, nint, int>)Sig.Find(Signatures.FreeLevels);
        _canBuildInState = (delegate* unmanaged<nint, nint, nint, byte, byte>)Sig.Find(Signatures.CanBuildInState);
        _canBuildInProvince = (delegate* unmanaged<nint, nint, nint, byte, byte>)Sig.Find(Signatures.CanBuildInProvince);
        _freeSharedSlots = (delegate* unmanaged<nint, int>)Sig.Find(Signatures.FreeSharedSlots);
        _tokenToString = (delegate* unmanaged<int, nint>)Sig.Find(Signatures.TokenToString);
        _game = new PerGame(() =>
        {
            _targets.Clear();
            _sent.Clear();
            _failed.Clear();
        });

        _click = Patches.Hook<ClickFn>(Sig.Find(Signatures.BuildingClick), item =>
        {
            var type = *(nint*)(item + TypeOfItem);
            _game.Check();
            // A plain click, or Ctrl+click without a target to lower, is the game's.
            if (type == 0 || !Gui.ShiftHeld && !(Gui.CtrlHeld && _targets.ContainsKey(type))) return _click!.OriginalFunction(item);
            var country = Game.Country(Game.ControlledTag);
            if (country == 0) return _click!.OriginalFunction(item);
            var count = Count(country, type);
            // Targets count from what the country has (built and queued): +1 means one more than now.
            var from = Math.Max(count, _targets.GetValueOrDefault(type, count));
            var target = Math.Max(0, from + (!Gui.ShiftHeld ? -1 : Gui.CtrlHeld ? 5 : 1));
            if (target <= count && !Gui.ShiftHeld) _targets.Remove(type); // lowered to what's there: off
            else _targets[type] = target;
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
    }

    private string Name(nint type) => Native.Utf8(Native.MsvcString(_tokenToString(*(int*)(type + TokenOfType))));

    /// <summary>
    /// Writes how many more the target wants ("+N", "+0" once reached) into the button's target box, a small number in its
    /// bottom right corner; nothing without a target.
    /// </summary>
    private void ShowTarget(nint item)
    {
        var type = *(nint*)(item + TypeOfItem);
        if (type == 0) return;
        if (_infrastructure == 0 && Name(type) == "infrastructure") _infrastructure = type;
        var box = Gui.FindTextBox(*(nint*)(item + WindowOfItem), TargetBox);
        if (!_reportedBox)
        {
            _reportedBox = true;
            if (box == 0) Log.Info($"no '{TargetBox}' element: targets work but don't show (is the Auto Construction UI mod enabled?)");
        }
        if (box == 0) return;
        var country = Game.Country(Game.ControlledTag);
        var text = _targets.TryGetValue(type, out var target) && country != 0 ? $"+{Math.Max(0, target - Count(country, type))}" : "";
        if (Native.Utf8(Gui.Text(box)) != text) Gui.SetText(box, text);
    }

    private void Pump()
    {
        _game.Check();
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        if (country == 0) return;
        var now = Environment.TickCount64;
        foreach (var (type, target) in _targets)
        {
            var count = Count(country, type);
            if (_sent.TryGetValue(type, out var sent) && count < sent.Count && now < sent.Until) continue; // not all in the queue yet
            _sent.Remove(type);
            if (count >= target) continue;
            var queued = Queue(country, tag, type, target - count);
            if (queued > 0)
            {
                _sent[type] = (count + queued, now + PendingMs);
                _failed.Remove(type);
            }
            else if (_failed.Add(type)) Log.Info($"{Name(type)}: {count} of {target}, but no state can take more (or the game refused the command)");
        }
    }

    /// <summary>Levels of <paramref name="type"/> built in the country's states plus levels queued.</summary>
    private int Count(nint country, nint type)
    {
        var count = 0;
        var perProvince = *(int*)(type + ProvinceLevel) > 0;
        foreach (var state in Native.PdxArray(country + StatesOfCountry))
        {
            if (!perProvince) count += Level(state + BuildingsOfState, type);
            else foreach (var province in Native.PdxArray(state + ProvincesOfState)) count += Level(province + BuildingsOfProvince, type);
        }
        var production = *(nint*)(country + ProductionOfCountry);
        if (production == 0) return count;
        var lineVTable = Sig.VTable(Signatures.BuildingLine);
        foreach (var line in Native.PdxArray(production + Lines))
        {
            if (*(nint*)line != lineVTable || *(byte*)(line + RepairLine) != 0) continue;
            var building = *(nint*)(line + BuildingOfLine);
            if (building != 0 && *(nint*)(building + TypeOfBuilding) == type) count += *(int*)(line + LevelsOfLine);
        }
        return count;
    }

    private int Level(nint container, nint type)
    {
        var building = _find(container, type);
        return building == 0 ? 0 : *(short*)(building + LevelOfBuilding);
    }

    /// <summary>
    /// Queues up to <paramref name="wanted"/> levels, one per place (state, or province for province buildings) from the
    /// best down, places that already have a line of this building last: separate lines are built at the same time, while
    /// the levels of one line are built one after another. Returns the levels queued.
    /// </summary>
    private int Queue(nint country, int tag, nint type, int wanted)
    {
        var perProvince = *(int*)(type + ProvinceLevel) > 0;
        var building = new HashSet<nint>(); // containers with a construction line of this type
        var production = *(nint*)(country + ProductionOfCountry);
        if (production != 0)
            foreach (var line in Native.PdxArray(production + Lines))
            {
                var b = *(nint*)line == Sig.VTable(Signatures.BuildingLine) ? *(nint*)(line + BuildingOfLine) : 0;
                if (b != 0 && *(nint*)(b + TypeOfBuilding) == type) building.Add(*(nint*)(b + ContainerOfBuilding));
            }

        var places = new List<(nint Container, int Id, int Free, (int, int, int, int) Rank)>();
        foreach (var state in Native.PdxArray(country + StatesOfCountry))
        {
            if (_canBuildInState(country, state, type, 1) == 0) continue;
            var rank = (Native.PdxArray<int>(state + CoresOfState).Contains(tag) ? 1 : 0, _infrastructure == 0 ? 0 : Level(state + BuildingsOfState, _infrastructure),
                _freeSharedSlots(state), -*(int*)(state + IdOfState));
            if (!perProvince) places.Add((state + BuildingsOfState, *(int*)(state + IdOfState), _freeLevels(state + BuildingsOfState, type), rank));
            else
                foreach (var province in Native.PdxArray(state + ProvincesOfState))
                    if (_canBuildInProvince(country, province, type, 1) != 0)
                        places.Add((province + BuildingsOfProvince, *(int*)(province + IdOfProvince), _freeLevels(province + BuildingsOfProvince, type), rank));
        }
        var order = places.Where(p => p.Free > 0)
            .OrderBy(p => building.Contains(p.Container)).ThenByDescending(p => p.Rank).ToList();

        var queued = 0;
        foreach (var place in order.Take(wanted))
        {
            var cmd = Commands.New(Signatures.ConstructionCommand, 0x50, tag, Sig.VTable(Signatures.BuildingReference));
            *(int*)(cmd + Location) = place.Id;
            *(int*)(cmd + Building) = *(int*)(type + TokenOfType);
            *(int*)(cmd + Levels) = 1;
            *(byte*)(cmd + Insertion) = AtEnd;
            if (Commands.TryPost(cmd)) queued++;
        }
        if (queued > 0) Log.Info($"{Name(type)}: queued 1 level in each of {queued} {(perProvince ? "provinces" : "states")}");
        return queued;
    }
}
