# Development

## Layout
| Path | What |
|---|---|
| `bootstrap/` | `version.dll` proxy (C++). Starts .NET 10 just before the game's entry point; also holds the mimalloc glue (`allocator.c`). |
| `HOI4ModLoader/` | The core: plugin loading, patch API, signature scanning, config, logging, game structures, the settings window's Mods tab, the main menu version label. |
| `plugins/<name>/` | One plugin per folder. |
| `plugins/<name>/mod/` | (optional) The plugin's HOI4 UI mod: `descriptor.mod` and static files. |
| `plugins/<name>/gui/` | (optional) GUI snippets for `[assembly: GuiPatch]`. |
| `plugins/<name>/localisation/` | (optional) Translations of the plugin's name, description and settings (`english.yml`, `korean.yml`). |
| `tests/Signatures.Tests/` | Checks every `[Signature]` and `[assembly: GuiPatch]` against the installed game (without running it). |

## Building
Requirements: .NET 10 SDK, Visual Studio's "Desktop development with C++" (for `bootstrap`), and the Steam version of HOI4 to deploy into.

```
dotnet build
```
- Every build deploys into the game folder (found through Steam's registry entry, or `-p:HOI4Dir=`) and the UI mods into `Documents\Paradox Interactive\Hearts of Iron IV\mod`.
- With the game running, plugins hot reload; locked core files only warn.
- `bootstrap` is built with the Visual Studio C++ tools when its sources change.
- mimalloc is Microsoft's official release (version and SHA-256 pinned), downloaded at build time; it isn't in the repository.

A release folder (`out\`, relative to the repository). Release builds leave out the `.pdb` symbols (`-p:DeploySymbols=true` keeps them):
```
dotnet build -c Release -p:DeployDir=out -p:HOI4ModDir=out\mod
```

## Releases
Pushing a version tag builds and publishes a GitHub release (`.github/workflows/release.yml`):
```
git tag v1.0.0
git push origin v1.0.0
```
The workflow builds on a Windows runner with the tag as the loader's version, zips `out\` (`version.dll`, `HOI4ModLoader\`, `mod\`) and attaches it to a new release with generated notes. The runner has no game, so the signature tests don't run there: run `dotnet test tests/Signatures.Tests` locally first.

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
- `MultiplayerOnly`: loaded only in multiplayer, for stand-ins of rule-changing plugins that only send vanilla commands.
- Register patches and `MainThread.Every` timers in `Load()`, so a reload cleans them up.
- Send game actions with `Commands.New` / `TryPost`: they are the game's own commands, the same the UI sends, so multiplayer stays in sync.
- Plugin ids look like `com.green1052.<name>`.

### Translations
Files in `localisation/<language>.yml` (the game's format, UTF-8 with BOM) are embedded in the DLL.

| Key | What |
|---|---|
| `<id>.name` | Plugin name (English comes from `[Plugin]`) |
| `<id>.desc` | Plugin description (the Mods tab tooltip) |
| `<id>.<setting key>`, `<id>.<setting key>.desc` | A setting's name and description |

Read them with `Loc.Get(key, fallback)`: the game's language, else English, else `fallback`.

### UI mods
A project's `mod/` folder (`descriptor.mod` plus static files) is deployed as a HOI4 mod named after the project. GUI files that change vanilla ones aren't copied from the game: declare them and the loader builds them from the installed game at startup, before the game reads its interface files.
```csharp
[assembly: GuiPatch("interface/countryconstructionsview.gui", "gui/auto_construction_target.gui",
    After = @"(?s)name = ""start_construction_entry"".*?(?=\n\t\})")]   // or Replace = regex, with ${1} etc. in the snippet
```
If a pattern stops matching after a game update, the loader leaves the vanilla file and logs an error; the signature tests catch it first.

## Debugging and hot reload
- Set `WaitForDebugger = True` in `config\HOI4ModLoader.cfg`, start the game and attach a debugger to `hoi4.exe`: it stops before `Load()`.
- Rebuilding a plugin with the game running reverts every patch and loads the new DLLs. Plugin state (queues and such) resets. `HotReload = False` turns it off.

## How mimalloc is wired in
- `hoi4.exe` carries its own allocation functions (`malloc`, `calloc`, `realloc`, `free`, `_msize`; static CRT). Before the game's CRT starts, the loader redirects them to a few lines of native code in `version.dll` (`bootstrap\allocator.c`).
- Memory allocated before the switch is freed back to the CRT heap, so mixing is safe.
- The process-wide redirect (ucrtbase) is off (`MIMALLOC_DISABLE_REDIRECT=1`): it doesn't reach the game itself and would only change other DLLs.

## Multiplayer detection
A session whose server is a `CNetworkServer` (host) or `CProxyServer` (client) is multiplayer. Joining a lobby unloads `AffectsGameState` plugins; returning to single player loads them again.

## After a game update
1. Run `dotnet test tests/Signatures.Tests` to find broken patterns and GUI patches.
2. Fix the patterns and struct offsets.
3. Raise the plugins' `GameVersion`.
