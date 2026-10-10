# HOI4ModLoader: HOI4 네이티브 C# 플러그인 로더

BepInEx처럼 `hoi4.exe`에 C# 플러그인을 로드합니다. 플레이어는 `version.dll`과 `hoi4mods\` 폴더를 게임 폴더에 넣기만 하면 됩니다. 함수 훅은 [Reloaded.Hooks](https://github.com/Reloaded-Project/Reloaded.Hooks)를 씁니다.

| 경로 | 역할 |
|---|---|
| `bootstrap/` | `version.dll` 프록시(C++). 유일한 네이티브 코드로, BepInEx의 Doorstop과 같은 역할입니다. 게임 entry 직전에 .NET 10을 띄웁니다. |
| `HOI4ModLoader/` | 코어. 플러그인 로드, 패치 API, 시그니처 검색, 설정, 로그, 게임 구조체, 멀티플레이 감지를 담습니다. |
| `plugins/<이름>/` | 플러그인 하나 = 폴더 하나. `.csproj` 하나와 `.cs`면 되고, 공통 설정은 `plugins/Directory.Build.props`에 있습니다. |
| `plugins/<이름>/mod/` | (선택) 함께 배포하는 일반 HOI4 모드(GUI 등). 빌드하면 `문서\Paradox Interactive\Hearts of Iron IV\mod`에 들어갑니다. |
| `tests/Signatures.Tests/` | 모든 `[Signature]`를 설치된 `hoi4.exe`에 대고 검사합니다(게임 실행 없음). |

게임 폴더 구성:
```
version.dll
hoi4mods\core\       로더 + Reloaded.Hooks
hoi4mods\plugins\    플러그인 DLL
hoi4mods\config\     플러그인별 .cfg (처음 실행 시 생성. Enabled, IgnoreGameVersion 등)
hoi4mods\hoimod.log  실행마다 새로 씁니다
hoi4mods\dotnet\     (선택) 동봉 런타임. 없으면 설치된 .NET 10을 씁니다
```

## 빌드
Rider에서 `HOI4ModLoader.slnx`를 빌드하거나 `dotnet build`를 실행합니다. 빌드할 때마다 게임 폴더(Steam 레지스트리로 자동 탐색, `HOI4Dir`로 변경 가능)에 바로 배포됩니다. 게임이 켜져 있으면 플러그인은 핫 리로드되고, 코어(`hoi4mods\core`)처럼 잠긴 파일은 경고만 내고 넘어갑니다.

`bootstrap`(C++)은 C# 빌드가 `vswhere`로 VS C++ 도구를 찾아 소스가 바뀌었을 때만 빌드합니다. C++ MSBuild와 SDK 스타일 MSBuild를 한 솔루션에서 함께 쓸 수 없어서 `.slnx`에는 넣지 않았습니다.

배포용 zip(런타임 동봉):
```
dotnet build -c Release -p:DeployDir=%CD%\out -p:HOI4ModDir=%CD%\out\mod -p:BundleRuntime=true
```

## 플러그인 작성
```csharp
public static class Signatures   // 테스트가 여기 [Signature]를 전부 검사합니다
{
    [Signature] public const string Foo = "48 89 5C 24 ? 57 48 83 EC 20";
    [Signature(Anchor = "Some engine string")] public const string Bar = "85 C0 0F 94 ?? E8"; // 그 문자열을 쓰는 함수 안에서만
}

[Plugin("com.green1052.myplugin", "My Plugin", "1.0.0", GameVersion = "1.19.3", AffectsGameState = true)]
[Dependency("com.green1052.other")]                         // (선택) 먼저 로드할 플러그인
public class MyPlugin : Plugin
{
    [Function(CallingConventions.Microsoft)] public delegate int FooFn(nint a);
    IHook<FooFn>? _foo;

    public override void Load()                    // throw하면 이 플러그인의 패치가 전부 되돌려집니다
    {
        var foo = Sig.Find(Signatures.Foo);        // 0개 또는 2개 이상 매치되면 throw
        var enabled = Config.Bind("Features", "Foo", true, "설명");
        Patches.Bytes(foo + 10, "74", "EB");       // 기대 바이트가 다르면 throw (게임 업데이트, 다른 플러그인)
        _foo = Patches.Hook<FooFn>(foo, a => _foo!.OriginalFunction(a) + 1);  // Prefix/Postfix. 예외가 나면 원본 실행
        Patches.Asm(foo + 20, 10, "use64", "nop");  // Transpiler: [at, +10)을 asm으로 교체
        Log.Info("hi");
    }
}
```
- `GameVersion`: 다른 게임 버전이면 로드하지 않습니다. 시그니처는 맞아도 구조체 오프셋이 바뀌었을 수 있기 때문입니다.
- `AffectsGameState`: 게임 규칙(시뮬레이션)을 바꾸는 플러그인입니다. 멀티플레이에서는 자동으로 꺼집니다(아래 참고).
- `MultiplayerOnly`: 멀티플레이에서만 로드합니다. 규칙을 바꾸는 플러그인 대신 바닐라 명령만으로 비슷하게 동작하는 대체 플러그인에 씁니다.
- `Patches`는 플러그인별로 패치를 기록합니다. 같은 바이트를 두 플러그인이 패치하면 거부하고, 로드에 실패하면 되돌립니다.

## 디버깅
`hoi4mods\config\HOI4ModLoader.cfg`에서 `WaitForDebugger = True`로 두고 게임을 켭니다. 그다음 Rider의 **Run → Attach to Process → hoi4.exe**로 붙이면 `Load()`부터 브레이크포인트가 걸립니다.

## 핫 리로드
게임이 켜진 채로 플러그인을 다시 빌드하면, 로더가 `hoi4mods\plugins`의 DLL이 바뀐 것을 감지해(1초 간격, 복사가 끝날 때까지 대기) 모든 플러그인을 다시 로드합니다.
- 기존 패치를 모두 되돌립니다. 훅이 덮어쓴 바이트까지 원래대로 돌려놓으므로 시그니처가 다시 맞습니다. 그다음 새 DLL로 `Load()`를 다시 실행합니다.
- 플러그인은 메모리로 읽어 로드하므로 DLL 파일이 잠기지 않습니다. 이전 버전은 메모리에 남습니다(재시작하면 정리됨).
- 플러그인 상태(예: 대기열)는 초기화됩니다.
- 끄려면 `hoi4mods\config\HOI4ModLoader.cfg`에서 `HotReload = False`로 둡니다.
- `MainThread.Every`와 패치는 `Load()` 안에서 등록해야 리로드 때 함께 정리됩니다.

## 멀티플레이
다른 플레이어에게는 바닐라로 보입니다. 게임 체크섬은 건드리지 않고, 함께 배포하는 UI 모드도 `interface`만 바꾸므로 체크섬에 들어가지 않습니다(`checksum_manifest.txt`는 `common`, `events`, `history`, `map`만 봅니다).
- **UI·명령 기반 플러그인**(Queues 등)은 멀티플레이에서도 그대로 동작합니다. 플레이어가 클릭으로 보내는 것과 같은 명령만 보내므로 다른 플레이어는 플러그인이 없어도 됩니다.
- **게임 규칙을 바꾸는 플러그인**(`AffectsGameState`)은 멀티플레이 로비에 들어가는 순간 꺼지고, 싱글로 돌아오면 다시 켜집니다. 멀티플레이는 모든 플레이어가 같은 시뮬레이션을 돌리므로, 혼자만 규칙이 다르면 디싱크가 납니다.
- 메인 메뉴 버전 표시 아래 줄에 `HOI4ModLoader 1.0.0 (N plugins)`가 붙습니다. 화면 글자만 바꾸므로 게임 버전 값(세이브, 멀티플레이 호환성)은 그대로입니다.
- 멀티플레이 판정: 세션의 서버가 `CNetworkServer`(호스트)나 `CProxyServer`(참가자)이면 멀티플레이입니다. 게임 텔레메트리가 싱글과 멀티를 구분하는 방식과 같습니다.

## 게임 업데이트 후
`dotnet test tests/Signatures.Tests`로 깨진 패턴을 확인합니다. 그다음 오프셋을 확인하고 `GameVersion`을 올립니다.

## 플러그인
### AirWingFieldMarshal (`com.green1052.airwingfieldmarshal`)
원수(집단군)에게도 공군을 배속합니다. 게임 규칙을 바꾸므로 싱글에서만 켜집니다.
- `GetAttachWingStatus`: 집단군이면 거부하던 분기를 제거합니다(바이트 패치).
- 매일 실행되는 배속 공군 임무 갱신: 집단군이면 소속 야전군들의 전선까지 합쳐 임무 지역을 계산합니다(게임 원래 순서를 유지해 멀티플레이 결정성 유지).
- CAS 대상 전투 필터: "사단의 야전군의 상위 집단군 == 배속 대상"도 허용합니다(asm 훅).
- 원수 카드 UI: 배속 슬롯 생성·갱신과, 공군을 선택한 상태의 우클릭 배속에서 원수 카드를 야전군 카드처럼 처리합니다.
- `mod/`: 빌드할 때 설치된 바닐라 `leadergroups.gui`에 슬롯(`gui/army_group_attached_units.gui`)만 삽입해 생성합니다. HOI4 런처에서 `Air Wing Field Marshal UI`를 켜야 합니다.

멀티플레이에서는 대신 `com.green1052.airwingfieldmarshal.multiplayer`(같은 DLL, `MultiplayerOnly`)가 켜집니다.
- 공군을 선택한 채 원수 카드를 우클릭하면, 공군을 그 원수의 야전군들에 나눠(선택 순서대로 돌아가며) 바닐라 배속 명령으로 붙입니다. 그다음 임무 지역과 기지 이동은 게임이 야전군 배속 공군처럼 처리합니다.
- 다른 플레이어에게는 평범한 야전군 배속으로 보이므로, 플러그인이 없어도 됩니다.
- 원수 카드의 배속 슬롯은 멀티플레이에서는 비어 있습니다. 배속된 공군은 각 야전군 카드에 보입니다.

확인한 내용(IDA):
- 집단군을 해체하면 배속이 해제됩니다.
- 세이브는 플러그인 없이도 로드됩니다.
- 야전군에 배속한 공군은 바닐라에서도 임무 지역(사단 위치와 전투 위치)에 가까운 공항으로 자동으로 옮깁니다(`ATTACHED_WINGS_ORDER_UPDATE_DAYS`마다, 여유 공간이 있는 가장 가까운 공항). 공항을 잃으면 가장 가까운 공항으로 즉시 옮겨집니다.

### AirbaseCapacity (`com.green1052.airbasecapacity`)
공군을 선택한 채 공항을 **Shift+우클릭**하면 들어갈 수 있는 만큼만 보냅니다. 남는 공간은 공항 수용량에서 주둔 중인 비행기와 오는 중인 비행기를 뺀 값이고, 실제 비행기 수(공항 툴팁의 숫자)로 셉니다. 들어가지 않는 공군은 제자리에 남습니다.
- 게임의 "선택한 공군을 공항으로 보내기" 함수가 쓰는 선택 목록만 줄이고, 이동 명령은 게임이 그대로 보냅니다. 멀티플레이에서도 동작합니다.
- 이미 그 공항에 있거나 그 공항으로 가는 중인 공군은 그대로 둡니다(바닐라 동작).

### AutoConstruction (`com.green1052.autoconstruction`)
건물마다 자동 건설 목표 개수(지어진 것과 대기 중인 것을 합친 총수)를 정합니다. 건설 탭의 건물 버튼을 **Shift+클릭**하면 목표가 1 늘고, **Ctrl+Shift+클릭**하면 5 늘고, **Ctrl+클릭**하면 1 줄어듭니다. 처음 누를 때는 지금 가진 수에서 시작하므로, Shift+클릭 한 번이면 "지금보다 하나 더"가 됩니다. 현재 수 이하로 내리면 꺼집니다.
- 목표 개수는 버튼 위에 `auto N`으로 표시됩니다. HOI4 런처에서 `Auto Construction UI`를 켜야 보입니다.
- 지어진 수와 대기 중인 수를 합쳐 목표보다 적으면, 모자란 만큼을 건설 대기열 끝에 넣습니다. 건설 탭과 같은 명령을 쓰므로 멀티플레이에서도 동작합니다.
- 위치는 지을 수 있는 주 중에서 고릅니다. 핵심주를 먼저, 그다음 기반시설 레벨이 높은 주, 그다음 빈 건물 슬롯이 많은 주 순입니다. 해군기지나 벙커처럼 프로빈스 단위 건물은 고른 주에서 지을 수 있는 첫 프로빈스에 짓습니다.
- 대기열 길이는 제한하지 않습니다. 목표를 크게 잡으면 그만큼 대기열이 길어집니다.

### AutoTrade (`com.green1052.autotrade`)
무역 화면에서 자원 아이콘을 **Shift+클릭**하면 그 자원의 자동 무역을 켜거나 끕니다. 켜진 자원은 아래에 `Auto`가 표시됩니다.
- 자원이 부족하면(무역 화면의 빨간 수치) 그만큼 수입합니다. 무역 화면과 같은 명령을 쓰므로 멀티플레이에서도 동작합니다.
- 판매국 우선순위: 내 괴뢰국(모든 자치 단계), 내 세력, 그다음 그 자원을 가장 많이 팔 수 있는 나라 순입니다. 같으면 무역 영향력이 높은 나라를 고릅니다.
- 수입량은 게임의 무역 창과 같은 방식으로 정합니다. 판매국이 내놓을 수 있는 양과 남은 민간 공장으로 살 수 있는 양을 넘지 않고, 공장 1개 단위로 맞춥니다.
- 자원이 남아도 이미 맺은 수입은 줄이지 않습니다. 수송선 부족은 확인하지 않습니다.

### AttachedCas (`com.green1052.attachedcas`)
장군에게 배속한 공군이 그 군 소속 사단이 싸우는 전투뿐 아니라, 임무 지역의 모든 전투에 CAS를 지원합니다(배속하지 않은 공군과 같음). 게임 규칙을 바꾸므로 싱글 전용입니다.
- CAS 대상 전투를 고르는 함수에서 "배속된 공군이면 소속 사단이 있는 전투만" 거르는 분기(`jz`)를 무조건 점프로 바꿉니다(바이트 패치).
- AirWingFieldMarshal과 함께 쓰면 원수에게 배속한 공군도 같은 규칙을 따릅니다.

### NoTelemetry (`com.green1052.notelemetry`)
게임 플레이 텔레메트리 이벤트를 Paradox로 보내지 않고 버립니다. 게임 옵션의 텔레메트리 설정과 상관없이 적용됩니다. 계정 로그인, 멀티플레이, 모드, 크래시 리포트는 건드리지 않습니다. PDX SDK가 직접 보내는 게임 시작/종료 이벤트는 막지 않습니다.

### ChecksumPatcher (`com.green1052.checksumpatcher`)
체크섬이 바뀌어도 업적과 철인 모드를 유지합니다. `"Active Mod Count: "`를 쓰는 함수 안의 `test eax,eax; sete`를 `xor eax,eax`로 바꿉니다. **메모리에서만** 패치하므로 exe는 그대로입니다.

### Queues (`com.green1052.queues`)
연구, 국가 중점, 정보부 업그레이드를 예약합니다. 기술, 중점, 업그레이드를 **Shift+클릭**하면 대기열에 넣거나 뺍니다. 연구 슬롯, 중점, 정보부가 비면 게임이 받아 주는 첫 항목을 UI와 같은 명령으로 시작하므로 멀티플레이에서도 동작합니다. 보낸 명령이 실제로 시작된 것을 확인한 뒤에 대기열에서 빼므로, 멀티플레이 지연이 있어도 같은 슬롯에 두 번 보내지 않습니다.
- 대기 순번을 표시합니다.
  - 연구: 기술 칸의 보너스 글자 자리에 `N.`을 붙입니다. 게임이 상태가 바뀔 때마다 그 글자를 다시 쓰므로 번호가 쌓이지 않습니다.
  - 중점: 중점 이름 앞에 `N. `을 붙이고, 중점 트리가 열려 있는 동안 매 프레임 맞춰 둡니다.
  - 정보부 업그레이드: 순번 칸(`gui/agency_queue_number.gui`)에 씁니다. HOI4 런처에서 `Queues UI`를 켜야 합니다.
  - 연구와 중점은 GUI 파일을 바꾸지 않으므로 Equestria at War처럼 트리 GUI를 덮어쓰는 모드와도 함께 쓸 수 있습니다.
- 다른 업그레이드가 진행 중이어도 버튼을 눌러 예약할 수 있게 둡니다.
- 여러 번 할 수 있는 정보부 업그레이드는 Shift+클릭할 때마다 한 단계씩 더 예약합니다(남은 단계까지). 순번 칸에는 `1x3`처럼 표시되고, 남은 단계를 다 예약한 뒤 한 번 더 누르면 모두 뺍니다.
- 중점을 대기열에서 빼면, 그 중점이 있어야만 할 수 있는 예약 중점도 함께 뺍니다. 선행 조건의 다른 선택지가 완료됐거나 진행 중이거나 예약돼 있으면 남겨 둡니다.

### PoliticalQueue (`com.green1052.politicalqueue`, Queues.dll)
정치력 대기열입니다. **Shift+클릭**으로 넣고 뺍니다.
- 대상: 국민정신, 법, 디자이너(각 슬롯의 선택 창), 고문, 이론가, 참모, 최고사령부(인물 선택 창), 결정.
- 게임이 받아 주는 첫 항목을 UI와 같은 명령으로 실행합니다. 비용 확인은 게임의 명령 검사가 하므로, 정치력이 모이는 대로 실행됩니다. 한 번에 하나씩 보내고 정치력이 바뀐 뒤 다음 항목을 봅니다.
- 이미 찬 슬롯(법, 참모 등)은 교체 명령으로 바꿉니다. 예약한 항목은 이름 앞에 `N. `이 붙습니다.
- 정치력이 모자라거나 조건이 안 돼서 게임이 막아 둔 버튼도 Shift+클릭은 되게 열어 둡니다. 그런 버튼을 그냥 클릭하면 아무 일도 일어나지 않습니다.
- 결정은 반복 가능한 것이 있으므로 한 번 실행하면 대기열에서 뺍니다.

### RailwayGuns (`com.green1052.railwayguns`, `com.green1052.railwaygunmovement`)
- **원수 배정:** 열차포를 선택한 채 원수 카드를 우클릭합니다. 다른 군에 배정된 열차포는 먼저 배정을 해제하고, 이미 그 군에 있는 열차포는 건너뜁니다(하나라도 섞이면 게임이 배정 전체를 거부합니다).
  - 싱글(`com.green1052.railwayguns`): 원수에게 직접 배정하고, 원수의 모든 야전군 사단을 기준으로 자리를 잡습니다. AirWingFieldMarshal이 원수 카드에 열차포 칸도 보여 줍니다.
  - 멀티(`com.green1052.railwayguns.multiplayer`): 그 원수의 야전군들에 선택 순서대로 나눠 바닐라 명령으로 배정합니다.
- **이동 개선 (싱글 전용):** 적이 점령한 프로빈스나 적 부대가 있는 프로빈스에 붙어 있는 후보지는 점수를 크게 깎습니다(`FrontPenalty`, 기본 500). 그래서 전선 바로 뒤에 자리 잡습니다. 바다 건너로 수송하지 않고 자기 철도망 안에서만 움직입니다(`NoSeaTransfer`). 게임 규칙을 바꾸므로 멀티플레이에서는 꺼집니다.
