# Development

## Layout
| Path | What |
|---|---|
| `bootstrap/` | `version.dll` proxy: C# compiled with Native AOT (`Boot.cs`: boots .NET 10 through hostfxr just before the game's entry point; the profiler and start-up prefetch) linked with C for what runs on the game's threads (`native.c`: the export forwarders, the entry point hook and the crash handler; the profiler's sampling, the prefetch's file recorder and the mimalloc glue, `allocator.c`). |
| `HOI4ModLoader/` | The core: plugin loading, patch API, signature scanning, config, logging, game structures, the settings window's Mods tab, the main menu version label. |
| `plugins/<name>/` | One plugin per folder. |
| `plugins/<name>/gui/` | (optional) GUI snippets for `[assembly: GuiPatch]`, embedded in the DLL. |
| `plugins/<name>/localisation/` | (optional) Translations of the plugin's name, description and settings (`english.yml`, `korean.yml`). |
| `tests/Signatures.Tests/` | Checks every `[Signature]` and `[assembly: GuiPatch]` against the installed game (without running it). |

## Building
Requirements: .NET 10 SDK, Visual Studio's "Desktop development with C++" (for `bootstrap`), and the Steam version of HOI4 to deploy into.

```
dotnet build
```
- Every build deploys into the game folder (found through Steam's registry entry, or `-p:HOI4Dir=`).
- With the game running, plugins hot reload; locked core files only warn.
- `bootstrap` is published (`dotnet publish bootstrap`, Native AOT) when its sources change; it needs the Visual Studio C++ tools for the C compiler and the linker.
- mimalloc is Microsoft's official release (version and SHA-256 pinned), downloaded at build time; it isn't in the repository.

A release folder (`out\`, relative to the repository). Release builds leave out the `.pdb` symbols (`-p:DeploySymbols=true` keeps them):
```
dotnet build -c Release -p:DeployDir=out
```

## Releases
The loader's version is `<Version>` in `HOI4ModLoader/HOI4ModLoader.csproj`. Raise it and push to `master`: `.github/workflows/release.yml` builds on a Windows runner, zips `out\` (`version.dll`, `HOI4ModLoader\`) and publishes release `v<Version>` with its tag and generated notes. A push that leaves the version as it is releases nothing (the release already exists); the workflow can also be run by hand from the Actions tab.

The runner has no game, so the signature tests don't run there: run `dotnet test tests/Signatures.Tests` locally first.

## Writing a plugin
```csharp
public static class Signatures   // the tests check every [Signature] here
{
    [Signature] public const string Foo = "48 89 5C 24 ? 57 48 83 EC 20";
    [Signature(Anchor = "Some engine string")] public const string Bar = "85 C0 0F 94 ?? E8"; // only inside functions using that string
}

[Plugin("com.green1052.myplugin", "My Plugin", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
[Dependency("com.green1052.other")]                // (optional) load after this plugin
public class MyPlugin : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate int FooFn(nint a);
    private IHook<FooFn>? _foo;

    public override void Load()                    // throwing reverts all of this plugin's patches
    {
        var foo = Sig.Find(Signatures.Foo);        // throws on 0 or 2+ matches
        var enabled = Config.Bind("Features", "Foo", true, "Description");
        Patches.Bytes(foo + 10, "74", "EB");       // throws if the expected bytes differ
        _foo = Patches.Hook<FooFn>(foo, a => _foo!.OriginalFunction(a) + 1);
        Patches.Asm(foo + 20, 10, "use64", "nop"); // replaces [at, +10) with assembly
        MainThread.Every(TimeSpan.FromSeconds(1), () => Log.Info("tick"));
    }
}
```
- `GameVersion`: the plugin is skipped on other game versions (struct offsets may have moved). Its config's `IgnoreGameVersion` overrides that.
- `AffectsGameState`: the plugin changes game rules. It is switched off in multiplayer.
- A plugin with a multiplayer-safe way of doing its job (vanilla commands only) leaves `AffectsGameState` off and branches on `Multiplayer` in `Load()`; every plugin reloads when the mode changes. Make that fallback a setting (e.g. ArmySupport's `FieldMarshal`: attach in single player, spread over the armies in multiplayer), not a second plugin.
- `Config.Bind` settings in any section but `[General]` show in the Mods tab (true/false as checkboxes, numbers with arrows), in the order they're bound; clamp numbers to a sane range.
- Bind every setting in `Load()`, unconditionally (even a switched-off feature's, before anything that can throw): after a successful load the loader deletes the entries the plugin didn't bind, as settings a newer version dropped. At startup it also deletes `config\com.green1052.*.cfg` files whose plugin is no longer in `plugins\`.
- Register patches and `MainThread.Every` timers in `Load()`, so a reload cleans them up.
- Send game actions with `Commands.New` / `TryPost`: they are the game's own commands, the same the UI sends, so multiplayer stays in sync. Ready-made: `Commands.Move`, `Unassign`, `NewOrderCommand` (battle plan play/stop, order delete...).
- Game structures more than one plugin reads live in the core, with their offsets and signatures: `Game` (countries, `Hour`, `AtWar`/`Allied`, `StrategicAir`, supply flow), `Province` (fields, terrain, forts, enemy/friendly, pockets, supply starvation), `State`, `Buildings`, `Division` (CArmy), `Order` (COrder), `OrdersGroup`, `ProductionLine`, `AirRegions` (strategic regions, air combat in them), `GroundCrews` (More Ground Crews); `Gui.HotkeyPressed` (Ctrl+Alt+key) and `Gui.Selection`. Their game functions resolve on first use and are safe on the game's AI threads. Add a layout there once a second plugin needs it.
- Plugin ids look like `com.green1052.<name>`.

### Translations
Files in `localisation/<language>.yml` (the game's format, UTF-8 with BOM) are embedded in the DLL.

| Key | What |
|---|---|
| `<id>.name` | Plugin name (English comes from `[Plugin]`) |
| `<id>.desc` | Plugin description (the Mods tab tooltip) |
| `<id>.<setting key>`, `<id>.<setting key>.desc` | A setting's name and description |

Read them with `Loc.Get(key, fallback)`: the game's language, else English, else `fallback`.

### Game files (GUI)
Plugins change vanilla interface files without shipping copies of them: declare the change and the loader builds the file from the installed game at startup into `HOI4ModLoader\ui`, which it mounts in front of the game's virtual file system (PhysFS, exported by `hoi4.exe`). Players install no mod. Files of disabled plugins are left out.
```csharp
[assembly: GuiPatch("interface/countryconstructionsview.gui", "gui/auto_construction_target.gui",
    After = @"(?s)name = ""start_construction_entry"".*?(?=\n\t\})")]   // or Replace = regex, with ${1} etc. in the snippet
```
If a pattern stops matching after a game update, the loader leaves the vanilla file and logs an error; the signature tests catch it first. Keep to `interface/` and `localisation/`: anything in the checksum (`common`, `events`, `history`, `map`) would break multiplayer and achievements.

### Map arrows
`MapArrows` draws arrows with the game's own map arrow manager, in the supply map's style, tinted, in any map mode; nothing is sent, so other players see nothing. The manager is caught from the game's first arrow in each game (`Ready`). `Make(r, g, b)` gives an arrow to keep for the game, `Draw(arrow, points)` shows it along map positions (`Position(province)`) with its head on the last, `Hide(arrow)` hides it (AutoTrade's trade routes). `Flash(fromId, toId, r, g, b)` shows an arrow between two provinces for about 8 seconds, from a small reused pool, only with `[Debug] AiActionArrows` on: the AI plugins mark what they just did with it. Main thread only.

## Debugging and hot reload
- Set `WaitForDebugger = True` in `config\HOI4ModLoader.cfg`, start the game and attach a debugger to `hoi4.exe`: it stops before `Load()`.
- Rebuilding a plugin with the game running reverts every patch and loads the new DLLs. Plugin state (queues and such) resets. `HotReload = False` turns it off.
- `HOI4ModLoader\crash.log` (kept across starts, deleted at start once past 1 MB): native faults on a thread inside a hook or timer (the label names the hook the thread is under; the faulting `module+offset` says whose code it was, so a hook's label also covers the game original it calls), faults outside every module (hook stubs), and .NET's own fatal errors with the managed stack. `CrashTest = 1` or `2` under `[Debug]` crashes the game on purpose once, to check it.

## How mimalloc is wired in
- `hoi4.exe` carries its own allocation functions (`malloc`, `calloc`, `realloc`, `free`, `_msize`; static CRT). Before the game's CRT starts, the loader redirects them to a few lines of native code in `version.dll` (`bootstrap\allocator.c`).
- Memory allocated before the switch is freed back to the CRT heap, so mixing is safe.
- The process-wide redirect (ucrtbase) is off (`MIMALLOC_DISABLE_REDIRECT=1`): it doesn't reach the game itself and would only change other DLLs.
- Off by default (`[Performance] Mimalloc`): measured about 2.5 % slower than the Windows heap at speed 5 (1936 save, restart A/B), most likely the quarantine below. The game has use-after-free bugs the Windows heap tolerates (it leaves a freed block's contents alone); mimalloc writes its free-list pointer over the first qword (a vtable). So `hm_free` quarantines blocks up to 4 KiB: each thread keeps its last 16384 such frees in a ring and frees the oldest as a new one comes in (mimalloc still frees on the freeing thread; the ring is flushed by an FLS callback when the thread exits).
- Known case, guaranteed beyond the quarantine: ActivateDecision (`Allocator.ActivateDecisionSig`) runs a targeted decision's effect, which can free the decision instance, then calls its vtable. With mimalloc on the loader hooks it and wraps it in `hm_defer(1)` / `hm_defer(0)`: that thread's frees are queued and done when the call returns (nesting counts; the queue is capped, past that frees happen at once).

## Multiplayer detection
A session whose server is a `CNetworkServer` (host) or `CProxyServer` (client) is multiplayer. Joining a lobby unloads `AffectsGameState` plugins; returning to single player loads them again.

## After a game update
1. Run `dotnet test tests/Signatures.Tests` to find broken patterns and GUI patches.
2. Fix the patterns and struct offsets.
3. Raise the plugins' `GameVersion`.
