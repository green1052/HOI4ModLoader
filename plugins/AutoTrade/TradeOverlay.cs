using HOI4ModLoader;

namespace AutoTrade;

/// <summary>
/// Ctrl+Alt+C draws our trade routes (imports and exports, land and sea) as gold arrows in any map mode
/// (<see cref="MapArrows"/>). Display only. One arrow per route: an arrow is a single polyline, so shared ones would zig-zag.
/// </summary>
public unsafe partial class AutoTrade
{
    // Layouts for HOI4 1.19.3.
    private const int RoutesOfResources = 0x788;   // CCountryResources: CResourceDeliveryRoute* per partner tag index; empty slots are 0
    private const int KindOfRoute = 8, TagAOfRoute = 48, TagBOfRoute = 52; // route: kind (0, 3: no live route), the two trading tags
    private const int StatesOfRoute = 72, RegionsOfRoute = 96; // route: the states (land) / regions (sea) it passes
    private const int ProvincesOfHolder = 24;      // CState and CStrategicRegion: CPdxArray<CProvince*>
    private const long RedrawMs = 5000;

    private readonly List<nint> _arrows = [];      // one per route drawn, made as needed
    private PerGame _map = null!;
    private long _drawn;
    private bool _on, _down, _reported;

    private void LoadOverlay()
    {
        // Another game or country: hide the old arrows and forget them.
        _map = new PerGame(() =>
        {
            foreach (var arrow in _arrows) MapArrows.Hide(arrow);
            _arrows.Clear();
        });
        MainThread.Every(TimeSpan.FromMilliseconds(30), Poll);
    }

    private void Poll()
    {
        _map.Check();
        if (Gui.HotkeyPressed('C', ref _down))
        {
            _on = !_on;
            _drawn = 0; // on: draw now, not on the next refresh
            if (!_on) foreach (var arrow in _arrows) MapArrows.Hide(arrow);
        }
        if (!_on || !Game.HasStarted || Game.ControlledTag == 0) return;
        if (!MapArrows.Ready)
        {
            if (!_reported) Log.Info("trade overlay: the map's arrow manager is not caught yet; select a unit or open a map mode with arrows, then toggle again");
            _reported = true;
            return;
        }
        var now = Environment.TickCount64;
        if (now - _drawn < RedrawMs) return;
        _drawn = now;
        Draw();
    }

    /// <summary>An arrow along each of our country's trade routes: over the land states it crosses, then the sea regions.</summary>
    private void Draw()
    {
        var tag = Game.ControlledTag;
        var country = Game.Country(tag);
        var resources = country == 0 ? 0 : *(nint*)(country + ResourcesOfCountry);
        if (resources == 0) return;
        var used = 0;
        foreach (var route in Native.PdxArray(resources + RoutesOfResources))
        {
            if (route == 0 || *(byte*)(route + KindOfRoute) is 0 or 3) continue;
            if (*(int*)(route + TagAOfRoute) != tag && *(int*)(route + TagBOfRoute) != tag) continue; // only ours
            var points = new List<(int X, int Y)>();
            Waypoints(route + StatesOfRoute, points);   // over land
            Waypoints(route + RegionsOfRoute, points);  // over sea
            if (points.Count < 2) continue;
            if (used == _arrows.Count)
            {
                var made = MapArrows.Make(255, 204, 38); // gold
                if (made == 0) break;
                _arrows.Add(made);
            }
            MapArrows.Draw(_arrows[used++], points);
        }
        foreach (var spare in _arrows.Skip(used)) MapArrows.Hide(spare); // routes gone since the last draw
    }

    /// <summary>The centre of each state or strategic region the route passes through, in the route's own order.</summary>
    private static void Waypoints(nint holders, List<(int X, int Y)> points)
    {
        foreach (var holder in Native.PdxArray(holders))
        {
            if (holder == 0) continue;
            long x = 0, y = 0;
            var n = 0;
            foreach (var province in Native.PdxArray(holder + ProvincesOfHolder))
            {
                if (province == 0 || *(nint*)(province + Province.StaticOffset) == 0) continue;
                var (px, py) = MapArrows.Position(province);
                x += px;
                y += py;
                n++;
            }
            if (n > 0) points.Add(((int)(x / n), (int)(y / n)));
        }
    }
}
