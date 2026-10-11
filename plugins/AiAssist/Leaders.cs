using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Leaders: the most important fronts choose first; infantry, doctrine and status traits count ----

    private const int FrontsOfGeneral = 0x48, HoldingFront = 0x104, TraitsOfLeader = 0xDC8, TypeOfLeader = 0xE7C, CombatsOfUnit = 0x1B4;

    [Function(CallingConventions.Microsoft)] public delegate void GeneralFn(nint general);
    [Function(CallingConventions.Microsoft)] public delegate int ScoreLeaderFn(nint leader, nint group, nint operation, nint front, byte defensive);

    private IHook<GeneralFn>? _updateLeaders;
    private IHook<ScoreLeaderFn>? _scoreLeader;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, string> TraitNames = new();

    private void LoadLeaders()
    {
        _updateLeaders = Patches.Hook<GeneralFn>(Sig.Find(Signatures.UpdateLeaders), general =>
        {
            var fronts = general + FrontsOfGeneral;
            nint[]? saved = null;
            Safely(() =>
            {
                if (!SheepMod.Active) return;
                var data = (nint*)*(nint*)fronts;
                var order = Native.PdxArray(fronts).ToArray();
                saved = order;
                var ranked = order.OrderByDescending(Importance).ToArray(); // stable: ties keep the game's order
                for (var i = 0; i < ranked.Length; i++) data[i] = ranked[i];
            });
            try { _updateLeaders!.OriginalFunction(general); }
            finally
            {
                // The game's order back (it only reads the list here; if it changed it anyway, leave it as it is).
                if (saved != null && *(int*)(fronts + 0xC) == saved.Length)
                    for (var i = 0; i < saved.Length; i++) ((nint*)*(nint*)fronts)[i] = saved[i];
            }
        });
        _scoreLeader = Patches.Hook<ScoreLeaderFn>(Sig.Find(Signatures.ScoreLeader), (leader, group, operation, front, defensive) =>
        {
            var score = _scoreLeader!.OriginalFunction(leader, group, operation, front, defensive);
            if (score <= 0 || front == 0 || !SheepMod.Active) return score;
            var fit = 1.0;
            Safely(() => fit = Fit(leader, front, defensive != 0));
            return (int)Math.Min(int.MaxValue, score * Math.Max(fit, 0.5));
        });
    }

    /// <summary>How much a front matters: its divisions, more when they fight, more when it attacks.</summary>
    private static double Importance(nint front)
    {
        var divisions = Native.PdxArray(front);
        if (divisions.Length == 0) return 0;
        var fighting = 0;
        foreach (var unit in divisions)
            if (*(int*)(unit + CombatsOfUnit) > 0) fighting++;
        return divisions.Length * (1 + 2.0 * fighting / divisions.Length) * (*(int*)(front + HoldingFront) != 0 ? 1 : 1.5);
    }

    /// <summary>Trait fit the game's score leaves out: infantry traits by the front's infantry share, field marshal doctrine by its posture, bad status.</summary>
    private static double Fit(nint leader, nint front, bool holding)
    {
        var divisions = Native.PdxArray(front);
        var armored = 0;
        foreach (var unit in divisions)
            if (*(nint*)(unit + StatsOfUnit) is var stats && stats != 0 && (*(long*)(stats + Hardness) >= Armored || *(long*)(stats + Armor) > 0)) armored++;
        var infantry = divisions.Length == 0 ? 0 : 1 - (double)armored / divisions.Length;
        var marshal = *(int*)(leader + TypeOfLeader) == 0;
        var fit = 1.0;
        foreach (var trait in Native.PdxArray(leader + TraitsOfLeader))
        {
            var name = TraitNames.GetOrAdd(trait, t => Native.Utf8(Native.MsvcString(t + 0x10)));
            fit += name switch
            {
                "infantry_leader" or "infantry_expert" or "ambusher" => 0.25 * infantry,
                "offensive_doctrine" or "aggressive_assaulter" or "fast_planner" when marshal => holding ? -0.05 : 0.2,
                "defensive_doctrine" or "unyielding_defender" or "thorough_planner" when marshal => holding ? 0.2 : -0.05,
                "sick" or "wounded" or "disgruntled" or "substance_abuser" or "substance_addict" => -0.15,
                _ => 0,
            };
        }
        return fit;
    }
}
