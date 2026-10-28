# HOI4ModLoader

A C# plugin loader for Hearts of Iron IV, in the spirit of BepInEx. It changes no game files: everything happens in memory while the game runs.

The bundled plugins add quality-of-life features: national focus and research queues, auto construction, air wings for field marshals and more.

## Requirements
- Hearts of Iron IV **1.19.4** (Steam, Windows 64-bit)
- [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installation
1. Install the [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download the latest zip from [Releases](../../releases) and extract it.
3. Put `version.dll` and the `HOI4ModLoader` folder in the game folder (where `hoi4.exe` is).
   - Steam library → right-click Hearts of Iron IV → Manage → Browse local files
4. Start the game: the main menu's version label shows `HOI4ModLoader <version>` under the game version.

There is no mod to enable in the launcher. The few interface files the plugins change are rebuilt from your installed game at each start, so they keep up with game updates.

### Update
- **Automatic:** at startup and every 6 hours the loader installs a newer GitHub release in the background. It takes effect at the next start; the main menu version tooltip says when one is waiting.
  - `[Update] CheckHours` in `HOI4ModLoader\config\HOI4ModLoader.cfg` sets the interval (0: only at startup, -1: off).
- **By hand:** delete the old `HOI4ModLoader\plugins` folder first (plugins get merged or renamed between versions), then install as above. Your settings in `HOI4ModLoader\config` stay.

### Uninstall
Delete `version.dll` and the `HOI4ModLoader` folder from the game folder.

## Usage
- **Settings → Mods tab:** turn plugins on or off and change their settings. Hover a plugin for its description.
- **Main menu version label:** hover it for the list of loaded plugins.
- Each plugin's settings file is created in `HOI4ModLoader\config\` on first start.

## Plugins
| Plugin | What it does | Multiplayer |
|---|---|---|
| Queues | National focus and research queues: Shift+click to reserve, numbers show the order | ✅ |
| AutoConstruction | Auto construction targets per building, auto supply, auto production | ✅ |
| FrontLine | Front line micro (rotation, plugging, cordons, escape, attacks, reserves) plus battle plan and operation (blitz, pincer) hotkeys | ✅ |
| ArmySupport | Air wings and railway guns for field marshals, railway gun positioning, airbase capacity, auto rebase, auto ground crews and aces, paradrops and invasions to several targets with auto support, battle plan keys for them, army organizer | ✅ (field marshals: spread over armies; railway gun positioning: single player) |
| ChecksumPatcher | Keeps achievements with mods | ✅ |
| Fonts | The game's text in an installed font or a .ttf/.otf/.ttc file | ✅ |
| Reroll | A fresh random seed at every load, so reloading doesn't replay the same dice | Single player only |
| AiAssist | Add-on for Sheep Mod: fixes AI logic its scripts can't reach | Single player only |
| Intelligence | Drag operatives in the intelligence screen's bottom bar to reorder them; finished operations clear themselves | ✅ (operative drag: single player) |
| DiscordPresence | Your country and the game date in your Discord status | ✅ |

### Queues
**Shift+click** a national focus or a technology to reserve it, again to unqueue it. Queued entries show their position as a number (before the focus name, or on the tech's bonus line).
- The first queued focus starts as soon as the focus slot is free; queued techs fill research slots the same way.
- Unqueueing a focus also unqueues the queued focuses that need it.
- Focuses that can no longer start are dropped; fully researched techs leave the queue.

### AutoConstruction
In the construction tab, **Shift+click** a building to raise its auto construction target by 1, **Ctrl+Shift+click** by 5, **Ctrl+click** to lower it by 1, **Alt+click** to turn it off and take its lines not started yet out of the queue.
- The button shows how many are still to be built as `+N`.
- Targets are kept per country for the session, across loads.
- Missing levels are queued one per state (or province), only as many as your idle civilian factories can staff. The rest follow as factories free up, with `QueueAhead` (2) more lines waiting so a finished one is replaced at once.
- Location priority: core states, then higher infrastructure (lower first for infrastructure itself), then free building slots.

**Auto supply** (`AutoSupply`, on): where your divisions have been low on supply for a week, the fix is queued.
- A saturated hub gets its railway to the capital upgraded, or a naval base if the bottleneck is at sea.
- A cut-off hub gets a railway to the nearest connected hub.
- An area with no hub near gets a new supply hub, at most one a month.

**Auto production** (`AutoProduction`, on): military factories move one at a time from a land or air line with a big surplus to one that is short.
- Settings: surplus threshold (`SurplusPercent`, 50 % of need), `CoverDays` (60), factories moved per game day (`MovesPerTurn`, 2).
- No line is created or emptied.

### FrontLine
Does the front line micro a player would do by hand, with the battle plan UI's own commands (multiplayer works).
- Divisions taken off a front go back onto it once their job is done.
- Yours are remembered in `cache\frontline_held.txt`, so a loaded save puts them back on their battle plan.
- Divisions in combat are never picked.

**Rotation**
- A worn-out division in contact rests a few provinces back while another holds its province.
- At most `MaxRestingPercent` (25) of a front rests at once.
- It returns once recovered, or after two weeks if it can't recover.

**Plugging**
- A defence being lost gets fresh divisions that can reach it in time; two if it falls within a day.
- Being lost: the attacker is past `LosingPercent` (50) of the battle, or the defender's organisation runs out within two days.
- **Thicken:** a line province facing more than twice its divisions gets one more before the attack.
- Plugs stay while enemies stand next to it, a week at most.

**Escape and breakout**
- **Escape:** divisions in a salient whose way back narrows to a one- or two-province neck pull back before the pocket closes. One stays in each province it held alone.
- **Breakout:** divisions already cut off attack together along the shortest way out, once most are out of supply.

**Attack**
- Empty enemy provinces next to the line are taken by a fresh division.
- Surrounded enemy divisions are attacked only with at least as many fresh divisions.
- Target order: closing a pocket, then victory points, supply hubs, airfields and ports, then shortening the front.
- Skipped: provinces our supply couldn't follow, and worthless ones that lengthen the front.
- Divisions on a beachhead without a port go for the nearest enemy port.
- **Mop-up:** enemy divisions in a pocket or behind our lines are finished off by any fresh divisions around them that outgun them.
- **Assaults:** an enemy province our neighbouring fresh divisions outgun twice over (`AttackOdds`, 200) is attacked by all of them, each province keeping one.
  - Your divisions assault only on a running battle plan.
  - `AttackOdds = 0` turns off assaults and mop-up.

**Fire brigade**
- Each front keeps one armored or motorized reserve per four line provinces (up to four), a province behind the line.
- Where the enemy breaks in, the nearest reserves counterattack, then return to their post.
- An AI country without spare mobile divisions uses infantry.

**Depth defence**
- Key line provinces (ports, victory points, river crossings, cities, mountains) get a second division a province behind them.

**Shuffle**
- A losing front gets spare fresh divisions from quiet fronts. They go back a day after the crisis.
- Meanwhile fresh divisions near the crisis pin idle enemy stacks with cheap attacks, so they can't be shifted in.
- A front facing only an encircled pocket keeps twice the pocket's divisions; the rest go to the army's main front.

**Cordon** (with Plugging)
- Around a breach of the last two days, our empty provinces are manned: its shoulders and tip, four a turn.
- The breakthrough hits a wall and its corridor stays narrow. Wall divisions hold a week at most.

**Lure** (AI only, with Apply to AI)
- An AI defence being lost is let go instead of plugged, to draw the attacker into a salient.
- Only when both shoulders hold and fresh mobile divisions are near to cut it off.

**Battle plan keys** (`BattlePlanKeys`, on)
- **Ctrl+Alt+E** activates every battle plan of all your armies and army groups; **Ctrl+Alt+H** halts them.

**Operations** (`OperationKeys`, on)
- **Blitz:** select an army (or divisions near the line) and press **Ctrl+Alt+B**.
  - Its fresh armored and motorized divisions break through the weakest enemy sector and push toward a deep objective.
  - Its infantry holds the corridor behind them.
- **Pincer:** select two armies or an army group and press **Ctrl+Alt+P**.
  - Two spearheads meet at the same objective behind the enemy; the infantry then holds the ring for a week.
- During both, other fresh divisions pin enemy stacks next to the breakthrough.
- An operation ends when its objective is taken, the corridor is cut, a wing's spearheads are worn out (`SpearheadOrganisation`, 35), the advance stalls, or you press the key again.
- Its divisions then go back onto their battle plan. A loaded save forgets running operations.
- The map shows an arrow per spearhead (red: blitz, orange: pincer). The log says why an operation didn't start.

**Your divisions stay put** (`PlayerRepositions`, off)
- While off, FrontLine doesn't move your divisions off your battle plans to stand elsewhere (depth defence, fire brigade, shuffles, cordons).
- It still acts where they stand (rotation, plugging, escape, breakouts, attacks) and on the operation keys.

**Apply to AI** (`ApplyToAI`, off, single player)
- AI countries get the same micro with the same settings, plus the lure, but not rotation, hotkeys or operations.
- AI Assist's own versions of these stand aside meanwhile. Without AI Assist, the AI's regrouping may take divisions back.

Settings: every threshold, each feature on/off.

### ArmySupport
- **Field marshals** (`FieldMarshal`, on): right-click a marshal's card with air wings or railway guns selected.
  - Single player: they are attached to the marshal. His air wings support all his armies' battles; his railway guns cover all his armies.
  - Multiplayer: they are spread over the marshal's armies instead.
  - Cards keep the **+** slot once every air wing type is attached, for more wings.
- **Railway gun positioning** (single player): guns stay just behind the front instead of on it, and never ship out by sea.
  - Settings: `FrontPenalty` (500; 0: off), `NoSeaTransfer` (on).
- **Airbase capacity:** with air wings selected, **Shift+right-click** an airbase to send only the wings that fit.
- **Auto rebase:** a wing whose airbase covers less than `Coverage` (100 %; 0: off) of its mission region moves to a base with room that covers more.
- **Auto aces** (`AutoAces`, on): daily, idle aces are put on air wings without an ace, wings on a mission first.
  - Fallen aces are taken off their wings, which vanilla never does (in single player, for every country).
- **Auto ground crews** (`AutoGroundCrews`, off): turns More Ground Crews on in the two regions where your wings fight hardest.
  - Only while command power stays above `GroundCrewsReserve` (50).
  - Turned off where the fighting stops for three days. Regions you turn off yourself are left alone.
- **Battle plan keys** (always on): **D** naval invasion, **Ctrl+D** naval invasion with a floating harbour, **G** paradrop. Vanilla has no keys for these buttons.
  - Added to the installed game's own battle plan interface, so nothing goes stale after an update. A workshop mod that replaces `interface/battleplantools.gui` with an older copy (e.g. "Naval Invasion Hotkey") is overridden; unsubscribe it.
- **Paradrop to several destinations** (`SplitDrops`, on): select several paratrooper divisions, press the paradrop button, click the airbase, then click destinations one after another.
  - Each further destination gets its share as a new paradrop from the same airbase; the divisions end up evenly spread.
- **Auto paradrop transports** (`AutoTransports`, on): a paradrop with no transport planes on the paradrop mission at its airbase gets the idle transport wings that reach its target.
- **Invasion to several beaches** (`SplitInvasions`, on): select several divisions, press the naval invasion button, click the port, then click beaches one after another.
  - Each further beach gets its share as a new invasion from the same port; the divisions end up evenly spread.
- **Auto invasion support** (`AutoSupport`, on): a naval invasion with no task force on naval invasion support gets your idle (holding, not fighting) task forces on that mission over the sea regions by its port and beaches.
- **Army organizer** (`Organize`, on): select divisions and press **Ctrl+Alt+G**.
  - They are split into full armies of nearby divisions; an army already at least half full stays as it is.
  - Each new army gets the best idle general and joins the army group most of its divisions came from (or a new one under the best idle field marshal).

### ChecksumPatcher
Keeps achievements available when mods change the checksum (Ironman already works with mods). Patched in memory only.

### Fonts
The game's text in a font you choose, with no font mod to make.
- **Pick a font:** in Settings → Mods → Fonts, the Font setting's ▶ lists installed fonts; tick one. Or set `Font` in `HOI4ModLoader\config\com.green1052.fonts.cfg` to a `.ttf`/`.otf`/`.ttc` file path. Restart the game.
- At startup the game's bitmap fonts are redrawn with it, matching the original size, weight, outline and shadow.
- Characters the font lacks keep the original glyph. Map labels and a few other fonts stay as they are.
- The first start with new settings takes a few seconds (Korean: about 10). The result is cached in `HOI4ModLoader\cache\fonts`.
- Settings: `HeaderFont` (titles; empty: same font), `SizePercent` (100), `Typewriter` (off: also redraw the typewriter fonts of documents).
- An unknown font name is reported in the log, and the game's fonts stay.

### Reroll
HOI4 saves its random seed, so reloading and doing the same thing replays the same battles and events. Reroll gives every game start and save load a fresh seed.
- Combat, `random_list` and mean-time-to-happen events come out differently after a load.
- Events already scheduled before the save stay.
- Off in Ironman unless `[Reroll] InIronman = True`.

### AiAssist
An add-on for [Sheep Mod](https://github.com/Sheep-of-Latrobe/sheeps-mod): it fixes vanilla AI logic that the mod's scripts can't reach, with no bonuses. It does nothing without Sheep Mod. Each feature can be turned off in its settings.

**Hold the line**
- Front sizes, at war:
  - Every line province keeps a defender, and each front at least as many divisions as the enemy across it.
  - A front short of supply (under 80 %) asks for no more divisions than it has.
  - A front holding twice the enemy gives its spare divisions to fronts that are short.
  - No front asks for more than three divisions per line province.
- Divisions on a line with enemies near are not redeployed to other fronts.
- Coast and home garrisons:
  - None against enemies that hold no port.
  - Only a division per home port before a land war, for a month after its line was short, or while a war on its home border is on or coming.
  - A tenth to a third of its divisions at war on a land line, rising from 1939 to 1942 (to 35 %-50 %).
- The AI's regrouping can't take the last defender of a province next to the enemy, divisions walling in a pocket, reserves, or divisions that arrived this week.
- Free divisions walk back into empty line provinces, key ground (ports, victory points, rivers, cities, mountains) first.
- A country with divisions to spare keeps reserves behind its line that counterattack break-ins, and a second division behind key provinces.
- **Rest:** divisions worn out by fighting rest a few provinces back, a quarter of those around at most.
- **Escape:** divisions in a closing salient pull out if another division holds their province.
- Neither rest nor escape happens where the AI outguns the enemy two to one.
- A country short of divisions for its line starts no attacks and stops offensives on thin fronts, unless it outguns the enemy two to one.
- Offensives that lose most of their attacks, or push into a closing salient, are called off.
- Army HQs are kept off the fronts.

**Beach defence**
- Free divisions near a fresh enemy beachhead wall it in and attack into it at once.

**Defensive postures** (needs Hold the line)
- An AI country facing an enemy twice its power along the line (1.6x while losing ground) falls back to its *grad*: the most defensible short line before its capital.
- The grad is redrawn weekly, or moved one line back once the enemy breaks in.
- It starts no offensives (landings still go). It only attacks fought-out enemy stacks next to the line, with two or more fresh divisions.
- It ends once every enemy it fell back from stays under 80 % of its power for a week.

**Breakthrough axis**
- Local attacks keep to one axis so pushes don't zig-zag: its armoured spearhead's path, else the thinnest enemy sector.
- Off the axis, the line only makes pinning attacks.
- Close air support backs battles on the axis first.

**Strategic invasions**
- Invasions target thinly held coast near victory points, capitals, supply hubs, airfields and resources, and avoid defended or fortified beaches.
- A planned landing waits up to a week while the beach is heavily defended.

**Attack targets**
- Local attacks go for pocket-closing targets, then valuable provinces, then ones that shorten the front.
- Next to a pocket of the AI's own divisions, opening a corridor to it comes first.
- Fresh divisions also take empty enemy provinces next to the line.

**Exploitation** (needs Hold the line)
- Fresh mobile divisions drive up to four provinces through a gap in the enemy line to the most valuable province there.

**Pincers** (needs Breakthrough axis)
- Where the enemy masses divisions, the axis splits into two arms that close behind the mass.

**Feints** (needs Breakthrough axis)
- Before a new main push, local attacks hit a strong enemy province elsewhere for 36 hours to draw its reserves.

**Offensive timing** (needs Hold the line)
- A battle plan's offensive waits while:
  - its divisions average under 60 % organisation, or over 30 % are still moving up;
  - the enemy holds over 70 % of the air over them, or most stand in mud;
  - the line ahead is fortified and they don't outgun it two to one;
  - one of them is in a closing salient;
  - the army's last offensive failed in the last three days.
- **Battle odds:** each target is rated with the game's own combat rules. If more attacks are predicted lost than won, the offensive waits.

**Armor terrain**
- Armored and motorized divisions don't attack into terrain or across rivers that cripple them.

**Supply-aware attacks**
- Local attacks skip provinces the attackers couldn't be supplied in (`SupplyAwareMinPercent`, 25 %), unless they close a pocket.

**Air retreat**
- Planes leave regions where the AI keeps losing the air war badly.

**Bombing targets**
- Strategic bombers favour enemy regions next to the land front over ones far behind it.

**Air training**
- Green air wings train instead of flying idle missions: at peace any wing, at war fighters over quiet regions.
- At most half of a country's wings train at once. Carrier wings never do.

**No fronts on bystanders' borders** (with Hold the line)
- AI fronts facing only countries that are no threat get no divisions, so they go where a war is or may come.
  - No threat: one its AI strategies don't target, a faction member, or a neutral too small to attack.
  - Countries it expects a war with, their guarantors and rivals always keep a front.
- An ally's colonial front overseas gets no divisions either, except a small expeditionary force (up to a tenth) when that ally is losing ground there.

**Break off losing attacks**
- An AI attack it is clearly losing is broken off, and the province isn't attacked again for three days.
- Pocket-closing attacks, feints and divisions another plugin runs are left alone.

**Stable leaders**
- The AI no longer takes a general or field marshal from an army or army group he commands.

**Army training**
- AI armies with nothing to do (at war: no front, no fighting, over 70 % strength) train their divisions up to regular.
- None trains while the AI expects a war.

**Naval training**
- Idle AI task forces train at peace until their ships are regulars, unless the AI expects a war.

**Shore bombardment**
- At war, idle task forces with guns or carriers support the country's coastal land battles from the next sea, one per battle within 6 sea provinces.

**Production lines**
- A small factory cut to an efficient line (35 %+) is held for `StickyDays` (30) days.

**Refit priority**
- Reinforcements and new equipment go to front-line divisions first.

**New divisions** (part of Hold the line)
- New divisions go where they are needed most: the grad's holes, then empty line provinces, then the most thinly held front.

**Invasion escort** (part of Strategic invasions)
- The sea regions on a planned landing's route get twice the fighters and naval bombers.

**Encirclement**
- Enemy salients with a neck of one to three provinces, cutting off 3+ enemy divisions, are cut at the neck by both flanks.

**Railway guns**
- Railway guns go to the armies on the breakthrough axis first.

**Supply hubs**
- Where AI divisions are low on supply, the railway from their hub to the capital is upgraded.
- From 1941, AI countries with 60+ civilian factories also build a hub where their divisions starve with none near.

**Ground crews**
- The AI spends spare command power (keeping 50) on More Ground Crews in its two hardest-fought air regions, which vanilla's AI never uses.

### Speed tips
Game options only, no plugin needed (measured at speed 5 in a 1936 save):
- At speed 5 the game runs one game hour per frame, and the simulation, not the graphics card, sets the pace. Lowering graphics settings or raising `max_refresh_rate` in `settings.txt` (`Documents\Paradox Interactive\Hearts of Iron IV`) changed nothing there.
- The `-render_thread` launch option (Steam → Properties → Launch options; DirectX 11) draws the screen beside the next hour's simulation instead of after it. It is an undocumented game option: if you see glitches, take it out.
- Turn vsync off: with it on, speed 5 waits for the monitor.
- Autosaves pause the game while they write. Binary saves (`save_as_binary=yes` in `settings.txt`) halve that; a longer autosave interval (Settings → Game) removes most of it.
- Keep the game window in front: in the background the game slows itself down.

### Intelligence
The drag is single player only: the game has no command to reorder operatives, so the agency's list itself is changed (and saved with the game). Clearing finished operations sends the game's own command and works in multiplayer too.
- In the intelligence screen, drag an operative in the bottom bar to another place, as army cards are dragged: it follows the mouse and the others make room.
- A finished operation clears itself from the operations list `DismissDays` (7) game days after it finished, as closing its overview does; its outcome was already applied. 0: never.


### DiscordPresence
Your Discord status shows the country you play (its name for its current ideology, in the game's language), the game date, multiplayer, and the time played; the main menu outside a game.
- Needs the Discord desktop app running on the same PC.
- `ApplicationId`: Discord shows the status under a Discord application's name. It works out of the box with the project's application; to show another name, create one at [discord.com/developers](https://discord.com/developers/applications) (New Application) and paste its Application ID. Empty: off.
- The status clears within a minute after the game closes or the plugin is turned off.

## Multiplayer
- Other players see vanilla. Only interface files change, not the checksum, so players without the loader can join.
- Plugins like the queues and auto construction send the same commands a player's clicks would.
- These switch off in a multiplayer lobby and back on in single player: field marshals holding air wings and railway guns (spread over his armies instead), railway gun positioning, FrontLine's Apply to AI, Intelligence's operative drag, and the single-player plugins.
- Settings changed during a multiplayer session, and rebuilt plugin DLLs, take effect back in single player.

## Loader: start-up speed
- **Prefetch:** each start records the files the game opens in its first two minutes (`HOI4ModLoader\cache\prefetch.txt`) and reads them ahead at the next start. An empty `HOI4ModLoader\no-prefetch` file turns it off.
- **Signature cache:** the loader's code lookups are cached per game exe (`HOI4ModLoader\cache\signatures-*.txt`).
- **Profiler:**
  - An empty `HOI4ModLoader\profile-startup` file makes the next start write `HOI4ModLoader\startup-profile.txt` (the first 150 s).
  - Creating `HOI4ModLoader\profile-now` while the game runs profiles the next 60 s into `profile-<tick>.txt`, with a section for the main thread alone (it sets the game's speed).

## Loader: game fixes
- **Intelligence agency:** fixes a crash on loading a save after the game removed an operative from the wrong agency (error.log: `RemoveOperative: ... not found in agency=---`).
- **Division stats:** fixes a crash when hovering a stat of a division template deleted while its view stayed open.

## Troubleshooting
- **".NET 10 Runtime (x64) not found" message:** install the .NET 10 Runtime (x64). The game still starts, without mods.
- **A plugin does nothing:** check `HOI4ModLoader\LogOutput.log`. Plugins made for a game version (1.19.4) don't load on another; `[General] IgnoreGameVersion = True` in the plugin's config loads them anyway (may crash).
- **The game crashed:** `HOI4ModLoader\crash.log` names the code that faulted (and the plugin hook, if any). Include it with a bug report.
- **UI elements missing or broken:** other mods replacing the same interface files (settings window, main menu, construction tab, intelligence agency, army group card, battle plan tools) can conflict. The loader's versions win.
- **Turning a plugin off:** use the Mods tab, or set `Enabled = False` in `config\<plugin id>.cfg`.
- **The log:** `HOI4ModLoader\LogOutput.log` rolls over to `LogOutput.old.log` past 20 MB. `[Debug] VerboseLog = True` in `HOI4ModLoader\config\HOI4ModLoader.cfg` logs every FrontLine move and AI Assist front change (very long).
- **What do FrontLine and AI Assist do?** `[Debug] AiActionArrows = True` in `HOI4ModLoader\config\HOI4ModLoader.cfg` flashes a map arrow for each front line action they take. Arrows show once the game has drawn one of its own (select a unit).

## Development
See [docs/development.md](docs/development.md) for building and writing plugins.

## License
HOI4ModLoader is free software under the [GNU Lesser General Public License v3.0](LICENSE). The components it ships with keep their own licenses: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer
HOI4ModLoader is an unofficial fan project. It is not affiliated with, endorsed by or sponsored by Paradox Interactive AB. Hearts of Iron and Paradox Interactive are trademarks of Paradox Interactive AB; they are used here only to say which game this works with.

The loader changes the running game in memory. Use it at your own risk and keep backups of your saves; it comes without any warranty. Keeping achievements with mods (ChecksumPatcher) may go against Paradox's terms: whether to use it is your call.
