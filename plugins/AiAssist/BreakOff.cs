using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Break off losing attacks: an AI attack that is losing, with nothing to gain, stops ----

    private const int BreakOffHours = 72, CancelSize = 0x48;
    private static delegate* unmanaged<nint, nint, byte, nint> _cancelMovement;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Tag, int Province), int> LostAttacks = new(); // -> hour broken off
    private static int _brokenOff, _breakOffHour = -1;

    private void LoadBreakOff()
    {
        var cancel = Sig.Find(Signatures.CancelMovement) + 28;
        if (*(byte*)cancel != 0xE8) throw new InvalidOperationException("break off: no call to the cancel movement constructor");
        _cancelMovement = (delegate* unmanaged<nint, nint, byte, nint>)(cancel + 5 + *(int*)(cancel + 1));
        MainThread.Every(TimeSpan.FromSeconds(1), BreakOff);
    }

    /// <summary>
    /// Once a game hour: breaks off AI attacks that are losing (under LosingProgressPercent against OutnumberedPercent defenders,
    /// reserves counted, or under HopelessProgressPercent). The province is then not attacked for BreakOffHours
    /// (<see cref="RuledOut"/>). Spared: attacks that close a pocket, reach a pocket of ours or are a feint, and divisions
    /// another plugin runs. Main thread.
    /// </summary>
    private void BreakOff()
    {
        var hour = Game.Hour;
        if (!Game.HasStarted || !SheepMod.Active || Game.State == 0 || hour == _breakOffHour) return;
        _breakOffHour = hour;
        _game.Check();
        foreach (var (key, at) in LostAttacks) if (hour - at is < 0 or >= BreakOffHours) LostAttacks.TryRemove(key, out _);
        var player = Game.ControlledTag;
        foreach (var combat in LandCombat.All())
        {
            if (LandCombat.Location(combat) is not (var p and not 0)) continue;
            var defender = LandCombat.Defender(combat);
            if (defender == 0) continue;
            var id = Game.Id(p);
            // Attackers: fighting AI divisions next to the battle, headed into it.
            var attackers = new Dictionary<int, List<nint>>();
            foreach (var n in Game.Neighbours(p))
                if (Game.Province(n) is var q && q != 0)
                    foreach (var unit in Province.Units(q))
                    {
                        if (unit == 0 || !Division.IsLand(unit) || !Division.Fighting(unit)) continue;
                        var tag = Division.Commander(unit);
                        if (tag <= 0 || tag == player) continue;
                        var path = Native.PdxArray<int>(unit + Division.PathOffset);
                        if (path.Length == 0 || path[^1] != id) continue;
                        if (!attackers.TryGetValue(tag, out var list)) attackers[tag] = list = [];
                        list.Add(unit);
                    }
            if (attackers.Count == 0) continue;
            var defenders = LandCombat.Divisions(defender).Length + LandCombat.Reserves(defender);
            var progress = LandCombat.AttackerShare(combat);
            var count = attackers.Values.Sum(l => l.Count);
            var losing = progress * 100 < HopelessProgressPercent * Native.FixedPoint
                         || progress * 100 < LosingProgressPercent * Native.FixedPoint && defenders * 100 >= OutnumberedPercent * count;
            if (!losing) continue;
            foreach (var (tag, units) in attackers)
            {
                var country = Game.Country(tag);
                if (country == 0 || !Province.Enemy(country, tag, p) || Reason(country, tag, p, hour)) continue;
                var stopped = 0;
                foreach (var unit in units)
                {
                    if (Jobs.Taken(unit, Id)) continue; // another plugin's job
                    var cmd = Allocator.AllocZeroed(CancelSize);
                    _cancelMovement(cmd, unit, 1);
                    if (Commands.TryPost(cmd, true)) stopped++;
                }
                if (stopped == 0) continue;
                LostAttacks[(tag, id)] = hour;
                Interlocked.Add(ref _brokenOff, stopped);
            }
        }
    }

    /// <summary>Why a losing attack on <paramref name="target"/> is worth its losses: a pocket to close, a way to our own pocket, a feint.</summary>
    private static bool Reason(nint country, int tag, nint target, int hour)
    {
        if (Province.Pocket(country, tag, target) > 0 || Relief(country, tag, target) > 0) return true;
        var id = Game.Id(target);
        foreach (var (_, feint) in Feints)
            if (feint.Tag == tag && hour < feint.Until && (feint.Anchor == id || feint.Core.Contains(id))) return true;
        return false;
    }

    /// <summary>True if an attack on this province was broken off in the last BreakOffHours.</summary>
    private static bool LostLately(int tag, nint target) =>
        _breakOff && LostAttacks.TryGetValue((tag, Game.Id(target)), out var at) && Game.Hour - at is >= 0 and < BreakOffHours;

    private static bool _breakOff;
}
