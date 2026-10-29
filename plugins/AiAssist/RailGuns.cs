using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Railway guns: the armies on a breakthrough axis get them first and keep them ----
    //
    // The AI general shares out railway guns hourly (0x14107E410): army score = UNITCOUNT_MULTIPLIER x divisions, largest
    // first, each up to divisions / (divisions per gun) in [1, RAILWAY_GUN_PER_ARMY_CAP]; a gun moves from B to A only when
    // SCORE_HOLD + score(B) <= score(A). A patch in its score loop adds a bonus to the score and division count (sort key) of
    // armies in our table. The stub calls nothing.

    private const int GunArmySlots = 64;
    private const long GunScoreBonus = 1000 * Native.FixedPoint; // fixed point, as the defines (10 per division, 20 to hold)
    private const int GunOrderBonus = 10_000;                    // divisions, for the sort only (quotas read the real count)

    private static bool _railGuns;
    private static int _gunned;
    private static int _gunHour = -1; // game hour the table was last rebuilt
    private static long* _gunTable; // [0] count, [1..count] COrdersGroup* of axis armies: compared, never dereferenced
    private static readonly object GunLock = new();
    // general -> the armies of its fronts on its axis (this world). Rebuilt into the table on every change.
    private static readonly ConcurrentDictionary<nint, (int Hour, int World, nint[] Armies)> GunArmies = new();

    private void LoadRailGuns()
    {
        var loop = Sig.Find(AiSignatures2.RailwayGunScore);
        _gunTable = (long*)NativeMemory.AllocZeroed(8 * (GunArmySlots + 1)); // never freed: the stub may run any time
        // Replaces `mov [r9+r8*8],rdx; mov rax,[r13+60h]; mov [rbp+1150h],rax` (15 bytes) with the same plus the bonus for an
        // army in the table. rcx, rdx, r11 and the flags are dead there.
        Patches.Asm(loop + 0x5C, 15,
            "use64",
            "mov [r9+r8*8], rdx",
            "mov rax, [r13+60h]",
            "mov rcx, [rax+r14*8]",
            $"mov rdx, 0x{(nint)_gunTable:X}",
            "mov r11, [rdx]",
            "test r11, r11",
            "jz done",
            "next:",
            "cmp rcx, [rdx+r11*8]",
            "je hit",
            "dec r11",
            "jnz next",
            "jmp done",
            "hit:",
            $"mov rdx, {GunScoreBonus}",
            "add [r9+r8*8], rdx",
            "mov rdx, [rbp-30h]",
            $"add dword [rdx+r8*4], {GunOrderBonus}",
            "done:",
            "mov rax, [r13+60h]",
            "mov [rbp+1150h], rax");
        _railGuns = true;
    }

    /// <summary>The armies of the theatre's fronts whose threatened line touches this hour's axis.</summary>
    private static nint[] AxisFrontArmies(nint general, List<(nint Front, HashSet<nint> Threatened)>? atWar, int hour)
    {
        if (atWar == null || !Axes.TryGetValue(general, out var axis) || axis.Hour != hour) return [];
        List<nint>? armies = null;
        foreach (var (front, threatened) in atWar)
        {
            var onAxis = false;
            foreach (var p in threatened) onAxis |= axis.Core.Contains(Game.Id(p));
            if (!onAxis) continue;
            foreach (var army in Native.PdxArray(front + ArmiesOfAiFront))
                if (army != 0) (armies ??= []).Add(army);
        }
        return armies?.ToArray() ?? [];
    }

    /// <summary>
    /// Records the general's axis armies; rewrites the table when they changed or an hour passed, dropping generals gone quiet.
    /// Any AI thread; a concurrent read by the stub gives one wrong bonus at worst.
    /// </summary>
    private static void PublishGunArmies(nint general, nint[] armies, int hour)
    {
        var world = Game.Generation;
        var same = GunArmies.TryGetValue(general, out var old) ? old.World == world && old.Armies.AsSpan().SequenceEqual(armies) : armies.Length == 0;
        if (armies.Length > 0) GunArmies[general] = (hour, world, armies);
        else GunArmies.TryRemove(general, out _);
        if (same && Volatile.Read(ref _gunHour) == hour) return;
        lock (GunLock)
        {
            var n = 0;
            foreach (var (g, e) in GunArmies)
            {
                if (e.World != world || hour - e.Hour is < 0 or >= LineFreshHours) GunArmies.TryRemove(g, out _); // a general gone quiet
                else foreach (var army in e.Armies) if (n < GunArmySlots) _gunTable[1 + n++] = army;
            }
            Volatile.Write(ref _gunTable[0], n);
            _gunHour = hour;
        }
        if (!same && armies.Length > 0) Interlocked.Increment(ref _gunned);
    }

    /// <summary>Per-game reset: clears every army's bonus.</summary>
    private static void ClearRailGuns()
    {
        GunArmies.Clear();
        _gunHour = -1;
        if (_gunTable != null) Volatile.Write(ref _gunTable[0], 0);
    }
}
