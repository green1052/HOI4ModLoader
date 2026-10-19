using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace Performance;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static class Signatures
{
    /// <summary>
    /// CCountryDecisions hourly build (decisions, bool force): once a day, on the country's slot hour, checks every state and
    /// country as a new target of every targeted decision. Called hourly for every country on the AI threads; force only on
    /// load and the game's own refreshes.
    /// </summary>
    [Signature] public const string DecisionTargets = "40 53 48 83 EC 20 48 8B D9 E8 72 1E 00 00 84 C0 74 0D 48 8B CB 48 83 C4 20 5B E9";

    /// <summary>
    /// Idler render (idler): draws map and interface and presents, once per frame after the game's tick.
    /// Shared by seven idlers' vtables; the in-game one is <see cref="InGameIdler"/>.
    /// </summary>
    [Signature] public const string Render = "48 8B C4 56 48 83 EC 70 0F B6 15 ? ? ? ? 48 8B F1 0F 29 70 E8 48 89 58 08 48 89 68 10 48 89 78 18 4C 89 70 20 4C 8B B1 F0 04 00 00";

    /// <summary>
    /// CGraphicalMap per-frame update (map, view, float dt, idler, bool layers): border LOD, map layers, province render state
    /// (a parallel pass), the province under the mouse, map entities. Nothing that later frames can't catch up on; dt drives
    /// border animations.
    /// </summary>
    [Signature] public const string MapUpdate = "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 48 89 54 24 ? 57 41 54 41 55 41 56 41 57 48 81 EC 80 00 00 00 0F 29 74 24";

    /// <summary>Map icons and labels per-frame rebuild (icons, view, float clock).</summary>
    [Signature] public const string MapIcons = "48 89 5C 24 ? 55 56 57 41 54 41 55 41 56 41 57 B8 70 10 00 00";

    [VTable] public const string InGameIdler = "CInGameIdler";
}

/// <summary>
/// Game speed. At speed 5 a frame is a game hour, so time cut from a frame is game time gained.
/// - Targeted decisions look for new targets every few days instead of daily, the biggest single cost of a game day.
///   Invalid targets still drop out daily; timers, missions and forced refreshes are untouched.
/// - At speeds 4 and 5 the map (province colours, units, icons) updates every few frames.
/// - Optionally (FastSpeedFrameMs, off by default) the screen is drawn less often at speeds 4 and 5. Input still runs every
///   frame.
/// </summary>
[Plugin("com.green1052.performance", "Performance", "1.1.0", GameVersion = "1.19.3", AffectsGameState = true)]
public unsafe class Performance : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate void DecisionTargetsFn(nint decisions, byte force);

    [Function(CallingConventions.Microsoft)] public delegate void RenderFn(nint idler);

    [Function(CallingConventions.Microsoft)] public delegate void MapUpdateFn(nint map, nint view, float dt, nint idler, byte layers);

    [Function(CallingConventions.Microsoft)] public delegate void MapIconsFn(nint icons, nint view, float clock);

    private const int SpeedOfGameState = 0x4BC, FastSpeed = 3; // speed index 0-4: 3 = speed 4
    private const int PausedOfIdler = 0x6C1, HeldOfIdler = 0x6C4;  // bytes: paused; a second hold of the clock

    private IHook<DecisionTargetsFn>? _decisionTargets;
    private IHook<RenderFn>? _render;
    private IHook<MapUpdateFn>? _mapUpdate;
    private IHook<MapIconsFn>? _mapIcons;
    private nint _inGame;
    private long _drawn;     // when the screen was last drawn (ms)
    private int _mapFrame;   // frames at a fast speed, for the map's turn
    private bool _mapSkipped; // this frame's map update was skipped: so are its icons
    private float _mapDt;    // time of the skipped map updates, handed to the next one
    private byte _mapLayers;

    public override void Load()
    {
        var days = Config.Bind("Performance", "DecisionTargetDays", 3,
            "Targeted decisions look for new targets every this many days (1: daily, as vanilla). A newly valid target shows up that much later.");
        var mapEvery = Config.Bind("Performance", "MapEveryFrames", 3,
            "At speeds 4 and 5, the map (province colours, units, icons) updates every this many frames (1: every frame, as vanilla).");
        var frameMs = Config.Bind("Performance", "FastSpeedFrameMs", 0,
            "At speeds 4 and 5, draw the screen at most every this many milliseconds (0: every frame, as vanilla). No effect with the -render_thread launch option, which already draws beside the game.");
        _inGame = Sig.VTable(Signatures.InGameIdler);
        if (frameMs > 0)
            _render = Patches.Hook<RenderFn>(Sig.Find(Signatures.Render), idler =>
            {
                var now = Environment.TickCount64;
                if (Fast(idler) && now - _drawn < frameMs) return;
                _drawn = now;
                _render!.OriginalFunction(idler);
            });
        if (mapEvery > 1)
        {
            _mapUpdate = Patches.Hook<MapUpdateFn>(Sig.Find(Signatures.MapUpdate), (map, view, dt, idler, layers) =>
            {
                _mapSkipped = Fast(idler) && ++_mapFrame % mapEvery != 0;
                if (_mapSkipped)
                {
                    _mapDt += dt;
                    _mapLayers |= layers;
                    return;
                }
                _mapUpdate!.OriginalFunction(map, view, dt + _mapDt, idler, (byte)(layers | _mapLayers));
                (_mapDt, _mapLayers) = (0, 0);
            });
            _mapIcons = Patches.Hook<MapIconsFn>(Sig.Find(Signatures.MapIcons), (icons, view, clock) =>
            {
                if (!_mapSkipped) _mapIcons!.OriginalFunction(icons, view, clock);
            });
        }
        if (days <= 1) return;
        _decisionTargets = Patches.Hook<DecisionTargetsFn>(Sig.Find(Signatures.DecisionTargets), (decisions, force) =>
        {
            // The day, not the hour: vanilla's gate inside still picks the country's hour of that day.
            if (force != 0 || Game.Hour / 24 % days == 0) _decisionTargets!.OriginalFunction(decisions, force);
        });
    }

    /// <summary>The in-game screen at speed 4 or 5 with the clock running.</summary>
    private bool Fast(nint idler) => *(nint*)idler == _inGame && *(byte*)(idler + PausedOfIdler) == 0 && *(byte*)(idler + HeldOfIdler) == 0
                                     && Game.State is var gs && gs != 0 && *(int*)(gs + SpeedOfGameState) >= FastSpeed;
}
