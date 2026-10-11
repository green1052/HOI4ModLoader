using HOI4ModLoader;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Navy: convoys under attack get the fuel budget; task forces pick their fights by the strength they see ----

    private const int HandlesOfNavy = 0x50;                                            // naval AI: task force handles, 32 bytes each (+0xC count)
    private const int StationOfForce = 0x1F0, RegionOfStation = 0xC8;                  // CTaskForce -> station -> CNavalRegion
    private const int OwnerOfForce = 0x1D8, MissionOfForce = 0x374, AggressionOfForce = 0x394;
    private const int ShipsOfForce = 0x348, SpottedOfRegion = 0xF8, StrengthOfShip = 0x6F8; // CPdxArrays; CShip strength (1e5)
    private const int ConvoyThreats = 0x698, CountriesOfThreats = 8, TagOfThreats = 0x10, ThreatsOfCountry = 0x80; // gamestate -> per country
    private const int ConvoyDefenseFuel = 0x337538, ThreatPerSunkConvoy = 0x337F70;    // NAI defines, from the image base
    private const int Patrol = 1, Strike = 2, Raiding = 3;                             // EMissionType

    [Function(CallingConventions.Microsoft)] public delegate void NavyFn(nint navalAI);
    [Function(CallingConventions.Microsoft)] public delegate nint EngagementFn(nint cmd, nint force, int level);

    private static bool _navalSortie, _navalEscort;
    private IHook<NavyFn>? _navyAllocator;
    private IHook<EngagementFn>? _engagement;
    private nint _convoyRatio, _threatPerConvoy;
    private long _convoyBaseline;
    private int _convoyBumps, _sortied, _escorted;

    private void LoadNavy()
    {
        var image = Sig.Find(Signatures.NavalAllocator) - 0x1A65AB0; // the allocator's own address gives the image base
        _convoyRatio = image + ConvoyDefenseFuel;
        _threatPerConvoy = image + ThreatPerSunkConvoy;
        _navyAllocator = Patches.Hook<NavyFn>(Sig.Find(Signatures.NavalAllocator), navalAI =>
        {
            var boosted = false;
            Safely(() => boosted = SheepMod.Active && _navalEscort && ConvoyPressure(navalAI));
            if (boosted && Interlocked.Increment(ref _convoyBumps) == 1)
                *(long*)_convoyRatio = ConvoyBaseline() * 2; // another country's worker may read the doubled ratio: once, too many escorts, never too few
            try { _navyAllocator!.OriginalFunction(navalAI); }
            finally
            {
                if (boosted && Interlocked.Decrement(ref _convoyBumps) == 0 && _convoyBaseline != 0)
                    *(long*)_convoyRatio = _convoyBaseline;
            }
        });
        _engagement = Patches.Hook<EngagementFn>(Sig.Find(Signatures.NavyEngagement), (cmd, force, level) =>
        {
            Safely(() =>
            {
                if (SheepMod.Active && _navalSortie) level = Engagement(force, level);
            });
            return _engagement!.OriginalFunction(cmd, force, level);
        });
    }

    /// <summary>The NAI define as it was loaded, captured on the first bump; every restore writes exactly this back.</summary>
    private long ConvoyBaseline()
    {
        var baseline = _convoyBaseline;
        if (baseline == 0) _convoyBaseline = baseline = *(long*)_convoyRatio;
        return baseline;
    }

    /// <summary>Whether the country is losing convoys fast: its worst naval region's threat, against the escort proposer's own gates. Read only: AI threads.</summary>
    private bool ConvoyPressure(nint navalAI)
    {
        var tag = NavyTag(navalAI);
        if (tag == 0 || tag == Game.ControlledTag) return false;
        var container = *(nint*)(Game.State + ConvoyThreats);
        var countries = container == 0 ? 0 : *(nint*)(container + CountriesOfThreats);
        if (countries == 0) return false;
        var data = *(nint*)(countries + 8 * Game.TagIndex(tag));
        if (data == 0 || *(int*)(data + TagOfThreats) != tag) return false;
        var threats = Native.PdxArray<int>(data + ThreatsOfCountry);
        var worst = 0;
        foreach (var threat in threats) worst = Math.Max(worst, threat);
        // The proposer only runs above 50, and calls a region under 4 sunks' worth of threat fine: below either, more escorts change nothing.
        if (worst <= 50 || worst < 4L * *(int*)_threatPerConvoy) return false;
        Interlocked.Increment(ref _escorted);
        return true;
    }

    /// <summary>The country whose naval AI this is, read off its first resolvable task force handle.</summary>
    private int NavyTag(nint navalAI)
    {
        var handles = *(nint*)(navalAI + HandlesOfNavy);
        for (int i = 0, n = *(int*)(navalAI + HandlesOfNavy + 0xC); i < n; i++)
        {
            var handle = handles + 32 * i;
            if (*(int*)handle == 0 && *(int*)(handle + 4) == 0) continue;
            var resolved = _resolve(handle);
            if (resolved == 0 || resolved == 16) continue;
            var tag = *(int*)(resolved - 16 + OwnerOfForce);
            if (tag is >= 0x414141 and <= 0x5A5A5A) return tag; // three letters, like "GER"
        }
        return 0;
    }

    /// <summary>
    /// The engagement level (0 never .. 4 always) a task force should have, by what it and the enemy it can see in its
    /// region are worth: twice the enemy it presses, half it slips away, a pair of ships only shadows. Read only: AI threads.
    /// </summary>
    private int Engagement(nint force, int level)
    {
        var tag = *(int*)(force + OwnerOfForce);
        if (tag == 0 || tag == Game.ControlledTag) return level; // the player's own button
        if (*(int*)(force + MissionOfForce) is not (Patrol or Strike or Raiding)) return level;
        var ships = Native.PdxArray<nint>(force + ShipsOfForce);
        if (ships.Length <= 2) return 0; // a pair on patrol or raiding shadows the enemy, it doesn't fight it
        long ours = 0;
        foreach (var ship in ships) ours += *(long*)(ship + StrengthOfShip);
        var station = *(nint*)(force + StationOfForce);
        var region = station == 0 ? 0 : *(nint*)(station + RegionOfStation);
        if (region == 0 || ours <= 0) return level;
        var me = Game.Country(tag);
        if (me == 0) return level;
        long theirs = 0;
        var pair = stackalloc int[2];
        foreach (var other in Native.PdxArray<nint>(region + SpottedOfRegion))
        {
            var owner = *(int*)(other + OwnerOfForce);
            if (owner == 0 || owner == tag) continue;
            pair[0] = owner;
            pair[1] = Game.TagIndex(owner);
            if (_atWar(me, pair) == 0) continue;
            foreach (var ship in Native.PdxArray<nint>(other + ShipsOfForce)) theirs += *(long*)(ship + StrengthOfShip);
        }
        var vanilla = level;
        if (theirs > 0 && ours >= theirs + theirs / 5) level = 4; // 1.2x over what we see: press it
        else if (ours * 2 <= theirs) level = 1;                  // half of it: slip away
        if (level != vanilla) Interlocked.Increment(ref _sortied);
        return level;
    }
}
