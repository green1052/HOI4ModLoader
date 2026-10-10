# 개발 문서

## 구조
| 경로 | 역할 |
|---|---|
| `bootstrap/` | `version.dll` 프록시(C++). 게임 entry 직전에 .NET 10을 띄웁니다. mimalloc 연결 코드(`allocator.c`)도 여기 있습니다. |
| `HOI4ModLoader/` | 코어: 플러그인 로드, 패치 API, 시그니처 검색, 설정, 로그, 게임 구조체, 설정 창 모드 탭, 메인 메뉴 버전 표시. |
| `plugins/<이름>/` | 플러그인 하나 = 폴더 하나. |
| `plugins/<이름>/mod/` | (선택) 함께 배포하는 HOI4 모드(GUI). |
| `plugins/<이름>/localisation/` | (선택) 플러그인 이름, 설명, 설정 이름의 번역(`english.yml`, `korean.yml`). |
| `tests/Signatures.Tests/` | 모든 `[Signature]`를 설치된 `hoi4.exe`에 대고 검사합니다(게임 실행 없음). |

## 빌드
필요한 것: .NET 10 SDK, Visual Studio의 C++ 데스크톱 개발 도구(`bootstrap`용), Steam판 HOI4.

```
dotnet build
```
- 빌드할 때마다 게임 폴더(Steam 레지스트리로 찾음, `-p:HOI4Dir=`로 변경 가능)에 배포하고, UI 모드는 `문서\Paradox Interactive\Hearts of Iron IV\mod`에 배포합니다.
- 게임이 켜져 있으면 플러그인은 핫 리로드되고, 잠긴 코어 파일은 경고만 내고 넘어갑니다.
- `bootstrap`은 VS C++ 도구로 소스가 바뀌었을 때만 빌드됩니다.
- mimalloc은 빌드할 때 Microsoft 공식 릴리스(버전과 SHA-256 고정)를 받아 넣습니다. 저장소에는 들어 있지 않습니다.

릴리스용 폴더(`out\`, 저장소 기준). Release 빌드에는 .pdb(오류 줄 번호, 디버깅용)를 넣지 않습니다(`-p:DeploySymbols=true`로 넣기).
```
dotnet build -c Release -p:DeployDir=out -p:HOI4ModDir=out\mod
```

## 플러그인 작성
```csharp
public static class Signatures   // 테스트가 여기 [Signature]를 전부 검사합니다
{
    [Signature] public const string Foo = "48 89 5C 24 ? 57 48 83 EC 20";
    [Signature(Anchor = "Some engine string")] public const string Bar = "85 C0 0F 94 ?? E8"; // 그 문자열을 쓰는 함수 안에서만
}

[Plugin("com.green1052.myplugin", "My Plugin", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
[Dependency("com.green1052.other")]                // (선택) 먼저 로드할 플러그인
public class MyPlugin : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate int FooFn(nint a);
    private IHook<FooFn>? _foo;

    public override void Load()                    // throw하면 이 플러그인의 패치가 전부 되돌려집니다
    {
        var foo = Sig.Find(Signatures.Foo);        // 0개 또는 2개 이상 매치되면 throw
        var enabled = Config.Bind("Features", "Foo", true, "Description");
        Patches.Bytes(foo + 10, "74", "EB");       // 기대 바이트가 다르면 throw
        _foo = Patches.Hook<FooFn>(foo, a => _foo!.OriginalFunction(a) + 1);
        Patches.Asm(foo + 20, 10, "use64", "nop"); // [at, +10)을 asm으로 교체
        MainThread.Every(TimeSpan.FromSeconds(1), () => Log.Info("tick"));
    }
}
```
- `GameVersion`: 다른 게임 버전이면 로드하지 않습니다(구조체 오프셋이 바뀌었을 수 있음). 설정의 `IgnoreGameVersion`으로 무시할 수 있습니다.
- `AffectsGameState`: 게임 규칙을 바꾸는 플러그인. 멀티플레이에서는 꺼집니다.
- `MultiplayerOnly`: 멀티플레이에서만 로드합니다. 규칙 변경 플러그인 대신 바닐라 명령으로 동작하는 대체 플러그인에 씁니다.
- 패치와 `MainThread.Every`는 `Load()` 안에서 등록합니다. 그래야 리로드 때 함께 정리됩니다.
- 게임 명령은 `Commands.New`/`TryPost`로 보냅니다. UI가 만드는 것과 같은 바닐라 명령이라 멀티플레이에서도 동기화됩니다.
- 플러그인 ID는 `com.green1052.<이름>` 형식입니다.

### 번역
`localisation/<언어>.yml`(게임 형식, UTF-8 BOM)에 넣으면 DLL에 포함됩니다.

| 키 | 내용 |
|---|---|
| `<id>.name` | 플러그인 이름(영어는 `[Plugin]`의 이름) |
| `<id>.desc` | 플러그인 설명(모드 탭 툴팁) |
| `<id>.<설정 키>`, `<id>.<설정 키>.desc` | 설정 이름과 설명 |

코드에서는 `Loc.Get(key, fallback)`으로 읽습니다. 게임 언어에 없으면 영어, 그것도 없으면 `fallback`을 씁니다.

### UI 모드
`mod/descriptor.mod`와 게임 파일을 두면 빌드할 때 HOI4 모드로 배포됩니다. 바닐라 GUI 파일을 통째로 복사하지 않고, `GuiPatch` 항목으로 설치된 게임 파일에 조각만 끼워 넣어 생성합니다(`HOI4Mod.targets`).

## 디버깅과 핫 리로드
- `config\HOI4ModLoader.cfg`에서 `WaitForDebugger = True`로 두고 게임을 켠 뒤 디버거를 `hoi4.exe`에 붙이면 `Load()`부터 멈춥니다.
- 게임이 켜진 채로 플러그인을 다시 빌드하면 모든 패치를 되돌리고 새 DLL로 다시 로드합니다. 플러그인 상태(대기열 등)는 초기화됩니다. `HotReload = False`로 끕니다.

## mimalloc 연결 방식
- 게임 exe는 할당 함수(`malloc`, `calloc`, `realloc`, `free`, `_msize`)를 자체적으로 들고 있습니다(정적 CRT). 로더가 게임 CRT가 시작되기 전에 이 함수들을 `version.dll` 안의 짧은 네이티브 코드(`bootstrap\allocator.c`)로 돌립니다.
- mimalloc 이전에 할당된 메모리는 원래 CRT 힙으로 해제하므로 섞여도 안전합니다.
- 프로세스 전체 교체(ucrtbase)는 끕니다(`MIMALLOC_DISABLE_REDIRECT=1`). 게임 본체에는 영향이 없고 다른 DLL만 바뀌기 때문입니다.

## 멀티플레이 판정
세션의 서버가 `CNetworkServer`(호스트)나 `CProxyServer`(참가자)이면 멀티플레이입니다. 로비에 들어가면 `AffectsGameState` 플러그인을 내리고, 싱글로 돌아오면 다시 올립니다.

## 게임 업데이트 후
1. `dotnet test tests/Signatures.Tests`로 깨진 패턴을 찾습니다.
2. 패턴과 구조체 오프셋을 고칩니다.
3. 플러그인의 `GameVersion`을 올립니다.
