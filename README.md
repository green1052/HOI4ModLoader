# HOI4ModLoader

BepInEx처럼 `hoi4.exe`에 C# 플러그인을 로드합니다. 함수 훅은 [Reloaded.Hooks](https://github.com/Reloaded-Project/Reloaded.Hooks)를 씁니다.

## 설치
1. [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)을 설치합니다.
2. `version.dll`과 `HOI4ModLoader\` 폴더를 게임 폴더에 넣습니다.
3. HOI4 런처에서 `HOI4ModLoader UI`와 쓰려는 플러그인의 UI 모드를 켭니다.

```
version.dll
HOI4ModLoader\core\          로더, Reloaded.Hooks, mimalloc
HOI4ModLoader\plugins\       플러그인 DLL
HOI4ModLoader\config\        플러그인별 .cfg (처음 실행 시 생성)
HOI4ModLoader\LogOutput.log  실행마다 새로 씁니다
```

게임 설정 창의 **모드** 탭에서 플러그인을 켜고 끄고, 플러그인별 설정을 바꿀 수 있습니다. 메인 메뉴 버전 표시에 마우스를 올리면 로드된 플러그인 목록이 보입니다.

## mimalloc
게임이 메모리를 할당하고 해제할 때 Windows 기본 힙 대신 Microsoft의 [mimalloc](https://github.com/microsoft/mimalloc)을 씁니다.
- **끄기:** `config\HOI4ModLoader.cfg`에서 `[Performance] Mimalloc = False`로 두면 다음 실행부터 적용됩니다. 튕김이나 메모리 문제가 의심될 때 먼저 꺼 보세요.
- **파일:** `core\mimalloc.dll`, `core\mimalloc-redirect.dll`. 빌드할 때 Microsoft 공식 릴리스(버전과 SHA-256 고정)를 받아 넣으며, 저장소에는 들어 있지 않습니다.
- **동작:** 게임 exe는 할당 함수(`malloc`, `calloc`, `realloc`, `free`, `_msize`)를 자체적으로 들고 있습니다(정적 CRT). 로더가 게임 시작 전에 이 함수들을 `version.dll` 안의 짧은 네이티브 코드(`bootstrap\allocator.c`)로 돌립니다. mimalloc 이전에 할당된 메모리는 원래 힙으로 해제하므로 섞여도 안전합니다.
- 게임 exe 밖의 DLL은 그대로 둡니다(`MIMALLOC_DISABLE_REDIRECT=1`). 바꿔도 게임 본체에는 영향이 없고 위험만 늘기 때문입니다.

## 구조
| 경로 | 역할 |
|---|---|
| `bootstrap/` | `version.dll` 프록시(C++). 게임 entry 직전에 .NET 10을 띄웁니다. |
| `HOI4ModLoader/` | 코어: 플러그인 로드, 패치 API, 시그니처 검색, 설정, 로그, 게임 구조체, 설정 창 모드 탭. |
| `plugins/<이름>/` | 플러그인 하나 = 폴더 하나. |
| `plugins/<이름>/mod/` | (선택) 함께 배포하는 HOI4 모드(GUI). `문서\Paradox Interactive\Hearts of Iron IV\mod`에 배포됩니다. |
| `plugins/<이름>/localisation/` | (선택) 플러그인 이름, 설명, 설정 이름의 번역(`english.yml`, `korean.yml`). |
| `tests/Signatures.Tests/` | 모든 `[Signature]`를 설치된 `hoi4.exe`에 대고 검사합니다(게임 실행 없음). |

## 빌드
`dotnet build`. 빌드할 때마다 게임 폴더(Steam 레지스트리로 찾음, `HOI4Dir`로 변경 가능)에 배포됩니다. 게임이 켜져 있으면 플러그인은 핫 리로드되고, 잠긴 코어 파일은 경고만 내고 넘어갑니다. `bootstrap`은 VS C++ 도구로 소스가 바뀌었을 때만 빌드됩니다.

배포용 폴더(`out\`, 저장소 기준):
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
- `GameVersion`: 다른 게임 버전이면 로드하지 않습니다(구조체 오프셋이 바뀌었을 수 있음).
- `AffectsGameState`: 게임 규칙을 바꾸는 플러그인. 멀티플레이에서는 꺼집니다.
- `MultiplayerOnly`: 멀티플레이에서만 로드합니다. 규칙 변경 플러그인 대신 바닐라 명령으로 동작하는 대체 플러그인에 씁니다.
- 패치와 `MainThread.Every`는 `Load()` 안에서 등록합니다. 그래야 리로드 때 함께 정리됩니다.
- 번역: `localisation/<언어>.yml`(게임 형식)에 `<id>.name`, `<id>.desc`, `<id>.<설정 키>`, `<id>.<설정 키>.desc`를 넣고 `Loc.Get`으로 읽습니다. 게임 언어에 없으면 영어, 그것도 없으면 코드의 문자열을 씁니다.

## 디버깅과 핫 리로드
- `config\HOI4ModLoader.cfg`에서 `WaitForDebugger = True`로 두고 게임을 켠 뒤 디버거를 `hoi4.exe`에 붙이면 `Load()`부터 멈춥니다.
- 게임이 켜진 채로 플러그인을 다시 빌드하면 모든 패치를 되돌리고 새 DLL로 다시 로드합니다. 플러그인 상태(대기열 등)는 초기화됩니다. `HotReload = False`로 끕니다.

## 멀티플레이
다른 플레이어에게는 바닐라로 보입니다. 체크섬은 건드리지 않고, UI 모드는 `interface`만 바꿉니다.
- UI·명령 기반 플러그인(Queues 등)은 플레이어가 클릭으로 보내는 것과 같은 명령만 보내므로, 다른 플레이어에게 플러그인이 없어도 됩니다.
- `AffectsGameState` 플러그인은 멀티플레이 로비에 들어가면 꺼지고, 싱글로 돌아오면 다시 켜집니다.

## 게임 업데이트 후
`dotnet test tests/Signatures.Tests`로 깨진 패턴을 찾고, 오프셋을 확인한 뒤 `GameVersion`을 올립니다.

## 플러그인
### AirWingFieldMarshal
원수(집단군)에게 공군을 배속합니다. 원수 카드에 배속 슬롯이 생깁니다(`Air Wing Field Marshal UI` 모드). 싱글 전용입니다.
- 멀티플레이 대체(`.multiplayer`): 공군을 선택한 채 원수 카드를 우클릭하면 그 원수의 야전군들에 나눠 바닐라 명령으로 배속합니다.

### AirbaseCapacity
공군을 선택한 채 공항을 **Shift+우클릭**하면 남은 수용량만큼만 보냅니다(주둔 중인 비행기와 오는 중인 비행기를 뺌).

### AttachedCas
배속된 공군이 소속 사단의 전투뿐 아니라 임무 지역의 모든 전투에 CAS를 지원합니다. 싱글 전용입니다.

### AutoConstruction
건설 탭의 건물 버튼을 **Shift+클릭**하면 자동 건설 목표가 1 늘고, **Ctrl+Shift+클릭**하면 5, **Ctrl+클릭**하면 1 줄어듭니다. 버튼에 남은 개수가 `+N`으로 표시됩니다(`Auto Construction UI` 모드).
- 모자란 만큼을 건설 대기열 끝에 넣습니다. 주(또는 프로빈스)마다 1레벨씩 나눠 동시에 짓습니다.
- 위치 우선순위: 핵심주, 기반시설 레벨, 빈 건물 슬롯 순.

### AutoTrade
무역 화면에서 자원 아이콘을 **Shift+클릭**하면 자동 무역을 켜고 끕니다(`Auto` 표시). 자원이 모자라면 그만큼 수입합니다.
- 판매국 우선순위: 내 괴뢰국, 내 세력, 가장 많이 팔 수 있는 나라 순. 같으면 무역 영향력 순.
- 이미 맺은 수입은 줄이지 않습니다.

### ChecksumPatcher
체크섬이 바뀌어도 업적과 철인 모드를 유지합니다. 메모리에서만 패치합니다.

### NoTelemetry
게임 플레이 텔레메트리를 보내지 않습니다. 로그인, 멀티플레이, 크래시 리포트는 그대로입니다.

### Queues
**Shift+클릭**으로 예약하고, 다시 누르면 뺍니다. 예약한 항목은 이름 앞에 `N. `이 붙습니다.
- 연구, 국가 중점, 정보부 업그레이드: 슬롯이 비면 게임이 받아 주는 첫 항목을 시작합니다.
  - 정보부 업그레이드는 순번 칸에 번호가 보이고(`Queues UI` 모드), 여러 번 할 수 있는 것은 누를 때마다 한 단계씩 더 예약합니다(`1x3`).
  - 중점을 빼면 그 중점이 있어야 하는 예약 중점도 함께 빠집니다.
- 국민정신, 법, 디자이너, 고문, 결정: 정치력이 모이는 대로 하나씩 실행하고, 찬 슬롯은 교체합니다.

### RailwayGuns
- **원수 배정:** 열차포를 선택한 채 원수 카드를 우클릭합니다. 싱글에서는 원수에게 직접 배정해 모든 야전군 사단을 기준으로 자리를 잡고, 멀티에서는 야전군들에 나눠 배정합니다.
- **이동 개선(싱글 전용):** 전선에 붙은 후보지는 피하고(`FrontPenalty`), 바다 건너로 수송하지 않습니다(`NoSeaTransfer`).
