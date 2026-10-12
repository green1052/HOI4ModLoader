using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Performance;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CCountryDecisions hourly build (decisions, bool force): on the country's slot hour of each day (vanilla's gate) it looks
    /// for new targets of every targeted decision, checking each state and country against it. Called for every country every
    /// hour on the AI threads, with force only on load and on the game's own refreshes.
    /// </summary>
    [Signature] public const string DecisionTargets = "40 53 48 83 EC 20 48 8B D9 E8 72 1E 00 00 84 C0 74 0D 48 8B CB 48 83 C4 20 5B E9";

    /// <summary>
    /// Idler render (idler): map, interface and Present, called once per frame near the end of Idle, after the game's tick.
    /// Shared by seven idlers' vtables; the in-game one is <see cref="InGameIdler"/>.
    /// </summary>
    [Signature] public const string Render = "48 8B C4 56 48 83 EC 70 0F B6 15 ? ? ? ? 48 8B F1 0F 29 70 E8 48 89 58 08 48 89 68 10 48 89 78 18 4C 89 70 20 4C 8B B1 F0 04 00 00";

    [VTable] public const string InGameIdler = "CInGameIdler";
}

/// <summary>
/// Game speed: lighter recurring work where nothing is lost but a short delay. Targeted decisions look for new targets every
/// few days instead of daily (checking every state against every such decision, for every country, was the biggest
/// single cost of a game day); targets that became invalid still drop out daily, and timers, missions and forced refreshes
/// are untouched. At speeds 4 and 5 the screen is drawn at most every few tens of milliseconds: at speed 5 each frame
/// advances an hour, so a frame not drawn is game time gained (input still runs every frame).
/// </summary>
[Plugin("com.green1052.performance", "Performance", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe class Performance : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate void DecisionTargetsFn(nint decisions, byte force);

    [Function(CallingConventions.Microsoft)] public delegate void RenderFn(nint idler);

    private const int SpeedOfGameState = 0x4BC, PausedOfGameState = 0x4C0, FastSpeed = 3; // speed index 0-4: 3 = speed 4

    private IHook<DecisionTargetsFn>? _decisionTargets;
    private IHook<RenderFn>? _render;
    private long _drawn; // when the screen was last drawn (ms)

    public override void Load()
    {
        var days = Config.Bind("Performance", "DecisionTargetDays", 3,
            "Targeted decisions look for new targets every this many days (1: daily, as vanilla). A newly valid target shows up that much later.");
        var frameMs = Config.Bind("Performance", "FastSpeedFrameMs", 0,
            "At speeds 4 and 5, draw the screen at most every this many milliseconds (0: every frame, as vanilla). Game time runs faster; the map and numbers update less smoothly.");
        if (frameMs > 0)
        {
            var inGame = Sig.VTable(Signatures.InGameIdler);
            _render = Patches.Hook<RenderFn>(Sig.Find(Signatures.Render), idler =>
            {
                var now = Environment.TickCount64;
                if (*(nint*)idler == inGame && Game.State is var gs && gs != 0 && *(byte*)(gs + PausedOfGameState) == 0
                    && *(int*)(gs + SpeedOfGameState) >= FastSpeed && now - _drawn < frameMs) return;
                _drawn = now;
                _render!.OriginalFunction(idler);
            });
        }
        if (days <= 1) return;
        _decisionTargets = Patches.Hook<DecisionTargetsFn>(Sig.Find(Signatures.DecisionTargets), (decisions, force) =>
        {
            // The day, not the hour: vanilla's gate inside still picks the country's hour of that day.
            if (force != 0 || Game.Hour / 24 % days == 0) _decisionTargets!.OriginalFunction(decisions, force);
        });
    }
}
