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
4. Start the game: the main menu's version label shows `HOI4ModLoader 1.0.0` under the game version.

There is no mod to enable in the launcher. The few interface files the plugins change are built from your installed game each time it starts, so they keep up with game updates.

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
| AutoConstruction | Auto construction targets per building | ✅ |
| AutoTrade | Imports resources you are short of | ✅ |
| AirbaseCapacity | Sends only the air wings an airbase has room for | ✅ |
| AirWingFieldMarshal | Air wings for field marshals | ✅ (stand-in) |
| RailwayGuns | Railway guns for field marshals, better positioning | Assignment only (stand-in) |
| AttachedCas | Attached air wings give CAS in their whole mission area | Single player only |
| ChecksumPatcher | Keeps achievements and ironman with mods | ✅ |
| NoTelemetry | Blocks gameplay telemetry | ✅ |

### Queues
**Shift+click** to queue, again to unqueue. Queued items show `N. ` in front of their name.
- **Research, national focus, intelligence agency upgrades:** the first queued item starts as soon as a slot is free.
  - Repeatable agency upgrades queue one more level per click (`1x3`).
  - Unqueueing a focus also unqueues the queued focuses that need it.
- **National spirits, laws, designers, advisors, decisions:** taken one at a time as soon as you have the political power; a full slot is replaced.

### AutoConstruction
In the construction tab, **Shift+click** a building to raise its auto construction target by 1, **Ctrl+Shift+click** by 5, **Ctrl+click** to lower it by 1. The button shows how many are left as `+N`.
- What's missing is added to the end of the construction queue, one level per state (or province) so they are built at the same time.
- Location priority: core states, then infrastructure level, then free building slots.

### AutoTrade
In the trade view, **Shift+click** a resource icon to turn auto trade on or off for it (`Auto` shows). A shortage is imported automatically.
- Seller priority: your subjects, your faction, then whoever can sell the most; trade influence breaks ties.
- Existing imports are never reduced.

### AirbaseCapacity
With air wings selected, **Shift+right-click** an airbase to send only the wings that fit. The rest stay where they are.

### AirWingFieldMarshal
Attach air wings to field marshals (army groups). The marshal's card gets attach slots.
- In multiplayer, right-clicking a marshal's card with air wings selected spreads them over the marshal's armies instead.

### RailwayGuns
- **Field marshal assignment:** right-click a marshal's card with railway guns selected. In single player they are assigned to the marshal; in multiplayer they are spread over the marshal's armies.
- **Better positioning (single player):** guns stay just behind the front line instead of on it, and never ship out by sea. Adjustable in the Mods tab.

### AttachedCas
Air wings attached to a general support every battle in their mission area, not only their own divisions' battles.

### ChecksumPatcher
Keeps achievements and ironman available when the checksum changes. Patched in memory only.

### NoTelemetry
Gameplay telemetry is not sent to Paradox. Login, multiplayer and crash reports are untouched.

## Multiplayer
- Other players see vanilla. Only interface files change, never the checksum, so players without the loader can join the same game.
- Plugins like the queues and auto construction send the same commands a player's clicks would, so they work as usual.
- Plugins that change game rules (AttachedCas, railway gun positioning, ...) switch off in a multiplayer lobby and back on in single player. Their multiplayer stand-ins start by themselves in multiplayer; the Mods tab shows both as active.

## Performance: mimalloc
The game's memory allocations use Microsoft's [mimalloc](https://github.com/microsoft/mimalloc) instead of the Windows heap. If you suspect crashes or memory problems, set `[Performance] Mimalloc = False` in `HOI4ModLoader\config\HOI4ModLoader.cfg` (takes effect at the next start).

## Troubleshooting
- **".NET 10 Runtime (x64) not found" message:** install the .NET 10 Runtime (x64). The game still starts, without mods.
- **A plugin does nothing:** check `HOI4ModLoader\LogOutput.log`. Plugins don't load on game versions other than 1.19.3.
- **UI elements missing or broken:** other mods that replace the same interface files (settings window, main menu, construction tab, intelligence agency, army group card) can conflict; the loader's versions win.
- **Turning a plugin off:** use the Mods tab, or set `Enabled = False` in `config\<plugin id>.cfg`.

## Development
See [docs/development.md](docs/development.md) for building and writing plugins.

## Disclaimer
HOI4ModLoader is an unofficial fan project. It is not affiliated with, endorsed by or sponsored by Paradox Interactive AB. Hearts of Iron and Paradox Interactive are trademarks of Paradox Interactive AB; they are used here only to say which game this works with.

The loader changes the running game in memory. Use it at your own risk and keep backups of your saves; it comes without any warranty. Keeping achievements with mods (ChecksumPatcher) may go against Paradox's terms: whether to use it is your call.
