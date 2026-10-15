using HOI4ModLoader;

namespace AiAssist;

public unsafe partial class AiAssist
{
    // ---- Break off losing attacks: an AI attack the attacker is losing, with no reason to bear the losses, stops ----

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
    /// Once a game hour: an AI attack the attacker is losing (under LosingProgressPercent of the battle against half again as
    /// many defenders, reserves counted, or under HopelessProgressPercent) is broken off, and the province isn't attacked again for
    /// BreakOffHours (<see cref="RuledOut"/>), unless there is a reason to bear the losses: it closes a pocket, opens the way to
    /// a pocket of ours, is a feint drawing the enemy's reserves, or another feature runs those divisions (a breakout, a
    /// pinning attack, an operation). Vanilla fought such battles to the last organisation point. Main thread.
    /// </summary>
    private void BreakOff()
    {
        var hour = Game.Hour;
        if (!Game.HasStarted || !SheepMod.Active || Game.State == 0 || hour == _breakOffHour) return;
        _breakOffHour = hour;
        _game.Check();
        foreach (var (key, at) in LostAttacks) if (hour - at is < 0 or >= BreakOffHours) LostAttacks.TryRemove(key, out _);
        var landCombat = Sig.VTable(LandCombat);
        var player = Game.ControlledTag;
        foreach (var combat in Native.PdxArray(Game.State + CombatsOfGameState).ToArray())
        {
            if (combat == 0 || *(nint*)combat != landCombat || *(nint*)(combat + ProvinceOfCombat) is not (var p and not 0)) continue;
            var defender = *(nint*)(combat + DefenderOfCombat);
            if (defender == 0) continue;
            var id = Game.Id(p);
            // Our attackers: AI divisions next to the battle, fighting, on their way into it.
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
            var defenders = *(int*)(defender + DivisionsOfSide + 0xC) + *(int*)(defender + ReservesOfSide + 0xC);
            long progress;
            ((delegate* unmanaged<nint, long*, long*>)(*(nint**)combat)[ProgressSlot])(combat, &progress); // the attacker's share
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
                    if (Jobs.Taken(unit, Id)) continue; // another plugin's job (FrontLine's breakouts, pinning attacks, operations)
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

    /// <summary>An attack on this province was lost lately: none again for BreakOffHours (see <see cref="BreakOff"/>).</summary>
    private static bool LostLately(int tag, nint target) =>
        _breakOff && LostAttacks.TryGetValue((tag, Game.Id(target)), out var at) && Game.Hour - at is >= 0 and < BreakOffHours;

    private static bool _breakOff;
}
