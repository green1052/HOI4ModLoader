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
| AutoConstruction | Auto construction targets per building, auto supply, auto production | ✅ |
| AutoTrade | Imports resources you are short of, cuts imports you no longer need, draws your trade routes | ✅ |
| FrontLine | Front line micro (rotation, plugging, escape, attacks) and battle plan hotkeys | ✅ |
| ArmySupport | Air wings and railway guns for field marshals, railway gun positioning, airbase capacity, auto rebase | ✅ (field marshals: spread over armies; railway gun positioning: single player) |
| ChecksumPatcher | Keeps achievements with mods | ✅ |
| AiAssist | Add-on for Sheep Mod: fixes AI logic its scripts can't reach | Single player only |

### Queues
**Shift+click** to queue, again to unqueue. Queued items show `N. ` in front of their name.
- **Research, national focus, intelligence agency upgrades:** the first queued item starts as soon as a slot is free.
  - Repeatable agency upgrades queue one more level per click (`1x3`).
  - Unqueueing a focus also unqueues the queued focuses that need it.
  - Items that can no longer start are dropped: researched techs, focuses already taken or shut out by a mutually exclusive focus, agency levels already done.
- **National spirits, laws, designers, advisors, decisions:** taken one at a time as soon as you have the political power; a full slot is replaced.
- Settings: the political power queue on/off.

### AutoConstruction
In the construction tab, **Shift+click** a building to raise its auto construction target by 1, **Ctrl+Shift+click** by 5, **Ctrl+click** to lower it by 1. The button shows how many are left as `+N`. Targets are kept per country for the whole session, across loads.
- What's missing is added to the end of the construction queue, one level per state (or province) so they are built at the same time.
- Location priority: core states, then higher infrastructure (lower infrastructure first when the building is infrastructure itself), then free building slots.
- **Auto supply:** where your divisions are low on supply, the right fix is queued: a saturated hub gets its railway to the capital upgraded (a naval base if the bottleneck is at sea), a cut-off hub a railway to the nearest connected one, and an area with no hub near a new supply hub (railway-connected provinces first). Setting: on/off.
- **Auto production:** military factories move one at a time from a line whose equipment has a big surplus to a line whose equipment is short (biggest shortage first), with the production screen's own command, at most a few per game day (nothing moves while paused). Land and air lines only; no line is created or emptied. Settings: on/off, what counts as surplus (% of need), cover days, factories moved per game day.

### AutoTrade
In the trade view, **Shift+click** a resource icon to turn auto trade on or off for it (`Auto` shows; kept per country for the session). A shortage is imported automatically, and an import that is no longer needed is cut back by whole steps (cancelled at 0), so the balance stays at the surplus you set.
- Seller priority: your subjects, your faction, then whoever can sell the most; trade influence breaks ties. Cuts go the other way round.
- Settings: allies first, and a surplus to keep (imports until the balance is at least that, not just 0).
- **Trade routes:** **Ctrl+Alt+C** draws your country's trade routes in gold, in any map mode: an arrow over the states a land route crosses and the strategic regions a sea route sails, for your imports and your exports alike.

### FrontLine
Front line micro a player does by hand, with the battle plan UI's own commands (multiplayer works). Divisions taken off a front go back onto it once rested.
- **Rotation:** a worn-out division in contact with the enemy rests a few provinces back, where another of ours holds its province; at most a share of a front rests at once. A division short of equipment or manpower goes back after two weeks once its organisation has recovered.
- **Plugging:** a defence of ours that is being lost gets the nearest fresh division (resting, unassigned, or spare on a quiet part of a front).
- **Escape:** a division in a salient whose way back narrows to one province next to the enemy pulls back before the pocket closes.
- **Attack:** empty enemy provinces next to the line are taken by a fresh division; surrounded enemy divisions are attacked only by enough fresh divisions to outnumber them, forts counted, and no more are sent while the first ones are still on their way or fighting.
- **Battle plan keys:** Ctrl+Alt+E activates every battle plan of all your armies and army groups at once, Ctrl+Alt+H halts them (the same command the battle plan window's button posts).
- Settings: every threshold, each feature on/off.

### ArmySupport
- **Field marshals:** right-click a marshal's card with air wings or railway guns selected. In single player they are attached to the marshal (army group): his card gets air wing slots, his air wings support his armies' battles, and his railway guns take positions along all of his armies' divisions. In multiplayer they are spread over the marshal's armies instead. Setting: on/off.
- **Railway gun positioning (single player):** guns stay just behind the front line instead of on it, and never ship out by sea. The penalty and sea transfer are settings.
- **Airbase capacity:** with air wings selected, **Shift+right-click** an airbase to send only the wings that fit. The rest stay where they are.
- **Auto rebase:** a wing whose airbase reaches less than a share of its mission region (setting, 100% by default; 0 turns it off) flies to a usable airbase with room that reaches more, keeping its mission. Wings attached to an army are left to the game, which already does this.

### ChecksumPatcher
Keeps achievements available when mods change the checksum (Ironman already works with mods). Patched in memory only.

### AiAssist
An add-on for [Sheep Mod](https://github.com/Sheep-of-Latrobe/sheeps-mod): it fixes vanilla AI logic that the mod's scripts can't reach, with no bonuses. It does nothing without Sheep Mod, and only in single player.
- **Hold the line:** at war, every province on the line keeps a defender; vanilla can ship a whole fighting front away (supply crisis, another front). The AI's regrouping can't take a province's last defender, the divisions walling in a pocket, a reserve, or a division that arrived in the last week (vanilla shuffles fresh arrivals back and forth). Free divisions walk back into empty line provinces — ports, victory points, river banks, cities and mountains first, key ground from twice as far — and a country with divisions to spare keeps reserves a province behind its line. Worn-out divisions in contact rest a few provinces back where another division holds their province, and divisions in a salient whose way back is closing pull out before the pocket closes; both go back to a front once rested. A country short of divisions for its line doesn't start attacks (advancing would widen it and get the spearhead encircled).
- **Beach defence:** a naval landing is answered at once: free divisions next to it wall the beachhead in and attack into it, instead of waiting for the front to collapse.
- **Breakthrough axis:** local attacks keep to the day's breakthrough axis, the front's thinnest enemy sector, so the pushes don't zig-zag; close air support backs battles on the axis first.
- **Attack targets:** local attacks go for pocket-closing targets first, then valuable ones (victory points, capitals, supply hubs, airfields, resources, empty or worn defences, dug-in defenders counting against a target), then ones that shorten the front, instead of a random pick. Next to a pocket of the AI's own divisions, the province that opens a corridor to it comes first (and breakouts from inside), ahead of pocket closing when at least as many of its divisions are trapped as the attack would cut off; vanilla has no relief logic. Fresh divisions also take empty enemy provinces next to the line, which vanilla's local attacks often skip.
- **Armor terrain:** armored and motorized divisions don't attack into terrain or across rivers that cripple them.
- **Supply-aware attacks:** local attacks skip provinces the attackers couldn't be supplied in once taken (setting: the supply share required); pocket-closing targets and the corridor to a pocket of ours are still taken.
- **Air retreat:** planes leave regions where the AI keeps losing the air war badly and go where it can win.
- **Bombing targets:** strategic bombers ask for twice the planes for enemy regions next to the land front and half for ones far behind it, which also rank lower; vanilla ignores how far a region is from the front.
- **Production lines:** a small factory cut to equipment whose lines run at 35 %+ efficiency is held for a while (setting: how many days), the factories coming off the smallest increases elsewhere; vanilla drains the last lines whatever their efficiency and restarts the factories at 10 %.
- **Refit priority:** reinforcements and new equipment go to front-line divisions (in combat or next to the enemy) before rear and garrison ones.

## Multiplayer
- Other players see vanilla. Only interface files change, never the checksum, so players without the loader can join the same game.
- Plugins like the queues and auto construction send the same commands a player's clicks would, so they work as usual.
- Features that change game rules (field marshals holding air wings and railway guns, railway gun positioning, AiAssist) switch off in a multiplayer lobby and back on in single player. Field marshal assignment switches to spreading over the marshal's armies with vanilla commands instead.

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
