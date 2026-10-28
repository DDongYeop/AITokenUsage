## Phase 0 — 저장소

### Task 1: 저장소와 솔루션 골격

git 저장소, 공통 빌드 속성, 솔루션과 3개 프로젝트, 앱 매니페스트, 창 없이 뜨는 빈 WPF 앱, 스모크 테스트를 만든다. 첫 커밋에는 `docs/`(설계서·조사 보고서·계획)를 함께 넣는다.

**Files:**
- Create: `.gitignore`
- Create: `Directory.Build.props`
- Create: `AITokenUsage.sln` (`dotnet new sln --format sln` + `dotnet sln add`로 생성, 손으로 쓰지 않는다)
- Create: `src/AITokenUsage.Core/AITokenUsage.Core.csproj`
- Create: `src/AITokenUsage/AITokenUsage.csproj`
- Create: `src/AITokenUsage/app.manifest`
- Create: `src/AITokenUsage/App.xaml`
- Create: `src/AITokenUsage/App.xaml.cs`
- Create: `src/AITokenUsage/AssemblyInfo.cs` (WPF 템플릿이 만든 `ThemeInfo` 파일을 그대로 둔다)
- Create: `tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj`
- Test: `tests/AITokenUsage.Core.Tests/SmokeTests.cs`
- Delete (템플릿 산물): `src/AITokenUsage.Core/Class1.cs`, `src/AITokenUsage/MainWindow.xaml`, `src/AITokenUsage/MainWindow.xaml.cs`, `tests/AITokenUsage.Core.Tests/UnitTest1.cs`

**Interfaces:**
- Consumes: 없음 (첫 Task).
- Produces:
  - 솔루션 `AITokenUsage.sln` = `src/AITokenUsage.Core`(classlib, `net10.0-windows`, WPF 없음) + `src/AITokenUsage`(WinExe, WPF+WinForms) + `tests/AITokenUsage.Core.Tests`(xUnit).
  - `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, `AllowUnsafeBlocks=true`, `Version=0.1.0`, `Product=AITokenUsage`. 이후 Task의 `User-Agent: AITokenUsage/<ver>`는 `typeof(X).Assembly.GetName().Version`(= 0.1.0.0)에서 읽을 수 있다.
  - Core → Tests `InternalsVisibleTo` (Task 2의 `FileAppLog` 내부 생성자 테스트가 사용).
  - 테스트 csproj: `Fixtures\**`를 출력 폴더로 복사(`PreserveNewest`) — Task 2의 `Fixture.Read`가 의존.
  - `AITokenUsage.App : Application` (`ShutdownMode="OnExplicitShutdown"`, `OnStartup`은 아직 `base` 호출만). Task 14가 확장한다.

- [ ] **Step 1: git 저장소 확인**

저장소 루트(`d:\AITokenUsage\WorkSpace\AITokenUsage`)의 PowerShell에서 실행한다. 이미 `.git`이 있으면 새로 만들지 않는다.

```powershell
if (-not (Test-Path .git)) { git init -b main }
git status
```

Expected: `On branch main`, `No commits yet`, untracked에 `docs/`만 보인다.

- [ ] **Step 2: `.gitignore` 작성**

```gitignore
# Build output
bin/
obj/
publish/

# IDE / user state
.vs/
.idea/
*.user
*.suo

# Test output
TestResults/
```

- [ ] **Step 3: `Directory.Build.props` 작성**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Version>0.1.0</Version>
    <Product>AITokenUsage</Product>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: 템플릿으로 솔루션과 프로젝트 생성, 템플릿 파일 제거**

SDK 10의 `dotnet new sln`은 기본이 `.slnx`이므로 `--format sln`을 꼭 붙인다. 프로젝트 템플릿의 TFM은 다음 단계에서 csproj를 통째로 바꾸며 `net10.0-windows`로 고친다.

```powershell
dotnet new sln -n AITokenUsage --format sln
dotnet new classlib -n AITokenUsage.Core -o src/AITokenUsage.Core -f net10.0
dotnet new wpf -n AITokenUsage -o src/AITokenUsage -f net10.0
dotnet new xunit -n AITokenUsage.Core.Tests -o tests/AITokenUsage.Core.Tests -f net10.0
Remove-Item src/AITokenUsage.Core/Class1.cs, src/AITokenUsage/MainWindow.xaml, src/AITokenUsage/MainWindow.xaml.cs, tests/AITokenUsage.Core.Tests/UnitTest1.cs
dotnet sln AITokenUsage.sln add src/AITokenUsage.Core/AITokenUsage.Core.csproj src/AITokenUsage/AITokenUsage.csproj tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj
```

Expected: 각 템플릿마다 `The template "..." was created successfully.`, 마지막에 `Project ... added to the solution.` 3줄. `AITokenUsage.sln` 안에 `src`, `tests` 솔루션 폴더가 자동으로 생긴다.

- [ ] **Step 5: Core csproj를 아래 내용으로 교체**

`net10.0-windows`지만 WPF/WinForms를 켜지 않으므로 `Microsoft.NETCore.App`만 참조한다(Credential Manager·DPAPI는 P/Invoke로 처리 — Task 2).

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>AITokenUsage.Core</RootNamespace>
    <AssemblyName>AITokenUsage.Core</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="AITokenUsage.Core.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: 앱 csproj, 매니페스트, App.xaml(.cs) 작성**

앱 csproj를 아래 내용으로 교체한다. WinForms는 트레이 `NotifyIcon` 전용이며, WinForms 암시적 using(`System.Windows.Forms`, `System.Drawing`)은 WPF 타입과 충돌하므로 뺀다.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>AITokenUsage</RootNamespace>
    <AssemblyName>AITokenUsage</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <Using Remove="System.Windows.Forms" />
    <Using Remove="System.Drawing" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AITokenUsage.Core\AITokenUsage.Core.csproj" />
  </ItemGroup>

</Project>
```

`src/AITokenUsage/app.manifest` — 설계서 §4.7 블록 그대로에 `assemblyIdentity`와 `asInvoker`를 더했다.

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="AITokenUsage.app" />

  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>

  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application>
  </compatibility>

  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/AITokenUsage/App.xaml` — `StartupUri`를 없애고 `ShutdownMode="OnExplicitShutdown"`으로 둔다. 창이 하나도 없어도 프로세스가 살아 있다.

```xml
<Application x:Class="AITokenUsage.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources />
</Application>
```

`src/AITokenUsage/App.xaml.cs`:

```csharp
using System.Windows;

namespace AITokenUsage;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The composition root (AppHost, tray icon, taskbar host) is added in Task 14.
        // With ShutdownMode=OnExplicitShutdown the process keeps running without any window.
        base.OnStartup(e);
    }
}
```

`src/AITokenUsage/AssemblyInfo.cs`는 템플릿이 만든 `[assembly: ThemeInfo(...)]` 그대로 둔다.

- [ ] **Step 7: 테스트 csproj와 스모크 테스트 작성**

패키지 4개(`coverlet.collector` 6.0.4, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4)는 SDK 10.0.401의 xunit 템플릿이 넣는 버전 그대로이고, 허용 목록의 `Microsoft.Extensions.TimeProvider.Testing` 10.11.0만 추가한다. JSON/JSONL은 기본 `None` 항목이므로 `Include`가 아니라 `Update`로 복사 속성만 붙인다.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>AITokenUsage.Core.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.11.0" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AITokenUsage.Core\AITokenUsage.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`tests/AITokenUsage.Core.Tests/SmokeTests.cs` — Core 프로젝트 참조가 출력에 실리는지 확인한다(이 시점엔 Core에 타입이 없으므로 이름으로 로드한다).

```csharp
using System.Reflection;

namespace AITokenUsage.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Tests_RunOnWindows()
    {
        Assert.True(OperatingSystem.IsWindows());
    }

    [Fact]
    public void CoreAssembly_IsReferencedAndLoadable()
    {
        Assembly core = Assembly.Load("AITokenUsage.Core");

        Assert.Equal("AITokenUsage.Core", core.GetName().Name);
    }
}
```

- [ ] **Step 8: 빌드**

```powershell
dotnet build AITokenUsage.sln
```

Expected: `Build succeeded.`, `0 Warning(s)`, `0 Error(s)`. (`Microsoft.Extensions.TimeProvider.Testing` 10.11.0 복원 포함.)

- [ ] **Step 9: 테스트 실행**

```powershell
dotnet test AITokenUsage.sln
```

Expected: `Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2`.

- [ ] **Step 10: 수동 확인 — 창 없이 시작하고 계속 살아 있는지**

```powershell
$p = Start-Process -FilePath .\src\AITokenUsage\bin\Debug\net10.0-windows\AITokenUsage.exe -PassThru
Start-Sleep -Seconds 3
Get-Process -Id $p.Id | Select-Object Id, ProcessName, MainWindowHandle, HasExited
Stop-Process -Id $p.Id
Start-Sleep -Milliseconds 500
Get-Process -Name AITokenUsage -ErrorAction SilentlyContinue
```

Expected: 3초 뒤에도 프로세스가 있고 `MainWindowHandle`이 `0`, `HasExited`가 `False`다. 화면·작업 표시줄에 창이 나타나지 않는다. `Stop-Process` 뒤 마지막 명령은 아무것도 출력하지 않는다. (작업 관리자 > 세부 정보에서 `AITokenUsage.exe`를 찾아 끝내도 같다.)

- [ ] **Step 11: 커밋**

`git status`로 `bin/`, `obj/`가 목록에 없는지 먼저 확인한다.

```powershell
git add .gitignore Directory.Build.props AITokenUsage.sln src tests docs
git commit -m "chore(repo): 솔루션 골격과 빈 WPF 앱, 설계 문서 추가" -m "Core·앱·테스트 3개 프로젝트와 PerMonitorV2 매니페스트, 스모크 테스트를 만든다. 설계서·조사 보고서·구현 계획을 함께 올린다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Phase 1 — Core 기반

### Task 2: 모델, 추상화, 실제 구현, 테스트 가짜

공급자·폴링·UI가 공유하는 모델과, 외부 의존(파일·환경 변수·프로세스·자격 증명·비밀 저장·로그·HTTP)을 감싸는 추상화와 실제 구현, 그리고 이후 모든 Core 테스트가 쓰는 가짜 구현을 만든다.

설계 결정 (구현자가 다시 조사하지 않도록 적어 둔다):
- **DPAPI**: `System.Security.Cryptography.ProtectedData.dll`은 `Microsoft.WindowsDesktop.App` 공유 프레임워크에만 있고(`C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.x\`), WPF를 켜지 않은 Core가 참조하는 `Microsoft.NETCore.App`에는 없다. NuGet 금지 규칙 때문에 `DpapiSecretStore`는 `crypt32!CryptProtectData/CryptUnprotectData`를 `LibraryImport`로 직접 부르고, 출력 버퍼는 `LocalFree`로 해제한다. 범위는 `CurrentUser`(플래그 없음) + `CRYPTPROTECT_UI_FORBIDDEN`, 앱 고유 엔트로피를 쓴다.
- **Credential Manager**: `CREDENTIALW`의 문자열 필드는 `IntPtr`로 선언해 구조체를 blittable로 만들고 `Marshal.PtrToStringUni`로 읽는다(`FILETIME`은 `uint` 2개). `CredEnumerateW`의 출력은 `PCREDENTIALW*`(포인터 배열)이므로 `Marshal.ReadIntPtr(array, i * IntPtr.Size)`로 하나씩 꺼내고, 배열 전체를 `CredFree` 한 번으로 해제한다. `ERROR_NOT_FOUND`(1168)·`ERROR_NO_SUCH_LOGON_SESSION`(1312)은 "없음"(null/빈 목록), 그 밖의 오류는 `Win32Exception`.
- **공유 읽기**: 모든 열기는 `FileShare.ReadWrite | FileShare.Delete` (codex-usage-api.md §1.2 — `FileShare.Read`로 열면 Codex의 `auth.json` 저장이 실패한다). 읽고 바로 닫는다.
- **프로세스**: stdin은 BOM 없는 UTF-8(`Encoding.UTF8`을 쓰면 첫 줄 앞에 BOM이 붙어 JSON-RPC가 깨진다), 줄바꿈 `\n`. 파이프 읽기는 취소 토큰을 제대로 따르지 않으므로 읽기 자체는 취소하지 않고 `WaitAsync(ct)`로 기다린다.
- 토큰을 담을 수 있는 `StoredCredential`은 `ToString()`에서 `Secret`을 빼서 로그에 실수로 찍히지 않게 한다.

**Files:**
- Create: `src/AITokenUsage.Core/Models/ProviderStatus.cs`, `Severity.cs`, `UsageMeter.cs`, `UsageSnapshot.cs`, `ProviderView.cs`, `ProviderIds.cs`, `MeterOption.cs`
- Create: `src/AITokenUsage.Core/Abstractions/IFileSystem.cs`, `IEnvironment.cs`, `IProcessRunner.cs`, `ICredentialStore.cs`, `ISecretStore.cs`, `IAppLog.cs`, `ProviderContext.cs`
- Create: `src/AITokenUsage.Core/Abstractions/RealFileSystem.cs`, `RealEnvironment.cs`, `RealProcessRunner.cs`, `RealInteractiveProcess.cs`, `WindowsCredentialStore.cs`, `DpapiSecretStore.cs`, `FileAppLog.cs`, `NullAppLog.cs`, `HttpClientFactory.cs`
- Create: `src/AITokenUsage.Core/Providers/IUsageProvider.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fakes/FakeFileSystem.cs`, `FakeEnvironment.cs`, `FakeProcessRunner.cs`, `FakeInteractiveProcess.cs`, `FakeCredentialStore.cs`, `FakeSecretStore.cs`, `StubHttpHandler.cs`, `NullLog.cs`, `RecordingLog.cs`, `Fixture.cs`, `TestContexts.cs`, `TempDirectory.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/common/fixture-check.json`
- Test: `tests/AITokenUsage.Core.Tests/Models/ProviderViewTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Fakes/FakesSelfTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Abstractions/RealFileSystemTests.cs`, `DpapiSecretStoreTests.cs`, `WindowsCredentialStoreTests.cs`, `RealProcessRunnerTests.cs`, `RealEnvironmentTests.cs`, `FileAppLogTests.cs`, `HttpClientFactoryTests.cs`

**Interfaces:**
- Consumes: Task 1의 프로젝트 구조, `InternalsVisibleTo`, `Fixtures\**` 복사 규칙.
- Produces (공유 계약 그대로 + 아래 확장):
  - `AITokenUsage.Core.Models`: `ProviderStatus`, `Severity`, `UsageMeter`, `UsageSnapshot` (+ `static WithStatus(...)`), `ProviderView` (`IsStale`, `DisplayMeters`, `DisplayPlan`, `HasAnyData`), `ProviderIds` (`Claude`, `Codex`, `Gemini`, `Copilot`, `All`), `MeterOption`.
  - `AITokenUsage.Core.Abstractions`: `IFileSystem`, `IEnvironment`, `ProcessSpec`, `ProcessResult`, `IInteractiveProcess`, `IProcessRunner`, `StoredCredential`, `ICredentialStore`, `ISecretStore`, `IAppLog`, `ProviderContext`.
  - 실제 구현: `RealFileSystem()`, `RealEnvironment()`, `RealProcessRunner()`, `WindowsCredentialStore()`, `DpapiSecretStore(string directory)` (파일 `<directory>\<name>.bin`), `FileAppLog(string directory)` (+ `string FilePath { get; }`, `const long DefaultMaxBytes = 1048576`, `const int RetainedFiles = 3`, 테스트용 `internal FileAppLog(string directory, long maxBytes, TimeProvider time)`), `NullAppLog` (+ `static readonly NullAppLog Instance`), `HttpClientFactory.Create()` (+ `static readonly TimeSpan RequestTimeout` = 15초).
  - `AITokenUsage.Core.Providers.IUsageProvider`.
  - 테스트 가짜(`AITokenUsage.Core.Tests.Fakes`):
    - `FakeFileSystem`: `AddFile(string path, string content, DateTime? lastWriteUtc = null)`, `AddDirectory(string path)`, `Remove(string path)`(하위 전체), `EnqueueReads(string path, params string?[] contents)`(다음 `ReadAllTextShared` 결과를 순서대로 덮어씀, `null` = 그 1회는 파일 없음), `ThrowOnRead(string path, Exception ex)`, `int ReadCount(string path)`, `static readonly DateTime DefaultLastWriteUtc`. 모든 메서드는 체이닝용으로 `FakeFileSystem`을 돌려준다. 경로는 `/`→`\`, 끝 `\` 제거 후 OrdinalIgnoreCase 비교(GetFullPath는 하지 않는다). 없는 파일의 `GetLastWriteTimeUtc`는 1601-01-01(실제 API와 같음).
    - `FakeEnvironment(string userProfile = @"C:\Users\test", string? localAppData = null, string? roamingAppData = null)` — 기본 LocalAppData `C:\Users\test\AppData\Local`, RoamingAppData `C:\Users\test\AppData\Roaming`; `FakeEnvironment Set(string name, string? value)`(`""` = 정의됐지만 빈 값, `null` = 제거).
    - `FakeProcessRunner`: `OnRun(Func<ProcessSpec, ProcessResult>)`, `OnInteractive(Func<ProcessSpec, FakeInteractiveProcess>)`, `SetPath(string command, string? fullPath)`, `IReadOnlyList<ProcessSpec> Calls`. 핸들러가 없으면 실제 `Process.Start`처럼 `Win32Exception(2)`.
    - `FakeInteractiveProcess`: `EnqueueOutput(string line)`, `CompleteOutput()`(EOF), `OnWrite(Func<string, IEnumerable<string>> responder)`(쓴 줄마다 응답 줄을 출력 큐에 넣음), `IReadOnlyList<string> Written`, `bool IsDisposed`. 출력이 없으면 `ReadLineAsync`는 ct가 취소될 때까지 기다린다.
    - `FakeCredentialStore`: `FakeCredentialStore Add(string targetName, string? userName, string secret)`; 필터는 `"prefix*"` 또는 정확한 이름.
    - `FakeSecretStore`: `IReadOnlyDictionary<string, string> Items`.
    - `StubHttpHandler`: `StubHttpHandler When(Func<HttpRequestMessage,bool>, Func<HttpRequestMessage,HttpResponseMessage>)`(등록 순서대로 첫 매칭), `IReadOnlyList<HttpRequestMessage> Requests`(헤더·콘텐츠 헤더·본문을 복사한 사본 — `Headers.Authorization`, `Headers.GetValues(...)`, `Content.ReadAsStringAsync()` 그대로 사용 가능), `IReadOnlyList<string?> RequestBodies`, 도우미 `static Get(string absoluteUrl)`, `static Post(string absoluteUrl)`(매칭 술어), `static Json(HttpStatusCode, string json)`, `static Text(HttpStatusCode, string body)`. 미매칭이면 `InvalidOperationException`. 응답 함수가 `HttpRequestException`을 던지면 네트워크 오류를 흉내 낸다.
    - `NullLog`, `RecordingLog`(`IReadOnlyList<LogEntry> Entries`, `string AllText`), `record LogEntry(string Level, string Message, Exception? Exception)`.
    - `Fixture.Read(string relativePath)`, `Fixture.GetPath(string relativePath)` — `AppContext.BaseDirectory\Fixtures\...`, `/` 구분자 허용.
    - `TestContexts.Create(StubHttpHandler http, FakeTimeProvider time, FakeFileSystem? files = null, FakeEnvironment? env = null, FakeProcessRunner? proc = null, FakeCredentialStore? creds = null, FakeSecretStore? secrets = null, IAppLog? log = null)` — 생략한 의존은 빈 가짜, 로그 기본은 `NullLog`.
    - `TempDirectory : IDisposable` — `%TEMP%\AITokenUsage.Tests\<guid>`, `string Path`, `string Combine(params string[] parts)`.

- [ ] **Step 1: 실패하는 테스트 작성 — `ProviderView` 규칙**

`tests/AITokenUsage.Core.Tests/Models/ProviderViewTests.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Tests.Models;

public class ProviderViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot OkSnapshot(string plan, double session) =>
        new(ProviderIds.Claude, ProviderStatus.Ok, plan,
            [new UsageMeter("session", "5h", "현재 세션 (5시간)", session, Now.AddHours(3))],
            Now);

    [Fact]
    public void Ok_UsesCurrentMetersAndPlan_AndIsNotStale()
    {
        UsageSnapshot current = OkSnapshot("Max 5x", 14);
        var view = new ProviderView(ProviderIds.Claude, "Claude", current, LastGood: OkSnapshot("Pro", 99));

        Assert.False(view.IsStale);
        Assert.Same(current.Meters, view.DisplayMeters);
        Assert.Equal("Max 5x", view.DisplayPlan);
        Assert.True(view.HasAnyData);
    }

    [Theory]
    [InlineData(ProviderStatus.Error)]
    [InlineData(ProviderStatus.RateLimited)]
    [InlineData(ProviderStatus.WaitingForCliRefresh)]
    [InlineData(ProviderStatus.SignedOut)]
    public void NotOk_WithLastGood_ShowsLastGoodMetersAndPlan_AndIsStale(ProviderStatus status)
    {
        UsageSnapshot lastGood = OkSnapshot("Team · Max 5x", 50);
        var current = UsageSnapshot.WithStatus(ProviderIds.Claude, status, Now, "요청 제한");
        var view = new ProviderView(ProviderIds.Claude, "Claude", current, lastGood);

        Assert.True(view.IsStale);
        Assert.Same(lastGood.Meters, view.DisplayMeters);
        Assert.Equal("Team · Max 5x", view.DisplayPlan);
        Assert.True(view.HasAnyData);
    }

    [Fact]
    public void NotOk_WithoutLastGood_HasNoData_AndIsNotStale()
    {
        var current = UsageSnapshot.WithStatus(ProviderIds.Codex, ProviderStatus.NotInstalled, Now, "설치 안 됨");
        var view = new ProviderView(ProviderIds.Codex, "Codex", current, LastGood: null);

        Assert.False(view.IsStale);
        Assert.Empty(view.DisplayMeters);
        Assert.Null(view.DisplayPlan);
        Assert.False(view.HasAnyData);
    }

    [Fact]
    public void Ok_WithNoMeters_DoesNotFallBackToLastGood()
    {
        var current = new UsageSnapshot(ProviderIds.Gemini, ProviderStatus.Ok, "Free", [], Now);
        var view = new ProviderView(ProviderIds.Gemini, "Gemini", current, OkSnapshot("Old", 10));

        Assert.False(view.IsStale);
        Assert.Empty(view.DisplayMeters);
        Assert.False(view.HasAnyData);
        Assert.Equal("Free", view.DisplayPlan);
    }

    [Fact]
    public void WithStatus_BuildsEmptySnapshot()
    {
        var snapshot = UsageSnapshot.WithStatus(ProviderIds.Copilot, ProviderStatus.RateLimited, Now,
            "요청 제한 — 14:20 이후 재시도", TimeSpan.FromMinutes(5));

        Assert.Equal(ProviderIds.Copilot, snapshot.ProviderId);
        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Null(snapshot.PlanLabel);
        Assert.Empty(snapshot.Meters);
        Assert.Equal(Now, snapshot.FetchedAt);
        Assert.Equal("요청 제한 — 14:20 이후 재시도", snapshot.StatusMessage);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.RetryAfter);
        Assert.Null(snapshot.ExtraLine);
    }

    [Fact]
    public void ProviderIds_All_IsInCanonicalOrder()
    {
        Assert.Equal(["claude", "codex", "gemini", "copilot"], ProviderIds.All);
    }
}
```

- [ ] **Step 2: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ProviderViewTests"
```

Expected: 빌드 실패 — `error CS0234: The type or namespace name 'Models' does not exist in the namespace 'AITokenUsage.Core'`.

- [ ] **Step 3: 모델 구현**

`src/AITokenUsage.Core/Models/ProviderStatus.cs`:

```csharp
namespace AITokenUsage.Core.Models;

public enum ProviderStatus
{
    Ok,
    NotInstalled,
    SignedOut,
    WaitingForCliRefresh,
    Unsupported,
    RateLimited,
    Error,
}
```

`src/AITokenUsage.Core/Models/Severity.cs`:

```csharp
namespace AITokenUsage.Core.Models;

public enum Severity
{
    Normal,
    Warning,
    Critical,
}
```

`src/AITokenUsage.Core/Models/UsageMeter.cs`:

```csharp
namespace AITokenUsage.Core.Models;

/// <summary>One usage limit, for example the Claude 5-hour session window.</summary>
/// <param name="Key">Stable key referenced by settings: "session", "weekly", "weekly:fable", "pro", "monthly", ...</param>
/// <param name="ShortLabel">2-3 character widget label: "5h", "주", "Pro", "월".</param>
/// <param name="LongLabel">Detail card label: "현재 세션 (5시간)".</param>
/// <param name="UsedPercent">Used percentage (0-100). Clamped only when displayed.</param>
/// <param name="ResetsAt">When the window resets, if known.</param>
/// <param name="ServerSeverity">Severity reported by the provider, if any.</param>
/// <param name="TextOverride">Text-only line such as "↻ 11/1" or "12 크레딧 사용".</param>
public sealed record UsageMeter(
    string Key,
    string ShortLabel,
    string LongLabel,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    Severity? ServerSeverity = null,
    string? TextOverride = null);
```

`src/AITokenUsage.Core/Models/UsageSnapshot.cs`:

```csharp
namespace AITokenUsage.Core.Models;

/// <summary>Result of one provider fetch. StatusMessage is a Korean, user-facing message.</summary>
public sealed record UsageSnapshot(
    string ProviderId,
    ProviderStatus Status,
    string? PlanLabel,
    IReadOnlyList<UsageMeter> Meters,
    DateTimeOffset FetchedAt,
    string? StatusMessage = null,
    TimeSpan? RetryAfter = null,
    string? ExtraLine = null)
{
    public static UsageSnapshot WithStatus(string providerId, ProviderStatus status, DateTimeOffset now,
        string? message = null, TimeSpan? retryAfter = null)
        => new(providerId, status, null, Array.Empty<UsageMeter>(), now, message, retryAfter);
}
```

`src/AITokenUsage.Core/Models/ProviderView.cs`:

```csharp
namespace AITokenUsage.Core.Models;

/// <summary>What the UI renders: the latest status combined with the last successful snapshot.</summary>
public sealed record ProviderView(string ProviderId, string DisplayName, UsageSnapshot Current, UsageSnapshot? LastGood)
{
    public bool IsStale => Current.Status != ProviderStatus.Ok && LastGood is not null;

    public IReadOnlyList<UsageMeter> DisplayMeters =>
        Current.Status == ProviderStatus.Ok ? Current.Meters : (LastGood?.Meters ?? Current.Meters);

    public string? DisplayPlan => Current.PlanLabel ?? LastGood?.PlanLabel;

    public bool HasAnyData => DisplayMeters.Count > 0;
}
```

`src/AITokenUsage.Core/Models/ProviderIds.cs`:

```csharp
namespace AITokenUsage.Core.Models;

public static class ProviderIds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";
    public const string Copilot = "copilot";

    /// <summary>Canonical order: claude, codex, gemini, copilot.</summary>
    public static readonly IReadOnlyList<string> All = [Claude, Codex, Gemini, Copilot];
}
```

`src/AITokenUsage.Core/Models/MeterOption.cs`:

```csharp
namespace AITokenUsage.Core.Models;

/// <summary>One entry of a per-line meter drop-down in the settings window.</summary>
public sealed record MeterOption(string Key, string Label);
```

- [ ] **Step 4: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ProviderViewTests"
```

Expected: `Passed!  - Failed:     0, Passed:     9`.

- [ ] **Step 5: 추상화 인터페이스와 `IUsageProvider` 작성**

동작이 없는 계약이라 별도 테스트 없이 빌드로 확인한다(다음 단계의 가짜와 실제 구현 테스트가 사용한다).

`src/AITokenUsage.Core/Abstractions/IFileSystem.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>
    /// Reads the whole file with FileShare.ReadWrite|Delete and closes it immediately.
    /// Returns null when the file or its directory does not exist. Other IO failures throw IOException.
    /// </summary>
    string? ReadAllTextShared(string path);

    /// <summary>Lists files matching <paramref name="searchPattern"/>. Empty when the directory does not exist.</summary>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive);

    DateTime GetLastWriteTimeUtc(string path);

    /// <summary>
    /// Opens the file in shared mode and yields its lines from the last one to the first (JSONL tail scan).
    /// A trailing newline does not produce an empty first line. Yields nothing when the file does not exist.
    /// </summary>
    IEnumerable<string> ReadLinesReverse(string path);
}
```

`src/AITokenUsage.Core/Abstractions/IEnvironment.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

public interface IEnvironment
{
    /// <summary>Returns null when the variable is not defined and "" when it is defined but empty.</summary>
    string? GetVariable(string name);

    string UserProfile { get; }

    string LocalAppData { get; }

    string RoamingAppData { get; }
}
```

`src/AITokenUsage.Core/Abstractions/IProcessRunner.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

/// <param name="FileName">Executable path or name.</param>
/// <param name="Arguments">Each element is passed as one argument.</param>
/// <param name="EnvironmentOverrides">Variables set for the child process. A null value removes the variable.</param>
/// <param name="Timeout">
/// RunAsync: kill the process tree after this long (default 30 s).
/// StartInteractive: kill the process tree after this long (default: no limit).
/// </param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null,
    TimeSpan? Timeout = null);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface IInteractiveProcess : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Returns null at end of stream (stdout closed or the process exited).</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);

    IInteractiveProcess StartInteractive(ProcessSpec spec);

    /// <summary>Full path resolved through PATH and PATHEXT, or null.</summary>
    string? FindOnPath(string commandName);
}
```

`src/AITokenUsage.Core/Abstractions/ICredentialStore.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

/// <summary>A generic Windows Credential Manager entry. <see cref="Secret"/> is the UTF-8 decoded blob.</summary>
public sealed record StoredCredential(string TargetName, string? UserName, string Secret)
{
    // Records print every member by default; never print the secret.
    public override string ToString() => $"StoredCredential {{ TargetName = {TargetName}, UserName = {UserName} }}";
}

public interface ICredentialStore
{
    /// <summary>Reads one CRED_TYPE_GENERIC entry and decodes its blob as UTF-8. Returns null when it does not exist.</summary>
    string? ReadGeneric(string targetName);

    /// <summary>CredEnumerate(filter), for example "copilot-cli*". Returns an empty list when nothing matches.</summary>
    IReadOnlyList<StoredCredential> EnumerateGeneric(string filter);
}
```

`src/AITokenUsage.Core/Abstractions/ISecretStore.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

/// <summary>Secrets owned by this app (for example the Copilot device-flow token), encrypted at rest.</summary>
public interface ISecretStore
{
    void Save(string name, string secret);

    /// <summary>Returns null when the secret does not exist or can no longer be decrypted.</summary>
    string? Load(string name);

    void Delete(string name);
}
```

`src/AITokenUsage.Core/Abstractions/IAppLog.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// Application log. Messages are English and must never contain tokens,
/// Authorization headers or raw response bodies.
/// </summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message, Exception? ex = null);

    void Error(string message, Exception? ex = null);
}
```

`src/AITokenUsage.Core/Abstractions/ProviderContext.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

/// <summary>Every external dependency a usage provider may touch. Tests replace each one with a fake.</summary>
public sealed record ProviderContext(
    HttpClient Http,
    TimeProvider Time,
    IFileSystem Files,
    IEnvironment Env,
    IProcessRunner Processes,
    ICredentialStore Credentials,
    ISecretStore Secrets,
    IAppLog Log);
```

`src/AITokenUsage.Core/Providers/IUsageProvider.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Providers;

public interface IUsageProvider
{
    /// <summary>One of <see cref="ProviderIds"/>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>The shortest polling interval this provider tolerates.</summary>
    TimeSpan MinInterval { get; }

    /// <summary>Absolute paths (files or directories) whose changes trigger an immediate refresh.</summary>
    IReadOnlyList<string> WatchPaths { get; }

    /// <summary>Default entries of the settings drop-down. Dynamic entries come from snapshot meters.</summary>
    IReadOnlyList<MeterOption> StaticMeterOptions { get; }

    /// <summary>Returns a snapshot whose status describes expected failures instead of throwing.</summary>
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);
}
```

```powershell
dotnet build src/AITokenUsage.Core
```

Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: 테스트 가짜 작성**

`tests/AITokenUsage.Core.Tests/Fakes/FakeFileSystem.cs`:

```csharp
using System.IO.Enumeration;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>In-memory file system. Paths compare OrdinalIgnoreCase; '/' and '\' are equivalent.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    public static readonly DateTime DefaultLastWriteUtc = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime MissingFileTime = DateTime.FromFileTimeUtc(0);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Content, DateTime LastWriteUtc)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<string?>> _queuedReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _readErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _readCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or replaces a file and registers all of its parent directories.</summary>
    public FakeFileSystem AddFile(string path, string content, DateTime? lastWriteUtc = null)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            _files[key] = (content, lastWriteUtc ?? DefaultLastWriteUtc);
            AddAncestors(key);
        }

        return this;
    }

    public FakeFileSystem AddDirectory(string path)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            _directories.Add(key);
            AddAncestors(key);
        }

        return this;
    }

    /// <summary>Removes a file, or a directory together with everything below it.</summary>
    public FakeFileSystem Remove(string path)
    {
        string key = Normalize(path);
        string prefix = key + "\\";
        lock (_gate)
        {
            _files.Remove(key);
            _directories.Remove(key);
            foreach (string file in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _files.Remove(file);
            }

            _directories.RemoveWhere(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return this;
    }

    /// <summary>
    /// Queues results returned by the next ReadAllTextShared calls for this path, before the stored
    /// content is used again. A null entry simulates "file missing" for that one read.
    /// Use it to simulate a half-written file followed by the complete one.
    /// </summary>
    public FakeFileSystem EnqueueReads(string path, params string?[] contents)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            if (!_queuedReads.TryGetValue(key, out Queue<string?>? queue))
            {
                queue = new Queue<string?>();
                _queuedReads[key] = queue;
            }

            foreach (string? content in contents)
            {
                queue.Enqueue(content);
            }
        }

        return this;
    }

    /// <summary>Every ReadAllTextShared call for this path throws <paramref name="exception"/>.</summary>
    public FakeFileSystem ThrowOnRead(string path, Exception exception)
    {
        lock (_gate)
        {
            _readErrors[Normalize(path)] = exception;
        }

        return this;
    }

    public int ReadCount(string path)
    {
        lock (_gate)
        {
            return _readCounts.TryGetValue(Normalize(path), out int count) ? count : 0;
        }
    }

    public bool FileExists(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(Normalize(path));
        }
    }

    public bool DirectoryExists(string path)
    {
        lock (_gate)
        {
            return _directories.Contains(Normalize(path));
        }
    }

    public string? ReadAllTextShared(string path)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            _readCounts[key] = (_readCounts.TryGetValue(key, out int count) ? count : 0) + 1;

            if (_readErrors.TryGetValue(key, out Exception? error))
            {
                throw error;
            }

            if (_queuedReads.TryGetValue(key, out Queue<string?>? queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return _files.TryGetValue(key, out var file) ? file.Content : null;
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        string key = Normalize(directory);
        string prefix = key + "\\";
        lock (_gate)
        {
            if (!_directories.Contains(key))
            {
                return [];
            }

            return _files.Keys
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(p => recursive || p.IndexOf('\\', prefix.Length) < 0)
                .Where(p => FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(p), ignoreCase: true))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(Normalize(path), out var file) ? file.LastWriteUtc : MissingFileTime;
        }
    }

    public IEnumerable<string> ReadLinesReverse(string path)
    {
        string? content;
        lock (_gate)
        {
            content = _files.TryGetValue(Normalize(path), out var file) ? file.Content : null;
        }

        if (content is null)
        {
            return [];
        }

        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        lines.Reverse();
        return lines;
    }

    private void AddAncestors(string key)
    {
        string? parent = Path.GetDirectoryName(key);
        while (!string.IsNullOrEmpty(parent))
        {
            _directories.Add(Normalize(parent));
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
```

`tests/AITokenUsage.Core.Tests/Fakes/FakeEnvironment.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed class FakeEnvironment : IEnvironment
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public FakeEnvironment(string userProfile = @"C:\Users\test", string? localAppData = null, string? roamingAppData = null)
    {
        UserProfile = userProfile;
        LocalAppData = localAppData ?? Path.Combine(userProfile, "AppData", "Local");
        RoamingAppData = roamingAppData ?? Path.Combine(userProfile, "AppData", "Roaming");
    }

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    /// <summary>Defines a variable ("" = defined but empty). A null value removes it.</summary>
    public FakeEnvironment Set(string name, string? value)
    {
        if (value is null)
        {
            _variables.Remove(name);
        }
        else
        {
            _variables[name] = value;
        }

        return this;
    }

    public string? GetVariable(string name) => _variables.TryGetValue(name, out string? value) ? value : null;
}
```

`tests/AITokenUsage.Core.Tests/Fakes/FakeProcessRunner.cs`:

```csharp
using System.ComponentModel;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted process runner. Without a handler, RunAsync/StartInteractive throw
/// Win32Exception(2) exactly like Process.Start does for a missing executable.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessSpec> _calls = [];
    private readonly Dictionary<string, string?> _paths = new(StringComparer.OrdinalIgnoreCase);
    private Func<ProcessSpec, ProcessResult>? _onRun;
    private Func<ProcessSpec, FakeInteractiveProcess>? _onInteractive;

    /// <summary>Every RunAsync and StartInteractive call, in order.</summary>
    public IReadOnlyList<ProcessSpec> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public FakeProcessRunner OnRun(Func<ProcessSpec, ProcessResult> handler)
    {
        _onRun = handler;
        return this;
    }

    public FakeProcessRunner OnInteractive(Func<ProcessSpec, FakeInteractiveProcess> handler)
    {
        _onInteractive = handler;
        return this;
    }

    /// <summary>Makes FindOnPath(command) return <paramref name="fullPath"/> (null = not found).</summary>
    public FakeProcessRunner SetPath(string command, string? fullPath)
    {
        _paths[command] = fullPath;
        return this;
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(spec);
        if (_onRun is null)
        {
            throw new Win32Exception(2, $"FakeProcessRunner: no OnRun handler for '{spec.FileName}'.");
        }

        return Task.FromResult(_onRun(spec));
    }

    public IInteractiveProcess StartInteractive(ProcessSpec spec)
    {
        Record(spec);
        if (_onInteractive is null)
        {
            throw new Win32Exception(2, $"FakeProcessRunner: no OnInteractive handler for '{spec.FileName}'.");
        }

        return _onInteractive(spec);
    }

    public string? FindOnPath(string commandName) =>
        _paths.TryGetValue(commandName, out string? fullPath) ? fullPath : null;

    private void Record(ProcessSpec spec)
    {
        lock (_gate)
        {
            _calls.Add(spec);
        }
    }
}
```

`tests/AITokenUsage.Core.Tests/Fakes/FakeInteractiveProcess.cs`:

```csharp
using System.Threading.Channels;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted stdin/stdout peer. ReadLineAsync returns queued lines in order, returns null after
/// CompleteOutput (EOF), and otherwise waits until the token is cancelled (an unresponsive child).
/// </summary>
public sealed class FakeInteractiveProcess : IInteractiveProcess
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly List<string> _written = [];
    private Func<string, IEnumerable<string>>? _onWrite;

    /// <summary>Lines the code under test wrote to stdin, in order.</summary>
    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_written)
            {
                return _written.ToArray();
            }
        }
    }

    public bool IsDisposed { get; private set; }

    public FakeInteractiveProcess EnqueueOutput(string line)
    {
        _output.Writer.TryWrite(line);
        return this;
    }

    /// <summary>Signals end of stdout: pending and later reads return null once the queue is empty.</summary>
    public FakeInteractiveProcess CompleteOutput()
    {
        _output.Writer.TryComplete();
        return this;
    }

    /// <summary>For every written line, the responder's lines are queued as stdout output.</summary>
    public FakeInteractiveProcess OnWrite(Func<string, IEnumerable<string>> responder)
    {
        _onWrite = responder;
        return this;
    }

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        lock (_written)
        {
            _written.Add(line);
        }

        if (_onWrite is not null)
        {
            foreach (string response in _onWrite(line))
            {
                _output.Writer.TryWrite(response);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (await _output.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_output.Reader.TryRead(out string? line))
            {
                return line;
            }
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
```

`tests/AITokenUsage.Core.Tests/Fakes/FakeCredentialStore.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>In-memory Credential Manager. Filters follow CredEnumerate: "prefix*" or an exact name.</summary>
public sealed class FakeCredentialStore : ICredentialStore
{
    private readonly List<StoredCredential> _items = [];

    public FakeCredentialStore Add(string targetName, string? userName, string secret)
    {
        _items.RemoveAll(c => string.Equals(c.TargetName, targetName, StringComparison.OrdinalIgnoreCase));
        _items.Add(new StoredCredential(targetName, userName, secret));
        return this;
    }

    public string? ReadGeneric(string targetName) =>
        _items.FirstOrDefault(c => string.Equals(c.TargetName, targetName, StringComparison.OrdinalIgnoreCase))?.Secret;

    public IReadOnlyList<StoredCredential> EnumerateGeneric(string filter) =>
        _items.Where(c => Matches(filter, c.TargetName)).ToList();

    private static bool Matches(string filter, string targetName)
    {
        if (filter.EndsWith('*'))
        {
            return targetName.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(filter, targetName, StringComparison.OrdinalIgnoreCase);
    }
}
```

`tests/AITokenUsage.Core.Tests/Fakes/FakeSecretStore.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Items => _items;

    public void Save(string name, string secret) => _items[name] = secret;

    public string? Load(string name) => _items.TryGetValue(name, out string? secret) ? secret : null;

    public void Delete(string name) => _items.Remove(name);
}
```

`tests/AITokenUsage.Core.Tests/Fakes/StubHttpHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted HTTP handler. Rules are checked in registration order and the first match answers.
/// A request that matches no rule throws InvalidOperationException. Every request is recorded as a
/// detached copy (method, URI, request and content headers, body) so tests can inspect it after the
/// caller disposed the original.
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _rules = [];
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string?> _bodies = [];

    /// <summary>Recorded copies of every request, in order. Read bodies with RequestBodies or Content.ReadAsStringAsync().</summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Request bodies decoded as UTF-8, parallel to <see cref="Requests"/> (null when there was no content).</summary>
    public IReadOnlyList<string?> RequestBodies
    {
        get
        {
            lock (_gate)
            {
                return _bodies.ToArray();
            }
        }
    }

    public StubHttpHandler When(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        lock (_gate)
        {
            _rules.Add((match, respond));
        }

        return this;
    }

    /// <summary>Predicate: GET of exactly this absolute URL (query string included).</summary>
    public static Func<HttpRequestMessage, bool> Get(string absoluteUrl) =>
        request => request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == new Uri(absoluteUrl).AbsoluteUri;

    /// <summary>Predicate: POST of exactly this absolute URL (query string included).</summary>
    public static Func<HttpRequestMessage, bool> Post(string absoluteUrl) =>
        request => request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == new Uri(absoluteUrl).AbsoluteUri;

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        string? body = null;
        if (request.Content is not null)
        {
            byte[] bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(bytes);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            copy.Content = content;
        }

        Func<HttpRequestMessage, HttpResponseMessage>? respond;
        lock (_gate)
        {
            _requests.Add(copy);
            _bodies.Add(body);
            respond = _rules.FirstOrDefault(rule => rule.Match(request)).Respond;
        }

        if (respond is null)
        {
            throw new InvalidOperationException($"StubHttpHandler: no rule matched {request.Method} {request.RequestUri}.");
        }

        HttpResponseMessage response = respond(request);
        response.RequestMessage ??= request;
        return response;
    }
}
```

`tests/AITokenUsage.Core.Tests/Fakes/NullLog.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed class NullLog : IAppLog
{
    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? ex = null)
    {
    }

    public void Error(string message, Exception? ex = null)
    {
    }
}
```

`tests/AITokenUsage.Core.Tests/Fakes/RecordingLog.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed record LogEntry(string Level, string Message, Exception? Exception);

/// <summary>Captures log calls so tests can assert on warnings and on the absence of secrets.</summary>
public sealed class RecordingLog : IAppLog
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Every message and exception text joined together, for "must not contain the token" assertions.</summary>
    public string AllText => string.Join("\n", Entries.Select(e => $"{e.Level} {e.Message} {e.Exception}"));

    public void Info(string message) => Add("INFO", message, null);

    public void Warn(string message, Exception? ex = null) => Add("WARN", message, ex);

    public void Error(string message, Exception? ex = null) => Add("ERROR", message, ex);

    private void Add(string level, string message, Exception? ex)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(level, message, ex));
        }
    }
}
```

`tests/AITokenUsage.Core.Tests/Fakes/Fixture.cs`:

```csharp
namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Reads sample files copied to the test output folder (Fixtures/&lt;provider&gt;/...).</summary>
public static class Fixture
{
    /// <param name="relativePath">Path below Fixtures, e.g. "claude/usage-limits.json".</param>
    public static string Read(string relativePath) => File.ReadAllText(GetPath(relativePath));

    public static string GetPath(string relativePath) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath.Replace('/', Path.DirectorySeparatorChar));
}
```

`tests/AITokenUsage.Core.Tests/Fakes/TestContexts.cs`:

```csharp
using AITokenUsage.Core.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Fakes;

public static class TestContexts
{
    /// <summary>Builds a ProviderContext from fakes; every omitted dependency gets a fresh empty fake.</summary>
    public static ProviderContext Create(
        StubHttpHandler http,
        FakeTimeProvider time,
        FakeFileSystem? files = null,
        FakeEnvironment? env = null,
        FakeProcessRunner? proc = null,
        FakeCredentialStore? creds = null,
        FakeSecretStore? secrets = null,
        IAppLog? log = null)
        => new(
            new HttpClient(http, disposeHandler: false),
            time,
            files ?? new FakeFileSystem(),
            env ?? new FakeEnvironment(),
            proc ?? new FakeProcessRunner(),
            creds ?? new FakeCredentialStore(),
            secrets ?? new FakeSecretStore(),
            log ?? new NullLog());
}
```

`tests/AITokenUsage.Core.Tests/Fakes/TempDirectory.cs`:

```csharp
namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>A unique real directory under %TEMP%\AITokenUsage.Tests, deleted on Dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AITokenUsage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A test left a handle open; the folder lives in %TEMP% and is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/common/fixture-check.json` (Fixtures 복사 규칙 확인용):

```json
{
  "ok": true,
  "purpose": "Verifies that Fixtures/** is copied to the test output folder."
}
```

- [ ] **Step 7: 가짜 자체 검증 테스트 작성 후 실행**

이후 공급자 테스트가 기대는 동작(헤더·본문 기록, 첫 매칭 규칙, 큐 읽기, 무응답 대기, CredEnumerate 필터, 빈 값과 미정의 구분, Fixtures 복사)을 고정한다.

`tests/AITokenUsage.Core.Tests/Fakes/FakesSelfTests.cs`:

```csharp
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Guards the behaviour later provider tests rely on.</summary>
public class FakesSelfTests
{
    [Fact]
    public async Task StubHttpHandler_RecordsHeadersAndBody_AndAnswersFirstMatchingRule()
    {
        var stub = new StubHttpHandler()
            .When(StubHttpHandler.Post("https://example.test/api?x=1"), _ => StubHttpHandler.Json(HttpStatusCode.OK, """{"ok":true}"""))
            .When(_ => true, _ => StubHttpHandler.Text(HttpStatusCode.NotFound, "fallback"));
        using var http = new HttpClient(stub);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api?x=1")
        {
            Content = new StringContent("""{"project":"p-1"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", "test-access-token");
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HttpRequestMessage recorded = Assert.Single(stub.Requests);
        Assert.Equal("test-access-token", recorded.Headers.Authorization!.Parameter);
        Assert.Equal("oauth-2025-04-20", recorded.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("application/json", recorded.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"project":"p-1"}""", Assert.Single(stub.RequestBodies));
        Assert.Equal("""{"project":"p-1"}""", await recorded.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StubHttpHandler_ThrowsForUnmatchedRequest()
    {
        using var http = new HttpClient(new StubHttpHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://example.test/none"));
    }

    [Fact]
    public void FakeFileSystem_TracksDirectoriesPatternsQueuedReadsAndErrors()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl", "a1\na2\n", new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc))
            .AddFile("C:/Users/test/.codex/auth.json", "{}")
            .EnqueueReads(@"C:\Users\test\.codex\auth.json", "{\"tok", null);

        Assert.True(fs.DirectoryExists(@"c:\users\test\.codex\sessions\2026"));
        Assert.Equal(
            [@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"],
            fs.EnumerateFiles(@"C:\Users\test\.codex\sessions", "rollout-*.jsonl", recursive: true));
        Assert.Empty(fs.EnumerateFiles(@"C:\Users\test\.codex\sessions", "rollout-*.jsonl", recursive: false));
        Assert.Equal(["a2", "a1"], fs.ReadLinesReverse(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"));

        Assert.Equal("{\"tok", fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));
        Assert.Null(fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));
        Assert.Equal("{}", fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));
        Assert.Equal(3, fs.ReadCount(@"C:\Users\test\.codex\auth.json"));

        fs.ThrowOnRead(@"C:\Users\test\.codex\auth.json", new IOException("locked"));
        Assert.Throws<IOException>(() => fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));

        fs.Remove(@"C:\Users\test\.codex\sessions");
        Assert.False(fs.FileExists(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"));
        Assert.Equal(DateTime.FromFileTimeUtc(0), fs.GetLastWriteTimeUtc(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"));
    }

    [Fact]
    public async Task FakeProcessRunner_RecordsCalls_AndThrowsWithoutHandler()
    {
        var runner = new FakeProcessRunner().SetPath("gh", @"C:\Tools\gh.exe");

        Assert.Equal(@"C:\Tools\gh.exe", runner.FindOnPath("gh"));
        Assert.Null(runner.FindOnPath("codex"));
        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAsync(new ProcessSpec("gh", ["auth", "token"]), CancellationToken.None));

        runner.OnRun(spec => new ProcessResult(0, "gho_test-token\n", "", false));
        ProcessResult result = await runner.RunAsync(new ProcessSpec("gh", ["auth", "token"]), CancellationToken.None);

        Assert.Equal("gho_test-token\n", result.StdOut);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["auth", "token"], runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task FakeInteractiveProcess_RespondsToWrites_WaitsWhenSilent_AndSignalsEof()
    {
        var process = new FakeInteractiveProcess()
            .OnWrite(line => line.Contains("initialize") ? ["""{"id":1,"result":{}}"""] : []);

        await process.WriteLineAsync("""{"id":1,"method":"initialize"}""", CancellationToken.None);
        Assert.Equal("""{"id":1,"result":{}}""", await process.ReadLineAsync(CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.ReadLineAsync(cts.Token));

        process.EnqueueOutput("last").CompleteOutput();
        Assert.Equal("last", await process.ReadLineAsync(CancellationToken.None));
        Assert.Null(await process.ReadLineAsync(CancellationToken.None));
        Assert.Equal(["""{"id":1,"method":"initialize"}"""], process.Written);
    }

    [Fact]
    public void FakeCredentialStore_FiltersLikeCredEnumerate()
    {
        var creds = new FakeCredentialStore()
            .Add("copilot-cli/https://github.com:alice", "https://github.com:alice", "gho_test-a")
            .Add("gemini-cli-oauth/main-account", "main-account", "{}");

        Assert.Equal("{}", creds.ReadGeneric("gemini-cli-oauth/main-account"));
        Assert.Null(creds.ReadGeneric("gemini-cli-oauth"));
        Assert.Equal("gho_test-a", Assert.Single(creds.EnumerateGeneric("copilot-cli*")).Secret);
        Assert.Empty(creds.EnumerateGeneric("gh:*"));
    }

    [Fact]
    public void FakeEnvironment_DistinguishesUndefinedFromEmpty()
    {
        var env = new FakeEnvironment().Set("CLAUDE_CONFIG_DIR", "").Set("GH_TOKEN", "x").Set("GH_TOKEN", null);

        Assert.Equal("", env.GetVariable("CLAUDE_CONFIG_DIR"));
        Assert.Null(env.GetVariable("GH_TOKEN"));
        Assert.Equal(@"C:\Users\test", env.UserProfile);
        Assert.Equal(@"C:\Users\test\AppData\Local", env.LocalAppData);
        Assert.Equal(@"C:\Users\test\AppData\Roaming", env.RoamingAppData);
    }

    [Fact]
    public void TestContexts_FillsMissingFakes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));

        ProviderContext ctx = TestContexts.Create(new StubHttpHandler(), time);

        Assert.Same(time, ctx.Time);
        Assert.IsType<FakeFileSystem>(ctx.Files);
        Assert.IsType<FakeEnvironment>(ctx.Env);
        Assert.IsType<FakeProcessRunner>(ctx.Processes);
        Assert.IsType<FakeCredentialStore>(ctx.Credentials);
        Assert.IsType<FakeSecretStore>(ctx.Secrets);
        Assert.IsType<NullLog>(ctx.Log);
    }

    [Fact]
    public void Fixture_ReadsCopiedSampleFiles()
    {
        using JsonDocument doc = JsonDocument.Parse(Fixture.Read("common/fixture-check.json"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(File.Exists(Fixture.GetPath("common/fixture-check.json")));
    }
}
```

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~FakesSelfTests"
```

Expected: `Passed!  - Failed:     0, Passed:     9`.

- [ ] **Step 8: 실패하는 테스트 작성 — `RealFileSystem` (공유 읽기, 역순 줄 읽기)**

핵심 사례: 다른 핸들이 `FileShare.ReadWrite|Delete`로 쓰기 중일 때 기본 `File.ReadAllText`는 `IOException`이지만 공유 읽기는 성공한다. 역순 읽기는 64KB 청크 경계와 한글(멀티바이트)을 넘나들어도 `File.ReadAllLines`를 뒤집은 것과 같아야 하고, 열거 중에도 CLI가 이어 쓰거나 지울 수 있어야 한다.

`tests/AITokenUsage.Core.Tests/Abstractions/RealFileSystemTests.cs`:

```csharp
using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Abstractions;

public sealed class RealFileSystemTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RealFileSystem _fs = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadAllTextShared_ReadsWhileAnotherHandleHoldsTheFileOpenForWriting()
    {
        string path = _temp.Combine("auth.json");
        File.WriteAllText(path, """{"tokens":{"access_token":"test-access-token"}}""");

        // Same share mode Rust std (Codex) and Node use for their writers.
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        // The default .NET reader (FileShare.Read) is refused while a writer is open...
        Assert.Throws<IOException>(() => File.ReadAllText(path));

        // ...but the shared reader is not.
        string? text = _fs.ReadAllTextShared(path);

        Assert.Equal("""{"tokens":{"access_token":"test-access-token"}}""", text);
    }

    [Fact]
    public void ReadAllTextShared_DoesNotBlockWriterFromReplacingTheFile()
    {
        string path = _temp.Combine("creds.json");
        File.WriteAllText(path, "old");

        Assert.Equal("old", _fs.ReadAllTextShared(path));

        // The handle is closed right away, so an in-place rewrite succeeds.
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writer.Write("new"u8);
        }

        Assert.Equal("new", _fs.ReadAllTextShared(path));
    }

    [Fact]
    public void ReadAllTextShared_ReturnsNull_WhenFileOrDirectoryIsMissing()
    {
        Assert.Null(_fs.ReadAllTextShared(_temp.Combine("missing.json")));
        Assert.Null(_fs.ReadAllTextShared(_temp.Combine("no-dir", "missing.json")));
    }

    [Fact]
    public void ReadAllTextShared_StripsUtf8Bom()
    {
        string path = _temp.Combine("bom.json");
        File.WriteAllText(path, "{\"a\":1}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("{\"a\":1}", _fs.ReadAllTextShared(path));
    }

    [Fact]
    public void ExistsMethods_ReflectDisk()
    {
        string file = _temp.Combine("a.txt");
        File.WriteAllText(file, "x");

        Assert.True(_fs.FileExists(file));
        Assert.False(_fs.FileExists(_temp.Combine("b.txt")));
        Assert.True(_fs.DirectoryExists(_temp.Path));
        Assert.False(_fs.DirectoryExists(_temp.Combine("nope")));
    }

    [Fact]
    public void EnumerateFiles_HonoursPatternAndRecursion_AndToleratesMissingDirectory()
    {
        string sessions = _temp.Combine("sessions");
        Directory.CreateDirectory(Path.Combine(sessions, "2026", "10", "28"));
        File.WriteAllText(Path.Combine(sessions, "rollout-top.jsonl"), "");
        File.WriteAllText(Path.Combine(sessions, "2026", "10", "28", "rollout-deep.jsonl"), "");
        File.WriteAllText(Path.Combine(sessions, "2026", "10", "28", "other.txt"), "");

        var flat = _fs.EnumerateFiles(sessions, "rollout-*.jsonl", recursive: false).Select(f => Path.GetFileName(f)).ToList();
        var deep = _fs.EnumerateFiles(sessions, "rollout-*.jsonl", recursive: true).Select(f => Path.GetFileName(f)).Order().ToList();

        Assert.Equal(["rollout-top.jsonl"], flat);
        Assert.Equal(["rollout-deep.jsonl", "rollout-top.jsonl"], deep);
        Assert.Empty(_fs.EnumerateFiles(_temp.Combine("missing"), "*", recursive: true));
    }

    [Fact]
    public void GetLastWriteTimeUtc_ReturnsDiskValue()
    {
        string path = _temp.Combine("t.txt");
        File.WriteAllText(path, "x");
        var stamp = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(stamp, _fs.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("l1\nl2\nl3\n", new[] { "l3", "l2", "l1" })]
    [InlineData("l1\nl2\nl3", new[] { "l3", "l2", "l1" })]
    [InlineData("l1\r\nl2\r\n", new[] { "l2", "l1" })]
    [InlineData("a\n\nb\n", new[] { "b", "", "a" })]
    [InlineData("only", new[] { "only" })]
    [InlineData("\n", new[] { "" })]
    [InlineData("", new string[0])]
    public void ReadLinesReverse_MatchesReadAllLinesReversed(string content, string[] expected)
    {
        string path = _temp.Combine("small.jsonl");
        File.WriteAllText(path, content, new UTF8Encoding(false));

        Assert.Equal(expected, _fs.ReadLinesReverse(path).ToArray());
        Assert.Equal(File.ReadAllLines(path).Reverse().ToArray(), expected);
    }

    [Fact]
    public void ReadLinesReverse_HandlesLinesAcrossChunkBoundaries_WithMultiByteText()
    {
        string path = _temp.Combine("big.jsonl");
        var lines = Enumerable.Range(0, 6000)
            .Select(i => $"{{\"n\":{i},\"text\":\"사용량 한도 {i} — {new string('가', i % 50)}\"}}")
            .ToArray();
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Assert.True(new FileInfo(path).Length > 3 * 64 * 1024);

        string[] reversed = _fs.ReadLinesReverse(path).ToArray();

        Assert.Equal(lines.Reverse().ToArray(), reversed);
    }

    [Fact]
    public void ReadLinesReverse_SkipsUtf8Bom()
    {
        string path = _temp.Combine("bom.jsonl");
        File.WriteAllText(path, "first\nsecond\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(["second", "first"], _fs.ReadLinesReverse(path).ToArray());
    }

    [Fact]
    public void ReadLinesReverse_YieldsNothing_WhenFileIsMissing()
    {
        Assert.Empty(_fs.ReadLinesReverse(_temp.Combine("missing.jsonl")));
    }

    [Fact]
    public void ReadLinesReverse_LetsWriterAppendAndDeleteWhileEnumerating()
    {
        string path = _temp.Combine("live.jsonl");
        File.WriteAllText(path, "a\nb\n");

        using IEnumerator<string> lines = _fs.ReadLinesReverse(path).GetEnumerator();
        Assert.True(lines.MoveNext());
        Assert.Equal("b", lines.Current);

        // The CLI keeps appending while we scan; our handle must not get in the way.
        using (var appender = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            appender.Write("c\n"u8);
        }

        Assert.True(lines.MoveNext());
        Assert.Equal("a", lines.Current);

        File.Delete(path);
        Assert.False(lines.MoveNext());
    }
}
```

- [ ] **Step 9: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~RealFileSystemTests"
```

Expected: 빌드 실패 — `error CS0246: The type or namespace name 'RealFileSystem' could not be found`.

- [ ] **Step 10: `RealFileSystem` 구현**

역순 읽기는 파일 끝에서 64KB씩 거꾸로 읽어 `\n`(0x0A) 바이트로 자른다. UTF-8에서 0x0A는 멀티바이트 문자 안에 나오지 않으므로 바이트 단위로 잘라도 안전하다. 줄 끝 `\r`과 파일 앞 BOM은 제거한다.

`src/AITokenUsage.Core/Abstractions/RealFileSystem.cs`:

```csharp
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// File access that never blocks CLI writers: every open uses FileShare.ReadWrite | FileShare.Delete
/// (Codex rewrites auth.json in place; a FileShare.Read reader would make its save fail).
/// </summary>
public sealed class RealFileSystem : IFileSystem
{
    private const int ReverseChunkSize = 64 * 1024;
    private const FileShare SharedMode = FileShare.ReadWrite | FileShare.Delete;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? ReadAllTextShared(string path)
    {
        using FileStream? stream = OpenShared(path);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            MatchType = MatchType.Simple,
        };
        return Directory.EnumerateFiles(directory, searchPattern, options);
    }

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public IEnumerable<string> ReadLinesReverse(string path)
    {
        FileStream? stream = OpenShared(path);
        if (stream is null)
        {
            yield break;
        }

        using (stream)
        {
            long position = stream.Length;
            byte[] carry = [];
            bool isFirstSegment = true;

            while (position > 0)
            {
                int toRead = (int)Math.Min(ReverseChunkSize, position);
                position -= toRead;

                // chunk = [bytes read from disk][carry: start of a line whose newline was not found yet]
                byte[] chunk = new byte[toRead + carry.Length];
                stream.Position = position;
                stream.ReadExactly(chunk, 0, toRead);
                Buffer.BlockCopy(carry, 0, chunk, toRead, carry.Length);

                int lineEnd = chunk.Length;
                for (int i = toRead - 1; i >= 0; i--)
                {
                    if (chunk[i] != (byte)'\n')
                    {
                        continue;
                    }

                    string line = DecodeLine(chunk, i + 1, lineEnd - (i + 1));
                    if (!(isFirstSegment && line.Length == 0))
                    {
                        yield return line;
                    }

                    isFirstSegment = false;
                    lineEnd = i;
                }

                carry = chunk[..lineEnd];
            }

            int offset = HasUtf8Bom(carry) ? 3 : 0;
            if (carry.Length - offset > 0 || !isFirstSegment)
            {
                yield return DecodeLine(carry, offset, carry.Length - offset);
            }
        }
    }

    private static FileStream? OpenShared(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, SharedMode);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string DecodeLine(byte[] buffer, int start, int count)
    {
        if (count > 0 && buffer[start + count - 1] == (byte)'\r')
        {
            count--;
        }

        return Encoding.UTF8.GetString(buffer, start, count);
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
```

- [ ] **Step 11: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~RealFileSystemTests"
```

Expected: `Passed!  - Failed:     0, Passed:    18`.

- [ ] **Step 12: 실패하는 테스트 작성 — `DpapiSecretStore` 왕복**

`tests/AITokenUsage.Core.Tests/Abstractions/DpapiSecretStoreTests.cs`:

```csharp
using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Abstractions;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SaveThenLoad_RoundTrips_AndFileIsNotPlainText()
    {
        string directory = _temp.Combine("AITokenUsage");
        var store = new DpapiSecretStore(directory);

        store.Save("copilot-token", "ghu_test-token-한글");

        string file = Path.Combine(directory, "copilot-token.bin");
        Assert.True(File.Exists(file));
        byte[] raw = File.ReadAllBytes(file);
        Assert.DoesNotContain("ghu_test", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("ghu_test", Encoding.Unicode.GetString(raw));
        Assert.Equal("ghu_test-token-한글", store.Load("copilot-token"));
        Assert.Equal("ghu_test-token-한글", new DpapiSecretStore(directory).Load("copilot-token"));
    }

    [Fact]
    public void Save_Overwrites_AndLeavesNoTempFile()
    {
        var store = new DpapiSecretStore(_temp.Path);

        store.Save("copilot-token", "first");
        store.Save("copilot-token", "second");

        Assert.Equal("second", store.Load("copilot-token"));
        Assert.Equal(["copilot-token.bin"], Directory.GetFiles(_temp.Path).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void Load_ReturnsNull_WhenMissing()
    {
        Assert.Null(new DpapiSecretStore(_temp.Combine("does-not-exist")).Load("copilot-token"));
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileIsCorrupt()
    {
        File.WriteAllBytes(_temp.Combine("copilot-token.bin"), [1, 2, 3, 4, 5]);

        Assert.Null(new DpapiSecretStore(_temp.Path).Load("copilot-token"));
    }

    [Fact]
    public void Delete_RemovesTheFile_AndIsIdempotent()
    {
        var store = new DpapiSecretStore(_temp.Path);
        store.Save("copilot-token", "secret");

        store.Delete("copilot-token");
        store.Delete("copilot-token");

        Assert.Null(store.Load("copilot-token"));
        Assert.False(File.Exists(_temp.Combine("copilot-token.bin")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    public void InvalidNames_Throw(string name)
    {
        var store = new DpapiSecretStore(_temp.Path);

        Assert.ThrowsAny<ArgumentException>(() => store.Save(name, "x"));
    }
}
```

- [ ] **Step 13: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~DpapiSecretStoreTests"
```

Expected: 빌드 실패 — `error CS0246: The type or namespace name 'DpapiSecretStore' could not be found`.

- [ ] **Step 14: `DpapiSecretStore` 구현 (crypt32 P/Invoke)**

`src/AITokenUsage.Core/Abstractions/DpapiSecretStore.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// Stores secrets as "&lt;name&gt;.bin" files encrypted with DPAPI (CurrentUser scope).
/// Uses crypt32 directly because System.Security.Cryptography.ProtectedData is not part of
/// Microsoft.NETCore.App and third-party/extra NuGet packages are not allowed.
/// </summary>
public sealed partial class DpapiSecretStore : ISecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AITokenUsage.SecretStore.v1");

    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    public void Save(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        string path = GetPath(name);
        Directory.CreateDirectory(_directory);

        byte[] encrypted = Protect(Encoding.UTF8.GetBytes(secret));
        string tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, path, overwrite: true);
    }

    public string? Load(string name)
    {
        string path = GetPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] encrypted = File.ReadAllBytes(path);
        try
        {
            return Encoding.UTF8.GetString(Unprotect(encrypted));
        }
        catch (CryptographicException)
        {
            // Corrupted file or encrypted by another Windows user: treat as missing.
            return null;
        }
    }

    public void Delete(string name)
    {
        string path = GetPath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            throw new ArgumentException("Secret name must be a plain file name.", nameof(name));
        }

        return Path.Combine(_directory, name + ".bin");
    }

    private static unsafe byte[] Protect(byte[] plain)
    {
        fixed (byte* plainPtr = plain)
        fixed (byte* entropyPtr = Entropy)
        {
            var input = new Native.DATA_BLOB { cbData = (uint)plain.Length, pbData = (IntPtr)plainPtr };
            var entropy = new Native.DATA_BLOB { cbData = (uint)Entropy.Length, pbData = (IntPtr)entropyPtr };
            if (!Native.CryptProtectData(in input, null, in entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out Native.DATA_BLOB output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return CopyAndFree(output);
        }
    }

    private static unsafe byte[] Unprotect(byte[] encrypted)
    {
        fixed (byte* encryptedPtr = encrypted)
        fixed (byte* entropyPtr = Entropy)
        {
            var input = new Native.DATA_BLOB { cbData = (uint)encrypted.Length, pbData = (IntPtr)encryptedPtr };
            var entropy = new Native.DATA_BLOB { cbData = (uint)Entropy.Length, pbData = (IntPtr)entropyPtr };
            if (!Native.CryptUnprotectData(in input, IntPtr.Zero, in entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out Native.DATA_BLOB output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return CopyAndFree(output);
        }
    }

    private static byte[] CopyAndFree(Native.DATA_BLOB blob)
    {
        try
        {
            byte[] result = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            // CryptProtectData/CryptUnprotectData allocate the output with LocalAlloc.
            Native.LocalFree(blob.pbData);
        }
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DATA_BLOB
        {
            public uint cbData;
            public IntPtr pbData;
        }

        [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CryptProtectData(
            in DATA_BLOB dataIn, string? description, in DATA_BLOB optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, uint flags, out DATA_BLOB dataOut);

        [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CryptUnprotectData(
            in DATA_BLOB dataIn, IntPtr description, in DATA_BLOB optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, uint flags, out DATA_BLOB dataOut);

        [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
        internal static partial IntPtr LocalFree(IntPtr memory);
    }
}
```

- [ ] **Step 15: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~DpapiSecretStoreTests"
```

Expected: `Passed!  - Failed:     0, Passed:    10`.

- [ ] **Step 16: 실패하는 테스트 작성 — `WindowsCredentialStore`**

실제 Credential Manager에 `AITokenUsage.Tests/<guid>/...` 이름의 임시 GENERIC 항목을 `CredWriteW`로 만들고 읽은 뒤 `Dispose`에서 `CredDeleteW`로 지운다. CLI 자격 증명은 건드리지 않는다. blob은 keytar·go-keyring과 같은 UTF-8이다.

`tests/AITokenUsage.Core.Tests/Abstractions/WindowsCredentialStoreTests.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

/// <summary>
/// Round-trips through the real Credential Manager using throw-away targets under
/// "AITokenUsage.Tests/&lt;guid&gt;/". Every entry is deleted in Dispose.
/// </summary>
public sealed partial class WindowsCredentialStoreTests : IDisposable
{
    private readonly string _prefix = $"AITokenUsage.Tests/{Guid.NewGuid():N}/";
    private readonly List<string> _created = [];
    private readonly WindowsCredentialStore _store = new();

    public void Dispose()
    {
        foreach (string target in _created)
        {
            Native.CredDelete(target, 1, 0);
        }
    }

    [Fact]
    public void ReadGeneric_DecodesUtf8Blob()
    {
        string target = _prefix + "main-account";
        WriteGeneric(target, "main-account", """{"token":{"accessToken":"test-access-token","note":"한글"}}""");

        Assert.Equal("""{"token":{"accessToken":"test-access-token","note":"한글"}}""", _store.ReadGeneric(target));
    }

    [Fact]
    public void ReadGeneric_ReturnsNull_WhenMissing()
    {
        Assert.Null(_store.ReadGeneric(_prefix + "missing"));
    }

    [Fact]
    public void EnumerateGeneric_ReturnsEveryMatch_WithUserNames()
    {
        WriteGeneric(_prefix + "https://github.com:alice", "https://github.com:alice", "gho_test-alice");
        WriteGeneric(_prefix + "https://github.com:bob", "https://github.com:bob", "gho_test-bob");

        var found = _store.EnumerateGeneric(_prefix + "*").OrderBy(c => c.TargetName, StringComparer.Ordinal).ToList();

        Assert.Equal(2, found.Count);
        Assert.Equal(_prefix + "https://github.com:alice", found[0].TargetName);
        Assert.Equal("https://github.com:alice", found[0].UserName);
        Assert.Equal("gho_test-alice", found[0].Secret);
        Assert.Equal("gho_test-bob", found[1].Secret);
    }

    [Fact]
    public void EnumerateGeneric_ReturnsEmpty_WhenNothingMatches()
    {
        Assert.Empty(_store.EnumerateGeneric(_prefix + "nothing-here*"));
    }

    [Fact]
    public void StoredCredential_ToString_DoesNotLeakSecret()
    {
        var credential = new StoredCredential("copilot-cli/https://github.com:alice", "alice", "gho_test-secret");

        Assert.DoesNotContain("gho_test-secret", credential.ToString());
    }

    private unsafe void WriteGeneric(string target, string userName, string secret)
    {
        byte[] blob = Encoding.UTF8.GetBytes(secret);
        IntPtr targetPtr = Marshal.StringToHGlobalUni(target);
        IntPtr userPtr = Marshal.StringToHGlobalUni(userName);
        try
        {
            fixed (byte* blobPtr = blob)
            {
                var credential = new Native.CREDENTIALW
                {
                    Type = 1, // CRED_TYPE_GENERIC
                    TargetName = targetPtr,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (IntPtr)blobPtr,
                    Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                    UserName = userPtr,
                };

                if (!Native.CredWrite(in credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            _created.Add(target);
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct CREDENTIALW
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public uint LastWrittenLow;
            public uint LastWrittenHigh;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredWrite(in CREDENTIALW credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredDelete(string target, uint type, uint flags);
    }
}
```

- [ ] **Step 17: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~WindowsCredentialStoreTests"
```

Expected: 빌드 실패 — `error CS0246: The type or namespace name 'WindowsCredentialStore' could not be found`.

- [ ] **Step 18: `WindowsCredentialStore` 구현 (advapi32 P/Invoke)**

`src/AITokenUsage.Core/Abstractions/WindowsCredentialStore.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>Read-only access to generic entries of Windows Credential Manager (advapi32 CredReadW / CredEnumerateW).</summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    public string? ReadGeneric(string targetName)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetName);

        if (!Native.CredRead(targetName, CredTypeGeneric, 0, out IntPtr credentialPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorNotFound or ErrorNoSuchLogonSession)
            {
                return null;
            }

            throw new Win32Exception(error, "CredReadW failed.");
        }

        try
        {
            Native.CREDENTIALW credential = Marshal.PtrToStructure<Native.CREDENTIALW>(credentialPtr);
            return ReadBlob(credential);
        }
        finally
        {
            Native.CredFree(credentialPtr);
        }
    }

    public IReadOnlyList<StoredCredential> EnumerateGeneric(string filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(filter);

        if (!Native.CredEnumerate(filter, 0, out uint count, out IntPtr arrayPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorNotFound or ErrorNoSuchLogonSession)
            {
                return [];
            }

            throw new Win32Exception(error, "CredEnumerateW failed.");
        }

        try
        {
            var results = new List<StoredCredential>((int)count);
            for (int i = 0; i < count; i++)
            {
                // arrayPtr is PCREDENTIALW*: an array of pointers, one per credential.
                IntPtr itemPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                Native.CREDENTIALW credential = Marshal.PtrToStructure<Native.CREDENTIALW>(itemPtr);
                if (credential.Type != CredTypeGeneric)
                {
                    continue;
                }

                string targetName = Marshal.PtrToStringUni(credential.TargetName) ?? "";
                string? userName = Marshal.PtrToStringUni(credential.UserName);
                results.Add(new StoredCredential(targetName, userName, ReadBlob(credential)));
            }

            return results;
        }
        finally
        {
            // One CredFree releases the pointer array and every credential it points to.
            Native.CredFree(arrayPtr);
        }
    }

    private static string ReadBlob(in Native.CREDENTIALW credential)
    {
        if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
        {
            return "";
        }

        byte[] bytes = new byte[credential.CredentialBlobSize];
        Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct CREDENTIALW
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public uint LastWrittenLow;
            public uint LastWrittenHigh;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentials);

        [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
        internal static partial void CredFree(IntPtr buffer);
    }
}
```

- [ ] **Step 19: 통과 확인 + 정리 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~WindowsCredentialStoreTests"
cmdkey /list | Select-String "AITokenUsage.Tests"
```

Expected: `Passed!  - Failed:     0, Passed:     5`. `cmdkey` 줄은 아무것도 출력하지 않는다(임시 항목이 모두 지워짐).

- [ ] **Step 20: 실패하는 테스트 작성 — `RealProcessRunner`, `RealEnvironment`**

`cmd.exe`(stdout·stderr·종료 코드·환경 변수·타임아웃·취소)와 Windows PowerShell 5.1(한 줄 주고받기)을 실제로 실행한다. 둘 다 모든 Windows 11에 있다.

`tests/AITokenUsage.Core.Tests/Abstractions/RealProcessRunnerTests.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

public class RealProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private readonly RealProcessRunner _runner = new();

    [Fact]
    public async Task RunAsync_CapturesStdoutStderrAndExitCode()
    {
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "echo out-line& echo err-line 1>&2& exit /b 3"]);

        ProcessResult result = await _runner.RunAsync(spec, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out-line", result.StdOut.Trim());
        Assert.Equal("err-line", result.StdErr.Trim());
    }

    [Fact]
    public async Task RunAsync_AppliesAndRemovesEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("AITU_TEST_REMOVE", "inherited");
        try
        {
            var spec = new ProcessSpec(Cmd, ["/d", "/c", "echo [%AITU_TEST_SET%][%AITU_TEST_REMOVE%]"],
                new Dictionary<string, string?> { ["AITU_TEST_SET"] = "value-1", ["AITU_TEST_REMOVE"] = null });

            ProcessResult result = await _runner.RunAsync(spec, CancellationToken.None);

            // cmd leaves an undefined %VAR% untouched.
            Assert.Equal("[value-1][%AITU_TEST_REMOVE%]", result.StdOut.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITU_TEST_REMOVE", null);
        }
    }

    [Fact]
    public async Task RunAsync_KillsTreeAndReportsTimeout()
    {
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"], Timeout: TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        ProcessResult result = await _runner.RunAsync(spec, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenCallerCancels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(spec, cts.Token));
    }

    [Fact]
    public async Task StartInteractive_ExchangesLines()
    {
        var spec = new ProcessSpec(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "$l = [Console]::In.ReadLine(); [Console]::Out.WriteLine('echo:' + $l)"],
            Timeout: TimeSpan.FromSeconds(30));

        await using IInteractiveProcess process = _runner.StartInteractive(spec);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await process.WriteLineAsync("""{"id":1,"method":"initialize"}""", cts.Token);
        string? reply = await process.ReadLineAsync(cts.Token);
        string? end = await process.ReadLineAsync(cts.Token);

        Assert.Equal("""echo:{"id":1,"method":"initialize"}""", reply);
        Assert.Null(end);
    }

    [Fact]
    public async Task StartInteractive_ReadLineHonoursCancellation()
    {
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"]);
        await using IInteractiveProcess process = _runner.StartInteractive(spec);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.ReadLineAsync(cts.Token));
    }

    [Fact]
    public void FindOnPath_ResolvesWithPathExt()
    {
        string? found = _runner.FindOnPath("cmd");

        Assert.NotNull(found);
        Assert.Equal("cmd.exe", Path.GetFileName(found), ignoreCase: true);
    }

    [Fact]
    public void FindOnPath_ReturnsNull_ForUnknownCommand()
    {
        Assert.Null(_runner.FindOnPath("aitokenusage-no-such-command-xyz"));
    }
}
```

`tests/AITokenUsage.Core.Tests/Abstractions/RealEnvironmentTests.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

public class RealEnvironmentTests
{
    private readonly RealEnvironment _env = new();

    [Fact]
    public void Folders_MatchEnvironmentSpecialFolders()
    {
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _env.UserProfile);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _env.LocalAppData);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _env.RoamingAppData);
    }

    [Fact]
    public void GetVariable_ReturnsValueOrNull()
    {
        Environment.SetEnvironmentVariable("AITU_TEST_VAR", "value-1");
        try
        {
            Assert.Equal("value-1", _env.GetVariable("AITU_TEST_VAR"));
            Assert.Null(_env.GetVariable("AITU_TEST_VAR_THAT_DOES_NOT_EXIST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITU_TEST_VAR", null);
        }
    }
}
```

- [ ] **Step 21: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~RealProcessRunnerTests|FullyQualifiedName~RealEnvironmentTests"
```

Expected: 빌드 실패 — `error CS0246: The type or namespace name 'RealProcessRunner' could not be found`, `error CS0246: The type or namespace name 'RealEnvironment' could not be found`.

- [ ] **Step 22: `RealProcessRunner`, `RealInteractiveProcess`, `RealEnvironment` 구현**

`src/AITokenUsage.Core/Abstractions/RealProcessRunner.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>Runs CLI tools without a console window and with UTF-8 stdout/stderr.</summary>
public sealed class RealProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] DefaultPathExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(spec, redirectStandardInput: false) };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Timeout ?? DefaultTimeout);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            ct.ThrowIfCancellationRequested();
            timedOut = true;
        }

        string stdout = await DrainAsync(stdoutTask).ConfigureAwait(false);
        string stderr = await DrainAsync(stderrTask).ConfigureAwait(false);
        int exitCode = timedOut ? -1 : process.ExitCode;
        return new ProcessResult(exitCode, stdout, stderr, timedOut);
    }

    public IInteractiveProcess StartInteractive(ProcessSpec spec)
    {
        var process = new Process { StartInfo = CreateStartInfo(spec, redirectStandardInput: true) };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new RealInteractiveProcess(process, spec.Timeout);
    }

    public string? FindOnPath(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(commandName))
        {
            return File.Exists(commandName) ? commandName : null;
        }

        string[] extensions = ReadPathExtensions();
        bool hasExtension = Path.HasExtension(commandName);
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (string rawDirectory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = rawDirectory.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            if (hasExtension)
            {
                string exact = Path.Combine(directory, commandName);
                if (File.Exists(exact))
                {
                    return exact;
                }

                continue;
            }

            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, commandName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (Win32Exception)
        {
            // Access denied or the process is exiting.
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessSpec spec, bool redirectStandardInput)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        if (redirectStandardInput)
        {
            // Without this the child would receive a UTF-8 BOM in front of the first line.
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        foreach (string argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.EnvironmentOverrides is not null)
        {
            foreach ((string name, string? value) in spec.EnvironmentOverrides)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        return startInfo;
    }

    private static string[] ReadPathExtensions()
    {
        string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return DefaultPathExtensions;
        }

        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<string> DrainAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (ObjectDisposedException)
        {
            return "";
        }
    }
}
```

`src/AITokenUsage.Core/Abstractions/RealInteractiveProcess.cs`:

```csharp
using System.Diagnostics;

namespace AITokenUsage.Core.Abstractions;

/// <summary>A running child process driven line by line over stdin/stdout (JSON-RPC style).</summary>
internal sealed class RealInteractiveProcess : IInteractiveProcess
{
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stderrDrain;
    private Task<string?>? _pendingRead;
    private bool _disposed;

    public RealInteractiveProcess(Process process, TimeSpan? timeout)
    {
        _process = process;
        _process.StandardInput.NewLine = "\n";
        _process.StandardInput.AutoFlush = true;

        // Keep stderr flowing so the child never blocks on a full pipe.
        _stderrDrain = _process.StandardError.ReadToEndAsync(CancellationToken.None);

        _lifetime.Token.Register(() => RealProcessRunner.KillTree(_process));
        if (timeout is { } limit)
        {
            _lifetime.CancelAfter(limit);
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pipe reads do not observe cancellation reliably, so the read itself is never cancelled.
        // A read abandoned by a cancelled caller is reused by the next call instead of being lost.
        _pendingRead ??= _process.StandardOutput.ReadLineAsync(CancellationToken.None).AsTask();
        string? line = await _pendingRead.WaitAsync(ct).ConfigureAwait(false);
        _pendingRead = null;
        return line;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The pipe is already broken because the child exited.
        }

        try
        {
            using var grace = new CancellationTokenSource(ExitGracePeriod);
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RealProcessRunner.KillTree(_process);
        }

        try
        {
            await _stderrDrain.WaitAsync(ExitGracePeriod).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
            // Nothing useful to do with a stuck stderr pipe.
        }

        _lifetime.Dispose();
        _process.Dispose();
    }
}
```

`src/AITokenUsage.Core/Abstractions/RealEnvironment.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

public sealed class RealEnvironment : IEnvironment
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
}
```

- [ ] **Step 23: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~RealProcessRunnerTests|FullyQualifiedName~RealEnvironmentTests"
```

Expected: `Passed!  - Failed:     0, Passed:    10` (PowerShell 기동 때문에 수 초 걸릴 수 있다).

- [ ] **Step 24: 실패하는 테스트 작성 — `FileAppLog`, `NullAppLog`, `HttpClientFactory`**

`tests/AITokenUsage.Core.Tests/Abstractions/FileAppLogTests.cs`:

```csharp
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Abstractions;

public sealed class FileAppLogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Writes_LevelMessageAndException()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 3, 4, 5, TimeSpan.Zero));
        string logs = _temp.Combine("logs");
        var log = new FileAppLog(logs, FileAppLog.DefaultMaxBytes, time);

        log.Info("started");
        log.Warn("claude fetch failed", new HttpRequestException("connection reset"));
        log.Error("boom");

        string text = File.ReadAllText(Path.Combine(logs, "app.log"));
        Assert.Contains("2026-10-28 03:04:05.000 +00:00 [INFO] started", text);
        Assert.Contains("[WARN] claude fetch failed", text);
        Assert.Contains("System.Net.Http.HttpRequestException: connection reset", text);
        Assert.Contains("[ERROR] boom", text);
    }

    [Fact]
    public void Rotates_AndKeepsAtMostThreeFiles()
    {
        var log = new FileAppLog(_temp.Path, maxBytes: 200, TimeProvider.System);

        for (int i = 0; i < 40; i++)
        {
            log.Info($"message number {i:D3} with some padding to grow the file");
        }

        string[] files = Directory.GetFiles(_temp.Path).Select(f => Path.GetFileName(f)).Order().ToArray();
        Assert.Equal(["app.1.log", "app.2.log", "app.log"], files);
        Assert.Contains("message number 039", File.ReadAllText(_temp.Combine("app.log")));
        Assert.All(files, f => Assert.True(new FileInfo(_temp.Combine(f)).Length < 400));
    }

    [Fact]
    public void PublicConstructor_UsesAppLogFileName()
    {
        var log = new FileAppLog(_temp.Path);

        log.Info("hello");

        Assert.Equal(_temp.Combine("app.log"), log.FilePath);
        Assert.True(File.Exists(log.FilePath));
    }

    [Fact]
    public void NullAppLog_IgnoresEverything()
    {
        NullAppLog.Instance.Info("x");
        NullAppLog.Instance.Warn("x", new InvalidOperationException());
        NullAppLog.Instance.Error("x");
    }
}
```

`tests/AITokenUsage.Core.Tests/Abstractions/HttpClientFactoryTests.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

public class HttpClientFactoryTests
{
    [Fact]
    public void Create_Uses15SecondTimeout()
    {
        using HttpClient client = HttpClientFactory.Create();

        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), HttpClientFactory.RequestTimeout);
    }
}
```

- [ ] **Step 25: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~FileAppLogTests|FullyQualifiedName~HttpClientFactoryTests"
```

Expected: 빌드 실패 — `error CS0246: The type or namespace name 'FileAppLog' could not be found`와 `error CS0103: The name 'NullAppLog' does not exist in the current context`, `error CS0103: The name 'HttpClientFactory' does not exist in the current context`.

- [ ] **Step 26: `FileAppLog`, `NullAppLog`, `HttpClientFactory` 구현**

`src/AITokenUsage.Core/Abstractions/FileAppLog.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// Thread-safe text log at &lt;directory&gt;\app.log. When app.log reaches 1 MB it is rotated to
/// app.1.log (and app.1.log to app.2.log), so at most 3 files exist. Logging never throws.
/// </summary>
public sealed class FileAppLog : IAppLog
{
    public const long DefaultMaxBytes = 1024 * 1024;
    public const int RetainedFiles = 3;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;

    public FileAppLog(string directory)
        : this(directory, DefaultMaxBytes, TimeProvider.System)
    {
    }

    internal FileAppLog(string directory, long maxBytes, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _directory = directory;
        _maxBytes = maxBytes;
        _time = time;
    }

    public string FilePath => Path.Combine(_directory, "app.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        var entry = new StringBuilder();
        entry.Append(_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        entry.Append(" [").Append(level).Append("] ").Append(message);
        if (ex is not null)
        {
            entry.AppendLine();
            entry.Append("    ").Append(ex.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
        }

        entry.AppendLine();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                RotateIfNeeded();
                File.AppendAllText(FilePath, entry.ToString(), Utf8NoBom);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }
    }

    private void RotateIfNeeded()
    {
        var current = new FileInfo(FilePath);
        if (!current.Exists || current.Length < _maxBytes)
        {
            return;
        }

        // app.1.log -> app.2.log, then app.log -> app.1.log. The oldest archive is overwritten.
        for (int index = RetainedFiles - 1; index >= 1; index--)
        {
            string source = index == 1 ? FilePath : ArchivePath(index - 1);
            if (File.Exists(source))
            {
                File.Move(source, ArchivePath(index), overwrite: true);
            }
        }
    }

    private string ArchivePath(int index) => Path.Combine(_directory, $"app.{index}.log");
}
```

`src/AITokenUsage.Core/Abstractions/NullAppLog.cs`:

```csharp
namespace AITokenUsage.Core.Abstractions;

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? ex = null)
    {
    }

    public void Error(string message, Exception? ex = null)
    {
    }
}
```

`src/AITokenUsage.Core/Abstractions/HttpClientFactory.cs`:

```csharp
using System.Net;

namespace AITokenUsage.Core.Abstractions;

public static class HttpClientFactory
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>One shared client for the whole app: no redirects, 15 s timeout, connections recycled every 10 minutes.</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
    }
}
```

- [ ] **Step 27: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~FileAppLogTests|FullyQualifiedName~HttpClientFactoryTests"
```

Expected: `Passed!  - Failed:     0, Passed:     5`.

- [ ] **Step 28: 전체 빌드·테스트**

```powershell
dotnet build AITokenUsage.sln
dotnet test AITokenUsage.sln
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed:     0, Passed:    68`.

- [ ] **Step 29: 커밋**

```powershell
git add src/AITokenUsage.Core tests/AITokenUsage.Core.Tests
git commit -m "feat(core): 모델·추상화·실제 구현과 테스트 가짜 추가" -m "공유 모드 파일 읽기, Credential Manager·DPAPI P/Invoke, 프로세스 실행기, 회전 로그, HttpClient 팩터리와 테스트용 가짜 구현을 더한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: 유틸 (Jwt, Json, SharedJson, SeverityRules, ResetTimeFormatter)

공급자 파서가 쓰는 관대한 JSON 접근자와 JWT 읽기, 위젯·카드가 쓰는 심각도 규칙과 한국어 리셋·경과 시각 문구를 만든다.

동작 규칙 (테스트가 고정한다):
- `Json.*`는 절대 던지지 않는다. 요소가 객체가 아니거나, 속성이 없거나, 종류가 다르면 `null`. `Num`은 숫자 또는 숫자 문자열(Invariant)이며 NaN·무한대는 `null`. `Iso`는 오프셋이 없으면 UTC로 보고, 9자리 소수 초도 받는다. `EpochSeconds`는 소수 초를 허용하고 범위 밖이면 `null`.
- `Jwt`는 서명을 검증하지 않는다. base64url(패딩 유무 무관) → UTF-8 JSON **객체**일 때만 성공. `GetExpiry`는 `exp`(초).
- `SeverityRules.Effective`: `ServerSeverity`가 있으면 그대로, 없으면 클램프한 사용 %가 90 이상이면 Critical, 75 이상이면 Warning. `Clamp`는 0..100, NaN은 `null`.
- `ResetTimeFormatter`(주입한 `TimeZoneInfo` 기준):
  - `FormatRelative`: 남은 시간이 1분 미만(지난 시각 포함)이면 `곧 리셋`. 아니면 `<기간> 후 · <날> HH:mm`. 기간은 버림이며 `N일 M시간`(시간 0이면 `N일`) / `H시간 M분`(분 0이면 `H시간`) / `M분`. 날은 같은 날짜면 `오늘`, 아니면 `M/d(요일)`. 요일은 `일월화수목금토`.
  - `FormatShortDate`: `11/1`. `FormatShortDateWithDay`: `11/1(일)` (설계서 §2.3 카드의 `11/1(토)` 형식).
  - `FormatAgo`: 1분 미만·미래는 `방금`, 그다음 `N분 전` / `N시간 전` / `N일 전`.
- 테스트는 `TimeZoneInfo.CreateCustomTimeZone("KST", +09:00, ...)`로 결정적이다. 2026-10-28은 수요일이므로 10/29(목), 10/30(금), 11/1(일)이다.

**Files:**
- Create: `src/AITokenUsage.Core/Util/Json.cs`, `SharedJson.cs`, `Jwt.cs`, `SeverityRules.cs`, `ResetTimeFormatter.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fakes/TestJwt.cs`
- Test: `tests/AITokenUsage.Core.Tests/Util/JsonTests.cs`, `JwtTests.cs`, `SeverityRulesTests.cs`, `ResetTimeFormatterTests.cs`

**Interfaces:**
- Consumes: Task 2의 `UsageMeter`, `Severity`, `AITokenUsage.Core.Tests.Fakes` 네임스페이스.
- Produces (공유 계약 그대로 + 아래 확장):
  - `Jwt.TryReadPayload(string? jwt, out JsonElement payload)` (payload는 `Clone()`된 독립 요소), `Jwt.GetExpiry(string? jwt)`.
  - `Json.Str/Num/Bool/Obj/Arr/Iso/EpochSeconds(JsonElement e, string name)` + **추가** `Json.EpochMilliseconds(JsonElement e, string name)` (Claude `expiresAt`, Gemini `expiry_date`처럼 ms 단위 값용).
  - `SharedJson.Lenient` (`JsonDocumentOptions`: 주석 Skip, 끝 쉼표 허용).
  - `SeverityRules.WarningPercent = 75`, `CriticalPercent = 90`, `Effective(UsageMeter)`, `Clamp(double?)`.
  - `ResetTimeFormatter(TimeZoneInfo zone)`: `FormatRelative`, `FormatShortDate`, `FormatAgo` + **추가** `FormatShortDateWithDay(DateTimeOffset at)` → `"11/1(일)"`.
  - 테스트 도우미 **추가**: `AITokenUsage.Core.Tests.Fakes.TestJwt` — `Create(string payloadJson)`(가짜 서명의 무서명 JWT), `Base64Url(string text)`, `Base64Url(byte[] bytes)`. Codex·Gemini 테스트에서 id_token/access_token을 만들 때 쓴다.

- [ ] **Step 1: 실패하는 테스트 작성 — `Json`, `SharedJson`, `Jwt`**

`tests/AITokenUsage.Core.Tests/Fakes/TestJwt.cs`:

```csharp
using System.Text;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Builds unsigned fake JWTs for tests: base64url(header).base64url(payload).fake-signature.</summary>
public static class TestJwt
{
    public static string Create(string payloadJson) =>
        $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(payloadJson)}.fake-signature";

    public static string Base64Url(string text) => Base64Url(Encoding.UTF8.GetBytes(text));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

`tests/AITokenUsage.Core.Tests/Util/JsonTests.cs`:

```csharp
using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class JsonTests
{
    private const string Sample = """
        {
          "s": "text",
          "n": 42.5,
          "nInt": 7,
          "nStr": "12.25",
          "nExp": "1e3",
          "nBad": "abc",
          "nNaN": "NaN",
          "t": true,
          "f": false,
          "o": { "inner": 1 },
          "a": [1, 2],
          "nul": null,
          "iso": "2026-10-28T10:00:00Z",
          "isoOffset": "2026-10-28T15:50:00.123456+09:00",
          "isoNanos": "2026-10-28T10:00:00.123456789Z",
          "isoDate": "2026-11-01",
          "isoBad": "tomorrow",
          "epoch": 1761654321,
          "epochFrac": 1761654321.25,
          "epochStr": "1761654321",
          "epochHuge": 1e300,
          "epochMs": 1761654321000
        }
        """;

    private static JsonElement Root()
    {
        using JsonDocument doc = JsonDocument.Parse(Sample);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Str_ReturnsOnlyStrings()
    {
        JsonElement e = Root();

        Assert.Equal("text", Json.Str(e, "s"));
        Assert.Null(Json.Str(e, "n"));
        Assert.Null(Json.Str(e, "nul"));
        Assert.Null(Json.Str(e, "missing"));
    }

    [Fact]
    public void Num_AcceptsNumbersAndNumericStrings_RejectsEverythingElse()
    {
        JsonElement e = Root();

        Assert.Equal(42.5, Json.Num(e, "n"));
        Assert.Equal(7, Json.Num(e, "nInt"));
        Assert.Equal(12.25, Json.Num(e, "nStr"));
        Assert.Equal(1000, Json.Num(e, "nExp"));
        Assert.Null(Json.Num(e, "nBad"));
        Assert.Null(Json.Num(e, "nNaN"));
        Assert.Null(Json.Num(e, "t"));
        Assert.Null(Json.Num(e, "nul"));
        Assert.Null(Json.Num(e, "missing"));
    }

    [Fact]
    public void Bool_ReturnsOnlyJsonBooleans()
    {
        JsonElement e = Root();

        Assert.True(Json.Bool(e, "t"));
        Assert.False(Json.Bool(e, "f"));
        Assert.Null(Json.Bool(e, "s"));
        Assert.Null(Json.Bool(e, "missing"));
    }

    [Fact]
    public void ObjAndArr_CheckValueKind()
    {
        JsonElement e = Root();

        Assert.Equal(1, Json.Obj(e, "o")!.Value.GetProperty("inner").GetInt32());
        Assert.Null(Json.Obj(e, "a"));
        Assert.Null(Json.Obj(e, "nul"));
        Assert.Equal(2, Json.Arr(e, "a")!.Value.GetArrayLength());
        Assert.Null(Json.Arr(e, "o"));
        Assert.Null(Json.Arr(e, "missing"));
    }

    [Fact]
    public void Accessors_ReturnNull_WhenElementIsNotAnObject()
    {
        JsonElement array = Json.Arr(Root(), "a")!.Value;

        Assert.Null(Json.Str(array, "s"));
        Assert.Null(Json.Num(array, "n"));
        Assert.Null(Json.Bool(array, "t"));
        Assert.Null(Json.Obj(array, "o"));
        Assert.Null(Json.Arr(array, "a"));
        Assert.Null(Json.Iso(array, "iso"));
        Assert.Null(Json.EpochSeconds(array, "epoch"));
        Assert.Null(Json.Str(default, "s"));
    }

    [Fact]
    public void Iso_ParsesUtcOffsetsFractionsAndDateOnly()
    {
        JsonElement e = Root();

        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero), Json.Iso(e, "iso"));

        DateTimeOffset withOffset = Json.Iso(e, "isoOffset")!.Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 15, 50, 0, 123, 456, TimeSpan.FromHours(9)), withOffset);
        Assert.Equal(TimeSpan.FromHours(9), withOffset.Offset);

        // 9 fractional digits are rounded to .NET's 100 ns ticks.
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_568), Json.Iso(e, "isoNanos"));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), Json.Iso(e, "isoDate"));
        Assert.Null(Json.Iso(e, "isoBad"));
        Assert.Null(Json.Iso(e, "epoch"));
    }

    [Fact]
    public void EpochSeconds_AcceptsIntegersFractionsAndStrings_RejectsOutOfRange()
    {
        JsonElement e = Root();

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Json.EpochSeconds(e, "epoch"));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761654321250), Json.EpochSeconds(e, "epochFrac"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Json.EpochSeconds(e, "epochStr"));
        Assert.Null(Json.EpochSeconds(e, "epochHuge"));
        Assert.Null(Json.EpochSeconds(e, "s"));
    }

    [Fact]
    public void EpochMilliseconds_ReadsMilliseconds()
    {
        JsonElement e = Root();

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Json.EpochMilliseconds(e, "epochMs"));
        Assert.Null(Json.EpochMilliseconds(e, "epochHuge"));
    }

    [Fact]
    public void SharedJsonLenient_AllowsCommentsAndTrailingCommas()
    {
        const string jsonc = """
            {
              // line comment
              "security": { "auth": { "selectedType": "oauth-personal", }, },
              /* block comment */
              "selectedAuthType": "gemini-api-key",
            }
            """;

        using JsonDocument doc = JsonDocument.Parse(jsonc, SharedJson.Lenient);

        JsonElement auth = Json.Obj(Json.Obj(doc.RootElement, "security")!.Value, "auth")!.Value;
        Assert.Equal("oauth-personal", Json.Str(auth, "selectedType"));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(jsonc).Dispose());
    }
}
```

`tests/AITokenUsage.Core.Tests/Util/JwtTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Tests.Fakes;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class JwtTests
{
    [Fact]
    public void GetExpiry_ReadsExpClaim()
    {
        string jwt = TestJwt.Create("""{"sub":"user-1","exp":1761654321}""");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Jwt.GetExpiry(jwt));
    }

    [Fact]
    public void GetExpiry_AcceptsFractionalExp()
    {
        string jwt = TestJwt.Create("""{"exp":1761654321.5}""");

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761654321500), Jwt.GetExpiry(jwt));
    }

    [Fact]
    public void GetExpiry_IsNull_WithoutUsableExp()
    {
        Assert.Null(Jwt.GetExpiry(TestJwt.Create("""{"sub":"user-1"}""")));
        Assert.Null(Jwt.GetExpiry(TestJwt.Create("""{"exp":"soon"}""")));
    }

    [Fact]
    public void TryReadPayload_DecodesBase64UrlAlphabetWithoutPadding()
    {
        const string payload = """{"note":"??>>~~ü","hd":"example.test","https://api.openai.com/auth":{"chatgpt_account_id":"acct-1"}}""";
        string jwt = TestJwt.Create(payload);
        string segment = jwt.Split('.')[1];
        Assert.True(segment.Contains('-') || segment.Contains('_'), "payload must exercise the base64url alphabet");
        Assert.NotEqual(0, segment.Length % 4);

        Assert.True(Jwt.TryReadPayload(jwt, out JsonElement claims));

        Assert.Equal("example.test", claims.GetProperty("hd").GetString());
        Assert.Equal("??>>~~ü", claims.GetProperty("note").GetString());
        Assert.Equal("acct-1", claims.GetProperty("https://api.openai.com/auth").GetProperty("chatgpt_account_id").GetString());
    }

    [Fact]
    public void TryReadPayload_AcceptsPaddedSegment()
    {
        // 10 bytes -> 16 base64 characters ending in "==".
        string padded = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"exp":12}""")).Replace('+', '-').Replace('/', '_');
        Assert.EndsWith("==", padded);

        Assert.True(Jwt.TryReadPayload($"h.{padded}.s", out JsonElement claims));
        Assert.Equal(12, claims.GetProperty("exp").GetInt32());
    }

    [Fact]
    public void TryReadPayload_PayloadOutlivesInternalDocument()
    {
        Assert.True(Jwt.TryReadPayload(TestJwt.Create("""{"exp":42}"""), out JsonElement claims));
        GC.Collect();

        Assert.Equal(42, claims.GetProperty("exp").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-dots-at-all")]
    [InlineData("header..signature")]
    [InlineData("header.@@@@.signature")]
    [InlineData("header.a.signature")]
    public void TryReadPayload_RejectsMalformedTokens(string? jwt)
    {
        Assert.False(Jwt.TryReadPayload(jwt, out _));
        Assert.Null(Jwt.GetExpiry(jwt));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public void TryReadPayload_RejectsNonObjectPayloads(string payload)
    {
        Assert.False(Jwt.TryReadPayload(TestJwt.Create(payload), out _));
    }

    [Fact]
    public void TryReadPayload_RejectsInvalidUtf8()
    {
        string segment = TestJwt.Base64Url([0x7B, 0xFF, 0xFE, 0x7D]);

        Assert.False(Jwt.TryReadPayload($"h.{segment}.s", out _));
    }
}
```

- [ ] **Step 2: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~AITokenUsage.Core.Tests.Util.JsonTests|FullyQualifiedName~AITokenUsage.Core.Tests.Util.JwtTests"
```

Expected: 빌드 실패 — `error CS0234: The type or namespace name 'Util' does not exist in the namespace 'AITokenUsage.Core'`.

- [ ] **Step 3: `Json`, `SharedJson`, `Jwt` 구현**

`src/AITokenUsage.Core/Util/Json.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace AITokenUsage.Core.Util;

/// <summary>
/// Tolerant JsonElement accessors. Every method returns null when <paramref name="e"/> is not an
/// object, the property is missing, or the value has the wrong kind; none of them throws.
/// </summary>
public static class Json
{
    private const double MinUnixMilliseconds = -62_135_596_800_000d;
    private const double MaxUnixMilliseconds = 253_402_300_799_999d;

    public static string? Str(JsonElement e, string name) =>
        TryGet(e, name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A JSON number, or a string holding an invariant-culture number. NaN and infinities give null.</summary>
    public static double? Num(JsonElement e, string name)
    {
        if (!TryGet(e, name, out JsonElement value))
        {
            return null;
        }

        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out number):
                break;
            case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number):
                break;
            default:
                return null;
        }

        return double.IsFinite(number) ? number : null;
    }

    /// <summary>JSON true/false only.</summary>
    public static bool? Bool(JsonElement e, string name)
    {
        if (!TryGet(e, name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static JsonElement? Obj(JsonElement e, string name) =>
        TryGet(e, name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;

    public static JsonElement? Arr(JsonElement e, string name) =>
        TryGet(e, name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value : null;

    /// <summary>ISO 8601 / RFC 3339 string. A value without an offset is taken as UTC.</summary>
    public static DateTimeOffset? Iso(JsonElement e, string name)
    {
        string? text = Str(e, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : null;
    }

    /// <summary>Unix time in seconds (number or numeric string, fractions allowed).</summary>
    public static DateTimeOffset? EpochSeconds(JsonElement e, string name) =>
        Num(e, name) is double seconds ? FromUnixMilliseconds(Math.Round(seconds * 1000)) : null;

    /// <summary>Unix time in milliseconds (number or numeric string), e.g. Claude expiresAt or Gemini expiry_date.</summary>
    public static DateTimeOffset? EpochMilliseconds(JsonElement e, string name) =>
        Num(e, name) is double milliseconds ? FromUnixMilliseconds(Math.Round(milliseconds)) : null;

    private static DateTimeOffset? FromUnixMilliseconds(double milliseconds)
    {
        if (milliseconds < MinUnixMilliseconds || milliseconds > MaxUnixMilliseconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
```

`src/AITokenUsage.Core/Util/SharedJson.cs`:

```csharp
using System.Text.Json;

namespace AITokenUsage.Core.Util;

public static class SharedJson
{
    /// <summary>For JSONC files such as ~/.gemini/settings.json: comments are skipped and trailing commas allowed.</summary>
    public static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
```

`src/AITokenUsage.Core/Util/Jwt.cs`:

```csharp
using System.Text.Json;

namespace AITokenUsage.Core.Util;

/// <summary>Reads JWT claims without verifying the signature (only used for expiry and account hints).</summary>
public static class Jwt
{
    /// <summary>Decodes the base64url payload segment. Returns false unless it is a JSON object.</summary>
    public static bool TryReadPayload(string? jwt, out JsonElement payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return false;
        }

        string[] parts = jwt.Split('.');
        if (parts.Length < 2 || parts[1].Length == 0 || !TryDecodeBase64Url(parts[1], out byte[] bytes))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The "exp" claim (Unix seconds), or null.</summary>
    public static DateTimeOffset? GetExpiry(string? jwt) =>
        TryReadPayload(jwt, out JsonElement payload) ? Json.EpochSeconds(payload, "exp") : null;

    private static bool TryDecodeBase64Url(string segment, out byte[] bytes)
    {
        string base64 = segment.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 1:
                bytes = [];
                return false;
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
        }

        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~AITokenUsage.Core.Tests.Util.JsonTests|FullyQualifiedName~AITokenUsage.Core.Tests.Util.JwtTests"
```

Expected: `Passed!  - Failed:     0, Passed:    26`.

- [ ] **Step 5: 실패하는 테스트 작성 — `SeverityRules`, `ResetTimeFormatter`**

`tests/AITokenUsage.Core.Tests/Util/SeverityRulesTests.cs`:

```csharp
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class SeverityRulesTests
{
    private static UsageMeter Meter(double? used, Severity? server = null) =>
        new("session", "5h", "현재 세션 (5시간)", used, null, server);

    [Theory]
    [InlineData(0, Severity.Normal)]
    [InlineData(74.9, Severity.Normal)]
    [InlineData(75, Severity.Warning)]
    [InlineData(89.99, Severity.Warning)]
    [InlineData(90, Severity.Critical)]
    [InlineData(100, Severity.Critical)]
    [InlineData(250, Severity.Critical)]
    [InlineData(-10, Severity.Normal)]
    public void WithoutServerSeverity_UsesThresholds(double used, Severity expected)
    {
        Assert.Equal(expected, SeverityRules.Effective(Meter(used)));
    }

    [Theory]
    [InlineData(10, Severity.Critical)]
    [InlineData(95, Severity.Normal)]
    [InlineData(80, Severity.Warning)]
    public void ServerSeverity_Wins(double used, Severity server)
    {
        Assert.Equal(server, SeverityRules.Effective(Meter(used, server)));
    }

    [Fact]
    public void UnknownPercent_IsNormal()
    {
        Assert.Equal(Severity.Normal, SeverityRules.Effective(Meter(null)));
        Assert.Equal(Severity.Normal, SeverityRules.Effective(Meter(double.NaN)));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(42.5, 42.5)]
    [InlineData(100, 100)]
    [InlineData(130, 100)]
    public void Clamp_LimitsToZeroToHundred(double input, double expected)
    {
        Assert.Equal(expected, SeverityRules.Clamp(input));
    }

    [Fact]
    public void Clamp_MapsNullAndNaNToNull_AndInfinityToBounds()
    {
        Assert.Null(SeverityRules.Clamp(null));
        Assert.Null(SeverityRules.Clamp(double.NaN));
        Assert.Equal(100, SeverityRules.Clamp(double.PositiveInfinity));
        Assert.Equal(0, SeverityRules.Clamp(double.NegativeInfinity));
    }
}
```

`tests/AITokenUsage.Core.Tests/Util/ResetTimeFormatterTests.cs`:

```csharp
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class ResetTimeFormatterTests
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    private static readonly TimeZoneInfo KstZone =
        TimeZoneInfo.CreateCustomTimeZone("KST", Kst, "Korea Standard Time", "Korea Standard Time");

    private readonly ResetTimeFormatter _formatter = new(KstZone);

    private static DateTimeOffset At(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, Kst);

    [Fact]
    public void SameDay_ShowsHoursMinutesAndToday()
    {
        Assert.Equal("3시간 12분 후 · 오늘 15:50", _formatter.FormatRelative(At(10, 28, 15, 50), At(10, 28, 12, 38)));
    }

    [Fact]
    public void LaterDay_ShowsDaysHoursAndDateWithKoreanWeekday()
    {
        // 2026-10-30 is a Friday.
        Assert.Equal("1일 8시간 후 · 10/30(금) 05:00", _formatter.FormatRelative(At(10, 30, 5, 0), At(10, 28, 21, 0)));
    }

    [Fact]
    public void CrossingMidnight_ShowsTheNextDate()
    {
        Assert.Equal("1시간 45분 후 · 10/29(목) 01:15", _formatter.FormatRelative(At(10, 29, 1, 15), At(10, 28, 23, 30)));
    }

    [Fact]
    public void UtcInputs_AreConvertedToTheConfiguredZone()
    {
        var resetUtc = new DateTimeOffset(2026, 10, 28, 6, 50, 0, TimeSpan.Zero);  // 15:50 KST
        var nowUtc = new DateTimeOffset(2026, 10, 28, 3, 38, 0, TimeSpan.Zero);    // 12:38 KST

        Assert.Equal("3시간 12분 후 · 오늘 15:50", _formatter.FormatRelative(resetUtc, nowUtc));
    }

    [Fact]
    public void TheZoneDecidesWhatTodayMeans()
    {
        var reset = new DateTimeOffset(2026, 10, 28, 16, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

        Assert.Equal("2시간 후 · 오늘 16:00", new ResetTimeFormatter(TimeZoneInfo.Utc).FormatRelative(reset, now));
        Assert.Equal("2시간 후 · 10/29(목) 01:00", _formatter.FormatRelative(reset, now));
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(59)]
    public void PastOrUnderOneMinute_IsResettingSoon(int secondsAhead)
    {
        DateTimeOffset now = At(10, 28, 12, 0);

        Assert.Equal("곧 리셋", _formatter.FormatRelative(now.AddSeconds(secondsAhead), now));
    }

    [Theory]
    [InlineData(60, "1분 후 · 오늘 12:01")]
    [InlineData(45 * 60 + 59, "45분 후 · 오늘 12:45")]
    [InlineData(2 * 3600, "2시간 후 · 오늘 14:00")]
    [InlineData(3 * 3600 + 12 * 60 + 59, "3시간 12분 후 · 오늘 15:12")]
    [InlineData(2 * 86400, "2일 후 · 10/30(금) 12:00")]
    [InlineData(2 * 86400 + 3600 + 1800, "2일 1시간 후 · 10/30(금) 13:30")]
    public void Durations_DropPartialUnits(int secondsAhead, string expected)
    {
        DateTimeOffset now = At(10, 28, 12, 0);

        Assert.Equal(expected, _formatter.FormatRelative(now.AddSeconds(secondsAhead), now));
    }

    [Theory]
    [InlineData(1, "11/1(일)")]
    [InlineData(2, "11/2(월)")]
    [InlineData(3, "11/3(화)")]
    [InlineData(4, "11/4(수)")]
    [InlineData(5, "11/5(목)")]
    [InlineData(6, "11/6(금)")]
    [InlineData(7, "11/7(토)")]
    public void FormatShortDateWithDay_UsesKoreanWeekdays(int day, string expected)
    {
        Assert.Equal(expected, _formatter.FormatShortDateWithDay(At(11, day, 9, 0)));
    }

    [Fact]
    public void FormatShortDate_IsMonthSlashDayInZone()
    {
        Assert.Equal("11/1", _formatter.FormatShortDate(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
        // 2026-10-31 16:00 UTC is already 11/1 01:00 in Korea.
        Assert.Equal("11/1", _formatter.FormatShortDate(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)));
        Assert.Equal("10/31", new ResetTimeFormatter(TimeZoneInfo.Utc).FormatShortDate(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(-120, "방금")]
    [InlineData(0, "방금")]
    [InlineData(59, "방금")]
    [InlineData(60, "1분 전")]
    [InlineData(3 * 60 + 30, "3분 전")]
    [InlineData(59 * 60 + 59, "59분 전")]
    [InlineData(3600, "1시간 전")]
    [InlineData(2 * 3600 + 59 * 60, "2시간 전")]
    [InlineData(86400, "1일 전")]
    [InlineData(3 * 86400 + 5, "3일 전")]
    public void FormatAgo_PicksTheLargestWholeUnit(int secondsAgo, string expected)
    {
        DateTimeOffset now = At(10, 28, 12, 0);

        Assert.Equal(expected, _formatter.FormatAgo(now.AddSeconds(-secondsAgo), now));
    }
}
```

- [ ] **Step 6: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~SeverityRulesTests|FullyQualifiedName~ResetTimeFormatterTests"
```

Expected: 빌드 실패 — `error CS0246: The type or namespace name 'ResetTimeFormatter' could not be found` (`SeverityRules` 사용처에서 `CS0103`이 함께 보일 수 있다).

- [ ] **Step 7: `SeverityRules`, `ResetTimeFormatter` 구현**

시계 문자열은 `CultureInfo.InvariantCulture`로 만든다(사용자 지역 설정의 시간 구분자가 `:`가 아니어도 `15:50`이 나오도록).

`src/AITokenUsage.Core/Util/SeverityRules.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Util;

public static class SeverityRules
{
    public const double WarningPercent = 75, CriticalPercent = 90;

    /// <summary>The provider's severity wins; otherwise used ≥ 90 % is critical and ≥ 75 % is warning.</summary>
    public static Severity Effective(UsageMeter meter)
    {
        if (meter.ServerSeverity is Severity serverSeverity)
        {
            return serverSeverity;
        }

        double? used = Clamp(meter.UsedPercent);
        if (used is null)
        {
            return Severity.Normal;
        }

        if (used >= CriticalPercent)
        {
            return Severity.Critical;
        }

        return used >= WarningPercent ? Severity.Warning : Severity.Normal;
    }

    /// <summary>Clamps to 0..100. NaN becomes null.</summary>
    public static double? Clamp(double? percent)
    {
        if (percent is not double value || double.IsNaN(value))
        {
            return null;
        }

        return Math.Clamp(value, 0, 100);
    }
}
```

`src/AITokenUsage.Core/Util/ResetTimeFormatter.cs`:

```csharp
using System.Globalization;

namespace AITokenUsage.Core.Util;

/// <summary>Korean reset/age texts. All wall-clock parts use the injected time zone (TimeZoneInfo.Local in the app).</summary>
public sealed class ResetTimeFormatter
{
    private static readonly string[] KoreanDayNames = ["일", "월", "화", "수", "목", "금", "토"];

    private readonly TimeZoneInfo _zone;

    public ResetTimeFormatter(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        _zone = zone;
    }

    /// <summary>
    /// "3시간 12분 후 · 오늘 15:50", "1일 8시간 후 · 10/30(금) 05:00", or "곧 리셋" when less than a minute is left
    /// (including resets in the past). Partial minutes are dropped.
    /// </summary>
    public string FormatRelative(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        TimeSpan remaining = resetsAt - now;
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "곧 리셋";
        }

        DateTimeOffset localReset = TimeZoneInfo.ConvertTime(resetsAt, _zone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, _zone);
        string clock = localReset.ToString("HH:mm", CultureInfo.InvariantCulture);
        string day = localReset.Date == localNow.Date ? "오늘" : FormatDayAndWeekday(localReset);

        return $"{FormatDuration(remaining)} 후 · {day} {clock}";
    }

    /// <summary>"11/1".</summary>
    public string FormatShortDate(DateTimeOffset at)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(at, _zone);
        return string.Create(CultureInfo.InvariantCulture, $"{local.Month}/{local.Day}");
    }

    /// <summary>"11/1(일)".</summary>
    public string FormatShortDateWithDay(DateTimeOffset at) =>
        FormatDayAndWeekday(TimeZoneInfo.ConvertTime(at, _zone));

    /// <summary>"방금", "3분 전", "2시간 전", "3일 전". Future times (clock skew) give "방금".</summary>
    public string FormatAgo(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan elapsed = now - at;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "방금";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}분 전";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}시간 전";
        }

        return $"{(int)elapsed.TotalDays}일 전";
    }

    private static string FormatDayAndWeekday(DateTimeOffset local) =>
        string.Create(CultureInfo.InvariantCulture, $"{local.Month}/{local.Day}({KoreanDayNames[(int)local.DayOfWeek]})");

    private static string FormatDuration(TimeSpan span)
    {
        long totalMinutes = (long)Math.Floor(span.TotalMinutes);
        long days = totalMinutes / (24 * 60);
        long hours = totalMinutes / 60 % 24;
        long minutes = totalMinutes % 60;

        if (days > 0)
        {
            return hours > 0 ? $"{days}일 {hours}시간" : $"{days}일";
        }

        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}시간 {minutes}분" : $"{hours}시간";
        }

        return $"{minutes}분";
    }
}
```

- [ ] **Step 8: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~SeverityRulesTests|FullyQualifiedName~ResetTimeFormatterTests"
```

Expected: `Passed!  - Failed:     0, Passed:    51`.

- [ ] **Step 9: 전체 빌드·테스트**

```powershell
dotnet build AITokenUsage.sln
dotnet test AITokenUsage.sln
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed:     0, Passed:   145`.

- [ ] **Step 10: 커밋**

```powershell
git add src/AITokenUsage.Core/Util tests/AITokenUsage.Core.Tests/Util tests/AITokenUsage.Core.Tests/Fakes/TestJwt.cs
git commit -m "feat(core): JWT·JSON 접근자와 심각도·리셋 시각 포맷 유틸 추가" -m "관대한 JsonElement 접근자, 서명 검증 없는 JWT 읽기, 75/90% 심각도 규칙, 한국어 리셋·경과 문구를 더한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 설정 (AppSettings, ProviderDefaults, SettingsStore)

설정 모델과 기본값, 정규화 규칙, 원자적 저장소를 만든다. 저장 형식은 camelCase JSON + 문자열 enum(`"placement": "beforeTray"`)이다.

동작 규칙 (테스트가 고정한다):
- `Normalize()`는 **자기 자신을 고쳐서 돌려준다**(새 객체를 만들지 않는다). 남아 있는 `ProviderSettings` 인스턴스는 그대로 두므로 설정 창 바인딩이 깨지지 않는다.
  - 공급자 Id는 공백 제거 + 소문자. 알 수 없는 Id·빈 Id·`null` 항목은 버리고, 중복은 **처음 것**을 남긴다.
  - 빠진 공급자는 `claude, codex, gemini, copilot` 순서로 **끝에** 기본값(켜짐, 기본 줄)으로 붙인다. 사용자가 정한 순서는 유지된다.
  - 빈/공백/`null` 줄 키는 그 공급자의 기본 줄로 바꾸고, 나머지 키는 앞뒤 공백만 자른다(동적 키 `weekly:fable` 등은 검증하지 않는다).
  - `RotationSeconds` 2..60 클램프, `RefreshMinutes`는 2/5/10/15 중 가장 가까운 값(동점은 없다), 정의되지 않은 enum 값은 기본값, 공백 `MonitorDeviceName`은 `null`, `Version`은 1.
- `SettingsStore.Load()`:
  - 파일 없음 → 기본값(파일을 만들지 않는다).
  - 읽기 실패(`IOException`/권한) → 기본값, 파일은 그대로 두고 경고 로그.
  - JSON 손상(문법 오류, 빈 파일, `null`, 배열, 알 수 없는 enum 문자열) → `settings.json.bak`으로 **이동**하고 기본값, 경고 로그.
  - 알 수 없는 필드는 무시, 빠진 필드는 기본값. 주석·끝 쉼표·PascalCase 이름·숫자 enum도 받는다. 결과는 항상 `Normalize()`된다.
- `SettingsStore.Save()`: `settings.json.tmp`에 쓰고 `File.Move(tmp, settings.json, overwrite: true)`(같은 볼륨의 원자적 이름 바꾸기). 폴더가 없으면 만든다. 실패하면 tmp를 지우고 예외를 그대로 던진다(호출자 = Task 14 `AppHost`가 로그). 내부 잠금으로 스레드 안전하다 — UI 스레드에서 블로킹하지 않도록 호출자가 백그라운드에서 부르면 된다.

**Files:**
- Create: `src/AITokenUsage.Core/Settings/SettingsEnums.cs` (`WidgetPlacement`, `ValueMode`, `TextColorMode`)
- Create: `src/AITokenUsage.Core/Settings/ProviderSettings.cs`, `ProviderDefaults.cs`, `AppSettings.cs`, `SettingsStore.cs`
- Test: `tests/AITokenUsage.Core.Tests/Settings/AppSettingsTests.cs`, `ProviderDefaultsTests.cs`, `SettingsStoreTests.cs`

**Interfaces:**
- Consumes: Task 2의 `ProviderIds`, `IAppLog`, 테스트 가짜 `TempDirectory`, `RecordingLog`.
- Produces (공유 계약 그대로 + 아래 확장):
  - `enum WidgetPlacement { LeftEdge, BeforeStart, BeforeTray }`, `enum ValueMode { Used, Remaining }`, `enum TextColorMode { Auto, Light, Dark }`.
  - `ProviderSettings { string Id; bool Enabled = true; string Line1Key; string Line2Key; }`.
  - `AppSettings` (모든 속성은 공유 계약 그대로) + **추가** `const int CurrentVersion = 1`, `const int MinRotationSeconds = 2`, `const int MaxRotationSeconds = 60` (설정 창 슬라이더 범위용). `AppSettings Normalize()`.
  - `ProviderDefaults.CreateDefaultProviders()`, `DefaultLines(string providerId)`(알 수 없는 Id는 `ArgumentOutOfRangeException`), `RefreshChoicesMinutes = [2, 5, 10, 15]` + **추가** `ProviderSettings CreateDefault(string providerId)`.
  - `SettingsStore(string filePath, IAppLog log)`: `string FilePath`, `AppSettings Load()`, `void Save(AppSettings)` + **추가** `string BackupPath` (= `FilePath + ".bak"`).

- [ ] **Step 1: 실패하는 테스트 작성 — 기본값과 `Normalize`**

`tests/AITokenUsage.Core.Tests/Settings/AppSettingsTests.cs`:

```csharp
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Tests.Settings;

public class AppSettingsTests
{
    private static string[] Ids(AppSettings settings) => settings.Providers.Select(p => p.Id).ToArray();

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var settings = new AppSettings();

        Assert.Equal(1, settings.Version);
        Assert.Equal(["claude", "codex", "gemini", "copilot"], Ids(settings));
        Assert.All(settings.Providers, p => Assert.True(p.Enabled));
        Assert.True(settings.RotationEnabled);
        Assert.Equal(5, settings.RotationSeconds);
        Assert.True(settings.PauseOnHover);
        Assert.True(settings.SkipUnavailable);
        Assert.Null(settings.MonitorDeviceName);
        Assert.Equal(WidgetPlacement.LeftEdge, settings.Placement);
        Assert.Equal(ValueMode.Used, settings.ValueMode);
        Assert.Equal(TextColorMode.Auto, settings.TextColor);
        Assert.Equal(5, settings.RefreshMinutes);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.ShowTrayIcon);
    }

    [Fact]
    public void Normalize_AppendsMissingProvidersInCanonicalOrder_KeepingUserOrderFirst()
    {
        var settings = new AppSettings
        {
            Providers = [new ProviderSettings { Id = "copilot", Enabled = false, Line1Key = "monthly", Line2Key = "completions" }],
        };

        settings.Normalize();

        Assert.Equal(["copilot", "claude", "codex", "gemini"], Ids(settings));
        Assert.False(settings.Providers[0].Enabled);
        Assert.Equal("completions", settings.Providers[0].Line2Key);
        Assert.Equal(("session", "weekly"), (settings.Providers[1].Line1Key, settings.Providers[1].Line2Key));
        Assert.True(settings.Providers[1].Enabled);
    }

    [Fact]
    public void Normalize_RemovesUnknownIds()
    {
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = "antigravity", Line1Key = "x", Line2Key = "y" },
                new ProviderSettings { Id = "", Line1Key = "x", Line2Key = "y" },
                new ProviderSettings { Id = "gemini", Line1Key = "flash-lite", Line2Key = "pro" },
            ],
        };

        settings.Normalize();

        Assert.Equal(["gemini", "claude", "codex", "copilot"], Ids(settings));
        Assert.Equal("flash-lite", settings.Providers[0].Line1Key);
    }

    [Fact]
    public void Normalize_RemovesDuplicates_FirstWins_IdsAreCaseInsensitive()
    {
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = " Claude ", Enabled = false, Line1Key = "weekly:fable", Line2Key = "weekly" },
                new ProviderSettings { Id = "claude", Enabled = true, Line1Key = "session", Line2Key = "session" },
                new ProviderSettings { Id = "CODEX", Line1Key = "5h", Line2Key = "weekly" },
            ],
        };

        settings.Normalize();

        Assert.Equal(["claude", "codex", "gemini", "copilot"], Ids(settings));
        Assert.False(settings.Providers[0].Enabled);
        Assert.Equal("weekly:fable", settings.Providers[0].Line1Key);
    }

    [Fact]
    public void Normalize_ReplacesBlankLineKeysWithDefaults_AndTrimsOthers()
    {
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = "claude", Line1Key = "", Line2Key = "  " },
                new ProviderSettings { Id = "codex", Line1Key = null!, Line2Key = " daily " },
                new ProviderSettings { Id = "gemini", Line1Key = "flash", Line2Key = "" },
                new ProviderSettings { Id = "copilot", Line1Key = "", Line2Key = "" },
            ],
        };

        settings.Normalize();

        Assert.Equal(("session", "weekly"), (settings.Providers[0].Line1Key, settings.Providers[0].Line2Key));
        Assert.Equal(("5h", "daily"), (settings.Providers[1].Line1Key, settings.Providers[1].Line2Key));
        Assert.Equal(("flash", "flash"), (settings.Providers[2].Line1Key, settings.Providers[2].Line2Key));
        Assert.Equal(("monthly", "reset"), (settings.Providers[3].Line1Key, settings.Providers[3].Line2Key));
    }

    [Fact]
    public void Normalize_HandlesNullListAndNullEntries()
    {
        var settings = new AppSettings { Providers = null! };
        settings.Normalize();
        Assert.Equal(["claude", "codex", "gemini", "copilot"], Ids(settings));

        var withNulls = new AppSettings { Providers = [null!, new ProviderSettings { Id = "gemini" }, null!] };
        withNulls.Normalize();
        Assert.Equal(["gemini", "claude", "codex", "copilot"], Ids(withNulls));
        Assert.Equal(("pro", "flash"), (withNulls.Providers[0].Line1Key, withNulls.Providers[0].Line2Key));
    }

    [Theory]
    [InlineData(-5, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(int.MaxValue, 60)]
    public void Normalize_ClampsRotationSeconds(int input, int expected)
    {
        var settings = new AppSettings { RotationSeconds = input }.Normalize();

        Assert.Equal(expected, settings.RotationSeconds);
    }

    [Theory]
    [InlineData(int.MinValue, 2)]
    [InlineData(-3, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 10)]
    [InlineData(10, 10)]
    [InlineData(12, 10)]
    [InlineData(13, 15)]
    [InlineData(15, 15)]
    [InlineData(100, 15)]
    [InlineData(int.MaxValue, 15)]
    public void Normalize_SnapsRefreshMinutesToNearestChoice(int input, int expected)
    {
        var settings = new AppSettings { RefreshMinutes = input }.Normalize();

        Assert.Equal(expected, settings.RefreshMinutes);
    }

    [Fact]
    public void Normalize_ResetsUndefinedEnumsVersionAndBlankMonitor()
    {
        var settings = new AppSettings
        {
            Version = 0,
            Placement = (WidgetPlacement)42,
            ValueMode = (ValueMode)(-1),
            TextColor = (TextColorMode)9,
            MonitorDeviceName = "   ",
        };

        settings.Normalize();

        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.Equal(WidgetPlacement.LeftEdge, settings.Placement);
        Assert.Equal(ValueMode.Used, settings.ValueMode);
        Assert.Equal(TextColorMode.Auto, settings.TextColor);
        Assert.Null(settings.MonitorDeviceName);
    }

    [Fact]
    public void Normalize_KeepsValidValuesAndInstances()
    {
        var settings = new AppSettings
        {
            Placement = WidgetPlacement.BeforeTray,
            ValueMode = ValueMode.Remaining,
            TextColor = TextColorMode.Dark,
            MonitorDeviceName = @"\\.\DISPLAY2",
            RotationEnabled = false,
            StartWithWindows = true,
        };
        ProviderSettings claude = settings.Providers[0];

        AppSettings returned = settings.Normalize();

        Assert.Same(settings, returned);
        Assert.Same(claude, settings.Providers[0]);
        Assert.Equal(WidgetPlacement.BeforeTray, settings.Placement);
        Assert.Equal(ValueMode.Remaining, settings.ValueMode);
        Assert.Equal(TextColorMode.Dark, settings.TextColor);
        Assert.Equal(@"\\.\DISPLAY2", settings.MonitorDeviceName);
        Assert.False(settings.RotationEnabled);
        Assert.True(settings.StartWithWindows);
    }
}
```

`tests/AITokenUsage.Core.Tests/Settings/ProviderDefaultsTests.cs`:

```csharp
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Tests.Settings;

public class ProviderDefaultsTests
{
    [Theory]
    [InlineData("claude", "session", "weekly")]
    [InlineData("codex", "5h", "weekly")]
    [InlineData("gemini", "pro", "flash")]
    [InlineData("copilot", "monthly", "reset")]
    public void DefaultLines_FollowTheSpecTable(string id, string line1, string line2)
    {
        Assert.Equal((line1, line2), ProviderDefaults.DefaultLines(id));
    }

    [Fact]
    public void DefaultLines_ThrowsForUnknownProvider()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderDefaults.DefaultLines("antigravity"));
    }

    [Fact]
    public void CreateDefaultProviders_ReturnsAFreshEnabledListInCanonicalOrder()
    {
        List<ProviderSettings> first = ProviderDefaults.CreateDefaultProviders();
        first[0].Enabled = false;
        first.RemoveAt(3);

        List<ProviderSettings> second = ProviderDefaults.CreateDefaultProviders();

        Assert.Equal(["claude", "codex", "gemini", "copilot"], second.Select(p => p.Id));
        Assert.All(second, p => Assert.True(p.Enabled));
        Assert.Equal("session", second[0].Line1Key);
    }

    [Fact]
    public void RefreshChoices_Are2_5_10_15()
    {
        Assert.Equal([2, 5, 10, 15], ProviderDefaults.RefreshChoicesMinutes);
    }
}
```

- [ ] **Step 2: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~AppSettingsTests|FullyQualifiedName~ProviderDefaultsTests"
```

Expected: 빌드 실패 — `error CS0234: The type or namespace name 'Settings' does not exist in the namespace 'AITokenUsage.Core'`.

- [ ] **Step 3: 설정 모델 구현**

`src/AITokenUsage.Core/Settings/SettingsEnums.cs`:

```csharp
namespace AITokenUsage.Core.Settings;

/// <summary>Where the widget sits on the taskbar (설정: 작업 표시줄 왼쪽 끝 / 시작 버튼 왼쪽 / 알림 영역 왼쪽).</summary>
public enum WidgetPlacement
{
    LeftEdge,
    BeforeStart,
    BeforeTray,
}

/// <summary>Show used % (default) or remaining % (= 100 − used).</summary>
public enum ValueMode
{
    Used,
    Remaining,
}

/// <summary>Auto follows the taskbar theme; Light/Dark force the text color.</summary>
public enum TextColorMode
{
    Auto,
    Light,
    Dark,
}
```

`src/AITokenUsage.Core/Settings/ProviderSettings.cs`:

```csharp
namespace AITokenUsage.Core.Settings;

public sealed class ProviderSettings
{
    /// <summary>One of ProviderIds (lower case).</summary>
    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Meter key shown on the first widget line, e.g. "session".</summary>
    public string Line1Key { get; set; } = "";

    /// <summary>Meter key shown on the second widget line, e.g. "weekly".</summary>
    public string Line2Key { get; set; } = "";
}
```

`src/AITokenUsage.Core/Settings/ProviderDefaults.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Settings;

public static class ProviderDefaults
{
    public static readonly int[] RefreshChoicesMinutes = [2, 5, 10, 15];

    /// <summary>All providers enabled, in canonical order, with their default lines. Returns a new list every call.</summary>
    public static List<ProviderSettings> CreateDefaultProviders() =>
        ProviderIds.All.Select(CreateDefault).ToList();

    /// <summary>claude: session/weekly, codex: 5h/weekly, gemini: pro/flash, copilot: monthly/reset.</summary>
    public static (string Line1, string Line2) DefaultLines(string providerId) => providerId switch
    {
        ProviderIds.Claude => ("session", "weekly"),
        ProviderIds.Codex => ("5h", "weekly"),
        ProviderIds.Gemini => ("pro", "flash"),
        ProviderIds.Copilot => ("monthly", "reset"),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId), providerId, "Unknown provider id."),
    };

    public static ProviderSettings CreateDefault(string providerId)
    {
        (string line1, string line2) = DefaultLines(providerId);
        return new ProviderSettings { Id = providerId, Enabled = true, Line1Key = line1, Line2Key = line2 };
    }
}
```

`src/AITokenUsage.Core/Settings/AppSettings.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Settings;

/// <summary>Persisted user settings (%APPDATA%\AITokenUsage\settings.json).</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public const int MinRotationSeconds = 2;
    public const int MaxRotationSeconds = 60;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Display order and per-provider options.</summary>
    public List<ProviderSettings> Providers { get; set; } = ProviderDefaults.CreateDefaultProviders();

    public bool RotationEnabled { get; set; } = true;

    /// <summary>2..60 seconds.</summary>
    public int RotationSeconds { get; set; } = 5;

    public bool PauseOnHover { get; set; } = true;

    public bool SkipUnavailable { get; set; } = true;

    /// <summary>null = primary monitor; otherwise a device name such as @"\\.\DISPLAY2".</summary>
    public string? MonitorDeviceName { get; set; }

    public WidgetPlacement Placement { get; set; } = WidgetPlacement.LeftEdge;

    public ValueMode ValueMode { get; set; } = ValueMode.Used;

    public TextColorMode TextColor { get; set; } = TextColorMode.Auto;

    /// <summary>One of ProviderDefaults.RefreshChoicesMinutes (2, 5, 10, 15).</summary>
    public int RefreshMinutes { get; set; } = 5;

    public bool StartWithWindows { get; set; }

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Repairs this instance in place and returns it:
    /// unknown/duplicate provider ids are dropped (first one wins, ids are lower-cased),
    /// missing providers are appended with defaults in canonical order, blank line keys get the
    /// provider defaults, RotationSeconds is clamped to 2..60, RefreshMinutes snaps to the nearest
    /// of 2/5/10/15, undefined enum values fall back to their defaults and a blank monitor name becomes null.
    /// Existing ProviderSettings instances are kept (UI bindings stay valid).
    /// </summary>
    public AppSettings Normalize()
    {
        Version = CurrentVersion;
        Providers = NormalizeProviders(Providers);
        RotationSeconds = Math.Clamp(RotationSeconds, MinRotationSeconds, MaxRotationSeconds);
        RefreshMinutes = NearestRefreshChoice(RefreshMinutes);

        if (!Enum.IsDefined(Placement))
        {
            Placement = WidgetPlacement.LeftEdge;
        }

        if (!Enum.IsDefined(ValueMode))
        {
            ValueMode = ValueMode.Used;
        }

        if (!Enum.IsDefined(TextColor))
        {
            TextColor = TextColorMode.Auto;
        }

        if (string.IsNullOrWhiteSpace(MonitorDeviceName))
        {
            MonitorDeviceName = null;
        }

        return this;
    }

    private static List<ProviderSettings> NormalizeProviders(List<ProviderSettings>? providers)
    {
        var result = new List<ProviderSettings>(ProviderIds.All.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ProviderSettings? provider in providers ?? [])
        {
            if (provider is null)
            {
                continue;
            }

            string id = (provider.Id ?? "").Trim().ToLowerInvariant();
            if (!ProviderIds.All.Contains(id) || !seen.Add(id))
            {
                continue;
            }

            (string line1, string line2) = ProviderDefaults.DefaultLines(id);
            provider.Id = id;
            provider.Line1Key = string.IsNullOrWhiteSpace(provider.Line1Key) ? line1 : provider.Line1Key.Trim();
            provider.Line2Key = string.IsNullOrWhiteSpace(provider.Line2Key) ? line2 : provider.Line2Key.Trim();
            result.Add(provider);
        }

        foreach (string id in ProviderIds.All)
        {
            if (seen.Add(id))
            {
                result.Add(ProviderDefaults.CreateDefault(id));
            }
        }

        return result;
    }

    private static int NearestRefreshChoice(int minutes) =>
        ProviderDefaults.RefreshChoicesMinutes.MinBy(choice => Math.Abs((long)choice - minutes));
}
```

- [ ] **Step 4: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~AppSettingsTests|FullyQualifiedName~ProviderDefaultsTests"
```

Expected: `Passed!  - Failed:     0, Passed:    40`.

- [ ] **Step 5: 실패하는 테스트 작성 — `SettingsStore`**

기본값 / 저장·복원 / 손상된 파일(5종) / 알 수 없는 필드 / 관대한 입력 / 디스크에서 읽은 공급자 정규화 / 폴더 생성을 다룬다(설계서 §7.1 SettingsStore 행).

`tests/AITokenUsage.Core.Tests/Settings/SettingsStoreTests.cs`:

```csharp
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly string _path;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _path = _temp.Combine("AITokenUsage", "settings.json");
        _store = new SettingsStore(_path, _log);
    }

    public void Dispose() => _temp.Dispose();

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults_WithoutCreatingIt()
    {
        AppSettings settings = _store.Load();

        Assert.Equal(["claude", "codex", "gemini", "copilot"], settings.Providers.Select(p => p.Id));
        Assert.Equal(5, settings.RotationSeconds);
        Assert.False(File.Exists(_path));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverySetting()
    {
        var original = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = "copilot", Enabled = true, Line1Key = "monthly", Line2Key = "completions" },
                new ProviderSettings { Id = "claude", Enabled = false, Line1Key = "weekly:fable", Line2Key = "session" },
                new ProviderSettings { Id = "gemini", Enabled = true, Line1Key = "flash-lite", Line2Key = "pro" },
                new ProviderSettings { Id = "codex", Enabled = true, Line1Key = "daily", Line2Key = "weekly" },
            ],
            RotationEnabled = false,
            RotationSeconds = 12,
            PauseOnHover = false,
            SkipUnavailable = false,
            MonitorDeviceName = @"\\.\DISPLAY2",
            Placement = WidgetPlacement.BeforeTray,
            ValueMode = ValueMode.Remaining,
            TextColor = TextColorMode.Light,
            RefreshMinutes = 10,
            StartWithWindows = true,
            ShowTrayIcon = false,
        };

        _store.Save(original);
        AppSettings loaded = new SettingsStore(_path, _log).Load();

        Assert.Equal(
            original.Providers.Select(p => (p.Id, p.Enabled, p.Line1Key, p.Line2Key)),
            loaded.Providers.Select(p => (p.Id, p.Enabled, p.Line1Key, p.Line2Key)));
        Assert.False(loaded.RotationEnabled);
        Assert.Equal(12, loaded.RotationSeconds);
        Assert.False(loaded.PauseOnHover);
        Assert.False(loaded.SkipUnavailable);
        Assert.Equal(@"\\.\DISPLAY2", loaded.MonitorDeviceName);
        Assert.Equal(WidgetPlacement.BeforeTray, loaded.Placement);
        Assert.Equal(ValueMode.Remaining, loaded.ValueMode);
        Assert.Equal(TextColorMode.Light, loaded.TextColor);
        Assert.Equal(10, loaded.RefreshMinutes);
        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.ShowTrayIcon);
    }

    [Fact]
    public void Save_WritesCamelCaseJsonWithStringEnums_AndLeavesNoTempFile()
    {
        _store.Save(new AppSettings { Placement = WidgetPlacement.BeforeStart, TextColor = TextColorMode.Dark });

        string json = File.ReadAllText(_path);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"placement\": \"beforeStart\"", json);
        Assert.Contains("\"textColor\": \"dark\"", json);
        Assert.Contains("\"line1Key\": \"session\"", json);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        _store.Save(new AppSettings { RotationSeconds = 7 });
        _store.Save(new AppSettings { RotationSeconds = 9 });

        Assert.Equal(9, _store.Load().RotationSeconds);
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(_path)!).Select(f => Path.GetFileName(f)));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"placement\": \"middle\"}")]
    public void Load_CorruptFile_BacksItUpAndReturnsDefaults(string corrupt)
    {
        WriteRaw(corrupt);

        AppSettings settings = _store.Load();

        Assert.Equal(5, settings.RotationSeconds);
        Assert.Equal(4, settings.Providers.Count);
        Assert.False(File.Exists(_path));
        Assert.Equal(corrupt, File.ReadAllText(_path + ".bak"));
        Assert.Equal(_path + ".bak", _store.BackupPath);
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains("corrupt"));
    }

    [Fact]
    public void Load_IgnoresUnknownFields_AndKeepsDefaultsForMissingOnes()
    {
        WriteRaw("""
            {
              "version": 1,
              "futureFeature": { "enabled": true },
              "rotationSeconds": 20,
              "providers": [ { "id": "gemini", "enabled": false, "line1Key": "pro", "line2Key": "flash", "color": "red" } ]
            }
            """);

        AppSettings settings = _store.Load();

        Assert.Equal(20, settings.RotationSeconds);
        Assert.True(settings.ShowTrayIcon);
        Assert.Equal(["gemini", "claude", "codex", "copilot"], settings.Providers.Select(p => p.Id));
        Assert.False(settings.Providers[0].Enabled);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".bak"));
    }

    [Fact]
    public void Load_ToleratesCommentsTrailingCommasPascalCaseAndNumericEnums()
    {
        WriteRaw("""
            {
              // hand-edited
              "RotationSeconds": 500,
              "placement": 2,
              "valueMode": "remaining",
              "refreshMinutes": 11,
              "textColor": 7,
            }
            """);

        AppSettings settings = _store.Load();

        Assert.Equal(60, settings.RotationSeconds);
        Assert.Equal(WidgetPlacement.BeforeTray, settings.Placement);
        Assert.Equal(ValueMode.Remaining, settings.ValueMode);
        Assert.Equal(10, settings.RefreshMinutes);
        Assert.Equal(TextColorMode.Auto, settings.TextColor);
    }

    [Fact]
    public void Load_NormalizesProvidersFromDisk()
    {
        WriteRaw("""
            { "providers": [ { "id": "codex" }, { "id": "codex" }, { "id": "bard" }, null ] }
            """);

        AppSettings settings = _store.Load();

        Assert.Equal(["codex", "claude", "gemini", "copilot"], settings.Providers.Select(p => p.Id));
        Assert.Equal(("5h", "weekly"), (settings.Providers[0].Line1Key, settings.Providers[0].Line2Key));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        string nested = _temp.Combine("a", "b", "settings.json");

        new SettingsStore(nested, _log).Save(new AppSettings());

        Assert.True(File.Exists(nested));
    }
}
```

- [ ] **Step 6: 실패 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~SettingsStoreTests"
```

Expected: 빌드 실패 — `error CS0246: The type or namespace name 'SettingsStore' could not be found`.

- [ ] **Step 7: `SettingsStore` 구현**

`src/AITokenUsage.Core/Settings/SettingsStore.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as camelCase JSON with string enums.
/// Thread-safe; callers should still keep it off the UI thread.
/// </summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly IAppLog _log;

    public SettingsStore(string filePath, IAppLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
        _log = log;
    }

    public string FilePath { get; }

    public string BackupPath => FilePath + ".bak";

    /// <summary>
    /// Missing file → defaults. Unreadable file → defaults (file left alone).
    /// Corrupt JSON → the file is moved to settings.json.bak and defaults are returned.
    /// The result is always normalized.
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            string json;
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new AppSettings().Normalize();
                }

                json = File.ReadAllText(FilePath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Settings file could not be read; using defaults for this session.", ex);
                return new AppSettings().Normalize();
            }

            try
            {
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is null)
                {
                    throw new JsonException("Settings root is null.");
                }

                return settings.Normalize();
            }
            catch (JsonException ex)
            {
                _log.Warn("Settings file is corrupt; moving it to settings.json.bak and using defaults.", ex);
                BackUpCorruptFile();
                return new AppSettings().Normalize();
            }
        }
    }

    /// <summary>Writes settings.json.tmp, then atomically renames it over settings.json.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string tempPath = FilePath + ".tmp";

        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                File.WriteAllText(tempPath, json, Utf8NoBom);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    private void BackUpCorruptFile()
    {
        try
        {
            File.Move(FilePath, BackupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Corrupt settings file could not be moved to settings.json.bak.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a stale .tmp file is overwritten by the next save.
        }
    }
}
```

- [ ] **Step 8: 통과 확인**

```powershell
dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~SettingsStoreTests"
```

Expected: `Passed!  - Failed:     0, Passed:    13`.

- [ ] **Step 9: 전체 빌드·테스트**

```powershell
dotnet build AITokenUsage.sln
dotnet test AITokenUsage.sln
```

Expected: `0 Warning(s)`, `0 Error(s)`; `Passed!  - Failed:     0, Passed:   198`.

- [ ] **Step 10: 커밋**

```powershell
git add src/AITokenUsage.Core/Settings tests/AITokenUsage.Core.Tests/Settings
git commit -m "feat(core): 설정 모델·정규화와 원자적 설정 저장소 추가" -m "누락 공급자 추가, 중복·알 수 없는 Id 제거, 간격 보정 규칙과 손상 파일 .bak 보관을 더한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
