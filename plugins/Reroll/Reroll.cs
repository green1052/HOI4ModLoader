using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Reroll;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CSetRandomSeed::Execute(command): CRandom::SetSeedHashedAndCount(seed at +0x28, count at +0x2C). The host posts it once a
    /// game starts or a save is loaded (Host::PostGameStartCommands), with the saved seed for a loaded game.
    /// </summary>
    [Signature] public const string SetSeedExecute = "48 83 EC 28 8B 51 2C 8B 49 28 E8 ? ? ? ? 48 8D 0D ? ? ? ? 48 83 C4 28 E9";
}

/// <summary>
/// A fresh random seed every time a game starts or a save is loaded, instead of the one saved with it: reloading a save no
/// longer replays the same dice (combat, events' random_list and mean time to happen, everything drawn from the synced
/// random number generator after the load). What was already decided before the save (an open event, a scheduled one)
/// stays. Single player only: every player's simulation must draw the same numbers.
/// </summary>
[Plugin("com.green1052.reroll", "Reroll", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe class Reroll : Plugin
{
    private const int SeedOfCommand = 0x28;
    private const int FlagsOfGameState = 0xC0, Ironman = 1; // the deironman console command clears this bit

    [Function(CallingConventions.Microsoft)] public delegate nint ExecuteFn(nint command);

    private IHook<ExecuteFn>? _execute;

    public override void Load()
    {
        var inIronman = Config.Bind("Reroll", "InIronman", false, "Also in Ironman games (whose point is that nothing is replayed).");
        _execute = Patches.Hook<ExecuteFn>(Sig.Find(Signatures.SetSeedExecute), command =>
        {
            Log.Guard("reroll", () =>
            {
                var state = Game.State;
                if (state == 0 || !inIronman && (*(int*)(state + FlagsOfGameState) & Ironman) != 0) return;
                *(int*)(command + SeedOfCommand) = Random.Shared.Next();
                Log.Info("new random seed");
            });
            return _execute!.OriginalFunction(command);
        });
    }
}
