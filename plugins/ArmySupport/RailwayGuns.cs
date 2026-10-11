using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace ArmySupport;

/// <summary>Byte patterns (reverse engineered against HOI4 1.19.3), checked by tests\Signatures.Tests.</summary>
public static partial class Signatures
{
    /// <summary>
    /// Railway gun distribution, per gun: `mov r13d,eax; cmp eax,r14d; setle [rsp+3Ch]` = "enough convoys to ship it",
    /// which lets a gun target provinces outside its own rail network by sea.
    /// </summary>
    [Signature] public const string ConvoysOk = "44 8B E8 41 3B C6 0F 9E 44 24 3C 48 8B 7D B8";

    /// <summary>
    /// Railway gun distribution: score of a candidate province (ctx**, long* out, int province) -> out. *ctx is the
    /// CPdxArray of 48-byte province records the distribution built: +0 id, +12 hostile units, +25 controller at war with us.
    /// </summary>
    [Signature] public const string ScoreProvince = "48 89 5C 24 ? 48 89 54 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 80 00 00 00 48 8B 19";
}

/// <summary>
/// Railway gun movement (single player, it changes the game's distribution): guns keep off the front line and stay on their
/// own rail network. Candidate provinces next to the enemy (enemy-controlled or with hostile units) get a large penalty, so
/// guns sit just behind the line instead of being overrun, and guns never ship out by sea (where submarines sink them).
/// </summary>
public unsafe partial class ArmySupport
{
    private const int RecordSize = 48, HostileUnits = 12, ControllerAtWar = 25; // province records of the distribution

    [Function(CallingConventions.Microsoft)] public delegate nint ScoreFn(nint ctx, nint score, int province);

    private IHook<ScoreFn>? _score;
    private long _frontPenalty;

    private void LoadRailwayGuns(int frontPenalty, bool noSea)
    {
        _frontPenalty = frontPenalty * Native.FixedPoint;
        if (noSea)
        {
            var setle = Sig.Find(Signatures.ConvoysOk) + 6;
            Patches.Bytes(setle, "0F 9E 44 24 3C", "C6 44 24 3C 00"); // setle [rsp+3Ch] -> mov byte [rsp+3Ch],0: never enough convoys
        }
        if (_frontPenalty > 0)
            _score = Patches.Hook<ScoreFn>(Sig.Find(Signatures.ScoreProvince), (ctx, score, province) =>
            {
                var result = _score!.OriginalFunction(ctx, score, province);
                if (IsFront(*(nint*)ctx, province)) *(long*)score -= _frontPenalty;
                return result;
            });
    }

    /// <summary>The province or a neighbor has hostile units or an enemy controller, per the distribution's own records.</summary>
    private static bool IsFront(nint records, int province)
    {
        if (Enemy(records, province)) return true;
        var p = Game.Province(province);
        return p != 0 && Game.Neighbours(p).Any(n => Enemy(records, n));
    }

    // ponytail: linear scan of the records per lookup; a dictionary per distribution run if it ever shows in a profile.
    private static bool Enemy(nint records, int province)
    {
        var data = *(nint*)records;
        for (int i = 0, n = *(int*)(records + 0xC); i < n; i++)
        {
            var r = data + i * RecordSize;
            if (*(int*)r == province) return *(byte*)(r + ControllerAtWar) != 0 || *(int*)(r + HostileUnits) > 0;
        }
        return false; // not near our divisions: not front
    }
}
