using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Refit priority: the daily stockpile distribution fills front-line AI divisions before rear and garrison ones ----

    private const int ArmyOfRequests = 0x20; // CArmyReinforcementRequests / CArmyUpgradesRequests -> CArmy
    private const int FrontFactor = 4;       // a front division comes first until it is 75 % full

    [Function(CallingConventions.Microsoft)] public delegate nint ScoreFn(nint requests, nint score, nint archetype);

    private IHook<ScoreFn>? _reinforceScore, _upgradeScore;
    // CArmy -> (day, front): the score runs per archetype in stock, the front test once per division per day. Never dereferenced.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int Day, bool Front)> FrontCache = new();
    private static int _frontDay = -1;
    private static int _refitted;

    private void LoadRefit()
    {
        _reinforceScore = Patches.Hook<ScoreFn>(Sig.Find(Signatures.ReinforceScore), (requests, score, archetype) =>
        {
            var result = _reinforceScore!.OriginalFunction(requests, score, archetype);
            Boost(requests, score);
            return result;
        });
        _upgradeScore = Patches.Hook<ScoreFn>(Sig.Find(Signatures.UpgradeScore), (requests, score, archetype) =>
        {
            var result = _upgradeScore!.OriginalFunction(requests, score, archetype);
            Boost(requests, score);
            return result;
        });
    }

    /// <summary>Raises a positive score of an AI front-line division. Read only on game state; no closure (runs very often).</summary>
    private static void Boost(nint requests, nint score)
    {
        try
        {
            if (*(long*)score <= 0) return;
            var army = *(nint*)(requests + ArmyOfRequests);
            if (army == 0) return;
            var tag = *(int*)(army + CommanderOfUnit);
            if (tag <= 0 || tag == Game.ControlledTag || !SheepMod.Active || !FrontLine(army, tag)) return;
            *(long*)score *= FrontFactor; // keeps the (1 - fill) factor: a nearly full front division still yields to empty ones
        }
        catch (Exception e) { Failed(e); }
    }

    /// <summary>The division is fighting, or stands next to ground held by a country its own is at war with (cached for the day).</summary>
    private static bool FrontLine(nint army, int tag)
    {
        var day = Hour / 24;
        if (day != _frontDay) { FrontCache.Clear(); _frontDay = day; }
        if (FrontCache.TryGetValue(army, out var cached)) return cached.Front;
        var front = *(int*)(army + CombatsOfUnit) > 0;
        var location = *(nint*)(army + LocationOfUnit);
        var country = Game.Country(tag);
        if (!front && location != 0 && country != 0)
            foreach (var id in Game.Neighbours(location))
            {
                var p = Game.Province(id);
                if (p != 0 && Enemy(country, tag, p)) { front = true; break; }
            }
        FrontCache[army] = (day, front);
        if (front) Interlocked.Increment(ref _refitted);
        return front;
    }
}
