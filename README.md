# HOI4ModLoader: HOI4 네이티브 C# 플러그인 로더

BepInEx처럼 `hoi4.exe`에 C# 플러그인을 로드합니다. 플레이어는 `version.dll`과 `hoi4mods\` 폴더를 게임 폴더에 넣기만 하면 됩니다. 함수 훅은 [Reloaded.Hooks](https://github.com/Reloaded-Project/Reloaded.Hooks)를 씁니다.

| 경로 | 역할 |
|---|---|
| `bootstrap/` | `version.dll` 프록시(C++). 유일한 네이티브 코드로, BepInEx의 Doorstop과 같은 역할입니다. 게임 entry 직전에 .NET 10을 띄웁니다. |
| `HOI4ModLoader/` | 코어. 플러그인 로드, 패치 API, 시그니처 검색, 설정, 로그, 체크섬, 게임 구조체를 담습니다. |
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
Rider에서 `HOI4ModLoader.slnx`를 빌드하거나 `dotnet build`를 실행합니다. 빌드할 때마다 게임 폴더(Steam 레지스트리로 자동 탐색, `HOI4Dir`로 변경 가능)에 바로 배포됩니다. 게임이 켜져 있으면 잠긴 파일은 경고만 내고 넘어갑니다.

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
- `AffectsGameState`: 게임 체크섬에 섞습니다. 멀티플레이 로비에서 플러그인 구성이 다르면 체크섬 불일치로 드러나므로 게임 중 디싱크를 막습니다.
- `Patches`는 플러그인별로 패치를 기록합니다. 같은 바이트를 두 플러그인이 패치하면 거부하고, 로드에 실패하면 되돌립니다.

## 디버깅
`hoi4mods\config\HOI4ModLoader.cfg`에서 `WaitForDebugger = True`로 두고 게임을 켭니다. 그다음 Rider의 **Run → Attach to Process → hoi4.exe**로 붙이면 `Load()`부터 브레이크포인트가 걸립니다.

## 게임 업데이트 후
`dotnet test tests/Signatures.Tests`로 깨진 패턴을 확인합니다. 그다음 오프셋을 확인하고 `GameVersion`을 올립니다.

## 플러그인
### AirWingFieldMarshal (`com.green1052.airwingfieldmarshal`)
원수(집단군)에게도 공군을 배속합니다.
- `GetAttachWingStatus`: 집단군이면 거부하던 분기를 제거합니다(바이트 패치).
- 매일 실행되는 배속 공군 임무 갱신: 집단군이면 소속 야전군들의 전선까지 합쳐 임무 지역을 계산합니다(게임 원래 순서를 유지해 멀티플레이 결정성 유지).
- CAS 대상 전투 필터: "사단의 야전군의 상위 집단군 == 배속 대상"도 허용합니다(asm 훅).
- 원수 카드 UI: 배속 슬롯 생성·갱신과, 공군을 선택한 상태의 우클릭 배속에서 원수 카드를 야전군 카드처럼 처리합니다.
- `mod/`: 빌드할 때 설치된 바닐라 `leadergroups.gui`에 슬롯(`gui/army_group_attached_units.gui`)만 삽입해 생성합니다. HOI4 런처에서 `Air Wing Field Marshal UI`를 켜야 합니다.

확인한 내용(IDA):
- 집단군을 해체하면 배속이 해제됩니다.
- 세이브는 플러그인 없이도 로드됩니다.

### ChecksumPatcher (`com.green1052.checksumpatcher`)
체크섬이 바뀌어도 업적과 철인 모드를 유지합니다. `"Active Mod Count: "`를 쓰는 함수 안의 `test eax,eax; sete`를 `xor eax,eax`로 바꿉니다. **메모리에서만** 패치하므로 exe는 그대로입니다.

### Queues (`com.green1052.queues`)
연구, 국가 중점, 정보부 업그레이드를 예약합니다. 기술, 중점, 업그레이드를 **Shift+클릭**하면 대기열에 넣거나 뺍니다. 연구 슬롯, 중점, 정보부가 비면 게임이 받아 주는 첫 항목을 UI와 같은 명령으로 시작하므로 멀티플레이에서도 동기화됩니다.
- 정보부 업그레이드에는 대기 순번을 표시합니다(`gui/agency_queue_number.gui`). HOI4 런처에서 `Queues UI`를 켜야 합니다.
- 다른 업그레이드가 진행 중이어도 버튼을 눌러 예약할 수 있게 둡니다.
