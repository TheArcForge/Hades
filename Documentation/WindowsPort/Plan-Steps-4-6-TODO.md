# Windows Support, Steps 4–6 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the Windows half of Hades — a WPF shell at parity with the macOS menu-bar app, a first-class headless CLI on both platforms, the Unity plugin's Windows arm, and two signed-later MSIs built and attached by CI.

**Architecture:** Three sequential slices of `docs/superpowers/specs/2026-08-23-hades-windows-shell-design.md` (Spec #5). Slice 4 builds the WPF shell as a pure client of the control API, supervised by the `CoreSupervisor` already built in steps 1–3. Slice 5 adds onboarding, promotes the `hades` CLI to a product surface on both platforms, and gives the Unity plugin its Windows path. Slice 6 packages it: per-user MSIs for x64 and arm64, a scripted installer, and a version-lockstep gate in the release pipeline.

**Tech Stack:** WPF (`net10.0-windows`, `UseWPF` + `UseWindowsForms`), WiX v6, GitHub Actions (`windows-latest`), plus the existing `Hades.Control.Client` and `Hades.Supervision`.

---

## Read this before Task 1

**This plan runs on the Windows machine.** Steps 1–3 were developed on the Mac; from here, `dotnet build` still works there but nothing meaningful can be *run* there. Every gate below assumes Windows unless it says otherwise.

**Two things have never executed anywhere** and Task 1 is the first chance to find out if they work:
- `Windows/Hades.Supervision/JobObject.cs` and `ProcessLauncher.cs` — the Job Object and `CreateProcess` P/Invoke
- `TokenFileWriter.WriteWindows` — the atomic token DACL

If either is broken, that surfaces in Task 1 or 2, not at the end. Treat a failure there as expected work, not a crisis.

**Known accepted flake:** roughly one `Hades.Server.Tests` run in five fails ONE test with a 30-second `TimeoutException`, in a varying class, passing in isolation. Triage by that *signature*, not by class name. Anything else is real.

**Do not weaken a test to make it pass.** If an assertion genuinely cannot survive a change, that is a finding to report.

---

## What already exists

| Component | Where | State |
|---|---|---|
| `Hades.Control.Client` | `Core/src/Hades.Control.Client/` | 39 DTOs, conformance test, 42 generated fixtures, `ControlClient` with 5 read routes + `ReleaseLeaseAsync` |
| `Hades.Supervision` | `Windows/Hades.Supervision/` | `CoreSupervisor` (platform-neutral, 8 tests passing), `ICoreProcessHost` seam, `JobObject`, `ProcessLauncher` |
| `FakeCore` | `Windows/FakeCore/` | Test fixture; runs on macOS and Windows |
| `hades` CLI | `Core/src/Hades.Cli/` | On the shared client; `status` / `projects` / `release` |
| Boundary guard | 3 layers | Each proven to bite with a real violation |
| CI | `.github/workflows/ci.yml` | macOS job + Windows job (Core and Windows solutions) |

## The reference implementation

The macOS shell is the specification for behaviour. Read these while working — they are the source of truth for **what** each surface shows, even though the Windows idiom differs:

| Surface | Mac reference |
|---|---|
| Tray/menu-bar content and states | `Mac/HadesApp/Sources/HadesApp/MenuBarContent.swift`, `Views/MenuBarContentView.swift`, `Views/SupervisionFooterView.swift` |
| Icon vocabularies (5 overloads) | `Mac/HadesApp/Sources/HadesApp/StatusIcon.swift` |
| Main window sections | `MainWindow/Section.swift`, `MainWindow/Views/*.swift` (13 views) |
| View-model behaviour (~1,500 LOC) | `MainWindow/ProjectsViewModel.swift`, `TracesViewModel.swift`, `MemoryViewModel.swift`, `SettingsViewModel.swift` |
| Onboarding | `Onboarding/OnboardingStep.swift`, `Onboarding/Views/*.swift` |
| OS-fact seams | `ShellFacts/LaunchAtLoginService.swift`, `ShellFacts/ResourceGuardReader.swift` |
| Core location + spawn | `AppDelegate.swift` (see `makeConfiguration`) |

**The rule these all obey:** the shell renders, the core decides. No business logic in the shell. Every displayed string that the core can author comes from the core verbatim — the shell never re-derives, re-filters, or re-words it. A three-layer build guard enforces the reference half of this; the wording half is review's job.

---

## File structure

**Slice 4 — the shell**

| File | Responsibility |
|---|---|
| `Windows/Hades.Shell/Hades.Shell.csproj` | WPF app, `UseWPF` + `UseWindowsForms` |
| `Windows/Hades.Shell/App.xaml`, `App.xaml.cs` | Entry point, single-instance mutex, tray lifetime |
| `Windows/Hades.Shell/Tray/TrayIcon.cs` | `NotifyIcon` ownership, icon-per-state |
| `Windows/Hades.Shell/Tray/TrayMenuBuilder.cs` | Context-menu contents from `SummaryResult` + supervisor state |
| `Windows/Hades.Shell/Tray/LeaseToast.cs` | Balloon notification on lease past threshold |
| `Windows/Hades.Shell/Icons/*.ico` | 6 tray icons |
| `Windows/Hades.Shell/StatusGlyph.cs` | The 5 icon vocabularies, Segoe Fluent glyphs |
| `Windows/Hades.Shell/MainWindow.xaml{,.cs}` | Sidebar + content host |
| `Windows/Hades.Shell/Sections/Projects*.{xaml,cs}` | Projects section |
| `Windows/Hades.Shell/Sections/Traces*.{xaml,cs}` | Charon section |
| `Windows/Hades.Shell/Sections/Memory*.{xaml,cs}` | Asphodel section |
| `Windows/Hades.Shell/Sections/Settings*.{xaml,cs}` | Settings section |
| `Windows/Hades.Shell/ShellFacts/LaunchAtLogin.cs` | Run key **and** `StartupApproved\Run` |
| `Windows/Hades.Shell/ShellFacts/PowerStatus.cs` | `GetSystemPowerStatus` battery saver |
| `Windows/Hades.Shell/ViewModels/*.cs` | Ports of the Swift view models; `Dispatcher`-free |
| `Windows/Hades.Shell.Tests/**` | View-model tests, headless |

**Slice 5 — onboarding, CLI, plugin**

| File | Responsibility |
|---|---|
| `Windows/Hades.Shell/Onboarding/*.{xaml,cs}` | 4-step wizard |
| `Core/src/Hades.Cli/Commands.cs` | New commands incl. `diagnose` |
| `Core/src/Hades.Control.Client/ControlClient.cs` | Routes the new commands need |
| `UnityPlugin/Assets/Hades/Transport/HadesConnectionFile.cs` | Windows arm (C# 9 only) |
| `install.sh`, `uninstall.sh`, `Mac/HadesApp/scripts/build-app.sh` | macOS `hades` on PATH |

**Slice 6 — packaging**

| File | Responsibility |
|---|---|
| `Windows/Installer/Hades.wxs` | WiX package definition |
| `Windows/Installer/build-msi.ps1` | Publish + harvest + build, per RID |
| `install.ps1` | Scripted installer using `curl.exe` |
| `scripts/check-version-lockstep.sh` | Fails CI on a version mismatch |
| `.github/workflows/release.yml` | Windows job building and attaching both MSIs |

---

# SLICE 4 — the WPF shell

### Task 1: Scaffold the shell and prove the Windows primitives actually run

This task exists to answer one question early: **does the never-executed P/Invoke work?**

**Files:**
- Create: `Windows/Hades.Shell/Hades.Shell.csproj`
- Create: `Windows/Hades.Shell/App.xaml`, `App.xaml.cs`
- Modify: `Windows/HadesWindows.slnx`

- [ ] **Step 1: Run the existing Windows suite on Windows for the first time**

```powershell
cd C:\path\to\Hades\Windows
dotnet test --filter "Platform!=Unix"
```

Expected: **11 passing** — the 8 `CoreSupervisorTests` that already pass on macOS, plus the 3 `JobObjectTests` that have never run anywhere.

If the Job Object tests fail, that is this task's real work. Likely causes, in order: a wrong struct layout in `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`, a `SafeHandle` being collected early, or `AssignProcessToJobObject` returning `ERROR_ACCESS_DENIED`. Fix `Windows/Hades.Supervision/JobObject.cs` / `ProcessLauncher.cs`; do not weaken the tests.

- [ ] **Step 2: Run the Core suite on Windows for the first time**

```powershell
cd C:\path\to\Hades\Core
dotnet test --filter "Platform!=Unix"
```

Expected: the `TokenFileWriterTests.RestrictsToTheOwnerOnWindows` test — never executed before — now runs. It asserts `AreAccessRulesProtected` and that the only ACE is the current user.

If it fails, fix `Core/src/Hades.Core/Storage/TokenFileWriter.cs`'s `WriteWindows`. The likely culprit is `SetAccessRuleProtection(true, false)` interacting with `FileInfo.Create`'s overload — the DACL must be applied **at creation**, never narrowed afterwards.

- [ ] **Step 3: Create the shell project**

`Windows/Hades.Shell/Hades.Shell.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <!-- WinForms is here for exactly one type: System.Windows.Forms.NotifyIcon, the in-box tray
         API. WPF has no tray primitive of its own, and this avoids a third-party package. -->
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Core\src\Hades.Control.Client\Hades.Control.Client.csproj" />
    <ProjectReference Include="..\Hades.Supervision\Hades.Supervision.csproj" />
  </ItemGroup>

</Project>
```

`Windows/Directory.Build.props` already supplies the TFM, nullability, `TreatWarningsAsErrors`, and the `EnsureShellIsAClient` guard — this project inherits all of it, including the ban on referencing `Hades.Core`/`Hades.Server`.

- [ ] **Step 4: Add an application manifest declaring DPI awareness**

`Windows/Hades.Shell/app.manifest` — without this, the tray icon and window are blurry on scaled displays, which is the first thing anyone notices:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 5: Add the project to the solution and build**

```powershell
cd C:\path\to\Hades\Windows
dotnet sln HadesWindows.slnx add Hades.Shell\Hades.Shell.csproj
dotnet build
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Confirm the boundary guard covers the new project**

Temporarily add to `Hades.Shell.csproj`:

```xml
<ItemGroup><ProjectReference Include="..\..\Core\src\Hades.Core\Hades.Core.csproj" /></ItemGroup>
```

```powershell
dotnet build Hades.Shell\Hades.Shell.csproj
```

Expected: `error : Hades.Shell must not reference Hades.Core or Hades.Server. It is a control-API client by design.`

Revert the reference and rebuild clean. A guard nobody has watched fail on this project is not yet protecting it.

---

### Task 2: Single instance, tray presence, no taskbar entry

The Mac app is `LSUIElement` — menu-bar only, no Dock icon, no Cmd+Tab entry. The Windows equivalent is a tray app with no taskbar button and no window at startup.

**Files:**
- Modify: `Windows/Hades.Shell/App.xaml`, `App.xaml.cs`
- Create: `Windows/Hades.Shell/Tray/TrayIcon.cs`

- [ ] **Step 1: App.xaml with no StartupUri**

```xml
<Application x:Class="Hades.Shell.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown" />
```

`ShutdownMode="OnExplicitShutdown"` is load-bearing: the default (`OnLastWindowClose`) would quit the app the first time the user closes the main window — and quitting kills a spawned core. There is deliberately no `StartupUri`; the app starts with a tray icon and no window, matching the Mac.

- [ ] **Step 2: Single-instance mutex in App.xaml.cs**

```csharp
using System.Threading;
using System.Windows;

namespace Hades.Shell;

public partial class App : Application
{
    // macOS gets single-instance free from the bundle model; Windows does not. A second launch
    // ACTIVATES the existing window rather than exiting silently - a user who double-clicks the
    // installed app expecting it to appear must not be met with nothing happening.
    const string InstanceMutexName = @"Local\Hades.Shell.SingleInstance";

    Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
```

Implement `ActivateExistingInstance()` by broadcasting a registered window message the first instance listens for. If that proves fiddly, a named `EventWaitHandle` the first instance waits on is equally acceptable — pick one and comment why.

- [ ] **Step 3: Create the tray icon**

`Windows/Hades.Shell/Tray/TrayIcon.cs` wraps `System.Windows.Forms.NotifyIcon`. Two things must be right or the icon misbehaves in ways that look like bugs:

```csharp
// NotifyIcon MUST be disposed explicitly. A tray icon whose owning process exits without
// disposing leaves a "ghost" icon in the notification area that only vanishes when the user
// hovers over it - a classic, very visible Windows bug.
```

Set `Visible = true`, a placeholder icon for now, and `Text = "Hades"`.

- [ ] **Step 4: Hand-run**

```powershell
cd C:\path\to\Hades\Windows
dotnet run --project Hades.Shell
```

Verify, by looking: a tray icon appears; **no taskbar button**; no window. Launch a second instance and confirm the first is activated rather than a second icon appearing. Exit via the tray and confirm the icon disappears immediately rather than ghosting.

---

### Task 3: The six tray icons and the glyph vocabularies

**Files:**
- Create: `Windows/Hades.Shell/Icons/{idle,indexing,attached,leaseHeld,error,unknown}.ico`
- Create: `Windows/Hades.Shell/StatusGlyph.cs`
- Create: `Windows/Hades.Shell.Tests/StatusGlyphTests.cs`

- [ ] **Step 1: Read the Mac source of truth**

`Mac/HadesApp/Sources/HadesApp/StatusIcon.swift` has **five** `symbolName(for:)` overloads — for `ControlIconState`, `ControlSeverity`, `OperationState`, `TraceOutcome`, and `MenuBarContent`. Only the first needs `.ico` files (the tray); the rest are glyphs rendered inside the window.

- [ ] **Step 2: Write the failing test**

`Windows/Hades.Shell.Tests/StatusGlyphTests.cs`:

```csharp
using Hades.Control.Client.Dtos;

namespace Hades.Shell.Tests;

public class StatusGlyphTests
{
    [Theory]
    [InlineData(ControlIconState.Idle)]
    [InlineData(ControlIconState.Indexing)]
    [InlineData(ControlIconState.Attached)]
    [InlineData(ControlIconState.LeaseHeld)]
    [InlineData(ControlIconState.Error)]
    [InlineData(ControlIconState.Unknown)]
    public void EveryIconStateHasAGlyph(ControlIconState state)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(state)));
    }

    [Theory]
    [InlineData(ControlSeverity.Ok)]
    [InlineData(ControlSeverity.Warning)]
    [InlineData(ControlSeverity.Error)]
    [InlineData(ControlSeverity.Unknown)]
    public void EverySeverityHasAGlyph(ControlSeverity severity)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(severity)));
    }

    [Theory]
    [InlineData(OperationState.Running)]
    [InlineData(OperationState.Done)]
    [InlineData(OperationState.Failed)]
    [InlineData(OperationState.Unknown)]
    public void EveryOperationStateHasAGlyph(OperationState state)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(state)));
    }

    [Theory]
    [InlineData(TraceOutcome.Ok)]
    [InlineData(TraceOutcome.Error)]
    [InlineData(TraceOutcome.Unknown)]
    public void EveryTraceOutcomeHasAGlyph(TraceOutcome outcome)
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For(outcome)));
    }

    // The whole point of an Unknown member is that a NEWER core can add a case and an OLDER shell
    // still renders something rather than crashing. That is only true if the switch has a default.
    [Fact]
    public void AnUnrecognisedValueFallsBackRatherThanThrowing()
    {
        Assert.False(string.IsNullOrEmpty(StatusGlyph.For((ControlIconState)9999)));
    }
}
```

- [ ] **Step 3: Run — expect FAIL** (`StatusGlyph` does not exist)

```powershell
cd C:\path\to\Hades\Windows
dotnet test Hades.Shell.Tests --filter "FullyQualifiedName~StatusGlyph"
```

- [ ] **Step 4: Implement `StatusGlyph`**

Map each enum to a **Segoe Fluent Icons** codepoint (Windows 11; Segoe MDL2 Assets is the Windows 10 fallback — pick codepoints present in both where possible and note any that are not). Every switch needs a `default` arm returning the unknown glyph — that is what the last test pins.

Comment the file with the rule this obeys: *these are pictures, not words.* Like the Mac's `StatusIcon`, it picks a glyph and nothing else. It never maps a state to display **text** — the core authors every string the user reads.

- [ ] **Step 5: Run — expect PASS**

- [ ] **Step 6: Generate the six `.ico` files**

The Mac generates `AppIcon.icns` from a single 1024px PNG at build time via `sips`/`iconutil` (see `Mac/HadesApp/scripts/build-app.sh`) rather than checking a binary into the repo. Do the equivalent here if you can; if not, check in the six `.ico` files and document why.

Each `.ico` must contain at least 16×16 and 32×32 — the notification area picks by DPI, and a single-size icon looks visibly wrong on a scaled display.

- [ ] **Step 7: Hand-run** — set the tray icon per state by temporarily forcing each value, and confirm all six are visually distinguishable **at 16×16**. An icon set that only reads at 256px is not usable in a tray.

---

### Task 4: Tray menu — supervision states and ownership

**This is the task most likely to lose something.** The Mac popover is the densest safety surface in the app, and the Windows context menu must carry the same information.

**Files:**
- Create: `Windows/Hades.Shell/Tray/TrayMenuBuilder.cs`
- Create: `Windows/Hades.Shell.Tests/TrayMenuBuilderTests.cs`

- [ ] **Step 1: Read the reference and enumerate what it renders**

`Mac/HadesApp/Sources/HadesApp/MenuBarContent.swift` resolves supervisor state + last summary into four cases: `notRunning`, `restarting(attempt:)`, `failed(attempts:)`, `running(ownership:summary:)`. `Views/MenuBarContentView.swift` renders them, and `Views/SupervisionFooterView.swift` renders the ownership line.

The two strings that must survive verbatim:

```
Adopted — quitting Hades leaves it running
Started by this app — quitting stops it
```

That distinction is the difference between a user quitting the app and unknowingly killing a core another process is using.

- [ ] **Step 2: Write the failing test**

Model the menu as **data**, not as WinForms controls, so it is testable headlessly:

```csharp
namespace Hades.Shell.Tests;

public class TrayMenuBuilderTests
{
    [Fact]
    public void NotRunning_SaysSo_AndOffersNoProjectRows()
    {
        var items = TrayMenuBuilder.Build(SupervisorState.NotStarted, summary: null);

        Assert.Contains(items, i => i.Text.Contains("not running", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, i => i.Text == "Open Hades");
        Assert.Contains(items, i => i.Text == "Quit Hades");
    }

    [Fact]
    public void Restarting_ReportsTheAttemptNumber()
    {
        var items = TrayMenuBuilder.Build(new SupervisorState.Restarting(3), summary: null);

        Assert.Contains(items, i => i.Text.Contains("3"));
    }

    [Fact]
    public void Failed_ReportsHowManyAttemptsWereMade()
    {
        var items = TrayMenuBuilder.Build(new SupervisorState.Failed(5), summary: null);

        Assert.Contains(items, i => i.Text.Contains("5"));
    }

    // The ownership footer is a safety statement, not decoration: it is the only place the user
    // learns whether quitting will stop a core that something else may be using.
    [Fact]
    public void Adopted_SaysQuittingLeavesTheCoreRunning()
    {
        var items = TrayMenuBuilder.Build(
            new SupervisorState.Running(Ownership.Adopted), SummaryFixture.Idle());

        Assert.Contains(items, i => i.Text == "Adopted — quitting Hades leaves it running");
    }

    [Fact]
    public void Spawned_SaysQuittingStopsTheCore()
    {
        var items = TrayMenuBuilder.Build(
            new SupervisorState.Running(Ownership.Spawned), SummaryFixture.Idle());

        Assert.Contains(items, i => i.Text == "Started by this app — quitting stops it");
    }

    // "The shell renders, the core decides": every project row's status text comes from the core
    // verbatim. The shell never re-words it.
    [Fact]
    public void ProjectRowsRenderTheCoresOwnStatusTextVerbatim()
    {
        var summary = SummaryFixture.WithProject(name: "MyGame", status: "Indexed, 1204 nodes");
        var items = TrayMenuBuilder.Build(new SupervisorState.Running(Ownership.Spawned), summary);

        Assert.Contains(items, i => i.Text.Contains("Indexed, 1204 nodes"));
    }
}
```

Build `SummaryFixture` on top of the **generated golden corpus** at `Core/tests/Fixtures/control-api/` rather than hand-writing DTOs — those files are what the server actually emits. Read `Core/tests/Hades.Control.Client.Tests/FixtureGenerationTests.cs` for how it locates that directory from the repo root.

- [ ] **Step 3: Run — expect FAIL**

- [ ] **Step 4: Implement `TrayMenuBuilder`**

It returns a list of plain records (`Text`, `Enabled`, `Action`, `IsSeparator`). A separate thin adapter turns that into a `ContextMenuStrip`. Keep the builder free of WinForms types — that is what makes it testable.

Order, top to bottom, mirroring `MenuBarContentView`:
1. Held-lease line + `Release` (Task 5)
2. Supervision state, when not running
3. Per-project rows, keyed by `productGuid`
4. Ownership footer
5. Separator, then `Open Hades`, `Quit Hades`

- [ ] **Step 5: Run — expect PASS**

- [ ] **Step 6: Wire it to the real `NotifyIcon`** — right-click shows the menu; double-click opens the main window (Task 6).

- [ ] **Step 7: Hand-run** — with the core running, confirm project rows appear with the core's own text; quit the core and confirm the menu switches to the not-running state.

---

### Task 5: The lease line, Release, and the toast

Spec #3 §3.1 made the lease indicator *"deliberately prominent"* as net #7 of the reload-safety design: **a user must never be confused about why their code stopped compiling.** A tray icon Windows hides behind the overflow chevron is not prominent, which is why the toast exists.

**Files:**
- Modify: `Windows/Hades.Shell/Tray/TrayMenuBuilder.cs`
- Create: `Windows/Hades.Shell/Tray/LeaseToast.cs`
- Modify: `Windows/Hades.Shell.Tests/TrayMenuBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void AHeldLeaseIsTheFirstItemInTheMenu()
    {
        var summary = SummaryFixture.WithHeldLease(leaseId: "hades-script-editing", releasable: true);
        var items = TrayMenuBuilder.Build(new SupervisorState.Running(Ownership.Spawned), summary);

        Assert.Contains("hades-script-editing", items[0].Text);
    }

    [Fact]
    public void ReleaseIsOfferedWhenTheLeaseIsReleasable()
    {
        var summary = SummaryFixture.WithHeldLease(leaseId: "hades-script-editing", releasable: true);
        var items = TrayMenuBuilder.Build(new SupervisorState.Running(Ownership.Spawned), summary);

        var release = Assert.Single(items, i => i.Text == "Release");
        Assert.True(release.Enabled);
    }

    // Disabled, not hidden: the user still needs to see that a lease is held and that releasing it
    // is not currently possible - exactly what the Mac's .disabled(!lease.releasable) does.
    [Fact]
    public void ReleaseIsShownButDisabledWhenTheLeaseIsNotReleasable()
    {
        var summary = SummaryFixture.WithHeldLease(leaseId: "hades-script-editing", releasable: false);
        var items = TrayMenuBuilder.Build(new SupervisorState.Running(Ownership.Spawned), summary);

        var release = Assert.Single(items, i => i.Text == "Release");
        Assert.False(release.Enabled);
    }

    [Fact]
    public void NoLeaseMeansNoReleaseItem()
    {
        var items = TrayMenuBuilder.Build(new SupervisorState.Running(Ownership.Spawned), SummaryFixture.Idle());

        Assert.DoesNotContain(items, i => i.Text == "Release");
    }
```

- [ ] **Step 2: Run — expect FAIL**

- [ ] **Step 3: Implement** the lease line and `Release`, calling `ControlClient.ReleaseLeaseAsync(leaseId)`.

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Implement the toast**

`LeaseToast` fires a balloon (`NotifyIcon.ShowBalloonTip`) when a held lease passes the warning threshold.

**Do not invent the threshold.** It is a plugin-side value the Mac surface already keys its warning off — find it (`grep -rn "Threshold" UnityPlugin/ Core/src/`) and read it from the same source. A second hard-coded copy is precisely the drift this codebase's rules exist to prevent. Fire **once per lease acquisition**, not repeatedly on every poll.

- [ ] **Step 6: Hand-run — the real test of this task**

With a Unity project open and the plugin attached, trigger a script-editing lease (compile something). Confirm: the tray icon changes to `leaseHeld`; the menu's first item names the lease; a toast appears once; clicking `Release` releases it and the menu updates. **Then hide the tray icon in the overflow chevron and repeat** — the toast must still be what tells the user, since the icon is invisible.

---

### Task 6: Main window — sidebar and sections

**Files:**
- Create: `Windows/Hades.Shell/MainWindow.xaml`, `MainWindow.xaml.cs`
- Create: `Windows/Hades.Shell/ViewModels/MainWindowViewModel.cs`
- Create: `Windows/Hades.Shell.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Read the reference** — `Mac/HadesApp/Sources/HadesApp/MainWindow/Section.swift`. Three sections with fixed, Swift-authored titles: `Projects`, `Charon` (traces), `Asphodel` (memory). Settings is a fourth destination.

Those product names are deliberate and are **not** to be renamed to generic labels.

- [ ] **Step 2: Write the failing test**

```csharp
public class MainWindowViewModelTests
{
    [Fact]
    public void OpensOnProjects()
    {
        Assert.Equal(Section.Projects, new MainWindowViewModel().SelectedSection);
    }

    [Fact]
    public void SectionTitlesMatchTheProductVocabulary()
    {
        Assert.Equal("Projects", Section.Projects.Title());
        Assert.Equal("Charon", Section.Traces.Title());
        Assert.Equal("Asphodel", Section.Memory.Title());
        Assert.Equal("Settings", Section.Settings.Title());
    }
}
```

- [ ] **Step 3: Run — expect FAIL**

- [ ] **Step 4: Implement** `Section`, its `Title()`, and `MainWindowViewModel`.

Keep every view model **free of `Dispatcher`** so tests need no STA apartment. Marshal to the UI thread in the view layer, not the view model.

- [ ] **Step 5: Run — expect PASS**

- [ ] **Step 6: Build the window** — a `ListBox` sidebar bound to the sections, a `ContentControl` host.

- [ ] **Step 7: Implement close-to-tray**

```csharp
    // Closing HIDES; only tray Exit ends the process. A naive WPF close would exit the app, which
    // closes the Job Object handle and kills a spawned core mid-index. On macOS the LSUIElement
    // model gives this for free; here it is explicit.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
```

- [ ] **Step 8: Hand-run** — double-click the tray icon to open; close the window and confirm the app and core survive (check the core process is still alive); reopen from the tray; then `Quit Hades` and confirm both exit.

- [ ] **Step 9: Decide Fluent theming — garnish, not identity**

Spec #5 §5.5 settles this, and it constrains how you write it. WPF's `ThemeMode` (Windows 11 Fluent styling) is **evaluation-gated** in .NET 10: using it emits `error WPF0001: 'ThemeMode' is for evaluation purposes only`, and .NET 11 previews show no stabilisation work on it.

**The decision: the shell must look acceptable under the default WPF theme.** Apply `ThemeMode` where it helps, but never depend on it — an `[Experimental]`-gated API is exempt from .NET's breaking-change policy, and its control coverage is still incomplete, so a half-Fluent/half-Aero2 mix is the realistic failure mode.

Two constraints follow:

**Suppress at the call site, never project-wide.** A `<NoWarn>WPF0001</NoWarn>` in the csproj would also silence *future* experimental APIs nobody chose to adopt, and it breaches this codebase's own standard of suppressing at the single site:

```csharp
#pragma warning disable WPF0001 // ThemeMode is evaluation-only in .NET 10; see Spec #5 §5.5.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
```

**Set it in code, not XAML.** Set as a XAML attribute on `Application`, the diagnostic is raised inside the generated `.g.cs`, where no `#pragma` of ours can live — which would force the project-wide `NoWarn` and defeat the decision above.

- [ ] **Step 10: Verify both themes**

Switch Windows between light and dark (Settings → Personalization → Colors) with the app running, and confirm the window is legible in both. A shell that only works in the theme the developer happens to use is a bug users hit immediately.

---

### Task 7: Projects section

The largest section. `Mac/HadesApp/Sources/HadesApp/MainWindow/ProjectsViewModel.swift` is the reference: `refresh`, `addProject`, `removeProject`, `rebuildProject`, `installPlugin`, `revealInFinder`, `openInUnity`, plus `rebuildProgress` polling by operation id.

**Files:**
- Create: `Windows/Hades.Shell/ViewModels/ProjectsViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/ProjectsView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/ProjectsViewModelTests.cs`

- [ ] **Step 1: Read `ProjectsViewModel.swift` in full**, including `pollTrackedOperations` and `recordServerMessage`.

- [ ] **Step 2: Add the routes the section needs to `ControlClient`**

`ControlClient` currently has 5 read routes plus `ReleaseLeaseAsync`. This section needs add/remove/rebuild/installPlugin/revealInFinder/openInUnity and `GET /control/operations/{id}`.

**Confirm every route against `Core/src/Hades.Server/Control/ControlListener.cs` before writing it.** Follow the existing `SendAsync<T>` / `GetAsync<T>` / `PostAsync<T>` pattern exactly. Add tests to `Core/tests/Hades.Control.Client.Tests/ControlClientTests.cs` in the established stub-handler style.

- [ ] **Step 3: Write the failing view-model tests**

Behind a fake client interface, mirroring how the Swift side fakes `ControlProjectsFetching`. Cover at minimum:
- `refresh` populates rows
- a failed action records **the server's own message**, never invented text
- `rebuildProject` tracks the returned operation id and polls until `done`
- `removeProject` requires the confirmed flag (guard against accidental destructive action)

- [ ] **Step 4: Run — expect FAIL**

- [ ] **Step 5: Implement the view model.**

- [ ] **Step 6: Run — expect PASS**

- [ ] **Step 7: Build the view** — a project list with per-project actions, warnings rendered with `StatusGlyph.For(ControlSeverity)`, and a rebuild progress indicator.

- [ ] **Step 8: Hand-run** — add a real Unity project via the folder dialog; watch it index; hit Rebuild and watch progress; Reveal in Explorer (confirm it **selects** the folder, not just opens the parent); Open in Unity; remove it.

---

### Task 8: Charon (traces) section

**Files:**
- Create: `Windows/Hades.Shell/ViewModels/TracesViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/TracesView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/TracesViewModelTests.cs`

- [ ] **Step 1: Read `MainWindow/TracesViewModel.swift`** (240 lines) and `Views/{TracesView,TraceSequenceRowView,TraceDetailView}.swift`. The surface is sequence-first: a list of sequences, drill-in to spans, plus slow-tools and failures views.

- [ ] **Step 2: Add the trace routes to `ControlClient`**, confirmed against `ControlListener.cs` (note the literal-segment routes are matched ahead of the `{traceId}` parameter route — read that file's comment).

- [ ] **Step 3: Write failing view-model tests** covering: sequences load; selecting one loads its detail; outcome renders via `StatusGlyph.For(TraceOutcome)`; an unknown outcome does not crash.

- [ ] **Step 4: Run — expect FAIL**

- [ ] **Step 5: Implement.**

- [ ] **Step 6: Run — expect PASS**

- [ ] **Step 7: Build the view.**

- [ ] **Step 8: Hand-run** — issue a few MCP tool calls from Claude Code, confirm they appear, drill into one, and confirm a failing call is visibly distinguishable from a successful one.

---

### Task 9: Asphodel (memory) section

**Files:**
- Create: `Windows/Hades.Shell/ViewModels/MemoryViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/MemoryView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/MemoryViewModelTests.cs`

- [ ] **Step 1: Read `MainWindow/MemoryViewModel.swift`** and `Views/{MemoryView,MemoryDocumentView,MemoryProposalRowView,ProposalQueueView}.swift`. Two surfaces: authored documents, and a proposal queue with accept/dismiss/defer.

- [ ] **Step 2: Add the memory routes to `ControlClient`**, confirmed against `ControlListener.cs`.

- [ ] **Step 3: Write failing tests** — documents list; opening one fetches content; accept/dismiss/defer each call the right route and surface the server's own message.

- [ ] **Step 4: Run — expect FAIL**

- [ ] **Step 5: Implement.**

- [ ] **Step 6: Run — expect PASS**

- [ ] **Step 7: Build the view.**

- [ ] **Step 8: Hand-run** — with a project that has `.arcforge/memory/`, confirm documents render and a proposal can be accepted, then verify the change actually landed in the file on disk.

---

### Task 10: Settings and the ShellFacts

Two OS facts only the shell can observe, and one of them has a trap that a previous spec revision got wrong.

**Files:**
- Create: `Windows/Hades.Shell/ShellFacts/LaunchAtLogin.cs`
- Create: `Windows/Hades.Shell/ShellFacts/PowerStatus.cs`
- Create: `Windows/Hades.Shell/ViewModels/SettingsViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/SettingsView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/SettingsViewModelTests.cs`

- [ ] **Step 1: Read the reference** — `ShellFacts/LaunchAtLoginService.swift`. Its non-negotiable discipline: **write, then re-read the OS, and report only what the re-read says.** Never infer success from the absence of an error.

- [ ] **Step 2: Understand the Windows trap before writing any code**

Windows stores the *user's* enable/disable decision in `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, **not** by removing the `Run` value. Disable the app in Task Manager and the `Run` value stays. So an implementation that writes the `Run` value and re-reads only the `Run` value reports "on" forever while Windows never launches the app.

**Read:** enabled only when the `Run` value exists **and** `StartupApproved\Run` either has no entry or holds an enabled state.

**Write:** enabling writes the `Run` value *and* **deletes** any `StartupApproved\Run` entry — do not try to author that entry's state bytes, whose format is undocumented. Deleting returns the app to the OS's own default-enabled path, which is the honest way to express "the user just re-enabled this from inside the app."

- [ ] **Step 3: Write the failing tests**

Behind an interface so tests never touch the real registry, with a fake registry double:

```csharp
    [Fact]
    public void ReportsDisabled_WhenTheUserDisabledItInTaskManager_EvenThoughTheRunValueRemains()
    {
        var registry = new FakeRegistry();
        registry.SetRunValue("Hades", @"C:\Hades\Hades.Shell.exe");
        registry.SetStartupApprovedDisabled("Hades");

        Assert.False(new LaunchAtLogin(registry).IsEnabled);
    }

    [Fact]
    public void Enabling_WritesTheRunValue_AndClearsAnyStartupApprovedVeto()
    {
        var registry = new FakeRegistry();
        registry.SetStartupApprovedDisabled("Hades");

        var launchAtLogin = new LaunchAtLogin(registry);
        launchAtLogin.SetEnabled(true);

        Assert.True(launchAtLogin.IsEnabled);
        Assert.False(registry.HasStartupApprovedEntry("Hades"));
    }

    // The Mac's discipline, ported: never trust the requested value, always re-read.
    [Fact]
    public void ReportsWhatTheOsSays_NotWhatWasRequested()
    {
        var registry = new FakeRegistry { RefusesWrites = true };
        var launchAtLogin = new LaunchAtLogin(registry);

        launchAtLogin.SetEnabled(true);

        Assert.False(launchAtLogin.IsEnabled);
    }
```

- [ ] **Step 4: Run — expect FAIL**

- [ ] **Step 5: Implement `LaunchAtLogin`** against `Microsoft.Win32.Registry`.

- [ ] **Step 6: Implement `PowerStatus`** — `GetSystemPowerStatus` → `SystemStatusFlag` (1 = battery saver on). **Display-only in Settings**, matching the Mac's treatment of Low Power Mode; nothing else consumes it. Thermal state has no Windows analogue and is dropped.

- [ ] **Step 7: Implement `SettingsViewModel`** — `mcpPort` and `logLevel` from `GET /control/settings` (both rendered verbatim), plus the two shell facts. Note `SettingsResult` deliberately carries only those two fields.

- [ ] **Step 8: Run — expect PASS**

- [ ] **Step 9: Build the view.**

- [ ] **Step 10: Hand-run — the part unit tests cannot reach**

Toggle launch-at-login in the app; confirm the app is listed in **Task Manager → Startup**. Then **disable it in Task Manager** and confirm the app's own toggle now reads **off**. That is the bug this task exists to prevent, and only a real machine shows it.

Reboot and confirm the app actually starts.

Also check whether `SystemStatusFlag` reflects Windows 11 24H2's plugged-in "energy saver" — if it does not, drop the row rather than show it wrong, and record what you measured.

---

### Task 11: Wire supervision — the shell owns a real core

**Files:**
- Modify: `Windows/Hades.Shell/App.xaml.cs`
- Create: `Windows/Hades.Shell/CoreLifetime.cs`

- [ ] **Step 1: Read `Mac/HadesApp/Sources/HadesApp/AppDelegate.swift`'s `makeConfiguration`** — it locates the bundled core at `Contents/Resources/HadesServer/Hades.Server` and falls back to `dotnet run --project <repo>/Core/src/Hades.Server --no-launch-profile`, logging loudly which path it took.

- [ ] **Step 2: Implement the Windows equivalent**

Release: `<install>\core\Hades.Server.exe`, next to the shell. Debug: fall back to `dotnet run`, logging that it did — that fallback needs the .NET SDK and this exact source tree, which is right for development and never right for a shipped app.

- [ ] **Step 3: Hold the `JobObject` for the process lifetime**

```csharp
// Rooted in a field for the app's whole lifetime, deliberately. A JobObject eligible for
// finalization would have the kernel kill a HEALTHY core mid-session - one of the two ways a
// correct-looking Job Object implementation silently fails.
readonly JobObject _job = new();
```

- [ ] **Step 4: Start the supervisor on launch, stop it on Quit**

`Quit Hades` calls `StopAsync()` — the **graceful** path. The Job Object is only the backstop for when the app never got to run it.

- [ ] **Step 5: Reflect supervisor state in the tray** — icon and menu update as the state changes.

- [ ] **Step 6: Hand-run the whole supervision contract.** This is the gate for Slice 4, and each of these is a distinct failure mode:

| Scenario | Expected |
|---|---|
| Launch with no core running | Core spawns; tray goes live |
| Launch with a core already running (`hades serve`) | **Adopted**; footer says "quitting leaves it running" |
| Quit the app after adopting | **Core survives** |
| Quit the app after spawning | Core exits |
| **End Task the app from Task Manager after spawning** | **Core dies too** — the Job Object's whole reason for existing |
| Kill the core externally | Shell restarts it with visible backoff |
| Kill the core repeatedly | After 5 attempts, state shows `Failed` |

The End Task row is the one that has never been verified anywhere. Check with Task Manager that no orphaned `Hades.Server.exe` remains.

---

# SLICE 5 — onboarding, CLI, and the Unity plugin

### Task 12: Onboarding — four steps, reworded

**Files:**
- Create: `Windows/Hades.Shell/Onboarding/OnboardingWindow.xaml{,.cs}`
- Create: `Windows/Hades.Shell/Onboarding/OnboardingViewModel.cs`
- Create: `Windows/Hades.Shell.Tests/OnboardingViewModelTests.cs`

- [ ] **Step 1: Read the reference** — `Onboarding/OnboardingStep.swift` has five cases: `install`, `permissions`, `claudeCode`, `projects`, `unityPlugin`.

**Windows has four.** `permissions` is macOS TCC folder access; Windows has no equivalent prompt, and explaining one that never fires would be a lie.

- [ ] **Step 2: Note the copy trap**

`Onboarding/Views/OnboardingInstallStepView.swift` hardcodes:

> "…five steps, and you can stop after the fourth with a fully working setup."

That is Swift-authored copy, not API-served. The Windows text must be **reworded, not ported** — it now has four steps.

- [ ] **Step 3: Write the failing tests**

```csharp
    [Fact]
    public void HasFourSteps_PermissionsIsNotOneOfThem()
    {
        var steps = OnboardingViewModel.AllSteps;

        Assert.Equal(4, steps.Length);
        Assert.DoesNotContain(OnboardingStep.Permissions, steps);
    }

    [Fact]
    public void StepsAreInAFixedOrder()
    {
        Assert.Equal(
            [OnboardingStep.Install, OnboardingStep.ClaudeCode, OnboardingStep.Projects, OnboardingStep.UnityPlugin],
            OnboardingViewModel.AllSteps);
    }

    [Fact]
    public void CopyDoesNotClaimFiveSteps()
    {
        foreach (var step in OnboardingViewModel.AllSteps)
            Assert.DoesNotContain("five steps", OnboardingViewModel.CopyFor(step), StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 4: Run — expect FAIL**

- [ ] **Step 5: Implement** the view model and the four step views.

The Claude Code step ports `Onboarding/ClaudeCodeVerifying.swift`: it reads `GET /control/settings` for the MCP port, then makes a raw `tools/list` JSON-RPC call to `http://127.0.0.1:{port}/mcp`. Read that file's doc comment for **what that proves and what it only assumes** — it proves the core is serving N tools, not that Claude Code has connected.

- [ ] **Step 6: Run — expect PASS**

- [ ] **Step 7: Show onboarding on first run only** — port `Onboarding/OnboardingCompletionTracking.swift`'s persistence idea to a per-user store.

- [ ] **Step 8: Hand-run** — on a machine with no prior Hades state, walk all four steps and finish with a working setup.

---

### Task 13: CLI — the remaining commands

Spec #5 §5.4 promotes the CLI from a diagnostic to a product surface, **on both platforms**.

**Files:**
- Modify: `Core/src/Hades.Cli/Commands.cs`, `Program.cs`
- Modify: `Core/src/Hades.Control.Client/ControlClient.cs`
- Modify: `Core/tests/Hades.Cli.Tests/CommandsTests.cs`
- Modify: `Core/src/Hades.Cli/Program.cs` header comment

- [ ] **Step 1: Retire the stale header**

`Program.cs` opens with *"NOT a product deliverable: its purpose is diagnostic."* That is no longer true. Rewrite it to say what the CLI now is: the supported headless path on both platforms, and the second consumer of `Hades.Control.Client`.

- [ ] **Step 2: Add commands** — `add-project <path>`, `remove-project <guid>`, `rebuild <guid>`, `install-plugin <guid>`, `traces`, `memory`.

Each is a thin call against a route that already exists, holding the existing "deliberately dumb" rule: print what the core decided, compute nothing, invent no text. Most routes were added to `ControlClient` in Tasks 7–9; add any that are still missing, confirmed against `ControlListener.cs`.

- [ ] **Step 3: Write tests** in `CommandsTests.cs`'s established style — against a **real** loopback `ControlListener`, not a mock. That property is why these tests are trustworthy; preserve it.

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Implement `hades serve`**

Runs the core in the foreground of the calling terminal and exits with it. **Deliberately no supervised `hades start`** — supervision is the shell's job, and a CLI that spawned a detached unsupervised core would violate the "no hanging state" rule.

This composes with what already exists: a core started by `hades serve` is simply **adopted** by the shell if one launches later, which is exactly what `Ownership.Adopted` was built for. Verify that end to end.

- [ ] **Step 6: Hand-run on both platforms** — every command on Windows, and the same set on the Mac to confirm nothing regressed there.

---

### Task 14: `hades diagnose`

§9.1 names this as the mitigation for the entire class of environmental failures CI cannot reach — OneDrive placeholders, antivirus locking, long paths, non-default Hub locations. For a maintainer who cannot reproduce those, one command a reporter can run is worth more than more tests.

**Files:**
- Modify: `Core/src/Hades.Cli/Commands.cs`
- Create: `Core/tests/Hades.Cli.Tests/DiagnoseTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Diagnose_ReportsTheEnvironmentEvenWhenNoCoreIsRunning()
    {
        var output = new StringWriter();

        await Commands.DiagnoseAsync(client: null, output, root: "/nonexistent-root");

        var text = output.ToString();
        Assert.Contains("Hades diagnostics", text);
        Assert.Contains("/nonexistent-root", text);      // the resolved storage root, named
        Assert.Contains(RuntimeInformation.OSDescription, text);
        Assert.Contains("not running", text, StringComparison.OrdinalIgnoreCase);
    }
```

"No core running" is the **most likely** state when someone runs `diagnose` in anger, so it must produce a useful report rather than an error.

- [ ] **Step 2: Run — expect FAIL**

- [ ] **Step 3: Implement**, reporting: OS version and edition; process architecture and whether it is emulated (`RuntimeInformation.ProcessArchitecture` vs `OSArchitecture`); the resolved storage root and whether it exists; whether `control.token` is present and parseable; core version and uptime from `/control/ping` if reachable; per-project paths, index state and node counts; and whether each project path is under OneDrive (check for `OneDrive` in the path and for a reparse point).

**No secrets.** The bearer token must never be printed — this output goes into bug reports. Print only whether the file exists and parses.

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Hand-run on Windows** with a real project, and check the output would actually help you diagnose a report from a stranger.

---

### Task 15: The Unity plugin's Windows arm — measured, not assumed

**This file exists because of the exact hazard this task reintroduces.** `HadesConnectionFile.cs`'s doc comment records that Unity's Mono resolves `SpecialFolder.ApplicationData` to `~/.config` while .NET 10 resolves it to `~/Library/Application Support` — same machine, same enum, different answer.

**Files:**
- Modify: `UnityPlugin/Assets/Hades/Transport/HadesConnectionFile.cs`

- [ ] **Step 1: Measure first, before writing the branch**

In a Windows Unity project, add a throwaway Editor script:

```csharp
using UnityEditor;
using UnityEngine;

public static class HadesPathProbe
{
    [MenuItem("Hades/Probe Paths")]
    public static void Probe()
    {
        Debug.Log($"ApplicationData      = {System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)}");
        Debug.Log($"LocalApplicationData = {System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)}");
        Debug.Log($"UserProfile          = {System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)}");
    }
}
```

Run it, and separately run `hades diagnose` to see what the **core** resolved. **Record both.** The core writes `editor.token` under `LocalApplicationData` on Windows (Spec #5 §6.4); the plugin must read the same directory.

- [ ] **Step 2: Write the branch to match what you measured**

The file is **C# 9 only** (Unity 6000.3's ceiling) — no file-scoped namespaces, no target-typed `new`, no records. It is also embedded as a resource in `Hades.Core.dll`, so changing it changes the core build.

Keep the existing structure: `HADES_HOME` wins if set, else the per-platform default. Add a Windows arm anchored on whatever your measurement proved correct, and update the doc comment with the measured values and the date — that comment is the reason the next person will not guess.

- [ ] **Step 3: Rebuild the core** so the embedded resource updates:

```powershell
cd C:\path\to\Hades\Core
dotnet build
dotnet test tests\Hades.Core.Tests --filter "FullyQualifiedName~PluginInstaller"
```

`Install_MatchesTheRealPluginSourceTreeExactly` compares the embedded copy against the files on disk — it will catch a stale embed.

- [ ] **Step 4: End-to-end hand-run — the gate for Slice 5**

Install the plugin into a real Windows Unity project from the shell, open the project, and confirm the Editor **attaches** — the tray icon goes to `attached` and the project row says so. Then exercise a live-editor MCP tool from Claude Code and confirm it round-trips.

---

### Task 16: `hades` on PATH — both platforms

**Files:**
- Modify: `install.sh`, `uninstall.sh`
- Modify: `Mac/HadesApp/scripts/build-app.sh`

Windows gets `hades` on PATH from the MSI (Task 19). macOS needs an answer too — Spec #5 §5.4 promises the CLI "on both platforms", and this is the only change this whole port makes to shipped macOS code.

- [ ] **Step 1: Publish `hades` into the app bundle**

In `build-app.sh`'s Release path, alongside the existing `dotnet publish` of `Hades.Server`, publish `Hades.Cli` into `Contents/Resources/`. Follow the existing step's conventions exactly — self-contained, same RID, same "why" comment density.

- [ ] **Step 2: Symlink it from `install.sh`**

```bash
# The one part of this installer that touches anything outside /Applications. Done only when
# /usr/local/bin already exists and is writable - never created, never sudo'd. If it cannot be
# made, say so and print the full path rather than failing an otherwise-good install.
```

- [ ] **Step 3: Remove it in `uninstall.sh`** — `uninstall.sh` already promises to remove the sidecars a drag-to-Trash leaves behind; a dangling symlink is exactly that.

- [ ] **Step 4: Make `hades serve` find the core**

On macOS it resolves relative to its own location (`Contents/Resources/HadesServer/Hades.Server`), falling back to `dotnet run` — the same two-mode design the shell uses.

- [ ] **Step 5: Hand-run on the Mac** — build a Release `.app`, run `install.sh`, confirm `hades status` works from a fresh terminal, run `uninstall.sh --dry-run` and confirm the symlink is listed, then actually uninstall and confirm it is gone.

---

# SLICE 6 — packaging and release

### Task 17: WiX scaffold and the x64 MSI

**Files:**
- Create: `Windows/Installer/Hades.wxs`
- Create: `Windows/Installer/build-msi.ps1`

- [ ] **Step 1: Accept the WiX EULA**

```powershell
dotnet tool install --global wix
wix eula
```

WiX v6+ carries an Open Source Maintenance Fee, but its EULA §1 applies it only to revenue-generating users with annual gross revenue ≥ US$10,000 — **free for this project**. Accept once.

- [ ] **Step 2: Write `Hades.wxs`**

Per-user install to `%LOCALAPPDATA%\Programs\Hades`:

```xml
<Package Name="Hades" Manufacturer="ArcForge" Version="$(var.Version)"
         UpgradeCode="PUT-A-STABLE-GUID-HERE-ONCE-AND-NEVER-CHANGE-IT" Scope="perUser">
```

Generate the `UpgradeCode` GUID **once** and never change it — it is what makes upgrades replace rather than install alongside.

Payload: `Hades.Shell.exe`, `hades.exe`, `core\` (the self-contained publish), and the icons. Include `MajorUpgrade` with a downgrade error message, a Start Menu shortcut, and a per-user `Environment` element putting the install directory on `PATH`.

Launch-at-login stays an **in-app setting**, not an MSI feature, matching the Mac.

- [ ] **Step 3: Add a launch condition for the OS floor**

An MSI `LaunchCondition` can check `VersionNT`/build number. It **cannot** check Windows *edition*, so the "Windows 10 Enterprise/IoT/LTSC only" nuance in .NET 10's supported-OS list cannot be enforced by the installer — it is a documented support statement, not a gate. Do not describe it as prevented.

- [ ] **Step 4: Write `build-msi.ps1`** — takes a RID (`win-x64` / `win-arm64`) and a version, does `dotnet publish` of `Hades.Shell` and `Hades.Cli` self-contained for that RID, then `wix build` producing `Hades-<version>-<rid>.msi`.

- [ ] **Step 5: Build the x64 MSI**

```powershell
cd C:\path\to\Hades\Windows\Installer
.\build-msi.ps1 -Rid win-x64 -Version 2.1.0
```

- [ ] **Step 6: Hand-run install/uninstall/upgrade**

Install it. Verify: **no UAC prompt**; it appears in Settings → Apps; `hades status` works from a **new** terminal (PATH takes a new session); the Start Menu shortcut launches it. Then build a `2.1.1` MSI and confirm it **upgrades in place** rather than installing alongside. Then uninstall and confirm the install directory and PATH entry are gone — and that `%LOCALAPPDATA%\Hades` (the data root) is **left alone**, matching `uninstall.sh`'s promise never to destroy authored data.

---

### Task 18: The arm64 MSI

- [ ] **Step 1: Build it**

```powershell
.\build-msi.ps1 -Rid win-arm64 -Version 2.1.0
```

An MSI carries one architecture, so this is a second artifact, not a second payload in the same file.

- [ ] **Step 2: Be honest about verification**

`dotnet publish -r win-arm64` was verified on the Mac to *produce files*. **No arm64 binary from this project has ever been executed.**

If ARM64 Windows hardware is available, install and run it, and record that. If not, ship it **labelled untested** in the release notes — do not describe an unexecuted binary as verified, and do not quietly drop it either.

- [ ] **Step 3: Record which you did**, in `Documentation/ReleasePipeline.md`.

---

### Task 19: `install.ps1` and the Mark-of-the-Web measurement

**Files:**
- Create: `install.ps1`

- [ ] **Step 1: Measure MotW before writing the script**

Download the MSI three ways and inspect each:

```powershell
curl.exe -L -o msi-curl.msi <url>
Invoke-WebRequest -Uri <url> -OutFile msi-iwr.msi
# and once through a browser
Get-Item msi-*.msi | ForEach-Object { $_.Name; Get-Content $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue }
```

Expected from the research: `curl.exe` (in-box since Windows 10 1803) writes **no** `Zone.Identifier`; a browser does. `Invoke-WebRequest`'s behaviour is contested — **this is the measurement that settles it.** Record all three results.

- [ ] **Step 2: Write `install.ps1` using whichever proved clean** (expected: `curl.exe`), mirroring `install.sh`'s structure: version and SHA256 pinned at the top, checksum verified before install, refuses to run as admin, refuses on the wrong architecture, and states plainly what it does and does not do.

- [ ] **Step 3: Verify the SmartScreen experience end to end, both paths**

Browser download → expect the *"Windows protected your PC"* dialog, default button **Don't run**, past which the user must click **More info → Run anyway**, publisher shown as *Unknown*.

`install.ps1` path → expect no interstitial.

**Record both verbatim**, including exact wording, and put the browser-path description into `Documentation/Installing.md`. Users deserve to be told what they will see rather than discovering it.

- [ ] **Step 4: Note the Smart App Control caveat** — on Windows 11 machines with SAC enabled (clean installs only), unsigned code is blocked outright with no override. If you can test on such a machine, do; if not, say so.

---

### Task 20: Version lockstep gate

`Documentation/ReleasePipeline.md` §2 lists the sites that must move together. That table has already failed twice in one release cycle — `plugin.json` sat at `0.1.0` through the entire 2.0.0 cycle because nothing compared it to anything. Two MSIs and an `install.ps1` make the table longer.

**Files:**
- Create: `scripts/check-version-lockstep.sh`
- Modify: `Documentation/ReleasePipeline.md`

- [ ] **Step 1: Read §2's table** and enumerate every current site, including the new Windows ones.

- [ ] **Step 2: Write the script** — takes the intended version, greps each site, prints a table of site → found version, exits non-zero on any mismatch. Generalises the existing `plugin.json`-vs-tag gate in `release.yml` to every row.

- [ ] **Step 3: Prove it fails**

```bash
bash scripts/check-version-lockstep.sh 2.1.0
```
Deliberately bump one site to a wrong value, re-run, confirm it names that site and exits non-zero, then revert. A gate nobody has watched fail is not a gate.

- [ ] **Step 4: Update §2's table** with the new sites.

---

### Task 21: Release pipeline — build and attach both MSIs

**Files:**
- Modify: `.github/workflows/release.yml`

- [ ] **Step 1: Read `release.yml` in full**, including its comments about the mistakes it already encodes — the plugin repo being tagged by accident, the 61 MB DMG published to the wrong repo.

- [ ] **Step 2: Add the lockstep gate** as an early step, so a mismatch fails before anything is built or pushed.

- [ ] **Step 3: Add a `windows-latest` job** that builds both MSIs and attaches them to the release with `gh release upload`.

Building **and attaching** from CI is the point: the previous manual attach step is exactly where the documented mistakes happened.

- [ ] **Step 4: Dry-run it** using the workflow's existing `workflow_dispatch` dry-run input. Confirm both MSIs are produced and the upload is attempted without publishing.

- [ ] **Step 5: Note the asymmetry** — the DMG is still built locally on the Mac while the MSIs come from CI. Two artifacts with two provenances is a drift risk. Record it in `ReleasePipeline.md` as known debt, with moving the DMG into CI as the fix.

---

### Task 22: Documentation

**Files:**
- Modify: `README.md`, `LIMITATIONS.md`, `Documentation/Architecture.md`, `Documentation/Installing.md`, `Documentation/ReleasePipeline.md`

- [ ] **Step 1: `README.md`** — platform badges, prerequisites, and the **beta** label for Windows.

- [ ] **Step 2: `LIMITATIONS.md`** — the Maturity section currently says *"macOS is the ONLY tested platform."* Update it, and add §9.1's environmental classes: long paths, OneDrive placeholders, antivirus locking WAL files, AppLocker/WDAC blocking `%LOCALAPPDATA%`, non-default Unity Hub drives.

- [ ] **Step 3: `Documentation/Architecture.md`** — §2.2 describes only the Mac shell; §8 only the DMG. Both need the Windows half. Follow that document's own convention: where it drifts from the code, the code is right.

- [ ] **Step 4: `Documentation/Installing.md`** — the Windows path, and §8.2's honest SmartScreen description from Task 19's measurement.

- [ ] **Step 5: `Documentation/ReleasePipeline.md`** — the Windows build steps and the §2 table updates from Task 20.

- [ ] **Step 6: Update Spec #5's status header** to record that steps 4–6 are implemented, and correct anything implementation disproved. Steps 1–3 corrected the spec four times; expect the same here, and treat it as the spec working rather than failing.

---

## Verification checklist

Slice 4:
- [ ] `dotnet test --filter "Platform!=Unix"` green in `Windows/` — including the 3 Job Object tests, executing for the first time
- [ ] `TokenFileWriterTests.RestrictsToTheOwnerOnWindows` passes on Windows
- [ ] Tray icon shows all six states, legible at 16×16
- [ ] Tray menu carries lease + Release, supervision states, project rows, ownership footer
- [ ] Closing the window hides it; only Quit exits
- [ ] **End Task on the app leaves no orphaned core**
- [ ] Adopted core survives app quit

Slice 5:
- [ ] Onboarding completes in four steps; no copy claims five
- [ ] Every CLI command works on Windows **and** macOS
- [ ] `hades serve` core is adopted by the shell
- [ ] `hades diagnose` is useful with no core running
- [ ] Unity Editor attaches from a real Windows project
- [ ] Plugin and core agree on the token path — **measured, not assumed**

Slice 6:
- [ ] Per-user MSI installs with no UAC prompt
- [ ] `hades` on PATH in a new terminal
- [ ] Upgrade replaces in place; uninstall leaves the data root alone
- [ ] MotW behaviour measured for all three download paths
- [ ] Lockstep script proven to fail on a mismatch
- [ ] Both MSIs built and attached by CI
- [ ] arm64 either executed on real hardware, or shipped explicitly labelled untested
