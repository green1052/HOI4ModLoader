using HOI4ModLoader;

namespace PlanTools;

/// <summary>The commands the battle-plan window's own buttons post (RTTI names), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>A stance button: (group CRefObj*, int type 0-2); an army group's stance also reaches its armies.</summary>
    [VTable] public const string SetStance = "CSetOrderGroupExecutionTypeCommand";

    /// <summary>The plan's play button: (group CRefObj*, int orderId 0 = the whole group, char execute, char alsoArmies).</summary>
    [VTable] public const string ExecutePlan = "COrderExecuteCommand";
}

/// <summary>
/// Batch controls for the player's armies: Ctrl+Alt+1/2/3 sets every group's stance at once (careful, balanced, rush),
/// Ctrl+Alt+E activates every battle plan, Ctrl+Alt+H halts them, Ctrl+Alt+R pulls every fighting division back.
/// Each key posts the same command the battle-plan window's own button posts for that group, so multiplayer lockstep
/// holds and other players need nothing.
/// </summary>
[Plugin("com.green1052.plantools", "Plan Tools", "1.0.0", GameVersion = "1.19.3")]
public unsafe class PlanTools : Plugin
{
    private const int ProvidersOfCountry = 0x168; // CCountry: CPdxArray<COrdersGroupProvider*>, one per theatre
    private const int GroupsOfProvider = 0xB0;    // provider: CPdxArray<COrdersGroup*>: its armies and army groups
    private const int StanceOfGroup = 0x1A4;      // int: 0 careful, 1 balanced, 2 rush (the AI also writes 4; never send it)
    private const int SelfRefOfGroup = 0x8;       // the group's CRef {id, idx}, what the command ctors copy into cmd+0x28
    private const int OrdersOfGroup = 0x98;       // first of the group's three COrder* arrays (+0x98/+0xB0/+0xC8, stride 0x18)
    private const int ExecutingOfOrder = 0x248;   // byte: the order is running
    private const int CommandSize = 0x38, GroupOfCommand = 0x28, ExecuteOfCommand = 0x34, AlsoArmiesOfCommand = 0x35; // COrderExecuteCommand ctor
    private const int UnitsOfCountry = 0x290, KindOfUnit = 8, CommanderTag = 0x1D8; // FrontLine's unit layouts
    private const int LocationOfUnit = 0x1F0, CombatsOfUnit = 0x1A8, Retreating = 0x24C;
    private const int ControllerOfProvince = 0x188;

    private static readonly string[] Stances = ["careful", "balanced", "rush"];
    private static readonly int[] Keys = ['1', '2', '3', 'E', 'H', 'R']; // with Ctrl+Alt held
    private readonly bool[] _down = new bool[Keys.Length];
    private bool _stance = true, _execute = true, _halt = true, _retreat = true;

    public override void Load()
    {
        _stance = Config.Bind("Keys", "Stance", true,
            "Ctrl+Alt+1/2/3 sets the stance of all your armies and army groups at once: careful, balanced, rush.");
        _execute = Config.Bind("Keys", "Execute", true, "Ctrl+Alt+E activates every battle plan at once.");
        _halt = Config.Bind("Keys", "Halt", true, "Ctrl+Alt+H halts every battle plan at once.");
        _retreat = Config.Bind("Keys", "Retreat", true,
            "Ctrl+Alt+R pulls every one of your divisions that is fighting back to a neighbouring province you control.");
        MainThread.Every(TimeSpan.FromMilliseconds(30), Poll);
    }

    /// <summary>Fires each action once per key press while Ctrl+Alt is held (a key held across a modifier change does not re-fire).</summary>
    private void Poll()
    {
        var hot = Gui.CtrlHeld && Gui.AltHeld && Gui.GameFocused;
        var hit = -1;
        for (var i = 0; i < Keys.Length; i++)
        {
            var down = Gui.KeyHeld(Keys[i]);
            if (hot && down && !_down[i]) hit = i;
            _down[i] = down;
        }
        if (hit < 0) return;
        var tag = Game.ControlledTag;
        if (tag == 0) return; // main menu, loading, observer
        if (hit <= 2 && _stance) SetStance(tag, hit);
        else if (hit == 3 && _execute) Execute(tag, true);
        else if (hit == 4 && _halt) Execute(tag, false);
        else if (hit == 5 && _retreat) Retreat(tag);
    }

    /// <summary>Every fighting division of <paramref name="tag"/> moves to a neighbouring province we control — a move
    /// order out of combat is the retreat the game's own button orders. Divisions with nowhere to go stay.</summary>
    private void Retreat(int tag)
    {
        var country = Game.Country(tag);
        if (country == 0) return;
        var moved = 0;
        foreach (var unit in Native.PdxArray(country + UnitsOfCountry))
        {
            if (*(int*)(unit + KindOfUnit) != 0 || *(int*)(unit + CommanderTag) != tag) continue;
            if (*(int*)(unit + CombatsOfUnit + 0xC) == 0 || *(byte*)(unit + Retreating) != 0) continue; // only the fighting
            var from = *(nint*)(unit + LocationOfUnit);
            if (from == 0) continue;
            foreach (var id in Game.Neighbours(from))
            {
                var to = Game.Province(id);
                if (to == 0 || *(int*)(to + ControllerOfProvince) != tag) continue; // ponytail: our own ground only; allied ground when asked for
                if (Commands.Move(unit, id)) moved++;
                break;
            }
        }
        Log.Info($"retreat: {moved} division(s) pulled out of combat");
    }

    private void SetStance(int tag, int stance)
    {
        var changed = 0;
        foreach (var group in Groups(tag))
        {
            if (*(int*)(group + StanceOfGroup) == stance) continue;
            var cmd = Commands.New(Signatures.SetStance, CommandSize, tag, stance);
            *(nint*)(cmd + GroupOfCommand) = *(nint*)(group + SelfRefOfGroup);
            if (Commands.TryPost(cmd)) changed++;
        }
        Log.Info($"stance {Stances[stance]}: {changed} group(s)");
    }

    private void Execute(int tag, bool activate)
    {
        var changed = 0;
        foreach (var group in Groups(tag))
        {
            if (!Pending(group, activate)) continue;
            var cmd = Commands.New(Signatures.ExecutePlan, CommandSize, tag, 0); // orderId 0: the whole group
            *(nint*)(cmd + GroupOfCommand) = *(nint*)(group + SelfRefOfGroup);
            *(byte*)(cmd + ExecuteOfCommand) = (byte)(activate ? 1 : 0);
            *(byte*)(cmd + AlsoArmiesOfCommand) = 1; // an army group's command also reaches its armies
            if (Commands.TryPost(cmd)) changed++;
        }
        Log.Info($"{(activate ? "activated" : "halted")} {changed} battle plan(s)");
    }

    /// <summary>Whether one of the group's orders runs counter to <paramref name="activate"/>: a group worth posting for.</summary>
    private static bool Pending(nint group, bool activate)
    {
        for (var array = OrdersOfGroup; array <= OrdersOfGroup + 0x30; array += 0x18)
            foreach (var order in Native.PdxArray(group + array))
                if (order != 0 && (*(byte*)(order + ExecutingOfOrder) != 0) != activate) return true;
        return false;
    }

    /// <summary>Every army and army group of <paramref name="tag"/> (each theatre's provider lists them all).</summary>
    private static List<nint> Groups(int tag)
    {
        var groups = new List<nint>();
        var country = Game.Country(tag);
        if (country == 0) return groups;
        foreach (var provider in Native.PdxArray(country + ProvidersOfCountry).ToArray())
            groups.AddRange(Native.PdxArray(provider + GroupsOfProvider).ToArray());
        return groups;
    }
}
