# HOI4ModLoader

A C# plugin loader for Hearts of Iron IV, in the spirit of BepInEx. It changes no game files: everything happens in memory while the game runs.

The bundled plugins add quality-of-life features: research, focus and political power queues, auto construction, auto trade, air wings for field marshals and more.

## Requirements
- Hearts of Iron IV **1.19.3** (Steam, Windows 64-bit)
- [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installation
1. Install the [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download the latest zip from [Releases](../../releases) and extract it.
3. Put `version.dll` and the `HOI4ModLoader` folder in the game folder (where `hoi4.exe` is).
   - Steam library → right-click Hearts of Iron IV → Manage → Browse local files
4. Start the game: the main menu's version label shows `HOI4ModLoader <version>` under the game version.

There is no mod to enable in the launcher. The few interface files the plugins change are built from your installed game each time it starts, so they keep up with game updates.

### Update
Automatic: at startup and every 6 hours the loader checks the latest GitHub release and installs a newer one in the background; it takes effect at the next start (the main menu version tooltip says when one is waiting). `[Update] CheckHours` in `HOI4ModLoader\config\HOI4ModLoader.cfg` sets the interval (0: only at startup, -1: off).

By hand: delete the old `HOI4ModLoader\plugins` folder first (plugins get merged or renamed between versions), then install as above. Your settings in `HOI4ModLoader\config` stay.

### Uninstall
Delete `version.dll` and the `HOI4ModLoader` folder from the game folder.

## Usage
- **Settings → Mods tab:** turn plugins on or off and change their settings. Hover a plugin for its description.
- **Main menu version label:** hover it for the list of loaded plugins.
- Each plugin's settings file is created in `HOI4ModLoader\config\` on first start.

## Plugins
| Plugin | What it does | Multiplayer |
|---|---|---|
| Queues | Research, focus, intelligence agency and political power queues | ✅ |
| AutoConstruction | Auto construction targets per building, auto supply | ✅ |
| AutoProduction | Moves factories from surplus equipment to equipment you are short of | ✅ |
| AutoTrade | Imports resources you are short of | ✅ |
| AirbaseCapacity | Sends only the air wings an airbase has room for, auto rebase | ✅ |
| FrontLine | Division rotation on the front line | ✅ (AI part: single player) |
| AirWingFieldMarshal | Air wings for field marshals, CAS in the whole mission area | Field marshals only (spread over armies) |
| RailwayGuns | Railway guns for field marshals, better positioning | Field marshals only (spread over armies) |
| ChecksumPatcher | Keeps achievements and ironman with mods | ✅ |
| NoTelemetry | Blocks gameplay telemetry | ✅ |
| AiAssist | Add-on for Sheep Mod: fixes AI logic its scripts can't reach | Single player only |
| PlanTools | Batch stance and battle-plan controls for all your armies at once | ✅ |
| WarStats | A war ledger: casualties, war score and equipment stockpile trends of every war, in one window | ✅ |

### Queues
**Shift+click** to queue, again to unqueue. Queued items show `N. ` in front of their name.
- **Research, national focus, intelligence agency upgrades:** the first queued item starts as soon as a slot is free.
  - Repeatable agency upgrades queue one more level per click (`1x3`).
  - Unqueueing a focus also unqueues the queued focuses that need it.
- **National spirits, laws, designers, advisors, decisions:** taken one at a time as soon as you have the political power; a full slot is replaced.
- Settings: each queue can be turned off (its Shift+click goes back to the game).

### AutoConstruction
In the construction tab, **Shift+click** a building to raise its auto construction target by 1, **Ctrl+Shift+click** by 5 (setting), **Ctrl+click** to lower it by 1. The button shows how many are left as `+N`.
- What's missing is added to the end of the construction queue, one level per state (or province) so they are built at the same time.
- Location priority: core states, then infrastructure level, then free building slots. Core and infrastructure priority are settings.
- **Auto supply:** where your divisions are low on supply, the right fix is queued: a saturated hub gets its railway to the capital upgraded (a naval base if the bottleneck is at sea), a cut-off hub a railway to the nearest connected one, and an area with no hub near a new supply hub (railway-connected provinces first). Settings: on/off, hub saturation, new hub distance.

### AutoTrade
In the trade view, **Shift+click** a resource icon to turn auto trade on or off for it (`Auto` shows). A shortage is imported automatically.
- Seller priority: your subjects, your faction, then whoever can sell the most; trade influence breaks ties.
- Settings: subject and faction priority, and a surplus to keep (imports until the balance is at least that, not just 0).
- Existing imports are never reduced.

### AirbaseCapacity
With air wings selected, **Shift+right-click** an airbase to send only the wings that fit. The rest stay where they are.
- Settings: capacity to keep free, and trimming every send without Shift.
- **Auto rebase:** a wing whose airbase only partly reaches its mission region flies to a usable airbase with room that reaches more, keeping its mission. Wings attached to an army are left to the game, which already does this.

### AirWingFieldMarshal
- **Field marshals:** attach air wings to field marshals (army groups). The marshal's card gets attach slots.
  - In multiplayer, right-clicking a marshal's card with air wings selected spreads them over the marshal's armies instead (setting).
- **CAS in the whole mission area (single player):** air wings attached to a general support every battle in their mission area, not only their own divisions' battles.

### RailwayGuns
- **Field marshal assignment:** right-click a marshal's card with railway guns selected. In single player they are assigned to the marshal; in multiplayer they are spread over the marshal's armies (setting).
- **Better positioning (single player):** guns stay just behind the front line instead of on it, and never ship out by sea. The penalty and sea transfer are settings.

### AutoProduction
Every few seconds one military factory moves from a line whose equipment has a big surplus to a line whose equipment is short (biggest shortage first), with the production screen's own command. Land and air lines only; no line is created or emptied.
- Settings: what counts as surplus (% of need, minimum units), minimum factories per line, factories moved at a time.

### FrontLine
Front line micro a player does by hand. Divisions taken off a front go back onto it once rested.
- **Rotation:** a worn-out division in contact with the enemy rests a few provinces back, where another of ours holds its province; at most a share of a front rests at once.
- **Plugging:** a defence of ours that is being lost gets the nearest fresh division (resting, unassigned, or spare on a quiet part of a front).
- **Escape:** a division in a salient whose way back narrows to one province next to the enemy pulls back before the pocket closes.
- **Opportunity attack / mop-up:** empty enemy provinces next to the line are taken, surrounded enemy divisions attacked.
- Settings: every threshold, each feature on/off, rotation and plugging for AI countries too (single player; their army management is kept off the divisions it handles), AI countries taking empty enemy provinces and AI divisions escaping closing pockets (single player, both on by default).

### ChecksumPatcher
Keeps achievements and ironman available when the checksum changes. Patched in memory only.

### NoTelemetry
Gameplay telemetry is not sent to Paradox. Login, multiplayer and crash reports are untouched.

### PlanTools
Hotkeys for all your armies and army groups at once, each posting the same command the battle-plan window's button posts: Ctrl+Alt+1/2/3 sets every stance (careful, balanced, rush), Ctrl+Alt+E activates every battle plan, Ctrl+Alt+H halts them. Each key is a setting.

### WarStats
A button under the topbar's flag opens a war ledger: every war with the casualties both sides have taken and the war score, biggest wars first, plus each produced equipment's stockpile as a 30-day trend line. Read-only, updated every second.

### AiAssist
An add-on for [Sheep Mod](https://github.com/Sheep-of-Latrobe/sheeps-mod): it fixes vanilla AI logic that the mod's scripts can't reach, with no bonuses. It does nothing without Sheep Mod, and only in single player.
- **Production:** factories move from equipment the AI has plenty of (or nothing uses) to equipment it is short of. Air and land totals stay as vanilla split them.
- **Offensive readiness:** an army doesn't start an attack while its divisions are worn out or out of supply, or while its country has too few divisions for the line it holds (advancing would widen it and get the spearhead encircled).
- **Local attacks:** pocket-closing targets first, then valuable ones (victory points, capitals, supply hubs, airfields, resources, empty or worn defences, dug-in defenders counting against a target), then ones that shorten the front — all on the day's breakthrough axis, the front's thinnest enemy sector, so the pushes don't zig-zag; armor avoids terrain and rivers that cripple it; line infantry holds while breakthrough divisions push.
- **Stationing:** a division the theatre shuffle just walked to a front stays there a week; vanilla shuffles freshly arrived divisions back and forth.
- **Hold the line:** at war, every province on the line keeps a defender; vanilla can ship a whole fighting front away (supply crisis, another front). The AI's regrouping can't take a province's last defender or the divisions walling in a pocket (it would staff the new front from far away and let the pocket break out meanwhile), free divisions walk back into empty line provinces, and a country short of divisions for its line doesn't start attacks.
- **Defence priority:** holes are refilled where holding counts first — ports, victory points, river banks, cities and mountains — and key ground is worth walking twice as far for.
- **Reserves:** a country with divisions to spare keeps reserve posts a province behind its line, on the ground covering the most front; the theatre shuffle leaves them there.
- **Beach defence:** a naval landing is answered at once: free divisions next to it wall the beachhead in and attack into it, instead of waiting for the front to collapse.
- **Concentration:** fronts with no enemy at war keep a thin screen, the rest goes where the enemy is strong; borders of a country in a warring country's faction keep their garrison, and fronts at war are never thinned.
- **Air:** planes leave regions where the AI keeps losing the air war badly, and airbases the enemy is about to overrun instead of waiting for the base to fall; close air support backs the day's breakthrough axis first — battles on the axis get the planes before ones elsewhere in the region.
- **Navy:** task forces pick their fights by the strength they see in their region (outnumbered 2:1 they slip away, clearly stronger they press, pairs of ships only shadow the enemy), and convoy defence gets the naval fuel budget while convoys are being sunk fast.
- **Leaders:** the biggest, fighting, attacking fronts choose their field marshals and generals first; infantry traits, doctrine and bad status count.

## Multiplayer
- Other players see vanilla. Only interface files change, never the checksum, so players without the loader can join the same game.
- Plugins like the queues and auto construction send the same commands a player's clicks would, so they work as usual.
- Features that change game rules (attached CAS, railway gun positioning, field marshals holding air wings and railway guns) switch off in a multiplayer lobby and back on in single player. Field marshal assignment switches to spreading over the marshal's armies with vanilla commands instead.

## Performance: mimalloc
The game's memory allocations use Microsoft's [mimalloc](https://github.com/microsoft/mimalloc) instead of the Windows heap. If you suspect crashes or memory problems, set `[Performance] Mimalloc = False` in `HOI4ModLoader\config\HOI4ModLoader.cfg` (takes effect at the next start).

## Troubleshooting
- **".NET 10 Runtime (x64) not found" message:** install the .NET 10 Runtime (x64). The game still starts, without mods.
- **A plugin does nothing:** check `HOI4ModLoader\LogOutput.log`. Plugins don't load on game versions other than 1.19.3.
- **UI elements missing or broken:** other mods that replace the same interface files (settings window, main menu, construction tab, intelligence agency, army group card) can conflict; the loader's versions win.
- **Turning a plugin off:** use the Mods tab, or set `Enabled = False` in `config\<plugin id>.cfg`.

## Development
See [docs/development.md](docs/development.md) for building and writing plugins.

## License
HOI4ModLoader is free software under the [GNU Lesser General Public License v3.0](LICENSE). The components it ships with keep their own licenses: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer
HOI4ModLoader is an unofficial fan project. It is not affiliated with, endorsed by or sponsored by Paradox Interactive AB. Hearts of Iron and Paradox Interactive are trademarks of Paradox Interactive AB; they are used here only to say which game this works with.

The loader changes the running game in memory. Use it at your own risk and keep backups of your saves; it comes without any warranty. Keeping achievements with mods (ChecksumPatcher) may go against Paradox's terms: whether to use it is your call.
