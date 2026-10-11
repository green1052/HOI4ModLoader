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
| FrontLine | Front line micro (rotation, plugging, escape, attacks, reserves) plus battle plan and operation (blitz, pincer) hotkeys | ✅ |
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
Front line micro a player does by hand, with the battle plan UI's own commands (multiplayer works). Divisions taken off a front go back onto it once their job is done. Divisions in combat are never picked (a defender can only retreat).
- **Rotation:** a worn-out division in contact with the enemy rests a few provinces back, where another of ours holds its province; at most a share of a front rests at once. A division short of equipment or manpower goes back after two weeks once its organisation has recovered.
- **Plugging:** a defence of ours that is being lost gets the nearest fresh division (resting, unassigned, or spare on a quiet part of a front).
- **Escape:** front divisions in a salient whose way back narrows to a neck of one or two provinces next to the enemy (salients up to 20 provinces) pull back through the neck, to safe ground behind it, before the pocket closes; one division stays in each province it held alone.
- **Attack:** empty enemy provinces next to the line are taken by a fresh division; surrounded enemy divisions are attacked only by enough fresh divisions to outnumber them, forts counted, and no more are sent while the first ones are still on their way or fighting. Best targets first: closing a pocket, then victory points, supply hubs, airfields and ports, then shortening the front; skipped are provinces our supply couldn't follow (unless they close a pocket) and worthless ones that lengthen the front.
- **Fire brigade:** each front keeps one armored or motorized reserve per six line provinces (up to three) on posts a province behind its line, off the battle plan. Where the enemy breaks in (a province of ours lost in the last two days, a landing, enemy troops inside our lines), the nearest reserves counterattack with enough to win and then return to their post; a post the line moved away from is given up and its division goes back to its front.
- **Depth defence:** key line provinces (ports, victory points, river crossings, cities and mountains) get a second division a province behind them, from spare divisions of a front; it stays while the line holds there.
- **Shuffle:** a front that is losing (three provinces lost in two days or defences being lost, or facing twice its divisions) gets spare fresh divisions of quiet fronts in its most pressed provinces; they go back to their own front a day after the crisis. Meanwhile fresh divisions next to the crisis pin idle enemy stacks of one or two divisions with a cheap attack each (broken off when their organisation runs low), so those stay locked in combat and can't be shuffled in.
- **Battle plan keys:** Ctrl+Alt+E activates every battle plan of all your armies and army groups at once, Ctrl+Alt+H halts them (the same command the battle plan window's button posts).
- **Operations:** select an army (or divisions) and press **Ctrl+Alt+B** for a blitz: its fresh armored and motorized divisions break through the cheapest enemy sector (few divisions, no forts, open ground, no river) and push province by province toward a deep objective (victory points, a supply hub, many enemy divisions along the way), while its infantry holds the corridor behind them, one division per corridor province next to the enemy. Select two armies or an army group and press **Ctrl+Alt+P** for a pincer: two spearheads, one per wing, meet at the same objective behind the enemy; once the ring closes they go back and the infantry holds the ring for a week. Meanwhile fresh divisions of those armies support-attack idle enemy stacks next to the breakthrough and the corridor (one each, broken off when worn), so they are locked in combat and can't be moved into the gap. An operation ends when its objective is taken, the corridor is cut or a defence of it is being lost, or the spearheads are worn out (an organisation setting); the same key again cancels it. Its divisions then go back onto their battle plan. What it does is written to the log.
- Settings: every threshold, each feature on/off.

### ArmySupport
- **Field marshals:** right-click a marshal's card with air wings or railway guns selected. In single player they are attached to the marshal (army group): his card gets air wing slots, his air wings support his armies' battles, and his railway guns take positions along all of his armies' divisions. In multiplayer they are spread over the marshal's armies instead. Setting: on/off.
- **Railway gun positioning (single player):** guns stay just behind the front line instead of on it, and never ship out by sea. The penalty and sea transfer are settings.
- **Airbase capacity:** with air wings selected, **Shift+right-click** an airbase to send only the wings that fit. The rest stay where they are.
- **Auto rebase:** a wing whose airbase reaches less than a share of its mission region (setting, 100% by default; 0 turns it off) flies to a usable airbase with room that reaches more, keeping its mission. Wings attached to an army are left to the game, which already does this.
- **Army organizer:** select divisions (drag a box, or click armies' and field marshals' cards) and press **Ctrl+Alt+G**. They are split into armies of at most the game's army size for a general (24 in vanilla), divisions standing near each other together; a whole army of at least half that size stays as it is. Each new army gets the best idle general, and all of them join the army group most of the divisions came from, or a new army group under the best idle field marshal. Armies without an idle general are left without one. The army list's own commands (multiplayer works). Setting: on/off.

### ChecksumPatcher
Keeps achievements available when mods change the checksum (Ironman already works with mods). Patched in memory only.

### AiAssist
An add-on for [Sheep Mod](https://github.com/Sheep-of-Latrobe/sheeps-mod): it fixes vanilla AI logic that the mod's scripts can't reach, with no bonuses. It does nothing without Sheep Mod, and only in single player.
- **Hold the line:** at war, every province on the line keeps a defender; vanilla can ship a whole fighting front away (supply crisis, another front). The AI's regrouping can't take a province's last defender, the divisions walling in a pocket, a reserve, or a division that arrived in the last week (vanilla shuffles fresh arrivals back and forth). Free divisions walk back into empty line provinces — ports, victory points, river banks, cities and mountains first, key ground from twice as far — and a country with divisions to spare keeps reserves a province behind its line. Worn-out divisions in contact rest a few provinces back where another division holds their province, and divisions in a salient whose way back is closing pull out before the pocket closes; both go back to a front once rested. A country short of divisions for its line doesn't start attacks (advancing would widen it and get the spearhead encircled).
- **Beach defence:** a naval landing is answered at once: free divisions next to it wall the beachhead in and attack into it, instead of waiting for the front to collapse.
- **Defensive postures** (needs Hold the line): an AI country facing a much stronger enemy (twice its share of power along the line, or 1.3x while losing line provinces or short of divisions) gives up exposed ground and holds its *grad*: the most defensible short line between the enemy and its capital, found as a minimum cut through its own provinces (rivers, mountains, marsh, forests, hills, cities and forts in front, few directions to be attacked from). It is redrawn weekly, or one line back once the enemy breaks in. Divisions walk there and stay put (victory point cities get one more). The country starts no offensives or paradrops (landings still go); it attacks only fought-out enemy stacks next to the line, from two sides, while single divisions pin the enemy stacks that could come to their aid, and worn-out attackers break off. At even strength, after two weeks at least, its divisions go back to the AI's fronts. (Vanilla's own fallback lines are switched off by Sheep Mod's defines.)
- **Breakthrough axis:** local attacks keep to a breakthrough axis so the pushes don't zig-zag: while the AI's own armoured spearhead (the game's force concentration) attacks, the axis is where its path crosses the enemy line, and it moves forward with the spearhead; otherwise it is the front's thinnest enemy sector, held for a day. Off the axis, the line only makes pinning attacks on enemy divisions next to it (a division under attack can't move except to retreat, so they can't shift into the breakthrough). Close air support backs battles on the axis first.
- **Strategic invasions:** naval invasions land where it hurts and can hold: thinly held coast near victory points, capitals, supply hubs, airfields and resources scores higher, beaches with enemy divisions nearby, forts or coastal bunkers lower (vanilla weighs only distance, ports and area priorities). A planned landing waits, a week at most, while the enemy divisions around its beach outnumber half the landing force.
- **Strategic paradrops:** the AI uses its paratroopers, which vanilla's AI never does (needs Breakthrough axis): up to four paradrop-capable divisions of an army on the axis fly from the nearest airbase that can carry them onto an empty enemy province behind the line near the axis (victory points, supply hubs, airfields and pockets first), only when vanilla allows the drop (prepared, enough transport planes, air superiority on the route). A drop that hasn't gone in two weeks is given up.
- **Attack targets:** local attacks go for pocket-closing targets first, then valuable ones (victory points, capitals, supply hubs, airfields, resources, empty or worn defences, dug-in defenders counting against a target), then ones that shorten the front, instead of a random pick. Next to a pocket of the AI's own divisions, the province that opens a corridor to it comes first (and breakouts from inside), ahead of pocket closing when at least as many of its divisions are trapped as the attack would cut off; vanilla has no relief logic. Fresh divisions also take empty enemy provinces next to the line, which vanilla's local attacks often skip.
- **Armor terrain:** armored and motorized divisions don't attack into terrain or across rivers that cripple them.
- **Supply-aware attacks:** local attacks skip provinces the attackers couldn't be supplied in once taken (setting: the supply share required); pocket-closing targets and the corridor to a pocket of ours are still taken.
- **Air retreat:** planes leave regions where the AI keeps losing the air war badly and go where it can win.
- **Bombing targets:** strategic bombers ask for twice the planes for enemy regions next to the land front and half for ones far behind it, which also rank lower; vanilla ignores how far a region is from the front.
- **Production lines:** a small factory cut to equipment whose lines run at 35 %+ efficiency is held for a while (setting: how many days), the factories coming off the smallest increases elsewhere; vanilla drains the last lines whatever their efficiency and restarts the factories at 10 %.
- **Refit priority:** reinforcements and new equipment go to front-line divisions (in combat or next to the enemy) before rear and garrison ones.
- **New divisions** (part of Hold the line): newly deployed divisions go where they are needed most first: the grad's holes, else the empty provinces of the threatened line, then the least held province of the most thinly held front, instead of wherever vanilla's theatre share-out sends them. They are left alone while they walk there.
- **Invasion escort** (part of Strategic invasions): the sea regions on a planned landing's route get twice the fighters the air AI wants there (100 at least) and twice the naval bombers. Vanilla's navy already has to win those regions before a landing sails, and its fleets already head there; its air AI never looks at the route.

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
