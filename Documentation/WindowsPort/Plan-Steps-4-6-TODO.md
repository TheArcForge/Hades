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
| `Windows/Hades.Shell/Icons/*.ico` | 7 tray icons, plus `app.ico`, the product icon |
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

- [x] **Step 1: Run the existing Windows suite on Windows for the first time**

```powershell
cd C:\path\to\Hades\Windows
dotnet test --filter "Platform!=Unix"
```

Expected: **11 passing** — the 8 `CoreSupervisorTests` that already pass on macOS, plus the 3 `JobObjectTests` that have never run anywhere.

If the Job Object tests fail, that is this task's real work. Likely causes, in order: a wrong struct layout in `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`, a `SafeHandle` being collected early, or `AssignProcessToJobObject` returning `ERROR_ACCESS_DENIED`. Fix `Windows/Hades.Supervision/JobObject.cs` / `ProcessLauncher.cs`; do not weaken the tests.

- [x] **Step 2: Run the Core suite on Windows for the first time**

```powershell
cd C:\path\to\Hades\Core
dotnet test --filter "Platform!=Unix"
```

Expected: the `TokenFileWriterTests.RestrictsToTheOwnerOnWindows` test — never executed before — now runs. It asserts `AreAccessRulesProtected` and that the only ACE is the current user.

If it fails, fix `Core/src/Hades.Core/Storage/TokenFileWriter.cs`'s `WriteWindows`. The likely culprit is `SetAccessRuleProtection(true, false)` interacting with `FileInfo.Create`'s overload — the DACL must be applied **at creation**, never narrowed afterwards.

- [x] **Step 3: Create the shell project**

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

- [x] **Step 4: Add an application manifest declaring DPI awareness**

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

- [x] **Step 5: Add the project to the solution and build**

```powershell
cd C:\path\to\Hades\Windows
dotnet sln HadesWindows.slnx add Hades.Shell\Hades.Shell.csproj
dotnet build
```

Expected: Build succeeded, 0 warnings.

- [x] **Step 6: Confirm the boundary guard covers the new project**

Temporarily add to `Hades.Shell.csproj`:

```xml
<ItemGroup><ProjectReference Include="..\..\Core\src\Hades.Core\Hades.Core.csproj" /></ItemGroup>
```

```powershell
dotnet build Hades.Shell\Hades.Shell.csproj
```

Expected: `error : Hades.Shell must not reference Hades.Core or Hades.Server. It is a control-API client by design.`

Revert the reference and rebuild clean. A guard nobody has watched fail on this project is not yet protecting it.

#### Outcome — 2026-08-29, first run on Windows 11 (x64, .NET SDK 10.0.400)

**Both never-executed paths work.** The Job Object / `CreateProcess` P/Invoke passes 11/11, and
`TokenFileWriter.WriteWindows` passed unmodified — the DACL this step was written around needed no
fixing. Four defects were found and fixed instead:

1. `JobObjectTests`' own `IsProcessInJob` declared the optional job handle as `SafeFileHandle?`.
   `[LibraryImport]`'s generated marshaller dereferences unconditionally, so passing the documented
   `NULL` threw `NullReferenceException`. Now `IntPtr`.
2. `dotnet test` never built `FakeCore`, so 8 of 11 tests failed on `FileNotFoundException`. FakeCore
   is launched as a process, not referenced, so it sat outside the build closure. **`ci.yml`'s
   Windows job has the same defect** and would have failed identically on its first run. Fixed at the
   root with a `ReferenceOutputAssembly="false"` dependency.
3. **SQLite connection pooling — 662 of 694 Core-suite failures.** Pooling is on by default, so a
   disposed connection returns its handle to the pool and the file stays open; POSIX allows unlinking
   an open file, Windows does not, so every `Dispose()` deleting a temp dir threw. `Pooling = false`
   on the three connection strings fixed all of them, introduced zero new failures, and cost no
   measurable runtime. This is likely also the pre-existing macOS teardown flake `TeardownDiagnostics`
   was built to investigate.
4. `AppPathsTests` hardcoded `/` separators in five expectations, and
   `DefaultRoot_IsUnderTheOsApplicationDataFolder` asserted the roaming folder when the Windows port
   uses `LocalApplicationData` — now Unix-gated, since the two trait-gated tests below it already
   cover both platforms exactly.

Two deviations from Step 3/4's literal text, both forced and both commented in the csproj:
`<Using Remove="System.Windows.Forms" />` (WPF + WinForms implicit usings make `Application`,
`MessageBox`, `Clipboard` ambiguous), and `NoWarn=WFO0003` (the WinForms analyzer rejects manifest
DPI settings, but its suggested `ApplicationHighDpiMode` only feeds WinForms' bootstrap, which a WPF
app never calls — following it would silently lose DPI awareness).

**Core suite went 694 → 27 failures at the time of this step**, and the triage below later took that
to **0**. Core is green on Windows.

#### Triage (2026-08-30) — 27 failures out of 1,939, in six clusters

`Hades.Cli.Tests` 37/37, `Hades.Contract.Tests` 119/119, `Hades.Control.Client.Tests` 49/49 all
green. The failures are 25 in `Hades.Core.Tests` and 2 in `Hades.Server.Tests`.

**One is a genuine product bug. The other 26 are environmental or test-authoring defects.**

| # | Cluster | Verdict |
|---|---|---|
| 4 | Live `ObservationService` watching | **PRODUCT BUG — FIXED, see below** |
| 9 | Local `file:` packages | Test bug — **fixed** |
| 5 | SQLite file handles | Test bug — **fixed** |
| 2 | Platform-specific assertions | Test bug — **fixed** |
| 1 | Socket close | Test bug — **fixed** |
| 6 | Symlink creation | Environmental — needs `SeCreateSymbolicLinkPrivilege`, not fixable in code |

#### Result: 27 → 0. The whole suite is green on Windows.

| suite | before | after |
|---|---|---|
| `Hades.Core.Tests` | 25 failed / 854 passed | **0 failed / 879 passed** |
| `Hades.Server.Tests` | 2 failed / 853 passed | **0 failed / 855 passed** |
| `Hades.Cli.Tests` | green | 37 passed |
| `Hades.Contract.Tests` | green | 119 passed |
| `Hades.Control.Client.Tests` | green | 49 passed |
| `Hades.Shell.Tests` · `Hades.Supervision.Tests` | green | 205 passed |
| **total** | | **2,144 tests, 0 failed** |

The last 6 were the symlink-privilege class and needed no code change: **enabling Developer Mode**
(`HKLM\…\AppModelUnlock\AllowDevelopmentWithoutDevLicense = 1`) turned all six green, which confirms
the environmental diagnosis rather than merely asserting it. That setting is a prerequisite for
running this suite on Windows and belongs in contributor setup notes; GitHub's `windows-latest`
runners are admin, so CI never needed it.

**One caveat recorded rather than buried.** A single run immediately after the Windows suite reported
2 Core failures and took 56 s against a usual 13 s. Four consecutive re-runs were clean at 12–14 s,
and no TRX was captured for the bad run, so those two tests cannot be named. Machine contention is
the likely cause, but this is **an unexplained flake, not a proven non-issue** — if Core ever shows
scattered failures alongside a wildly inflated duration, that is this, and it is worth capturing a
TRX at the time.

> **Characterised on 2026-09-01, and it is no longer unexplained.** Six consecutive
> `dotnet test Core/Hades.sln --filter "Platform!=Unix"` runs: **four had failures, two were clean.**
> The failing SET varies run to run — `ProjectsBuildAsyncTests.Add_ValidUnityProject_...`,
> `AnimationApplyTests...`, two different `InspectToolTests` — but one test was present in **all four**
> failing runs: `Hades.Core.Tests.Observation.ObservationServiceTests.ALiveChangeIsIndexedWithoutARestart`.
>
> That test passes **5 of 5 in isolation, at ~400 ms each**, and `Hades.Core.Tests` alone passes
> 882/882. It only fails when the whole solution runs, which executes several test assemblies
> concurrently. It is a FILE-WATCHER test, so it is timing-sensitive by construction: under enough
> parallel filesystem load the change it is waiting for does not arrive inside its window.
>
> **A tested and REJECTED hypothesis:** that a live Hades core, sweeping projects on its five-minute
> timer, was the contending load. Three runs with the core stopped still produced failures in two of
> them, with the same test leading. The contention is the test run's own.
>
> Practical consequence: scattered failures in a full-solution run are this and not a regression.
> Confirm by re-running the affected assembly alone before investigating.
>
> **Fixed for the watcher test, 2026-09-01 — and the suite is still flaky, which is the honest
> result.** A captured TRX showed the test consuming its full 8 s ceiling rather than failing an
> assertion, so it was latency, not a lost event. The cause was self-inflicted: the wait called
> `service.Search(...)` every 100 ms — up to **eighty SQLite opens per waiting test**, across five
> such tests in that class — so a watcher test was generating a large share of the contention that
> made it miss its own deadline. All five now wait on the service's own `ProjectSynced` event
> instead, which costs no I/O at all while waiting, subscribing before the change so the signal
> cannot be missed. The graph assertion is separate, so "synced but the node is absent" reads as an
> indexing defect rather than as slowness. The unused `Eventually` helper was deleted.
>
> **Measured after: `ALiveChangeIsIndexedWithoutARestart` appeared in 0 of 8 full-solution runs**,
> against 4 of 4 failing runs before. If its old rate had held, eight clean appearances would be
> about a 1-in-6000 coincidence, so the fix is real.
>
> **But the suite's overall failure rate did not move: 4 of 8 runs still failed, against 4 of 6
> before.** The failures are now entirely different tests — `InspectToolTests` (three distinct
> tests across runs) and `AckGapTests.VerifierConfirmsNotApplied_ThrowsClearRetrySafeError`. So the
> watcher test was never the only cause; it was the loudest one. The remaining two share the same
> shape of defect:
>
> - `InspectToolTests` is `IClassFixture<WebApplicationFactory<Program>>` — it boots the real
>   `Program`, and `McpBinding`'s own doc comment already records these racing the fixed port 7823.
> - `AckGapTests` sets hard `CommandTimeout = 2 s` and `CharonProbeTimeout = 5 s`, which parallel
>   load exceeds easily.
>
>
> **Later the same day: 5 consecutive clean full-solution runs — and that is NOT evidence of a fix.**
> The 4-of-8 baseline above was measured on the SAME code, after the ObservationServiceTests change,
> so nothing in the repository changed between the two samples. What changed is the machine: several
> sign-out cycles left Discord, Adobe and Unity not running, and total load lower. That is exactly
> what a load-dependent flake predicts, and it is the trap this note exists to prevent — a green run
> here means "the machine was quiet", not "the tests are sound". Judge this by per-assembly runs and
> by repeated full-solution samples, never by one green result.
>
> **2026-09-02: a THIRD test joins them**, found on the pre-push full run.
> `EditorsProgramWiringTests.ControlListener_ReleaseAction_ReachesTheRealAttachedEditor_ThroughTheSharedEditorProxy`
> failed once in a full-solution run (869 of 870), then passed **3 of 3 in isolation** and **2 of 2**
> as a whole assembly immediately afterwards. Same signature as the other two: it boots real wiring
> and a real editor proxy, so it is timing-sensitive under parallel load. Nothing in that area was
> touched by the change being pushed. Recording it because the roster matters — the flaky set is not
> the two tests this note originally named, and a future green run proves no more than the last one.
> **Neither is fixed.** Do not read a clean run as proof of anything here; use per-assembly runs.

**What the fixes were.** Two new shared test helpers, so each defect has one definition rather than
N patched call sites:

- `Core/tests/Hades.Core.Tests/ManifestJson.cs` — builds manifest JSON with the path escaped by
  `JsonSerializer` rather than pasted in raw. Nine call sites across five files now use it.
  Escaping is delegated rather than hand-rolled as `Replace("\\", "\\\\")`, which is the obvious fix
  and happens to handle only the one character that broke here.
- `Core/tests/Hades.Core.Tests/TestSqlite.cs` — a `Pooling=false` connection string for the raw
  `SqliteConnection`s tests open beside the product's own. Five sites; two of them were not failing
  yet and were latent.

`AnUnreadableDirectory_PreservesItsNodes_OnFullReindex` gained `[Trait(Platform, Unix)]` — its
`#pragma` already *said* "POSIX-only test" while nothing enforced it. A Windows equivalent needs an
ACL denying traverse rather than a chmod: a rewrite, not a trait, and not attempted.

`V12DetectorTests.Detect_ManifestEntry_FileForm` now uses an absolute path in the running platform's
shape. It was pinning the literal it wrote (`/Users/mike/…`) rather than the contract; that path *is*
rooted on Windows, just without a drive, so `Path.GetFullPath` correctly returned `D:\Users\mike\…`.

`EditorListenerTests.WrongToken_…` now accepts either spelling of "the server hung up". It leaves a
line unread in the server's receive buffer, so the close sends RST rather than FIN and Windows
surfaces `WSAECONNRESET` instead of EOF. Its passing sibling half-closes cleanly after sending
nothing, which is exactly why that one gets the FIN its assertion expects.

**One failure surfaced only after the others were fixed**, which is the useful kind of progress:
`SkipsALocalPackageThatIsAnAncestorOfTheProjectRoot` had been failing on the manifest JSON; once it
could run, it failed in cleanup instead. It opened its graph database with `using var db` — disposed
at *method end* — inside the very directory it then deleted. Unix unlinks an open file happily;
Windows refuses. Fixed with an explicit `using` scope that closes the database before the delete.
Checked the same shape elsewhere: the other `Directory.Delete` sites remove a *package* directory
while the database lives outside it, so they are correct as written.

**PRODUCT BUG — `ProjectWatcher.IsIgnored` walks the whole absolute path.**

```csharp
foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar))
    if (segment is "Library" or "Temp" or "Logs" or "Build" or "obj" or "bin" or "node_modules") return true;
```

It splits the **entire absolute path**, so a directory *above the project root* can silently disable
live watching for the whole project. Any Windows user with a project under `D:\Build\…`,
`…\bin\…`, or similar loses live indexing; it degrades to the periodic sweep with no warning.
Correctness survives — the sweep is authoritative, which is exactly why this went unnoticed — but
freshness does not.

Confirmed, not inferred. The tests build fixtures under `Path.GetTempPath()`, which on Windows is
`C:\Users\…\AppData\Local\`**`Temp`**`\…`. Re-running the same tests with `TMP` pointed at a path
with no matching segment turned all four green in **232–760 ms** instead of timing out at 8 s.
`FileSystemWatcher` itself was separately verified working here (a first probe reporting zero events
was my own PowerShell error — `Register-ObjectEvent -Action` runs in another runspace — and a
queue-based re-probe saw the event).

It never showed on macOS because `Path.GetTempPath()` there is `/var/folders/…`, which contains no
matching segment.

**The contrast is the tell:** `ProjectWalker.IsExcludedDirectory(string name)` takes a directory
*name* and is applied per-directory below the root — correct. Only the watcher's copy takes a full
path. It is also case-**sensitive** (`segment is "Temp"`) where the walker uses
`StringComparer.OrdinalIgnoreCase`, so on a case-insensitive filesystem a `library\` directory is
pruned by the indexer but not ignored by the watcher.

##### Fixed (2026-08-30)

`ProjectWatcher.IsIgnored` now judges `e.Name` — already relative to the watched root — and delegates
each segment to `ProjectWalker.IsExcludedDirectory`, which was made `internal`. **One definition, two
callers**, so the case-sensitivity divergence is gone too and cannot silently return: a grep confirms
the directory-name list now exists in exactly one place.

Watched failing first, as the plan's own discipline requires. The new regression test
`AProjectUnderADirectoryNamedLikeAnIgnoredOneIsStillWatchedLive` builds a project at
`<temp>/Build/Game`. **"Build" rather than "Temp" is deliberate**: it is on the exclusion list but
appears in no OS temp path, so the test fails against the old code on macOS and Linux too, instead of
being an accident of Windows putting every fixture under `%LOCALAPPDATA%\Temp`. Reproducing the old
scoping (`e.Name` → `e.FullPath`) made it fail in 8 s with its own diagnostic; restoring the fix made
it pass in 354 ms. The reproduction was reverted byte-identically (md5 compared).

Result: **27 → 23 failures.** `Hades.Core.Tests` went 879 → 880 tests, 854 → 859 passed, 25 → 21
failed — +1 test, +4 fixed, and no regressions in any other project.

The macOS effect is the same fix, not a Windows special case: a Mac project under `~/Library/…` was
equally unwatched before and is watched now.

**Local `file:` packages (9) — test bug, and the product is fine.** The fixtures put a raw path into
JSON: on Windows that yields `"file:C:\Users\…"`, where `\U` and `\A` are invalid escapes, so the
manifest is malformed and no package resolves. Verified by parsing the exact string
(`Unrecognized escape sequence`). Escaping the path at one call site made
`IndexesLocalPackagesDeclaredWithAFileDependency` **pass**, which establishes that `ScriptIndexer`
resolves `file:` packages correctly on Windows.

**9 call sites, and they are not all the same shape** — a `file:{` grep finds only 8 and silently
misses `ScriptIndexerTests.cs:354`, which concatenates (`"file:" + container +`) rather than
interpolating. A fix driven by that grep would have left one test failing. All nine now go through
`ManifestJson`: `ScriptIndexerTests` ×5 (including the concatenated one), `AssetIndexerTests`,
`BinaryAssetIndexerTests`, `IncrementalIndexTests`, `ObservationServiceTests`.

`V12DetectorTests.Detect_ManifestEntry_FileForm` is adjacent but distinct — it hardcodes the POSIX
absolute path `/Users/mike/Projects/Hades`, which Windows correctly roots onto the current drive as
`D:\Users\…`. That is counted under platform-specific assertions, not here.

**Symlinks (6) — environmental.** `A required privilege is not held by the client`. Enable Developer
Mode or run elevated. GitHub's `windows-latest` runners are admin, so these should pass in CI.

**SQLite handles (5) — test bug, and the assertions all passed.** 3 `GraphDatabaseTests`,
2 `TraceRetentionTests`. Every stack trace ends in `Dispose()` → `RemoveDirectoryRecursive`: the
*teardown* fails, not the test. These fixtures open their own `new SqliteConnection($"Data
Source={dbPath}")` without `Pooling=False`, so a pooled handle outlives `Dispose` and the temp
directory cannot be deleted. Same root cause as the 694-failure fix, in test-local connection
strings that the product-code fix did not reach.

**Platform-specific assertions (2).** `ScriptIndexerTests.AnUnreadableDirectory_PreservesItsNodes_OnFullReindex`
calls `File.SetUnixFileMode` and throws `PlatformNotSupportedException` — it should carry
`[Trait(Platform, Unix)]` and does not. (A Windows equivalent would need an ACL-based unreadable
directory, which is a rewrite rather than a trait.) And `V12DetectorTests.Detect_ManifestEntry_FileForm`,
the POSIX-absolute-path case above.

**Socket close (1).** `EditorListenerTests.WrongToken_…` asserts `read == 0`. It writes a second
line the server never consumes, so the server closes with unread data buffered and Windows answers
with **RST**, surfacing as `WSAECONNRESET` rather than a clean EOF. Its passing sibling
`NoToken_…` sends zero bytes and half-closes cleanly, which is precisely why it gets the FIN the
assertion expects. The product did the right thing on both; the test asserts one platform's spelling
of "the server hung up".

---

### Task 2: Single instance, tray presence, no taskbar entry

The Mac app is `LSUIElement` — menu-bar only, no Dock icon, no Cmd+Tab entry. The Windows equivalent is a tray app with no taskbar button and no window at startup.

**Files:**
- Modify: `Windows/Hades.Shell/App.xaml`, `App.xaml.cs`
- Create: `Windows/Hades.Shell/Tray/TrayIcon.cs`

- [x] **Step 1: App.xaml with no StartupUri**

```xml
<Application x:Class="Hades.Shell.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown" />
```

`ShutdownMode="OnExplicitShutdown"` is load-bearing: the default (`OnLastWindowClose`) would quit the app the first time the user closes the main window — and quitting kills a spawned core. There is deliberately no `StartupUri`; the app starts with a tray icon and no window, matching the Mac.

- [x] **Step 2: Single-instance mutex in App.xaml.cs**

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

- [x] **Step 3: Create the tray icon**

`Windows/Hades.Shell/Tray/TrayIcon.cs` wraps `System.Windows.Forms.NotifyIcon`. Two things must be right or the icon misbehaves in ways that look like bugs:

```csharp
// NotifyIcon MUST be disposed explicitly. A tray icon whose owning process exits without
// disposing leaves a "ghost" icon in the notification area that only vanishes when the user
// hovers over it - a classic, very visible Windows bug.
```

Set `Visible = true`, a placeholder icon for now, and `Text = "Hades"`.

- [x] **Step 4: Hand-run**

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

- [x] **Step 1: Read the Mac source of truth**

`Mac/HadesApp/Sources/HadesApp/StatusIcon.swift` has **five** `symbolName(for:)` overloads — for `ControlIconState`, `ControlSeverity`, `OperationState`, `TraceOutcome`, and `MenuBarContent`. Only the first needs `.ico` files (the tray); the rest are glyphs rendered inside the window.

- [x] **Step 2: Write the failing test**

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

- [x] **Step 3: Run — expect FAIL** (`StatusGlyph` does not exist)

```powershell
cd C:\path\to\Hades\Windows
dotnet test Hades.Shell.Tests --filter "FullyQualifiedName~StatusGlyph"
```

- [x] **Step 4: Implement `StatusGlyph`**

Map each enum to a **Segoe Fluent Icons** codepoint (Windows 11; Segoe MDL2 Assets is the Windows 10 fallback — pick codepoints present in both where possible and note any that are not). Every switch needs a `default` arm returning the unknown glyph — that is what the last test pins.

Comment the file with the rule this obeys: *these are pictures, not words.* Like the Mac's `StatusIcon`, it picks a glyph and nothing else. It never maps a state to display **text** — the core authors every string the user reads.

- [x] **Step 5: Run — expect PASS**

- [x] **Step 6: Generate the six `.ico` files**

The Mac generates `AppIcon.icns` from a single 1024px PNG at build time via `sips`/`iconutil` (see `Mac/HadesApp/scripts/build-app.sh`) rather than checking a binary into the repo. Do the equivalent here if you can; if not, check in the six `.ico` files and document why.

Each `.ico` must contain at least 16×16 and 32×32 — the notification area picks by DPI, and a single-size icon looks visibly wrong on a scaled display.

- [x] **Step 7: Hand-run** — set the tray icon per state by temporarily forcing each value, and confirm all six are visually distinguishable **at 16×16**. An icon set that only reads at 256px is not usable in a tray.

#### Outcome — Tasks 2 and 3, 2026-08-29

Three decisions the plan left open or that measurement overturned:

1. **Activation uses a named `EventWaitHandle`, not a broadcast window message.** Step 2 allowed
   either. The message route needs an HWND to broadcast to and this app deliberately owns no window
   at startup; the message-only window it would need is precisely the kind `HWND_BROADCAST` does not
   reach, so the fiddlier option is also the one that would not have worked.

2. **The six tray icons are COLOURED, where the Mac's `StatusIcon` is monochrome.** macOS tints
   menu-bar icons for the current appearance automatically; Windows does not tint tray icons at all,
   so one monochrome set is legible on exactly one theme. Three styles were rendered and compared at
   16px on both a light and a dark taskbar: white-fill/dark-stroke reads well on dark and hollow on
   light, dark-fill/light-stroke is its mirror, and semantic colour is the only one that reads on
   both. This is still a fixed one-to-one state → picture mapping; it decides nothing the core has
   not already resolved. The plan's own rule from Task 7 Step 10 is what settles it: *a shell that
   only works in the theme the developer happens to use is a bug users hit immediately.*

3. **The `.ico` files must contain BMP/DIB images, never PNG-compressed ones.** The first generated
   set used PNG, which Explorer accepts and has since Vista. GDI+ does not: it reads the PNG bytes as
   a DIB and renders noise, and `NotifyIcon.Icon` is a `System.Drawing.Icon`. This reached the screen
   as six panels of coloured static before it was caught. `TrayIconResourceTests.NoImageIsPngCompressed`
   now pins it, and was watched failing against a deliberately PNG-encoded icon.

Codepoints were verified present in **both** Segoe Fluent Icons and Segoe MDL2 Assets and then
rendered and looked at, because the documented MDL2 names mislead: `E91F` is named "CircleRing" and
draws a filled dot, `E739` is named "RadioBtnOff" and draws a square. Two mappings are deliberate
approximations, noted at their use sites — neither font has a filled lock-in-a-circle or a filled
octagon, so `LeaseHeld` uses a bare padlock and the `xmark.octagon.fill` states use a filled
cross-in-circle.

`Hades.Shell.Tests` was created here (the plan's file table lists it but no task creates it), and
`Windows/Hades.Shell.csproj` needed two additions the plan's literal csproj omits, both commented in
place: `<Using Remove="System.Windows.Forms" />` and `NoWarn=WFO0003`.

**Both hand-runs passed** on 2026-08-29, alongside Task 4's. Tray icon present with no taskbar
button, a second launch activating rather than duplicating, and Exit removing the icon immediately
with no ghost. Icon legibility was verified from generated previews at 16px on both a light and a
dark taskbar; `idle` and `notRunning` were additionally confirmed in the live notification area,
including the transition between them. The remaining five were not forced live — doing so needs
throwaway code, and Task 4's wiring now drives them from real state anyway.

One bug found by the hand-run and fixed: double-clicking the tray icon showed "Hades is already
running", because `OpenRequested` (double-click and the Open Hades item) had been wired to the
*second-launch* handler. A click on the running app's own icon is not a second launch. `TrayIcon`
no longer owns any message text — it exposes `ShowBalloon(message)` and the caller supplies the
wording, so one class can no longer apply a single message to two different situations. The two
converge in Task 6, when both should raise the main window.

**Addendum, 2026-09-01 — the PRODUCT icon, which this task never covered.** Step 6 said "generate
the six `.ico` files" and meant the tray. Nobody noticed the app had no icon of its own:
`ApplicationIcon` was absent from `Hades.Shell.csproj`, so the executable carried .NET's generic
default and the Start menu, taskbar, Alt+Tab, Explorer and both windows' title bars showed a blank
page with a blue square. The Mac never had the gap, because `build-app.sh` generates `AppIcon.icns`
into the bundle; the port simply never grew the equivalent step. It was found by looking at a Start
menu search result, which is the only way it could have been found — the suite was green throughout
and not one of its tests could have asked this question.

`generate-icons.ps1` now also writes `app.ico`, from the **same 1024px master the Mac uses**, read
out of `Mac/HadesApp/Resources/` rather than copied so the two platforms cannot drift. One
deliberate difference: it converts `AppIcon-source-fullbleed.png` and re-masks it, rather than
taking the shipped `AppIcon-1024.png`. That file has macOS's icon grid baked in — measured, a 9.8%
transparent margin on every side — which macOS composites around and Windows does not, so used
as-is it draws a mark filling 80% of its box beside neighbours that fill theirs. All three options
were rendered at 16/24/32/48 on light and dark and compared before choosing. The mask keeps the
Mac's own 22% corner radius: same silhouette, scaled to fill rather than to sit inside a margin.

Three surfaces, one file, each verified rather than assumed:

- the **executable** embeds it via `ApplicationIcon` — the built exe's icon is pixel-identical to
  `app.ico` at 32x32;
- **both windows** inherit it with no `Icon` attribute anywhere in the XAML. Measured, not taken on
  trust: `WM_GETICON` against the live main window returns 0 differing pixels against `app.ico` at
  both 16x16 and 32x32. That is why `app.ico` is excluded from the `<Resource>` glob — a second
  embedded copy would be 372 KB that nothing reads;
- **Add/Remove Programs** is the one surface with no executable to inherit from, so `Hades.wxs`
  sets `ARPPRODUCTICON`. Verified after a real install: MSI extracted it to the per-user icon store
  at the same 381,038 bytes, 0 differing pixels.

The Start menu **shortcut** deliberately sets no icon of its own (`IconLocation` is `,0`), so it
resolves the target executable's — one source rather than a copy that can disagree with it.

`ConvertTo-IconDib` was rewritten from per-pixel `GetPixel` to a `LockBits` row copy along the way,
because the tray glyphs top out at 48px and `app.ico` carries a 256px entry. The rewrite is
provably behaviour-preserving: all seven tray `.ico` files regenerate **byte-identical** to the
committed ones, which is the check that licensed the change.


---

### Task 4: Tray menu — supervision states and ownership

**This is the task most likely to lose something.** The Mac popover is the densest safety surface in the app, and the Windows context menu must carry the same information.

**Files:**
- Create: `Windows/Hades.Shell/Tray/TrayMenuBuilder.cs`
- Create: `Windows/Hades.Shell.Tests/TrayMenuBuilderTests.cs`

- [x] **Step 1: Read the reference and enumerate what it renders**

`Mac/HadesApp/Sources/HadesApp/MenuBarContent.swift` resolves supervisor state + last summary into four cases: `notRunning`, `restarting(attempt:)`, `failed(attempts:)`, `running(ownership:summary:)`. `Views/MenuBarContentView.swift` renders them, and `Views/SupervisionFooterView.swift` renders the ownership line.

The two strings that must survive verbatim:

```
Adopted — quitting Hades leaves it running
Started by this app — quitting stops it
```

That distinction is the difference between a user quitting the app and unknowingly killing a core another process is using.

- [x] **Step 2: Write the failing test**

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

- [x] **Step 3: Run — expect FAIL**

- [x] **Step 4: Implement `TrayMenuBuilder`**

It returns a list of plain records (`Text`, `Enabled`, `Action`, `IsSeparator`). A separate thin adapter turns that into a `ContextMenuStrip`. Keep the builder free of WinForms types — that is what makes it testable.

Order, top to bottom, mirroring `MenuBarContentView`:
1. Held-lease line + `Release` (Task 5)
2. Supervision state, when not running
3. Per-project rows, keyed by `productGuid`
4. Ownership footer
5. Separator, then `Open Hades`, `Quit Hades`

- [x] **Step 5: Run — expect PASS**

- [x] **Step 6: Wire it to the real `NotifyIcon`** — right-click shows the menu; double-click opens the main window (Task 6).

- [x] **Step 7: Hand-run** — with the core running, confirm project rows appear with the core's own text; quit the core and confirm the menu switches to the not-running state.

#### Outcome — Task 4, 2026-08-29

**Step 7 needed wiring this plan never assigns, and that gap was filled here.** It requires the
shell to hold a `CoreSupervisor` and poll `/control/summary` — but Task 4 Step 6 only connects the
menu to the `NotifyIcon`, and nothing in Slice 4 ever gives it a supervisor or a summary, so
`TrayIcon.Update` was called exactly once with `NotStarted`. Task 5 assumes polling already exists
("fire once per lease acquisition, not repeatedly on every poll"), so it had to come first. See
**Task 4a** below.

Step 7 then passed against a real core: the shell adopted a manually started core, rendered its
headline verbatim with the `Adopted — quitting Hades leaves it running` footer, and — when that core
was killed — dropped to `Hades is not running` with the summary cleared, without respawning it. That
last part matters: `control.token` was still on disk pointing at a dead port, so the supervisor had
to move off Running on *liveness* rather than on the file being gone.

Three things worth carrying forward:

1. **`MenuContent` was added** (`Tray/MenuContent.cs`), porting `MenuBarContent.swift`. The plan
   folds its four cases into `TrayMenuBuilder`, but both the menu *and* the tray icon need the same
   resolved value, and duplicating that switch is how the two drift. It also completes
   `StatusGlyph`'s fifth overload, which Task 3 Step 1 names but Task 3 could not write because the
   type did not exist yet.

2. **A project row is TWO menu lines, not one.** `ProjectRowView.swift` stacks `row.project` and
   `row.status` as separate `Text` views and says explicitly that neither field is ever *built* by
   the shell, only laid out. A `ToolStripMenuItem` holds a single caption, so the tempting move is
   `$"{row.Project} — {row.Status}"` — which makes the shell the author of a string the core never
   sent. Two lines, the status indented by *padding* rather than by prefixing spaces, keeps the rule
   exact. This is the specific thing "the task most likely to lose something" was warning about.

3. **A seventh icon, `notRunning` (U+F16A, dotted circle), was added.** The six the plan lists are
   `ControlIconState`'s members, but `MenuContent` has three supervision-only cases with no core to
   read an `iconState` from. The Mac gives `notRunning` its own symbol (`circle.dotted`) distinct
   from idle's `circle`, and collapsing them would make the tray unable to show the difference
   between "no core is running" and "a core is running with nothing to do" — the exact confusion the
   ownership footer exists to prevent. `Restarting` and `Failed` reuse `indexing` and `error`.

The plan's draft test wrote `new SupervisorState.Restarting(3)`; the real type is a
`readonly record struct` with static factories, so the tests call `SupervisorState.Restarting(3)`.
`TrayMenuBuilderTests` also gained cases the draft did not cover: a `Running` supervisor with no
summary yet must render as not-running rather than as an empty menu, `Starting` collapses into
not-running, and two projects sharing a display name must both appear (rows are keyed by
`productGuid`, and keying on name once collided them).

---

### Task 4a: Supervision and summary polling — DONE, added 2026-08-29

**This task did not exist in the plan as written.** It is recorded here because Task 4 Step 7 could
not run without it and Task 5 assumes it: nothing between Task 1 and Task 5 ever hands the tray a
supervisor or a summary.

**Files:**
- Create: `Windows/Hades.Supervision/ICoreSupervisor.cs`
- Create: `Windows/Hades.Shell/Tray/TrayViewModel.cs`
- Create: `Windows/Hades.Shell.Tests/TrayViewModelTests.cs`
- Create: `Core/src/Hades.Control.Client/ClientPaths.cs`
- Modify: `Windows/Hades.Shell/App.xaml.cs`, `Core/src/Hades.Cli/Program.cs`

- [x] **`TrayViewModel`**, the port of `MenuBarViewModel.swift`, keeping its `tick()` contract:
  discovery re-read on **every** tick and never cached (that alone is the whole stale-token recovery
  story); every fetch failure swallowed, leaving the last good summary in place; the summary cleared
  the instant the supervisor leaves Running, which is `MenuContent.Resolve`'s own precondition.
- [x] **`ICoreSupervisor`**, the port of Swift's `CoreSupervising`, so the view model can be driven
  through every supervision state without spawning a process.
- [x] **`ClientPaths.DefaultRoot()`** in `Hades.Control.Client`, with `Hades.Cli` moved onto it. The
  shell needs the identical HADES_HOME-else-per-platform-default rule the CLI had inline, and a
  second copy is how the two drift. Deliberately NOT folded into `Discovery`, which documents itself
  as a pure reader that takes the root as a parameter.

**Two things worth keeping:**

1. **The polling cadence diverges from the Mac, on purpose.** `MenuBarViewModel` polls only while the
   dropdown is open — "a background app has no business polling continuously" — and it can afford
   that because its status item is a fixed "H" that never varies by state. This tray has seven
   state-dependent icons, so an icon refreshed only while the menu is open is wrong for as long as
   the menu is shut. Two cadences: 5s idle to keep the icon honest, the Mac's 1Hz while open.

2. **A fresh `HttpClient` per `ControlClient`, over a SHARED `SocketsHttpHandler`.** `ControlClient`'s
   constructor rewrites `BaseAddress` and `Authorization`, and `HttpClient` throws on both once it
   has sent a request — so a shared client breaks after the first poll, in production, because every
   core restart yields a fresh port. Sharing only the handler keeps the connection pool without
   burning a socket per tick.

**A bug this found, in Task 4's own code:** the shell crashed on launch with
`InvalidOperationException: Collection was modified`. `ToolStripItem.Dispose()` removes the item from
its owner's collection, so `TrayMenuAdapter`'s dispose-then-clear loop mutated what it was
enumerating — on the second `Update`, i.e. the first poll tick, i.e. every run. The order must be
snapshot, `Clear()`, then dispose. The adapter had been written off as "too thin to test"; it now has
its own tests, and the new one was watched failing against the old code.

---

### Task 5: The lease line, Release, and the toast

Spec #3 §3.1 made the lease indicator *"deliberately prominent"* as net #7 of the reload-safety design: **a user must never be confused about why their code stopped compiling.** A tray icon Windows hides behind the overflow chevron is not prominent, which is why the toast exists.

**Files:**
- Modify: `Windows/Hades.Shell/Tray/TrayMenuBuilder.cs`
- Create: `Windows/Hades.Shell/Tray/LeaseToast.cs`
- Modify: `Windows/Hades.Shell.Tests/TrayMenuBuilderTests.cs`

- [x] **Step 1: Write the failing tests**

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

- [x] **Step 2: Run — expect FAIL**

- [x] **Step 3: Implement** the lease line and `Release`, calling `ControlClient.ReleaseLeaseAsync(leaseId)`.

- [x] **Step 4: Run — expect PASS**

- [x] **Step 5: Implement the toast**

`LeaseToast` fires a balloon (`NotifyIcon.ShowBalloonTip`) when a held lease passes the warning threshold.

**Do not invent the threshold.** It is a plugin-side value the Mac surface already keys its warning off — find it (`grep -rn "Threshold" UnityPlugin/ Core/src/`) and read it from the same source. A second hard-coded copy is precisely the drift this codebase's rules exist to prevent. Fire **once per lease acquisition**, not repeatedly on every poll.

#### Outcome — Task 5, 2026-08-29

**Step 6 is blocked on this machine: no Unity is installed**, so no plugin can attach and no
script-editing lease can be taken. Everything else is done and unit-tested (87 shell tests). The
hand-run has to happen wherever Unity lives, and the overflow-chevron half of it is the part not to
skip - that is the whole reason the toast exists.

**The threshold's premise in Step 5 is wrong, and the correction matters.** It says the value is one
"the Mac surface already keys its warning off" and to read it from that source. The Mac has **no
reference to it at all** - `grep -rn -i threshold Mac/` returns nothing. It lives in exactly one
place, `ReloadGate.HeldWarningThreshold` (10s) in `UnityPlugin/Assets/Hades/Runtime/ReloadGate.cs`,
which the shell cannot reference: the plugin is a Unity C# project in no solution the shell builds.

So the duplication is forced. The drift is not: `LeaseToast.HeldWarningThreshold` mirrors it, and
`LeaseToastTests.ThresholdMatchesTheUnityPlugin` parses ReloadGate.cs and fails if the two ever
disagree - watched failing against a deliberately mismatched value. Two surfaces telling a user
different things about when a reload lock has been held "too long" is the exact confusion this
feature exists to prevent. **The durable fix is for the core to report the threshold in the control
API**, at which point the constant and its guard both go away; that is a server change and was left
out of scope here.

`LeaseToast` mirrors ReloadGate's semantics rather than inventing its own: measured from ORIGINAL
acquisition (which is what `SummaryLease.HeldForSeconds` already reports, so a lease an agent keeps
renewing still warns), and fired at most once per continuous hold, resetting when the lease is
released so the next stuck lease is not met with silence.

**One deviation from Step 1's draft test.** It asserted `items[0].Text` contains the LEASE ID. That
is not what the Mac renders and not usable by a reader: `MenuBarContentView` draws `summary.headline`
first - for a held lease the core writes it about the lease - and puts Release directly beneath it,
and a real `leaseId` is a hex GUID (see `summary_result.json`), so printing one as the menu's first
line is noise rather than prominence. What ships: headline, then the lease's PROJECT name verbatim,
then Release, all above the project rows. The tests pin the substance instead - that Release precedes
the project list, names the project holding the lease, carries the right lease id, and is shown
disabled rather than hidden when `releasable` is false.

- [x] **Step 6: Hand-run — the real test of this task**

With a Unity project open and the plugin attached, trigger a script-editing lease (compile something). Confirm: the tray icon changes to `leaseHeld`; the menu's first item names the lease; a toast appears once; clicking `Release` releases it and the menu updates. **Then hide the tray icon in the overflow chevron and repeat** — the toast must still be what tells the user, since the icon is invisible.

#### Outcome — Task 5 Step 6 (Run 3), 2026-09-01. **PASSED, both configurations.**

Driven against a real attached Editor (Unity 6000.3.2f1, pid 9176, `project_aurora`). The lease was
acquired by calling `script_editing_session` with `action='begin'` over the core's own MCP endpoint
at `127.0.0.1:7823` - the same call an agent makes, not a stub. `hades` the CLI cannot do this: it
exposes `release` only, by design.

**The hidden-icon case was run FIRST, by accident of the machine's real configuration** - the Hades
icon was in the overflow chevron, which is Windows' default for a new icon. That is the case this
task exists for, and the one the Mac never has to handle.

| Check | Icon hidden (overflow) | Icon pinned (visible) |
|---|---|---|
| Icon -> `leaseHeld` | server-side from t+3.3s | **amber H, seen** - green -> amber -> green |
| Menu's first item names the lease | confirmed | (proven in the hidden pass) |
| Toast fires, exactly once | **seen, and captured** | window missed - see below |
| Release releases it, menu updates | confirmed, `leaseHeld=false` server-side | (proven in the hidden pass) |

The toast's text was captured and matches `LeaseToast.Evaluate` character for character: *"project_aurora
has held Unity's reload lock for over 10s. Unity will not recompile until it is released."* No second
toast fired across 110s of continued holding, which is the once-per-hold rule holding.

`Release` was verified as a real round trip rather than a UI update: after the click,
`hades_charon_status` reported `leaseHeld=false` and the summary icon returned to `attached`, so the
click reached the shell, the control API, and the plugin's own `ReloadGate`.

**Three things measurement corrected, all mine rather than the product's:**

1. **The first attach watcher was a false positive.** It tested `$status -match 'Editor attached'` -
   and the string **"No Editor attached"** contains that substring, so it fired on its own first poll
   and printed its own contradiction two lines later. Re-run against
   `hades_charon_status.attached`, a boolean. A prose match for a negatable fact is not a test.
2. **The first toast test proved nothing.** It released and re-acquired with a 4-second gap - but the
   tray polls every **5 seconds when idle** (1s only while the menu is open), and the lease id is the
   constant `hades-script-editing`, so the shell never observed a lease-free tick and correctly read
   it as one continuous hold. The "no toast" reading was a test artifact. Re-run with a 14s gap.
3. **The visible-icon pass missed the toast window.** Frames started around t+29s; the toast fires at
   t+10-15s and lasts about 5s. Recorded as missed rather than claimed - the behaviour was already
   proven twice in the hidden pass, and `LeaseToast` has no knowledge of whether the icon is pinned.

**Two findings worth carrying forward, neither blocking:**

- **The toast lags its own threshold by up to 5 seconds.** `HeldWarningThreshold` is 10s, but it can
  only fire on a poll, and the idle poll is 5s - measured firing at t+16.5s. The wording ("for over
  10s") stays true, so this is a latency note, not a defect: the guarantee is "within ~15s".
- **The notification's sender name reads "Hades.Shell", not "Hades".** Windows is using the
  executable name. Cosmetic, but it is the product's name in front of a user.


---

### Task 6: Main window — sidebar and sections

**Files:**
- Create: `Windows/Hades.Shell/MainWindow.xaml`, `MainWindow.xaml.cs`
- Create: `Windows/Hades.Shell/ViewModels/MainWindowViewModel.cs`
- Create: `Windows/Hades.Shell.Tests/MainWindowViewModelTests.cs`

- [x] **Step 1: Read the reference** — `Mac/HadesApp/Sources/HadesApp/MainWindow/Section.swift`. Three sections with fixed, Swift-authored titles: `Projects`, `Charon` (traces), `Asphodel` (memory). Settings is a fourth destination.

Those product names are deliberate and are **not** to be renamed to generic labels.

- [x] **Step 2: Write the failing test**

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

- [x] **Step 3: Run — expect FAIL**

- [x] **Step 4: Implement** `Section`, its `Title()`, and `MainWindowViewModel`.

Keep every view model **free of `Dispatcher`** so tests need no STA apartment. Marshal to the UI thread in the view layer, not the view model.

- [x] **Step 5: Run — expect PASS**

- [x] **Step 6: Build the window** — a `ListBox` sidebar bound to the sections, a `ContentControl` host.

- [x] **Step 7: Implement close-to-tray**

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

- [x] **Step 8: Hand-run** — double-click the tray icon to open; close the window and confirm the app and core survive (check the core process is still alive); reopen from the tray; then `Quit Hades` and confirm both exit.

- [x] **Step 9: Decide Fluent theming — garnish, not identity**

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

#### Outcome — Task 6, 2026-08-29

**Step 8 passed, including the part the handover said had never been verified anywhere.** With a core
SPAWNED (not adopted — a published core was placed in the shell's own `core\` directory, per §8.1):
the window opened, `WM_CLOSE` hid it while **the shell and the core both survived**, it reopened, and
`Quit Hades` took both down leaving nothing behind. Then the shell was **force-killed** —
Task Manager's End Task, where no `OnExit` runs and nothing tells the core to stop — and the core
died with it. Only `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` can do that, and it worked.

**`Section` gains a `Settings` member the Mac deliberately omits.** `Section.swift` says so
explicitly: on macOS, Settings is a standard Settings scene reached by Cmd-comma, so a sidebar
destination would be wrong there. Windows has no such convention — settings live inside the app, at
the bottom of the nav pane. Same destination, different idiom.

**Two real bugs found by running it, neither in Task 6's own code:**

1. **The spawned core showed a console window.** `ProcessLauncher` passed
   `CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT` and no `CREATE_NO_WINDOW`, and the core is a
   console app — so a tray app with no window of its own put a terminal on screen, scrolling request
   logs, with no obvious relationship to Hades. Closing that window would have sent `CTRL_CLOSE` to
   the core. The Mac never had to think about this; launching from a bundle shows nothing. Fixed and
   verified: zero visible windows owned by the core process.

2. **The spawned core never read its own `appsettings.json`.** `CoreSupervisor.BuildStartInfo` passed
   `WorkingDirectory: null`, and `CreateProcess` then gives the child the PARENT's current directory
   — the shell's. ASP.NET Core takes its content root from the current directory, so the core looked
   for `appsettings.json` beside `Hades.Shell.exe`, did not find it, and ran on framework defaults.
   That is how the log noise in bug 1 was Information-level in the first place: the file setting
   `Microsoft.AspNetCore` to `Warning` was never read — and `AllowedHosts` was being ignored with it.
   Fixed by launching with the core executable's own directory. Verified by A/B: three
   `Request starting` lines with the old working directory, zero with the fix.

   The general lesson is worth keeping: **configuration that is silently absent is worse than
   configuration that fails loudly.** Nothing about the core's behaviour looked wrong; it just
   quietly was not configured.

**Step 10 was verified without touching the machine's theme.** Windows Personalization is disabled on
an unactivated install, so Settings → Colors could not be used. Instead `ThemeMode` was forced to
`Dark` and then `Light` in turn, and the window captured with `PrintWindow` (PW_RENDERFULLCONTENT,
which DWM-composited windows need) and looked at. Both are legible: correct background/foreground
inversion, sidebar selection readable in each, product names intact. `ThemeMode.System` was restored
afterwards. Worth remembering as the general technique — forcing the app's own theme proves more than
switching the OS does, and needs no system settings changed.

`MainWindow.xaml` names **no colours at all**, deliberately. That is what makes both themes work: the
active theme supplies everything, so there is no palette to be wrong in the theme the developer did
not happen to be using.

- [x] **Step 10: Verify both themes**

Switch Windows between light and dark (Settings → Personalization → Colors) with the app running, and confirm the window is legible in both. A shell that only works in the theme the developer happens to use is a bug users hit immediately.

---

### Task 7: Projects section

The largest section. `Mac/HadesApp/Sources/HadesApp/MainWindow/ProjectsViewModel.swift` is the reference: `refresh`, `addProject`, `removeProject`, `rebuildProject`, `installPlugin`, `revealInFinder`, `openInUnity`, plus `rebuildProgress` polling by operation id.

**Files:**
- Create: `Windows/Hades.Shell/ViewModels/ProjectsViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/ProjectsView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/ProjectsViewModelTests.cs`

- [x] **Step 1: Read `ProjectsViewModel.swift` in full**, including `pollTrackedOperations` and `recordServerMessage`.

- [x] **Step 2: Add the routes the section needs to `ControlClient`**

`ControlClient` currently has 5 read routes plus `ReleaseLeaseAsync`. This section needs add/remove/rebuild/installPlugin/revealInFinder/openInUnity and `GET /control/operations/{id}`.

**Confirm every route against `Core/src/Hades.Server/Control/ControlListener.cs` before writing it.** Follow the existing `SendAsync<T>` / `GetAsync<T>` / `PostAsync<T>` pattern exactly. Add tests to `Core/tests/Hades.Control.Client.Tests/ControlClientTests.cs` in the established stub-handler style.

- [x] **Step 3: Write the failing view-model tests**

Behind a fake client interface, mirroring how the Swift side fakes `ControlProjectsFetching`. Cover at minimum:
- `refresh` populates rows
- a failed action records **the server's own message**, never invented text
- `rebuildProject` tracks the returned operation id and polls until `done`
- `removeProject` requires the confirmed flag (guard against accidental destructive action)

- [x] **Step 4: Run — expect FAIL**

- [x] **Step 5: Implement the view model.**

- [x] **Step 6: Run — expect PASS**

- [x] **Step 7: Build the view** — a project list with per-project actions, warnings rendered with `StatusGlyph.For(ControlSeverity)`, and a rebuild progress indicator.

#### Outcome — Task 7, 2026-08-29

**Step 8 is blocked on Unity, same as Task 5 Step 6** — there is no Unity on this machine yet, so no
project can be added, indexed or opened. Everything above it is done: 7 new client routes with 10
client tests, and 18 view-model tests.

**`ControlClientException` gained a `StatusCode`, and that was a real gap rather than a nicety.**
Swift's case is `.server(status:message:)` and callers switch on the status; this port dropped it.
That made `pollTrackedOperations` impossible to write faithfully: a 404 from
`GET /control/operations/{id}` means "unknown operation — it may have completed and been pruned",
which is an **ordinary** outcome for a rebuild that finished more than five minutes ago, and without
the status it is indistinguishable from a genuine server error. Now carried, and pinned by a test.

Two things preserved from the reference that are easy to lose:

1. **`AddProjectAsync` refreshes explicitly rather than waiting for a tick.** Waiting works only
   where something drives one, and onboarding drives no tick at all — an add there completed
   server-side and left "No projects yet" on screen, the success real and entirely invisible. It also
   clears an earlier failure message, so "that folder is not a Unity project" cannot sit above the
   row that just added fine.

2. **`confirmed` on remove is the gate, not a hint.** `false` never reaches the network. That is what
   makes "never remove without confirming" provable in a headless test rather than merely trusted of
   the dialog — the dialog lives in `ProjectsView.xaml.cs` precisely so the view model stays testable.

The route keeps its macOS name (`revealInFinder`) on Windows because it is the SERVER's route and the
server decides what revealing means per platform; only the button says "Reveal in Explorer". The
folder picker is `Microsoft.Win32.OpenFolderDialog`, in-box since .NET 8 — no WinForms
`FolderBrowserDialog`, no package.

**A BUILD TRAP THAT COSTS AN HOUR IF YOU MEET IT COLD.** After the view landed, the window rendered
completely blank — correct title bar and size, empty client area, `Process.Responding` **true**. It
survived every reasonable hypothesis: not the theme (forcing Light was equally blank), not the
content (a bare `<TextBlock Text="PROBE"/>` was equally blank), not the capture method.

It was a **stale build reporting success**. Running the shell locks
`bin\Debug\net10.0-windows\Hades.Shell.exe`, so a `dotnet build` while it is running fails with
`MSB3027`/`MSB3021` ("file is locked by: Hades.Shell"). Killing the process and rebuilding then
reports **Build succeeded** while continuing to ship stale BAML — so the XAML on disk and the XAML
in the running app silently diverge. `rm -rf Hades.Shell/bin Hades.Shell/obj` and rebuilding fixed
it instantly.

Two rules follow, and the second is the one that matters:

- **Stop the shell before building.** Every hand-run in Slice 4 leaves one running.
- **A blank WPF window is a build symptom before it is a code symptom.** Nothing about it looks like
  a stale binary: the window is responsive, correctly sized, correctly titled, and the build is
  green. Check the build before debugging the XAML. The fastest discriminator found here was setting
  `Background="Red"` on the root Grid — if the colour appears, WPF is rendering and the problem is
  content; if the window stays blank, stop and rebuild clean.

- [x] **Step 8: Hand-run** — add a real Unity project via the folder dialog; watch it index; hit Rebuild and watch progress; Reveal in Explorer (confirm it **selects** the folder, not just opens the parent); Open in Unity; remove it.

#### Outcome (2026-08-30) — all six actions work; three defects found and fixed

Walked on a real 6,858-file project (`project_aurora`, 28,838 nodes). Add through the folder dialog,
index with live per-phase counts, Rebuild to completion, **Reveal in Explorer with the folder
genuinely highlighted** (not merely the parent opened — `explorer.exe /select,<path>`, and the comma
placement matters), Open in Unity launching the editor, and Remove refusing to act without its
confirmation. The project folder survived removal.

**1. "Open in Unity" could not find an installed editor.** It looked only in
`C:\Program Files\Unity\Hub\Editor`, while the editor was installed and working in
`D:\Unity Editors`. `UnityHubEditorExecutablePath`'s own comment had *predicted* this — "users
relocate editors to another drive far more often than Mac users move /Applications, so a miss here
is expected more often" — and concluded that real Hub discovery was too costly to do. The prediction
was right and the conclusion was wrong: Hub records a relocated root in one small JSON file
(`%APPDATA%\UnityHub\secondaryInstallPath.json`), so honouring it is a file read. Both roots are now
searched, because editors installed before the root was changed stay where they were, and the
not-found message lists every path tried rather than naming one directory the user never used.

`LIMITATIONS.md` had this listed as an untested risk. It is now tested, was broken, and is fixed.

**2. The Remove dialog claimed a deletion that does not happen.** "Its index is deleted; the project
folder on disk is untouched" — the index is *not* deleted; `graph.db` survives byte for byte. The
CLI's help carried the identical false claim and was corrected the same day; the shell had its own
copy. This is the worse direction of the two possible errors: someone removing a project to reclaim
disk space would believe they had.

**3. Same-version installs registered a SECOND product — the serious one.** Found while trying to
get fix (1) onto the machine, through binaries that kept coming back stale. WiX regenerates the
`ProductCode` every build and `MajorUpgrade` removes only *strictly lower* versions, so each rebuild
of 2.1.0 installed a **new product alongside** the previous ones: three "Hades" entries registered at
once, and an install directory owned by whichever wrote last. Fixed with
`AllowSameVersionUpgrades="yes"`, verified by installing twice — one product, 862 files, fresh
binaries. Full detail in `ReleasePipeline.md` §9.2.

**Task 17's upgrade test passed throughout**, because 2.1.0 → 2.1.1 differ in version and
`MajorUpgrade` did its job. The same-version case was the one nobody ran — and it is the one every
developer meets on their second build.

---

### Task 8: Charon (traces) section

**Files:**
- Create: `Windows/Hades.Shell/ViewModels/TracesViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/TracesView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/TracesViewModelTests.cs`

- [x] **Step 1: Read `MainWindow/TracesViewModel.swift`** (240 lines) and `Views/{TracesView,TraceSequenceRowView,TraceDetailView}.swift`. The surface is sequence-first: a list of sequences, drill-in to spans, plus slow-tools and failures views.

- [x] **Step 2: Add the trace routes to `ControlClient`**, confirmed against `ControlListener.cs` (note the literal-segment routes are matched ahead of the `{traceId}` parameter route — read that file's comment).

- [x] **Step 3: Write failing view-model tests** covering: sequences load; selecting one loads its detail; outcome renders via `StatusGlyph.For(TraceOutcome)`; an unknown outcome does not crash.

- [x] **Step 4: Run — expect FAIL**

- [x] **Step 5: Implement.**

- [x] **Step 6: Run — expect PASS**

- [x] **Step 7: Build the view.**

#### Outcome — Task 8, 2026-08-29

Done through Step 7: 4 client routes with 7 tests, and 17 view-model tests. Step 8 needs a core with
real trace data, so it goes with the other Unity-dependent hand-runs.

**The four fetches self-heal independently, and that is the whole design.** Failures and slow calls
come from their own endpoints and are never filtered client-side out of the sequences list — the
server groups calls into sequences BEFORE filtering, so doing it the other way round corrupts the
grouping. One fetch failing must not stop the other three updating or clear what is on screen, and
there is a test per case.

`RefreshError` is the one narrowing of that self-heal: a server error carrying a message (most often
"Hades knows N projects, so this call needs a 'project' argument") is not a transient blip but the
server explaining something the shell cannot act on silently. It surfaces verbatim, the data is still
left untouched, and it is recomputed every refresh rather than being a sticky banner that outlives
its cause.

`limit` is omitted rather than defaulted to today's 200, so the route's own default stays the single
source of truth — a client that hardcoded it would be keeping a stale copy of a server-owned policy
value. Pinned by a test asserting the query string is empty when no filters are given.

**Two defects the rendered window showed that no test would have.** Both were found by capturing the
view and looking at it:

1. `EmptyToCollapsedConverter` had no `bool` case, so it fell through to Visible. Bound to
   `SequencesTruncated`, that put "Showing a truncated list — narrow the filters to see the rest"
   above a list that was **not** truncated: telling the user data was being withheld when none was.
   Now handled, and `ConverterTests` pins every case.
2. The filter row was a horizontal `StackPanel`, which clips its last child rather than shrinking —
   it silently hid the Apply button at the default window size, making the filters unusable. Now a
   `WrapPanel`.

The lesson generalises: converters are view code but they DECIDE what is shown and hidden, so those
decisions belong in tests. Rendering a section and looking at it caught both in one pass.

- [x] **Step 8: Hand-run** — issue a few MCP tool calls from Claude Code, confirm they appear, drill into one, and confirm a failing call is visibly distinguishable from a successful one.

#### Outcome — Task 8 Step 8 (Run 5), 2026-09-01. **PASSED, after four defects were fixed.**

Driven by issuing real tool calls over the core's own MCP endpoint at `127.0.0.1:7823` - the same
surface an agent uses. One sequence carried the whole test: seven calls, four successful and three
failed, including **`search_by_name` twice, once failed and once succeeded**. That pair is the
sharpest available form of "a failing call is visibly distinguishable from a successful one":
anything keyed on the tool name rather than the individual call would collapse them and hide an
outcome. The third of those failures was originally the author's own bad parameter name; it was a
better test case than anything contrived, so it was kept and the correct call added beside it.

**The run found four defects. Every one of them shipped through a green suite.**

1. **Only the FIRST call of a sequence was reachable.** The list drilled into `TraceIds[0]` and
   nothing else, with a comment saying so. Since a sequence is flagged failed when ANY call in it
   failed, the ordinary case was a row carrying the error glyph whose detail pane then reported
   `ok` - the first call having succeeded - while the calls that actually failed could not be opened
   from that tab at all. The UI contradicted itself. Traced through every layer before blaming one:
   the core is fine (`TraceIds` carries every id; `Id` is just the first, used as a key), and the Mac
   has always expanded a sequence with `ForEach(Array(zip(sequence.tools, sequence.traceIds)))`. A
   Windows-only regression.

2. **The list deselected itself every few seconds.** Each poll replaced the collection, so the
   selected instance was no longer in it and WPF dropped the selection - while the calls pane went on
   describing a sequence that was no longer highlighted. Measured: selected in **14 of 30** samples
   over 24s; after the fix, **30 of 30**. This is almost certainly what the tester experienced as the
   first attempt "autofolding while I try to read it".

3. **Every list announced a raw record dump** to assistive technology -
   `TraceSequenceRow { Id = c6af091d..., Tools = ... }` - and the sidebar announced **"Traces"** and
   **"Memory"** where the screen reads **Charon** and **Asphodel**. Measured with UI Automation, not
   inferred. Two defects in one: a screen reader speaks a word that is nowhere on screen, and voice
   control cannot act on "click Charon" because nothing claims that name. It matters especially here
   because those product names are vocabulary this plan explicitly refuses to genericise - and the
   generic label was leaking out through the accessibility tree anyway.

4. **"1 calls"**, on screen and spoken. Visible in every screenshot taken during the run.

**What replaced the first attempt, and why.** An `Expander` per row was tried first and was worse
three ways, all found by looking rather than by testing: WPF reads `_` in a header as an access key,
so `hades_ping` rendered as "hadesping"; the expander re-collapsed on every refresh because a poll
rebuilds the item containers; and the taller rows clipped. It was replaced with a master/detail - the
sequence list stays compact, and the calls live in the detail pane where there is room for their
numbers:

```
Calls
      tool                         at       took
1 ok  hades_ping                 +0 ms      0 ms
3 ERR search_by_name           +968 ms      1 ms
7 ok  search_by_name             +18 s     12 ms
```

`at` is the offset from the sequence's own start, `took` is that call's duration, both formatted by
magnitude (`0 ms`, `1.4 s`, `3m 09s`) instead of the raw `18181ms` the view printed everywhere
before. Durations now read the same way in the sequence rows, the span detail and Slow tools.

Resolving that pane needs per-call data a sequence row does not carry - it has tool names and ids
only - so selecting a sequence fetches each call's detail concurrently. **A call that cannot be
fetched is dropped, not faked**: retention can prune a trace out from under a still-listed sequence,
and inventing a placeholder row would put an outcome on screen the core never reported, which is the
same class of lie this whole task was fixing.

**Two dead ends, documented at the code so they are not retried.** Binding `SelectedValue` to the
row id does not preserve selection: replacing `ItemsSource` nulls it first and a TwoWay binding
writes that null back. Reconciling an `ObservableCollection` in place does not work either - the
refresh runs on the poll thread, mutating a bound collection off the UI thread throws, and it emptied
the entire section by taking the projects and failures fetches down with it. It also violates this
shell's stated rule that view models never touch the Dispatcher. The fix that worked is smaller than
both: an unchanged refresh simply does not republish the list, and the view re-selects by id when it
genuinely does.

**A process failure worth recording.** While watching a test fail, the deliberately-broken file was
restored with `Copy-Item`, which preserved its ORIGINAL timestamp - older than the assembly compiled
from the broken source. MSBuild saw source-older-than-output, skipped the rebuild, and a stale broken
assembly was packaged into an MSI and installed. It was caught only because the test run in the same
command reported failures that should have been impossible. This is the second time incremental
staleness has bitten in this port (see `Hades.Shell.csproj`'s ApplicationIcon note for the first).
**Restoring a file after watching a guard fail must bump its timestamp**, or the next build silently
reuses the broken one.

Windows suite **239 + 11**, core **1,956**, all green. Verified against the installed MSI throughout,
never a developer build.

---

### Task 9: Asphodel (memory) section

**Files:**
- Create: `Windows/Hades.Shell/ViewModels/MemoryViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/MemoryView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/MemoryViewModelTests.cs`

- [x] **Step 1: Read `MainWindow/MemoryViewModel.swift`** and `Views/{MemoryView,MemoryDocumentView,MemoryProposalRowView,ProposalQueueView}.swift`. Two surfaces: authored documents, and a proposal queue with accept/dismiss/defer.

- [x] **Step 2: Add the memory routes to `ControlClient`**, confirmed against `ControlListener.cs`.

- [x] **Step 3: Write failing tests** — documents list; opening one fetches content; accept/dismiss/defer each call the right route and surface the server's own message.

- [x] **Step 4: Run — expect FAIL**

- [x] **Step 5: Implement.**

- [x] **Step 6: Run — expect PASS**

- [x] **Step 7: Build the view.**

#### Outcome — Task 9, 2026-08-29

Done through Step 7: 6 client routes with 7 tests, and 19 view-model tests. Step 8 joins the other
hand-runs waiting on Unity, and its second half is the part not to skip — **verify the change
actually landed in the file on disk**, not merely that the UI said so.

**The confirmation asymmetry is the design, and the tests pin it deliberately.** Memory is authored
and irreplaceable: the graph, trace and memory-index databases are all derived and rebuildable, but
`memory/*.md` has no other copy. So exactly two actions are gated on `confirmed`, enforced in the
view model where `false` never reaches the network:

- **Save** overwrites an authored file — atomic, no merge, no version history.
- **Dismiss** deletes the proposal file. The core also refuses without `confirm=true`, so the
  client-side gate is defence in depth rather than the only one.

**Accept and Defer deliberately have NO gate**, and there are tests asserting that too, because the
temptation is to add them "for consistency". Accepting only ever APPENDS to the target document
(creating it if missing) and deferring is pure bookkeeping that never touches an authored file.
Gating them would train the user to click through confirmations that never mattered, which is exactly
how the two that do matter stop being read.

**`RefreshError` and `LastActionMessage` are separate properties on purpose.** A passive poll failure
overwriting a just-seen action result — or the reverse — would be actively misleading, since each
reflects only its own kind of attempt. There is a test for that specific interleaving.

An open document is a fixed snapshot: `RefreshAsync` never touches `SelectedDocument`, so a tick
cannot overwrite an in-progress edit out from under the user. Only a deliberate project change ends
its lifetime, the same rule Charon holds for a selected trace.

- [x] **Step 8: Hand-run** — confirm documents render and a proposal can be accepted, then verify the change actually landed in the file on disk.

  **The file is under `AppPaths.MemoryDir(productGuid)`** — `%LOCALAPPDATA%\Hades\projects\<guid>\memory\` on Windows — **not in the project.** This step used to say "with a project that has `.arcforge/memory/`", which is wrong and actively misleading: that path is the **v1.2 import source** `MemoryStore.ImportFromArcforge` reads once and, in its own words, "is only ever read, never written to or deleted". Following the old wording sends you to look inside the Unity project, find nothing, and conclude the accept failed — which is exactly what happened when this run was performed.

#### Outcome — Task 9 Step 8 (Run 4), 2026-09-01. **PASSED. The step's own wording was the only defect.**

`project_aurora` had no authored memory at all, so the run bootstrapped its own through the product's
real path rather than hand-placing files: `propose_memory_update` over the MCP endpoint created a
proposal, which was then accepted in the Asphodel view.

| Check | Result |
|---|---|
| Proposals render | filename, age, rationale, full proposed content, Accept / Defer / Dismiss |
| A proposal can be accepted | accepted; the core moved it to `[accepted]` and wrote the document |
| The change landed on disk | `memory\windows-port-hand-run.md`, 339 B, content byte-for-byte intact |
| Documents render | listed with its size, content shown in the reader pane, Save available |
| Dismiss | deleted the spent proposal file; `proposals: (none)` afterwards |

**THE STEP TEXT WAS WRONG AND IT COST REAL CONFUSION.** It said to use "a project that has
`.arcforge/memory/`" and to verify the change landed there. That path is the **v1.2 import source**:
`MemoryStore.ImportFromArcforge` reads it exactly once, non-destructively, and its own doc comment
states the source directory "is only ever read, never written to or deleted". Authored memory in v2
lives under `AppPaths.MemoryDir(productGuid)`. Following the step literally meant checking the Unity
project, finding no `.arcforge` at all, and concluding the accept had silently failed - which is what
happened, and the tester's report was "did it applied? Not sure its working". The accept had worked
perfectly the whole time. The step has been corrected in place.

A second, smaller confusion followed from the same root: after the accept appeared to do nothing, the
tester pressed **Dismiss**, whose banner ("Proposal dismissed.") then read as a verdict on the accept.
It was not - it was the core correctly reporting the dismiss, which correctly deleted the now-spent
proposal file. Both actions did exactly what they claim. Nothing in the UI is at fault here; the map
was.

**The project is never written to, and that is worth stating plainly** because the run began by
warning the opposite. `.arcforge/` was not created, and `project_aurora`'s git status was unchanged
at three entries before and after. A user's Unity repo does not acquire untracked files from
accepting a memory proposal.

**One further defect found, and fixed: the Project picker rendered empty** in both Asphodel and
Charon. The view models were never at fault - each defaults `ProjectFilter` to the first known
project, matching the Mac - but `KnownProjects` was republished with fresh instances on every poll,
which clears a ComboBox's `SelectedValue`, and the binding is `OneWay`, so nothing ever pushed it
back. Same root cause as the sequences-list selection loss in Run 5, and the same fix: do not
republish an unchanged list (`ProjectPicker.SameProjects`).

That comparison deliberately looks at **only `ProductGuid` and `Name`**, and getting this wrong
would have produced a fix that changed nothing: most of `ProjectRow` is volatile by design -
`IndexStatus` is a relative-time sentence ("indexed 5s ago") that differs on almost every tick - so a
whole-record comparison would report "changed" continuously and republish anyway. `ProjectPickerTests`
pins that case specifically.

The same measurement showed the picker's items announcing a raw `ProjectRow { Name = ..., Path = ...
}` dump to assistive technology - `DisplayMemberPath` sets the visible text but not the accessible
name. Fixed alongside, matching the lists.

Verified on the installed build: both sections select and announce `project_aurora`, and hold it
across refreshes.

**Cleanup done:** the run's `windows-port-hand-run.md` was deleted from app storage; `hades memory`
reports no documents and no proposals.

---

### Task 10: Settings and the ShellFacts

Two OS facts only the shell can observe, and one of them has a trap that a previous spec revision got wrong.

**Files:**
- Create: `Windows/Hades.Shell/ShellFacts/LaunchAtLogin.cs`
- Create: `Windows/Hades.Shell/ShellFacts/PowerStatus.cs`
- Create: `Windows/Hades.Shell/ViewModels/SettingsViewModel.cs`
- Create: `Windows/Hades.Shell/Sections/SettingsView.xaml{,.cs}`
- Create: `Windows/Hades.Shell.Tests/SettingsViewModelTests.cs`

- [x] **Step 1: Read the reference** — `ShellFacts/LaunchAtLoginService.swift`. Its non-negotiable discipline: **write, then re-read the OS, and report only what the re-read says.** Never infer success from the absence of an error.

- [x] **Step 2: Understand the Windows trap before writing any code**

Windows stores the *user's* enable/disable decision in `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, **not** by removing the `Run` value. Disable the app in Task Manager and the `Run` value stays. So an implementation that writes the `Run` value and re-reads only the `Run` value reports "on" forever while Windows never launches the app.

**Read:** enabled only when the `Run` value exists **and** `StartupApproved\Run` either has no entry or holds an enabled state.

**Write:** enabling writes the `Run` value *and* **deletes** any `StartupApproved\Run` entry — do not try to author that entry's state bytes, whose format is undocumented. Deleting returns the app to the OS's own default-enabled path, which is the honest way to express "the user just re-enabled this from inside the app."

- [x] **Step 3: Write the failing tests**

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

- [x] **Step 4: Run — expect FAIL**

- [x] **Step 5: Implement `LaunchAtLogin`** against `Microsoft.Win32.Registry`.

- [x] **Step 6: Implement `PowerStatus`** — `GetSystemPowerStatus` → `SystemStatusFlag` (1 = battery saver on). **Display-only in Settings**, matching the Mac's treatment of Low Power Mode; nothing else consumes it. Thermal state has no Windows analogue and is dropped.

- [x] **Step 7: Implement `SettingsViewModel`** — `mcpPort` and `logLevel` from `GET /control/settings` (both rendered verbatim), plus the two shell facts. Note `SettingsResult` deliberately carries only those two fields.

- [x] **Step 8: Run — expect PASS**

- [x] **Step 9: Build the view.**

#### Outcome — Task 10, 2026-08-29

Done through Step 9: `LaunchAtLogin` with 9 tests, `WindowsPowerStatus`, `SettingsViewModel` with 7.

**The StartupApproved trap is implemented as the plan describes, and pinned by the test that names
it.** Reading requires the Run value to exist AND StartupApproved to hold no veto; enabling writes
the Run value and DELETES any StartupApproved entry rather than authoring its undocumented state
bytes. For reading, the observable rule is that the first byte's low bit is set when disabled (0x03,
0x07) and clear when enabled (0x02, 0x06) — a heuristic over an undocumented format, which is exactly
why the write path deletes instead of constructing one.

The Mac's discipline ported intact: `SetEnabled` writes, then RE-READS, and returns what the re-read
says. Tests cover both directions of a refused write, and the checkbox is bound **one-way** on purpose
— a two-way binding would show the value the user clicked rather than the value the OS ended up in,
which is precisely the failure this section exists to surface.

**Step 10's third question is MEASURED but UNANSWERABLE on this machine.** `GetSystemPowerStatus`
here returns:

```
ACLineStatus       = 1     (plugged in)
BatteryFlag        = 128   (NO SYSTEM BATTERY)
BatteryLifePercent = 255
SystemStatusFlag   = 0
```

on Windows 10.0.26200 / 25H2. With no battery present, this box can never enter battery saver, so it
cannot tell us whether `SystemStatusFlag` reflects Windows 11's plugged-in "energy saver" — that
needs a laptop. **The row is kept for now and the question stays open**; the decision the plan asks
for (drop the row rather than show it wrong) still has to be made on battery-powered hardware.
`WindowsPowerStatus` carries that caveat in its own doc comment so it is not lost.

One defect the render caught: the battery-saver row printed a raw `False`. Now "On"/"Off".

A note on the test double, because the same shape will recur: `FakeRegistry.SetRunValue` honours
`RefusesWrites` because it implements the interface, so using it to SEED state made one test
silently set nothing up and then assert against the empty registry it had accidentally created.
Seeding now goes through `SeedRunValue`, which bypasses the flag — `RefusesWrites` simulates the OS
refusing *this app's* write, not the state that was already there.

- [ ] **Step 10: Hand-run — the part unit tests cannot reach**

Toggle launch-at-login in the app; confirm the app is listed in **Task Manager → Startup**. Then **disable it in Task Manager** and confirm the app's own toggle now reads **off**. That is the bug this task exists to prevent, and only a real machine shows it.

Reboot and confirm the app actually starts.

Also check whether `SystemStatusFlag` reflects Windows 11 24H2's plugged-in "energy saver" — if it does not, drop the row rather than show it wrong, and record what you measured.


#### Outcome — Task 10 Step 10, 2026-09-01. **STEPS 1, 2 AND 4 PASS. STEP 3 IS UNVERIFIABLE ON THIS MACHINE — see the resolution below; it is not a Hades defect.**

**Step 1 — the toggle writes the entry.** A real click wrote
`Run\Hades = %LOCALAPPDATA%\Programs\Hades\Hades.Shell.exe` and deleted any StartupApproved entry,
exactly as `LaunchAtLogin` documents. Its APPEARANCE in Task Manager was never confirmed: see step 3.

**Step 2 — the app honours a Task Manager veto.** With the Run value present and the veto byte set
(`03 00 ...`, the low bit of byte 0), the app reports **off**. It does not claim to start at login
when Windows has overruled it, which is the bug this task exists to prevent.

**Step 4 — energy saver.** `SystemStatusFlag = 0`, reported correctly. `BatteryFlag = 128` means no
system battery, so the interesting case cannot be tested on this hardware. Recorded rather than
guessed at; the row is not shown wrong, it is simply always off here.

**Two defects found and fixed along the way**, both in the settings toggle, both from one cause -
a `Click` handler assigning `IsChecked` on a `OneWay` binding:

1. **The toggle stopped tracking reality after one click.** Assigning a dependency property that
   carries a binding replaces the binding with a local value, so from the first click onward the box
   never saw the view model again - the Task Manager veto was read correctly and never reached the
   screen. Measured both ways: a clicked instance stayed "on" through a real veto, a never-clicked
   instance tracked the registry live.
2. **The control was inoperable by assistive technology.** `TogglePattern.Toggle()` - what a screen
   reader or voice control invokes - raises `OnToggle`, never `Click`, so toggling it that way wrote
   nothing at all.

Both fixed by binding `IsChecked` TwoWay to a view-model property whose setter writes and then
publishes what the OS reports back, raising PropertyChanged unconditionally so a refused request
pulls the box back. Verified on the installed build in the exact scenarios that exposed them.

---

**STEP 3 FAILED, AND IS UNEXPLAINED.** With `Run\Hades` present and unvetoed since 13:55:27, an
interactive logon at 16:18:33 did not start Hades. Two hours later no Hades process existed, and
`control.token` still carried its pre-logon timestamp - a core that had run would have rewritten it.

Other entries in the SAME key ran at that logon: Steam at 16:18:40, RazerAppEngine at 16:18:44.
`Microsoft-Windows-Shell-Core/Operational` records the Run key being enumerated at 16:18:39-16:18:43
and contains no mention of Hades at all.

Ruled out, each by measurement rather than reasoning:

| Hypothesis | Result |
|---|---|
| Malformed value | Raw `RegEnumValue` dump: name `Hades`, clean UTF-16, `REG_SZ`, properly terminated - byte-identical in shape to Steam's, which works |
| StartupApproved veto | Absent for Hades |
| Bad target path | Present on disk |
| Entry added after logon | Run key last written 13:55:27; logon 16:18:33 |
| Defender / SmartScreen / Mark-of-the-Web | No block logged, no Zone.Identifier |
| Requests elevation (Windows silently skips those) | `app.manifest` has no `requestedExecutionLevel`, so `asInvoker` |
| Windows caches its startup list per session | **This was an earlier theory of mine and it is WRONG** - the entry is still not enumerated after a fresh logon |

**The strongest remaining hypothesis, unproven:** a Run-key app can start before Explorer has created
the taskbar, at which point `Shell_NotifyIcon` fails. `TrayIcon.Show()` is a bare
`_icon.Visible = true` with no guard and no handling of the `TaskbarCreated` broadcast that
well-behaved tray apps use to re-add their icon. That would produce exactly this signature - starts,
fails, leaves nothing behind. It is a genuine robustness gap either way, and worth closing on its own
merits. It is NOT recorded here as the cause, because it has not been shown to be.

**Next step is a reproduction, not a fix.** One observation is not enough to call this a defect, and
fixing on an unproven hypothesis is how a real cause gets hidden. A sign-out and sign-in - cheaper
than a reboot - with the entry left in place is the decisive test.
#### Outcome — mechanism verified against the real registry (2026-08-30). UI and reboot still outstanding.

---

**STEP 3 — RESOLVED 2026-09-01, AND NOT AS A HADES DEFECT. It cannot be verified on this machine,
because the machine does not honour ANY newly-added HKCU Run entry.**

The failure above is real and reproduced across four consecutive logons. What changed is the
attribution. Two hypotheses were formed, tested, and **both refuted** — each cost one sign-in rather
than one code change, which is the only reason the real answer surfaced:

1. **"Windows requires a StartupApproved record; the app deletes it on enable."** `LaunchAtLogin`
   does exactly that, and documents the choice ("enabling writes the Run value and DELETES any
   StartupApproved entry... it does not try to author one", because the byte format is undocumented).
   Every entry that ran had an enabled record; Hades had none. Tested by authoring a record
   byte-identical in shape to Steam's working one (`02 00 00 ...`). **Hades still did not start.**
   Refuted.

2. **"The value must be quoted."** Hades was the only unquoted entry, and every quoted one ran. This
   fit all five data points — but so had the first hypothesis, because Hades and the earlier control
   differed from the working entries in BOTH ways at once. That control was badly designed: two
   variables moved together and it discriminated nothing.

**The single-variable test that settled it.** Three Run entries, all carrying an enabled approval
record, differing only in quoting:

| | Entry | Quoted | Ran |
|---|---|---|---|
| A | `Hades` | yes | **no** |
| B | `ZZUnquotedProbe` -> `C:\Windows\System32\notepad.exe` | no | **no** |
| C | `ZZQuotedProbe` -> `"C:\Windows\System32\notepad.exe"` | yes | **no** |
| — | Steam, RazerAppEngine (pre-existing) | yes | yes, 7s and 9s after Explorer |

173 seconds after logon, none of the three had run. `Microsoft-Windows-Shell-Core/Operational` shows
the Run key enumerated at 18:49:37-42 and only pre-existing commands executed.
`Win32_StartupCommand` has never once listed any of the three, across every logon.

**So Windows on this machine ignores newly-added Run entries — including Microsoft's own
`notepad.exe`, quoted, with a valid approval record.** Whatever set of startup items it honours was
fixed at some earlier point and has not been refreshed by four sign-outs.

**What this means for the app.** The entry Hades writes is correct, verified at the raw-byte level
via `RegEnumValue`: name `Hades`, clean UTF-16, `REG_SZ`, properly terminated, byte-identical in
shape to Steam's, which runs. Also ruled out by measurement: StartupApproved veto (absent), bad path
(present on disk), entry written after logon (13:55 vs 16:18), Defender/SmartScreen/Mark-of-the-Web
(no block, no Zone.Identifier), and an elevation request (`app.manifest` has no
`requestedExecutionLevel`, so `asInvoker`). No crash dump, no WER report, no error event, and the
executable launches fine by hand — it never starts, so it is not starting and dying either.

**This step is therefore NOT ticked and NOT recorded as failed.** It is unverifiable here. Closing it
needs a different Windows machine or a fresh user profile. Do not "fix" the app against this symptom:
a change made against it would appear to do nothing and would bury whatever the real behaviour is.

**One hypothesis discarded but worth keeping as separate work.** Before the process was shown never
to start, the leading guess was that a Run-key app can start before Explorer creates the taskbar, at
which point `Shell_NotifyIcon` fails - and `TrayIcon.Show()` is a bare `_icon.Visible = true` with no
guard and no handling of the `TaskbarCreated` broadcast that tray apps use to re-add their icon. The
crash evidence rules it out as the cause HERE, but the gap is real and will bite on a machine where
launch-at-login does work. It is not a Task 10 finding; it is its own.

`LaunchAtLogin`'s doc comment says `WindowsStartupRegistry` is *"not unit tested: it is a
pass-through to HKCU, and exercising it in a suite would register a real login item"* — so the
two-location logic had never run against a real registry anywhere. A throwaway harness drove the
real `WindowsStartupRegistry` through the whole sequence. **All ten checks passed:**

| | check | result |
|---|---|---|
| 1 | `SetEnabled(true)` writes the `Run` value and reports on | pass |
| 2 | **Task Manager's veto is honoured** — with the `Run` value still present and a `0x03…` blob under `StartupApproved`, `IsEnabled` reads **off** | pass |
| 3 | Re-enabling **deletes** the veto rather than authoring undocumented state bytes | pass |
| 4 | A `0x02…` blob is *enabled*, not a veto — the low-bit heuristic is not "any entry vetoes" | pass |
| 5 | `SetEnabled(false)` removes the `Run` value and leaves no `StartupApproved` residue | pass |

Check 2 is the bug this task exists to prevent, and it is now demonstrated on a real machine rather
than against `FakeRegistry`. Check 4 is the one a careless implementation fails in the *other*
direction — treating the mere presence of an approval entry as a veto, so the toggle reads off while
Windows launches the app quite happily.

The harness registered a real login item and removed it; the registry was confirmed clean
afterwards, by both registry read and `Win32_StartupCommand`.

**Still outstanding, and genuinely requiring a person:** the app's own toggle UI, Task Manager's
Startup list showing the entry, the reboot, and the battery row (which needs a laptop).

---

### Task 11: Wire supervision — the shell owns a real core

**Files:**
- Modify: `Windows/Hades.Shell/App.xaml.cs`
- Create: `Windows/Hades.Shell/CoreLifetime.cs`

- [x] **Step 1: Read `Mac/HadesApp/Sources/HadesApp/AppDelegate.swift`'s `makeConfiguration`** — it locates the bundled core at `Contents/Resources/HadesServer/Hades.Server` and falls back to `dotnet run --project <repo>/Core/src/Hades.Server --no-launch-profile`, logging loudly which path it took.

- [x] **Step 2: Implement the Windows equivalent**

Release: `<install>\core\Hades.Server.exe`, next to the shell. Debug: fall back to `dotnet run`, logging that it did — that fallback needs the .NET SDK and this exact source tree, which is right for development and never right for a shipped app.

- [x] **Step 3: Hold the `JobObject` for the process lifetime**

```csharp
// Rooted in a field for the app's whole lifetime, deliberately. A JobObject eligible for
// finalization would have the kernel kill a HEALTHY core mid-session - one of the two ways a
// correct-looking Job Object implementation silently fails.
readonly JobObject _job = new();
```

- [x] **Step 4: Start the supervisor on launch, stop it on Quit**

`Quit Hades` calls `StopAsync()` — the **graceful** path. The Job Object is only the backstop for when the app never got to run it.

- [x] **Step 5: Reflect supervisor state in the tray** — icon and menu update as the state changes.

- [x] **Step 6: Hand-run the whole supervision contract.** This is the gate for Slice 4, and each of these is a distinct failure mode:

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

#### Outcome — Task 11, 2026-08-29. **SLICE 4'S GATE IS PASSED.**

Most of this task was already done as **Task 4a**, which had to exist before Task 4's own Step 7
could run. What was genuinely outstanding, and is now done:

**Step 2 was missing entirely** — there was no development fallback and no logging; the shell only
ever looked for the installed core and silently failed to spawn when it was absent. Now
`CoreLifetime` resolves `core\Hades.Server.exe` beside the shell, else
`dotnet run --project <repo>\Core\src\Hades.Server --no-launch-profile`, saying via `Trace` which
path it took. Resolution is a pure function of the two paths, so it has 5 tests; the repository root
comes from `[CallerFilePath]` (the C# equivalent of Swift's `#filePath`) rather than counting
directories up from `bin\Debug\net10.0-windows`, which would break on any configuration or TFM
change. The fallback was then exercised live — with `core\` renamed away, the shell brought a core up
through `dotnet run` and it wrote its discovery file.

**Step 3 was already satisfied, and the plan's own suggested code would have been wrong here.** It
proposes `readonly JobObject _job = new()` on `App`. But `Win32CoreProcessHost` already creates a job
per spawn and `Win32CoreProcess` holds it in a field, so the rooting chain is
`App._supervisor → CoreSupervisor._currentProcess → Win32CoreProcess._job → JobObject` — the job
cannot be collected while the core is supervised. Adding a field on `App` would create a SECOND,
unused job object that no core was ever assigned to, while the real one stayed exactly where it is.
Per-spawn is also better than app-lifetime: a restarted core gets a fresh job. The plan's underlying
concern (a finalized job killing a healthy core) is real; the ownership just already answers it.

**Step 6 — every row verified, mostly automated.**

| Scenario | Result |
|---|---|
| Launch with no core running | Core spawns, tray goes live |
| Launch with a core already running | **Adopted** — one core, not two; footer read "Adopted — quitting Hades leaves it running" |
| Quit after adopting → core survives | Verified through the STRONGER path: the shell was **force-killed** and the adopted core survived. An adopted core is never assigned to the Job Object, so kill-on-close cannot reach it — force-kill is the case where only that membership decides. The graceful path is separately unit-tested by `Stop_Does_Not_Kill_An_Adopted_Core`. |
| Quit after spawning → core exits | Verified by hand during Task 6 |
| **End Task after spawning → core dies** | **Verified.** No `OnExit` runs, so only `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` can do it — and it did, leaving nothing behind. This is the row the handover said had never been verified anywhere. |
| Kill the core externally | Shell noticed and respawned a DIFFERENT process; shell survived |
| Kill it repeatedly | Stopped after exactly 5 attempts, then stayed quiet for 45s. The shell survived giving up rather than crashing. |

One extra worth recording: under the `dotnet run` fallback the process assigned to the job is the
`dotnet` host and the server is its CHILD. Force-killing the shell still left **zero** stray
`dotnet`/`Hades.Server` processes, so kill-on-close takes the whole descendant tree, not just the
process that was assigned.

A single `Hades.Supervision.Tests` failure appeared once during a parallel build (20s vs the usual
7s) and did not reproduce in three consecutive runs — contention, not a defect.

---

# SLICE 5 — onboarding, CLI, and the Unity plugin

### Task 12: Onboarding — four steps, reworded

**Files:**
- Create: `Windows/Hades.Shell/Onboarding/OnboardingWindow.xaml{,.cs}`
- Create: `Windows/Hades.Shell/Onboarding/OnboardingViewModel.cs`
- Create: `Windows/Hades.Shell.Tests/OnboardingViewModelTests.cs`

- [x] **Step 1: Read the reference** — `Onboarding/OnboardingStep.swift` has five cases: `install`, `permissions`, `claudeCode`, `projects`, `unityPlugin`.

**Windows has four.** `permissions` is macOS TCC folder access; Windows has no equivalent prompt, and explaining one that never fires would be a lie.

- [x] **Step 2: Note the copy trap**

`Onboarding/Views/OnboardingInstallStepView.swift` hardcodes:

> "…five steps, and you can stop after the fourth with a fully working setup."

That is Swift-authored copy, not API-served. The Windows text must be **reworded, not ported** — it now has four steps.

- [x] **Step 3: Write the failing tests**

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

- [x] **Step 4: Run — expect FAIL**

- [x] **Step 5: Implement** the view model and the four step views.

The Claude Code step ports `Onboarding/ClaudeCodeVerifying.swift`: it reads `GET /control/settings` for the MCP port, then makes a raw `tools/list` JSON-RPC call to `http://127.0.0.1:{port}/mcp`. Read that file's doc comment for **what that proves and what it only assumes** — it proves the core is serving N tools, not that Claude Code has connected.

- [x] **Step 6: Run — expect PASS**

- [x] **Step 7: Show onboarding on first run only** — port `Onboarding/OnboardingCompletionTracking.swift`'s persistence idea to a per-user store.

#### Outcome — Task 12, 2026-08-29

Done through Step 7, with 13 tests. Step 8's full walk-through still wants a person clicking, but the
mechanics either side of it are verified live (below).

**The copy trap is handled, and then generalised.** The Mac's install step hardcodes "…five steps,
and you can stop after the fourth with a fully working setup" — authored copy, not API-served, and
simply wrong here. The Windows text is reworded for four. But the deeper fix is that
`OnboardingWindow` builds "Step N of 4" from `AllSteps.Length` rather than writing the number in
prose at all: a count stated in prose is exactly what went stale on the Mac the moment a platform had
a different number of steps. Two tests pin it — no step's copy may say "five steps", and none may
mention permissions.

**`OnboardingStep.Permissions` still exists as an enum member but is excluded from `AllSteps`.** That
is deliberate rather than sloppy: the plan's own draft test asserts
`DoesNotContain(OnboardingStep.Permissions, steps)`, which needs the member to exist, and keeping it
makes the exclusion explicit and testable instead of a silent omission somebody later "restores".

**The Claude Code step's honesty is pinned by a test.** A reachable result proves the CORE is up and
serving N tools — it does NOT prove Claude Code has connected, because the check never inspects
Claude Code's own state (doing so would mean touching another program's files, or depending on a CLI
that may not be on PATH inside the very app the check runs from). The copy says so, and
`TheClaudeCodeStepCopyDoesNotClaimClaudeCodeIsConnected` fails if that wording is ever softened.

**Verified live against a real core**, which matters because `LiveClaudeCodeVerifier` dials a real
socket and is not unit-tested:

- Onboarding appears on a clean profile; `onboarding.json` is NOT written until finish or skip.
- The full check path works: `GET /control/settings` → `mcpPort` 7823 → MCP `tools/list` →
  **32 tools**. Reading the port from settings rather than assuming 7823 is what makes a
  conflict-remedy port override work.
- Marking it completed and relaunching shows **no** onboarding window — first-run-only holds.

Completion is stored as a small JSON file under the application-data root rather than in the
registry: it is app state, it belongs beside the rest of Hades' per-user data, and deleting that
folder should take it with it. Every failure to read it means "show onboarding", since being shown it
twice costs far less than never being shown it.

- [x] **Step 8: Hand-run** — on a machine with no prior Hades state, walk all four steps and finish with a working setup.

#### Outcome so far (2026-08-30) — the hand-run found four real defects, all now fixed

**The step count and copy were right; two of the four steps did not work.** The Projects step said
"Add a Unity project so Hades can index it" and the Unity-plugin step said "Installing the Unity
plugin lets Hades see the editor live" — and `OnboardingWindow` had exactly **one** action panel,
for the Claude Code check. Neither step had any control at all.

Step 5 of this task said "implement the view model and **the four step views**". One window rendering
four step *copies* and one step *view* is not that. The Mac has a dedicated
`OnboardingProjectsStepView` and an `OnboardingViewModel` exposing both `addProject(path:)` and
`installPlugin(productGuid:)`.

**Thirteen tests passed the entire time.** Every one asked about step count, order, or copy; none
asked whether a step could *do* anything. That is the hole, and it is why the fix is not just wiring:
`OnboardingAction` now models what each step lets the user do as **data**, so the window switches
panels on it and a headless test can assert the invariant without a `Window` (which this test project
deliberately never touches):

```csharp
Assert.Equal([OnboardingStep.Install], withoutAnAction);   // Install is the ONLY informational step
```

Watched failing against the shipped behaviour first — that assertion, both
`EachStepOffersTheActionItsCopyDescribes` cases and `CurrentActionTracksTheStep` all went red, then
green. Source restored byte-identically (md5 compared). **194 → 203 shell tests.**

Three further defects came out of the same walk, each reported by the user looking at the screen:

1. **"A progress bar would be nice, a bit unclear it's not frozen."** Correct, and the cause is
   real: `POST /control/projects/add` calls `AdoptAndIndex` **synchronously**, so the add genuinely
   blocks — measured at ~6.4s for a 6,858-file project, ~10s on the first add after launch. A greyed
   button and the word "Adding…" is indistinguishable from a hang. Now an indeterminate
   `ProgressBar`, and copy that says what it is waiting on: *"Indexing the project — this can take a
   few seconds on a large one."*

2. **"To what project am I installing? If I have 10 then what?"** The step had one nameless button
   targeting whatever was added last, which cannot answer that. The Mac's own step says "Install it
   now **per project** below" and renders a list. Now the same: every project, each row with its own
   button carrying its own productGuid. `AddedProductGuid` and the no-arg `InstallPluginAsync` were
   deleted rather than left as a second way to do it.

3. **The list came up empty with two projects registered** — found by reading the rendered
   automation tree rather than trusting the code. Onboarding's `ProjectsViewModel` starts empty and
   fills only as a side effect of an add, and this window has no poll tick, so anyone who already had
   projects was told "no projects yet". It now fetches on entering the step. Same root cause as the
   gap `ProjectsViewModel.AddProjectAsync`'s own comment describes.

**A process note worth keeping.** While verifying, a `dotnet build` ran with the shell still open and
failed `MSB3027` (file locked) — after which the *old* binary was inspected and read as "still
broken". This task's own Task 7 note records that trap and the rule that prevents it: **stop the
shell before building.** The rule is in the plan because it costs an hour; it cost time again here.

A second round of feedback from the same walk produced four more, all fixed: **Skip and Next are
disabled while work is in flight** (advancing mid-add tore down the step that owned the outstanding
await); the wait **names the project and ticks elapsed seconds**; the Projects step now **lists what
was added** with its index status and node count; and an installed row **becomes "✓ Installed"**
rather than staying an identical-looking button. That last one uses the `success` flag the server was
already sending and `ProjectsViewModel` was discarding — a row that flipped to installed on a
refusal would be both wrong and reassuring.

#### Done: a real progress counter, and `add` no longer blocks

The request was "processed 1234/50000", and it needed three things that were all missing:
`OperationResult.progress` was declared and populated by nothing; the indexers reported
`FilesScanned` only as a final total; and `POST /control/projects/add` was synchronous with no
operation to poll. All three are now built, after an explicit decision to take the cross-platform
change rather than fake a number.

- **`IndexProgressUpdate`** (`Core/src/Hades.Core/Indexing/IndexProgress.cs`) — phase, completed,
  total, with one authored `Format()` so every client words it identically. **Per phase, not one
  global total**: scripts and assets are separate walks and the second's size is unknown while the
  first runs, so a single total would have to grow while you watch it.
- **`OperationRegistry.Start`** gained an overload handing the work a reporter. Reports after the
  operation is terminal are ignored — a straggling callback from work that already threw must not
  overwrite a failure with a cheerful count. This is the use that field's own doc comment reserved.
- **`AddAsync` adopts, then indexes as an operation.** Measured: the call returns in **146 ms**
  where it used to block **~6–10 s**. The response is additive — the row is still the body, plus a
  new `indexOperationId` a client can ignore entirely — so nothing on the Mac breaks. It also makes
  the Mac's own onboarding copy *true* for the first time: "Indexing starts right away and continues
  in the background — nothing here waits on it."

Real output, `project_aurora`:

```
Scripts: 0 of 1,774 files … Scripts: 1,774 of 1,774 files
Assets: 50 of 615 files   … Assets: 615 of 615 files
Binary assets: 930 of 930 files
```

**A 2× regression, caught by measuring rather than assuming.** The first implementation learned each
total with a separate counting pass, which walks every directory twice: **12.1 s against a 6.4 s
baseline**. Materialising each root's file list once and reusing it as the walk brought it back to
**6.3–6.5 s across three runs — indistinguishable from no progress at all.** The intermediate 10.4 s
reading was not overhead either: it was a first index after a core restart, matching the ~10 s
cold-start already measured for Task 13.

Two tests then failed in teardown, both correctly: a background index holds `graph.db` open while
the fixture deletes the directory around it. Each now waits for the work it started — the same
responsibility the CLI's own rebuild test already documented.

Still outstanding: the user's own uninterrupted walk of all four steps on the shipped MSI, which is
currently blocked — `nuget.org` was unreachable, and `dotnet publish -r win-x64` fails on `NU1900`
(a vulnerability-audit warning) promoted to an error by `TreatWarningsAsErrors`. **That fragility is
worth noting for release: a transient network outage fails the release build outright.**

---

### Task 13: CLI — the remaining commands

Spec #5 §5.4 promotes the CLI from a diagnostic to a product surface, **on both platforms**.

**Files:**
- Modify: `Core/src/Hades.Cli/Commands.cs`, `Program.cs`
- Modify: `Core/src/Hades.Control.Client/ControlClient.cs`
- Modify: `Core/tests/Hades.Cli.Tests/CommandsTests.cs`
- Modify: `Core/src/Hades.Cli/Program.cs` header comment

- [x] **Step 1: Retire the stale header**

`Program.cs` opens with *"NOT a product deliverable: its purpose is diagnostic."* That is no longer true. Rewrite it to say what the CLI now is: the supported headless path on both platforms, and the second consumer of `Hades.Control.Client`.

- [x] **Step 2: Add commands** — `add-project <path>`, `remove-project <guid>`, `rebuild <guid>`, `install-plugin <guid>`, `traces`, `memory`.

Each is a thin call against a route that already exists, holding the existing "deliberately dumb" rule: print what the core decided, compute nothing, invent no text. Most routes were added to `ControlClient` in Tasks 7–9; add any that are still missing, confirmed against `ControlListener.cs`.

- [x] **Step 3: Write tests** in `CommandsTests.cs`'s established style — against a **real** loopback `ControlListener`, not a mock. That property is why these tests are trustworthy; preserve it.

- [x] **Step 4: Run — expect PASS**

- [x] **Step 5: Implement `hades serve`**

Runs the core in the foreground of the calling terminal and exits with it. **Deliberately no supervised `hades start`** — supervision is the shell's job, and a CLI that spawned a detached unsupervised core would violate the "no hanging state" rule.

This composes with what already exists: a core started by `hades serve` is simply **adopted** by the shell if one launches later, which is exactly what `Ownership.Adopted` was built for. Verify that end to end.

#### Outcome — Task 13, 2026-08-29

Done through Step 5. Step 6's macOS half cannot be done from here at all — that machine is the only
place to confirm nothing regressed there.

Commands added: `add-project`, `remove-project`, `rebuild`, `install-plugin`, `traces`, `memory`,
plus **`operation <id>`**, which the plan's list omits but `rebuild` requires: rebuild answers an
operation id and returns, so without a way to ask about that id the CLI could start a rebuild and
never report on it. Every route already existed on `ControlClient` from Tasks 7-9; none had to be
added. The "deliberately dumb" rule holds throughout — `rebuild` prints the operation id rather than
polling to completion, because blocking would invent a progress model the route does not offer.

**`hades serve` runs the core; it does not host it.** `Hades.Cli` is barred from referencing
`Hades.Server` by the same three-layer guard the shell is, so serving in-process would not compile —
which is the boundary doing its job rather than an obstacle. It resolves the core beside the CLI,
then `core/` beside it, then the `dotnet run` source fallback, and inherits stdout/stderr so Ctrl+C
reaches the core through the shared console group with no signal forwarding to get wrong. It also
sets the working directory to the core's own, because the core reads `appsettings.json` from the
current directory — the same mistake already made once in the shell and fixed there.

`serve` is dispatched BEFORE discovery, and has to be: it exists to START a core, so requiring a
running one first would make it unusable for its only purpose.

**Step 5's composition claim is verified end to end.** With `hades serve` running, launching the
shell left the core count **unchanged at 1** — it adopted rather than spawning — and force-killing
the shell left that core **still running**. The terminal keeps ownership, which is exactly what
`Ownership.Adopted` and the "quitting Hades leaves it running" footer promise.

**A test race worth remembering.** The new `rebuild` test failed intermittently with
`graph.db … used by another process`. Not the connection-pooling bug from Task 1 — a *different*
cause with the same symptom: rebuilding is asynchronous SERVER-side, so a test that starts one and
returns immediately races its own teardown deleting the directory the rebuild is still writing into.
The fix is the test's responsibility, not the fixture's: it created the background work, so it waits
for the operation to leave `running` before disposing. Stable across three consecutive runs.

Also: `Hades.Cli.Tests` references BOTH `Hades.Server` and `Hades.Control.Client`, and each defines
its own `OperationState` — the deliberate wire-type duplication the conformance suite keeps in step.
A bare `OperationState.Running` binds to the SERVER's, which does not compile against a client DTO.
It has to be qualified.

No regressions: the Core suite's 25 remaining failures are all within the previously documented
pre-existing set.

- [ ] **Step 6: Hand-run on both platforms** — every command on Windows, and the same set on the Mac to confirm nothing regressed there.

#### Outcome — Windows half done (2026-08-30). Mac half still outstanding.

All twelve commands run against the **installed MSI build**, not the source tree, with a real core
started by `hades serve`: `serve`, `diagnose`, `status`, `projects`, `add-project`, `remove-project`,
`rebuild`, `operation`, `install-plugin`, `traces`, `memory`, `release`, plus the no-args usage.
Every one behaved. `diagnose` works with no core running and does not print the token; `release`
against a bad id exits non-zero with a readable error.

Three things the hand-run found that the suite could not.

**1. A wrong CLI help line — fixed.** `hades --help` said `remove-project <guid>  Forget a project
(its index is deleted; the folder is not)`. Measured: after `remove-project`, `graph.db` is still
73,728 bytes and untouched; only `project.json` changes, by one byte. **The server's message was
right and the help was wrong** — it told users their index had been deleted when nothing on disk
had been. Corrected to "nothing on disk is deleted". Nothing pinned that line, which is why it drifted.

**2. `hades serve` orphans its core when force-killed — reported, NOT fixed.** `Serve.cs` deliberately
inherits the console so Ctrl+C reaches the core as part of the same control group, and documents that
choice. `TerminateProcess` (End Task on the process) is not a console event, so it kills only the
parent. Measured consequence: the orphan keeps port 7823 and **the next `hades serve` fails outright**
— it does not adopt. The shell solves the identical problem with a Job Object (§2.2). Whether `serve`
should do the same, or adopt-or-spawn like the shell, is a design decision and is left for a
maintainer rather than taken here.

**3. A macOS-only command printed on Windows — fixed.** That failure in (2) recommended
``lsof -nP -iTCP:7823 -sTCP:LISTEN`` — a tool Windows does not have, offered at the exact moment the
user is most stuck. `McpBinding` now picks `netstat -ano | findstr :<port>` on Windows.

The fix also collapsed a duplication the code had already warned about: `RemedyForPortInUse`'s doc
comment says it exists so there is "exactly one authored recommendation, never two independently
drifting copies", while `DescribePortInUseFailure` carried its own second copy of the same sentence.
It now calls the shared one.

**Two tests pinned the `lsof` literal and passed on Windows**, so the suite was actively endorsing
the bug. Both now assert the platform-appropriate command — spelled out concretely rather than
delegated back to `McpBinding`, which would tautologically match whatever the implementation emits.

---

### Task 14: `hades diagnose`

§9.1 names this as the mitigation for the entire class of environmental failures CI cannot reach — OneDrive placeholders, antivirus locking, long paths, non-default Hub locations. For a maintainer who cannot reproduce those, one command a reporter can run is worth more than more tests.

**Files:**
- Modify: `Core/src/Hades.Cli/Commands.cs`
- Create: `Core/tests/Hades.Cli.Tests/DiagnoseTests.cs`

- [x] **Step 1: Write the failing test**

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

- [x] **Step 2: Run — expect FAIL**

- [x] **Step 3: Implement**, reporting: OS version and edition; process architecture and whether it is emulated (`RuntimeInformation.ProcessArchitecture` vs `OSArchitecture`); the resolved storage root and whether it exists; whether `control.token` is present and parseable; core version and uptime from `/control/ping` if reachable; per-project paths, index state and node counts; and whether each project path is under OneDrive (check for `OneDrive` in the path and for a reparse point).

**No secrets.** The bearer token must never be printed — this output goes into bug reports. Print only whether the file exists and parses.

- [x] **Step 4: Run — expect PASS**

- [x] **Step 5: Hand-run on Windows** with a real project, and check the output would actually help you diagnose a report from a stranger.

#### Outcome — Task 14, 2026-08-29. Complete, all five steps.

11 tests, and hand-run in both states that matter: with no core (the state someone runs this in when
angry) and against a live core with a real indexed project.

**No secrets, pinned by its own test.** `Diagnose_NeverPrintsTheBearerToken` exists because this
output goes straight into issue trackers, pasted by people who will not read it first, and the token
grants full control-API access on that machine. Only whether the discovery file EXISTS and PARSES is
reported.

**Two rows added beyond the plan's list, both earning their place:**

- **`longPaths`** — reads `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled`. Long
  paths are one of the four failure classes §9.1 names this command for, and `pathLength` alone only
  shows the symptom: it cannot distinguish "this path is long" from "this path is long AND this
  machine still enforces the 260-character limit".
- **`edition`/`release`** — Home versus Pro decides whether Developer Mode is available, which decides
  whether an unelevated process may create symlinks at all. Reachable with no package reference:
  `Microsoft.Win32.Registry` resolves for `net10.0` out of the box, verified by compiling a probe
  before committing to it.

**A reporting trap, found by reading the output rather than the code.** The registry's `ProductName`
still says **"Windows 10 Home" on Windows 11** — Microsoft never updated that key. Next to build
26200 in a bug report, that is actively misleading. The raw value is still printed, because a
diagnostic must report what the machine says rather than what we would prefer it said; a caveat line
is emitted beside it when `ProductName` claims Windows 10 while the build is >= 22000, pointing the
reader at the build number as authoritative.

Sample output, no core running:

```
environment:
  os:          Microsoft Windows 10.0.26200
  runtime:     .NET 10.0.11
  processArch: X64
  osArch:      X64
  edition:     Windows 10 Home
               (registry ProductName reports "Windows 10" on Windows 11 too - the build number above is authoritative)
  release:     25H2
  longPaths:   True
```

Per project it reports path, index state and status, node and edge counts, `oneDrive`, `reparse` and
`pathLength`. The OneDrive check is a name match reported as a plain fact rather than a diagnosis,
with the reparse-point flag beside it — either can explain a path that reads differently than it
looks, and neither alone is proof.

---

### Task 15: The Unity plugin's Windows arm — measured, not assumed

**This file exists because of the exact hazard this task reintroduces.** `HadesConnectionFile.cs`'s doc comment records that Unity's Mono resolves `SpecialFolder.ApplicationData` to `~/.config` while .NET 10 resolves it to `~/Library/Application Support` — same machine, same enum, different answer.

**Files:**
- Modify: `UnityPlugin/Assets/Hades/Transport/HadesConnectionFile.cs`

- [x] **Step 1: Measure first, before writing the branch**

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

- [x] **Step 2: Write the branch to match what you measured**

The file is **C# 9 only** (Unity 6000.3's ceiling) — no file-scoped namespaces, no target-typed `new`, no records. It is also embedded as a resource in `Hades.Core.dll`, so changing it changes the core build.

Keep the existing structure: `HADES_HOME` wins if set, else the per-platform default. Add a Windows arm anchored on whatever your measurement proved correct, and update the doc comment with the measured values and the date — that comment is the reason the next person will not guess.

- [x] **Step 3: Rebuild the core** so the embedded resource updates:

```powershell
cd C:\path\to\Hades\Core
dotnet build
dotnet test tests\Hades.Core.Tests --filter "FullyQualifiedName~PluginInstaller"
```

`Install_MatchesTheRealPluginSourceTreeExactly` compares the embedded copy against the files on disk — it will catch a stale embed.

- [x] **Step 4: End-to-end hand-run — the gate for Slice 5**

Install the plugin into a real Windows Unity project from the shell, open the project, and confirm the Editor **attaches** — the tray icon goes to `attached` and the project row says so. Then exercise a live-editor MCP tool from Claude Code and confirm it round-trips.

#### Outcome — Task 15, 2026-08-29. **SLICE 5'S GATE IS PASSED.**

Unity 6000.3.2f1, installed at `D:\Unity Editors` — a NON-DEFAULT Hub location, which is itself one
of the environmental hazards §9.1 names, and it caused no trouble.

**THE MEASUREMENT, which is the whole point of this task.** Probed by running an Editor script in
Unity's own Mono via `-batchmode -executeMethod`, against `.NET 10.0.11` on the same machine:

```
                        Unity Mono (Environment.Version 4.0.30319.42000)   .NET 10.0.11
  ApplicationData       C:\Users\u\AppData\Roaming                         (roaming, unused)
  LocalApplicationData  C:\Users\u\AppData\Local                           C:\Users\u\AppData\Local
  UserProfile           C:\Users\u                                         C:\Users\u
```

**The two runtimes AGREE on Windows** — the opposite of macOS, where Mono said `~/.config` and .NET
said `~/Library/Application Support`. So the Windows arm uses
`SpecialFolder.LocalApplicationData` DIRECTLY rather than hard-coding `UserProfile\AppData\Local`.

That difference from the macOS arm is deliberate, and the reasoning is recorded in the file: hard-
coding is a workaround for runtimes that disagree, and where they agree the API is strictly better —
Windows folder redirection (roaming profiles, managed desktops) moves AppData, and the API follows
where a hard-coded path silently would not.

**The OS check was measured too, not assumed.** Both `RuntimeInformation.IsOSPlatform(Windows)` and
`Environment.OSVersion.Platform` answered correctly under Unity's Mono (`True` and `Win32NT`). The
RuntimeInformation form was chosen because `PlatformID` is the API that historically lies — Mono
reports `Unix` for macOS, exactly the kind of answer this file exists to stop trusting.

**Step 4 passed end to end, with NO `HADES_HOME` set** — so the new default-path branch is what was
actually exercised:

1. Plugin installed through the real route; 30+ files landed under `Assets/Hades/`.
2. Unity opened the project, compiled `Hades.dll` with no errors, and logged
   `[Hades.ReloadGate] BOOT reconcile`.
3. `hades status` → `icon: attached`, `HadesE2E — Editor attached`.
4. `hades_charon_status` over MCP → `attached: true`, Unity 6000.3.2f1, **pluginVersion 1.4.0**,
   correct PID.
5. **A true Editor round-trip**: `scene_manage` (documented "Needs a live Editor") → `2 applied,
   0 failed`, with `Assets/Main.unity` saved and `Assets/HadesRoundTrip.unity` created, both
   verified on disk.

Worth keeping from step 5: the FIRST attempt came back
`"Cannot create a new scene additively with an untitled scene unsaved."` That is Unity's own
constraint, surfaced verbatim through the MCP channel — a *failed* call that proved the round-trip
just as conclusively as the successful one did.

Two incidental findings:

- **A hand-written `ProjectSettings.asset` is not enough.** Unity rejected the minimal one used to
  bootstrap the fixture ("File may be corrupted") and regenerated it with a DIFFERENT `productGUID`,
  so the project Hades had registered no longer matched the one Unity was running. The editor
  connected but matched no project, which looks exactly like a plugin that does not work. Re-adding
  the project fixed it instantly. Anyone scripting a Unity fixture should let Unity generate its own
  project settings first.
- The stale-embed guard was verified to BITE: appending a line to the plugin source without
  rebuilding made `Install_MatchesTheRealPluginSourceTreeExactly` fail, as designed.

---

### Task 16: `hades` on PATH — both platforms

**Files:**
- Modify: `install.sh`, `uninstall.sh`
- Modify: `Mac/HadesApp/scripts/build-app.sh`

Windows gets `hades` on PATH from the MSI (Task 19). macOS needs an answer too — Spec #5 §5.4 promises the CLI "on both platforms", and this is the only change this whole port makes to shipped macOS code.

- [x] **Step 1: Publish `hades` into the app bundle**

In `build-app.sh`'s Release path, alongside the existing `dotnet publish` of `Hades.Server`, publish `Hades.Cli` into `Contents/Resources/`. Follow the existing step's conventions exactly — self-contained, same RID, same "why" comment density.

- [x] **Step 2: Symlink it from `install.sh`**

```bash
# The one part of this installer that touches anything outside /Applications. Done only when
# /usr/local/bin already exists and is writable - never created, never sudo'd. If it cannot be
# made, say so and print the full path rather than failing an otherwise-good install.
```

- [x] **Step 3: Remove it in `uninstall.sh`** — `uninstall.sh` already promises to remove the sidecars a drag-to-Trash leaves behind; a dangling symlink is exactly that.

- [x] **Step 4: Make `hades serve` find the core**

On macOS it resolves relative to its own location (`Contents/Resources/HadesServer/Hades.Server`), falling back to `dotnet run` — the same two-mode design the shell uses.

#### Outcome — Task 16, 2026-08-29

Steps 1-4 written; **Step 5 cannot be done from Windows and its absence matters more here than
anywhere else in this port.** This is the only task that changes shipped macOS code, and the three
shell scripts have been syntax-checked (`bash -n`) but **never executed** — nobody has run
`build-app.sh`, `install.sh` or `uninstall.sh` with these edits. Treat them the way steps 1-3 of this
whole plan treated the Job Object: compile-and-review verified only, and the hand-run is where the
real findings will be.

`build-app.sh` publishes `Hades.Cli` self-contained beside the core, into
`Contents/Resources/HadesCli/`, covered by the same `codesign --deep` pass. Self-contained for the
same reason the core is: a user who installed a GUI app has not agreed to install a .NET runtime.

`install.sh` symlinks `/usr/local/bin/hades` at the bundle, only when that directory already exists
and is writable — never created, never `sudo`'d, and a failure warns with the full path rather than
failing an otherwise-good install. A symlink rather than a shell-profile edit: a profile edit has to
guess the shell, survives uninstall badly, and does nothing for anyone whose PATH is managed
elsewhere.

**Two bugs found by reading `uninstall.sh` before adding to it**, both of which would have been
silent:

1. **The `TARGETS` loop tests `[[ -e ]]`, which is FALSE for a dangling symlink** — and `$APP` is
   removed first, which is precisely what makes the link dangle. Adding the symlink to that list
   would have listed it and then never removed it. It is handled in its own block instead.
2. **`/usr/local/bin/hades` might not be ours.** Deleting it unconditionally would remove someone
   else's binary of the same name. It is removed only when it is a symlink whose target is inside
   the bundle being uninstalled (`readlink` works on a dangling link, so this holds either way);
   anything else is reported as left alone, with the reason.

`hades serve` gained the bundle layout as a fourth candidate — `../HadesServer/Hades.Server`,
resolved from `AppContext.BaseDirectory`, which is the real directory even when invoked through the
symlink, so the symlink needs no knowledge of where the bundle lives.

`CoreLocator` had **four resolution branches and zero tests** — it was added in Task 13 without any,
and this task added a fourth. It now has 7, including one pinning the ORDER: an installed core must
beat the development fallback on a machine that has both, which is every maintainer's machine.

- [ ] **Step 5: Hand-run on the Mac** — build a Release `.app`, run `install.sh`, confirm `hades status` works from a fresh terminal, run `uninstall.sh --dry-run` and confirm the symlink is listed, then actually uninstall and confirm it is gone.

#### Outcome — Task 16 Step 5, partially closed 2026-09-02. **Both scripts EXECUTED for the first time, under a macOS-command harness on Windows. Two real defects found and fixed.**

Step 5 stays unticked: it says "on the Mac", and this was not a Mac. But the sentence that mattered
in the checklist below — "have still **never been executed**, on any machine" — is no longer true,
and it was the largest untested surface in the project.

**What made execution possible at all.** Git Bash's root maps to `D:\Git\`, which is user-writable,
so `/Applications`, `/Volumes` and `/usr/local/bin` could be created as *real absolute paths* — the
scripts ran completely **unmodified**, not rewritten to point at a sandbox. Developer Mode (enabled
earlier for the icon work) makes `ln -s` produce native symlinks, so `[[ -L ]]`, `[[ -e ]]` and
`readlink` all carry true POSIX semantics. That was verified first, because the whole CLI-symlink
design rests on it: **a dangling symlink reports `-e` FALSE, `-L` TRUE, and `readlink` still
resolves** — exactly the three facts the two bugs found while writing `uninstall.sh` depend on. They
had been reasoned about correctly; now they are measured.

The twelve macOS-only commands (`sw_vers`, `hdiutil`, `ditto`, `xattr`, `osascript`, `open`,
`defaults`, `pgrep`, `shasum`, `curl`, `uname`, `id`) are stubs on `PATH`, each switchable by
environment variable so every branch could be driven. **The artifact under test is the WORKING TREE
converted to LF**, not `HEAD` — the Windows-port edits to both scripts are uncommitted, and an early
run against `HEAD` tested an `uninstall.sh` that had no CLI-symlink block at all.

**Defect 1 — `install.sh` died silently instead of printing its own error.** CONFIRMED by execution:

```
MOUNTPOINT="$(hdiutil attach "$DMG" -nobrowse -readonly | grep -o '/Volumes/.*' | head -1)"
[[ -n "$MOUNTPOINT" && -d "$MOUNTPOINT" ]] || die "Could not mount ${DMG_NAME}."   # <- unreachable
```

Under `set -euo pipefail`, a `grep` that matches nothing exits 1; `head` exits 0; `pipefail` makes
the pipeline status 1; the assignment inherits it; `set -e` aborts **at the assignment**. The `die`
on the next line — whose entire purpose is to explain this failure — could never run. Measured
before the fix: a failing `hdiutil attach` exited 1 having printed nothing after
`==> Mounting the disk image`. Fixed with `|| true` on the substitution, which makes the existing
guard reachable; re-measured after, both mount-failure modes now print
`error: Could not mount Hades-2.0.0-unsigned.dmg.` and exit 1. Two sibling assignments share the
shape (`MACOS_MAJOR=`, `ACTUAL=`) and are left alone: neither has a guard behind it that the abort
would skip, and `sw_vers` failing on a Mac is not a real case.

**Defect 2 — `uninstall.sh` printed full paths where it meant to print `~`.** `${t/#$HOME/~}` looks
correct and is not: the REPLACEMENT half of `${var/pattern/string}` undergoes **tilde expansion**, so
the bare `~` expands straight back to `$HOME` and the substitution is a no-op. Proven rather than
guessed by setting `HOME=/ZZZ` and watching the output follow it, which also rules out a failed
match. Cosmetic only — no path is computed from it — but it defeated a deliberate readability
choice on every line the uninstaller prints. Fixed with `\~`.

**What ran, and what it proved.** `uninstall.sh`, eight scenarios:

- `--dry-run` on a fully installed machine lists all seven items and, verified by a before/after
  manifest of every path with SHA256s, **removes nothing**; the login item stays registered.
- A real run removes all seven and exits 0.
- **Five deliberate bystanders survive**, including the one the script's own header names as the
  reason never to glob on "arcforge": `~/Library/Preferences/unity.DefaultCompany.ArcForge.plist`,
  which is UNITY's file for a company called ArcForge. Also surviving: a project's `.arcforge/`
  with authored memory, its `Assets/Hades/`, and a neighbouring `com.arcforge.hades.shell.helper`
  cache that an over-broad bundle-id match would have taken.
- All three `/usr/local/bin/hades` ownership branches behave as designed — removed when it is a
  symlink into the bundle; **left alone with a stated reason** when it is a real file
  ("a real file, not our symlink") or a symlink pointing elsewhere ("does not point into
  /Applications/Hades.app").
- A machine with nothing installed reports `nothing found - Hades was not installed here`, exit 0.
- An app that refuses to quit aborts with exit **1** having removed **nothing** (36 paths before,
  36 after) — the guard that matters most, since removing the bundle out from under a running
  process is the failure this check exists to prevent.

`install.sh`, thirteen scenarios: the happy path installs the bundle, creates the symlink, and the
CLI **executes through the link**; the mountpoint is detached and the `mktemp` workdir cleaned by
the `EXIT` trap. Every guard fires correctly and installs nothing — non-macOS, `sudo`, Intel,
macOS 13, already-running, download failure, checksum mismatch, DMG without a `Hades.app`, `ditto`
failure. Quarantine present warns but still installs. A bundle with no CLI, and an absent
`/usr/local/bin`, both warn and still exit **0** — an otherwise-good install is not failed by
either, as intended.

**Then the round trip Step 5 actually asks for**: `install.sh` → the app writes a data root and
sidecars → `uninstall.sh --dry-run` (which **sees the symlink `install.sh` itself created**, the one
cross-script agreement no amount of reading can confirm) → real `uninstall.sh`. Everything Hades
owns is gone; `.arcforge/memory.md` survives. This is what closes the loop between the two scripts:
`install.sh`'s `CLI_SOURCE` and `uninstall.sh`'s `"$CLI_TARGET" == "$APP"/*` were shown to agree at
runtime, not by comparing string literals.

**Checked statically alongside, because execution cannot reach it here:**

- **The three-way path agreement holds.** `install.sh` symlinks
  `Contents/Resources/HadesCli/hades`; `build-app.sh` creates that exact directory and copies the
  publish output into it; `Hades.Cli.csproj` sets `<AssemblyName>hades</AssemblyName>`, so the
  apphost really is named `hades`. A mismatch in any of the three would have made the whole feature
  silently print "no bundled CLI in this build".
- **No bash 4+ syntax.** macOS still ships **bash 3.2.57** as `/bin/bash`, and `curl … | bash` uses
  whatever `bash` resolves to. Scanned for `mapfile`/`readarray`/`declare -A`/`${x,,}`/`${x^^}`/
  `coproc`/`|&`, and for GNU-only `readlink -f`/`sed -i`/`grep -P`: none present.
- **`HADES_HOME` is the right variable**, honoured by both `ClientPaths.DefaultRoot()` and
  `Hades.Server`'s `Program.cs`, and the default `~/Library/Application Support/Hades` matches
  `AppPaths`.

**Line endings are a non-issue, but only just.** Both scripts are **CRLF in a Windows working tree**
— `.gitattributes` normalizes `*.cs`, `*.md`, `*.json`, `*.yml` and others with `eol=lf` but has no
rule for `*.sh`, so `* text=auto` plus `core.eol=native` checks them out with CRLF. The committed
blobs are clean LF (measured: 0 CRs), so what `raw.githubusercontent.com` serves — the only way
these are ever consumed — is correct, and `text=auto` normalizes on commit so a Windows edit cannot
poison it. Worth an `*.sh text eol=lf` rule anyway, since a CRLF `curl | bash` fails on macOS with
an error message that names none of this.

**What this still does NOT establish**, and no harness on Windows can: that `hdiutil` mounts the real
DMG, that `ditto` preserves the bundle correctly, that `codesign --deep` survives the copy, that
Gatekeeper stays quiet, that `osascript` really removes an `SMAppService` login item, or that
`shasum` matches the released artifact. The happy-path checksum comparison was satisfied by a stub
returning the expected value — the **mismatch** direction used a genuine SHA-256 and did die. The
`-x` test on the bundled CLI could not be exercised with a real permission bit either: `chmod +x` is
a no-op on this filesystem, verified with a control, so the fake CLI carries a shebang to read as
executable to MSYS. Those are the reasons the box stays unticked.

---

# SLICE 6 — packaging and release

### Task 17: WiX scaffold and the x64 MSI

**Files:**
- Create: `Windows/Installer/Hades.wxs`
- Create: `Windows/Installer/build-msi.ps1`

- [x] **Step 1: Accept the WiX EULA**

```powershell
dotnet tool install --global wix
wix eula
```

WiX v6+ carries an Open Source Maintenance Fee, but its EULA §1 applies it only to revenue-generating users with annual gross revenue ≥ US$10,000 — **free for this project**. Accept once.

- [x] **Step 2: Write `Hades.wxs`**

Per-user install to `%LOCALAPPDATA%\Programs\Hades`:

```xml
<Package Name="Hades" Manufacturer="ArcForge" Version="$(var.Version)"
         UpgradeCode="PUT-A-STABLE-GUID-HERE-ONCE-AND-NEVER-CHANGE-IT" Scope="perUser">
```

Generate the `UpgradeCode` GUID **once** and never change it — it is what makes upgrades replace rather than install alongside.

Payload: `Hades.Shell.exe`, `hades.exe`, `core\` (the self-contained publish), and the icons. Include `MajorUpgrade` with a downgrade error message, a Start Menu shortcut, and a per-user `Environment` element putting the install directory on `PATH`.

Launch-at-login stays an **in-app setting**, not an MSI feature, matching the Mac.

- [x] **Step 3: Add a launch condition for the OS floor**

An MSI `LaunchCondition` can check `VersionNT`/build number. It **cannot** check Windows *edition*, so the "Windows 10 Enterprise/IoT/LTSC only" nuance in .NET 10's supported-OS list cannot be enforced by the installer — it is a documented support statement, not a gate. Do not describe it as prevented.

- [x] **Step 4: Write `build-msi.ps1`** — takes a RID (`win-x64` / `win-arm64`) and a version, does `dotnet publish` of `Hades.Shell` and `Hades.Cli` self-contained for that RID, then `wix build` producing `Hades-<version>-<rid>.msi`.

- [x] **Step 5: Build the x64 MSI**

```powershell
cd C:\path\to\Hades\Windows\Installer
.\build-msi.ps1 -Rid win-x64 -Version 2.1.0
```

- [x] **Step 6: Hand-run install/uninstall/upgrade**

Install it. Verify: **no UAC prompt**; it appears in Settings → Apps; `hades status` works from a **new** terminal (PATH takes a new session); the Start Menu shortcut launches it. Then build a `2.1.1` MSI and confirm it **upgrades in place** rather than installing alongside. Then uninstall and confirm the install directory and PATH entry are gone — and that `%LOCALAPPDATA%\Hades` (the data root) is **left alone**, matching `uninstall.sh`'s promise never to destroy authored data.

#### Outcome

WiX **7.0.0** (the plan anticipated v6). `UpgradeCode = CE41CF62-CFE0-4F72-93B5-2E4E33E3A0FF`, generated once. Both MSIs build: x64 103 MB / 862 files, arm64 96.7 MB / 861 files.

**Step 3 was written wrong, and only a real install found it.** The first MSI failed to install with exit code 1603. The verbose log said:

```
Action ended: LaunchConditions. Return value 3.
Property(S): WindowsBuild = 9600
```

`WindowsBuild = 9600` is **Windows 8.1**, on a machine running Windows 11 build 26200. MSI's `WindowsBuild` and `VersionNT` are frozen at 6.3.9600 for unmanifested packages, so the condition `WindowsBuild >= 14393` is false on **every** supported Windows — that installer would have refused to install for every user. The plan's claim that a LaunchCondition "can check `VersionNT`/build number" is therefore only half true: it can check them, but they do not mean what they appear to.

The fix reads `HKLM\...\CurrentVersion\CurrentBuildNumber`, which is not shimmed and reports the real 26200. That value is a `REG_SZ`, which raised a second question — does MSI compare it numerically or lexicographically? Measured with a throwaway probe package rather than assumed, using a floor that discriminates (`26200 >= 9999` is true numerically, false lexicographically since `'2' < '9'`):

| floor | result | conclusion |
|---|---|---|
| 14393 | passed | — |
| **9999** | **passed** | comparison is **numeric** |
| 999999 | **blocked** | the guard genuinely refuses; it is not merely never firing |

The Task 17 note about **edition** still stands unchanged and is still not enforced.

**A second latent bug, found in passing.** A mistyped `StagingDir` made `wix build` emit `WIX8601`/`WIX8600` ("zero files harvested") as **warnings**, exit **0**, and produce a **0.04 MB MSI** that would install cleanly and deliver nothing. WiX 7.0.0 has no warnings-as-errors switch (`wix build -h` lists none), so `build-msi.ps1` now counts rows in the built MSI's `File` table and requires an exact match with the staged file count — which catches a partial harvest too, not only a total one. Guard watched failing: `MSI carries 0 files but staging holds 862`.

Step 6, all verified on this machine:

| check | result |
|---|---|
| No UAC prompt | unelevated `/qn` install returned 0; a per-machine install would fail 1925. `AssignmentType = 0` confirms per-user |
| Settings → Apps | `Get-Package` reports `Hades 2.1.0 msi` |
| Payload | 862/862 files, incl. `hades.exe`, `Hades.Shell.exe`, `core\Hades.Server.exe`, `core\appsettings.json` |
| PATH from a new session | resolves via a PATH rebuilt from the registry, and `hades` runs. Exactly one entry, last |
| Start Menu shortcut | target and working directory both correct |
| 2.1.1 upgrade | **one** product registered afterwards, `RemoveExistingProducts` ran, ProductCode rotated, UpgradeCode held. PATH entry still exactly one |
| Uninstall | install dir, PATH entry and shortcut all gone |
| **Data root** | **`%LOCALAPPDATA%\Hades` survived intact — 7 files before, 7 after** |

`hades diagnose` from the installed copy reports correctly and does **not** print the token, only that it is present and parses.

One thing verified structurally rather than by hand: the Start Menu shortcut was checked for correct target and working directory, not clicked.

---

### Task 18: The arm64 MSI

- [x] **Step 1: Build it**

```powershell
.\build-msi.ps1 -Rid win-arm64 -Version 2.1.0
```

An MSI carries one architecture, so this is a second artifact, not a second payload in the same file.

- [x] **Step 2: Be honest about verification**

`dotnet publish -r win-arm64` was verified on the Mac to *produce files*. **No arm64 binary from this project has ever been executed.**

If ARM64 Windows hardware is available, install and run it, and record that. If not, ship it **labelled untested** in the release notes — do not describe an unexecuted binary as verified, and do not quietly drop it either.

- [x] **Step 3: Record which you did**, in `Documentation/ReleasePipeline.md`.

#### Outcome

Built: `Hades-2.1.0-win-arm64.msi`, 96.7 MB, 861 files, MSI Template summary `Arm64;0` (which is what stops it installing on x64 and vice versa).

**Still never executed.** No ARM64 Windows hardware was available, so the honest label in Task 18 Step 2 stands and the release notes must carry it.

What *was* verified goes beyond the plan's "produces files", because the failure this guards against is specific: a silently-x64 native dependency would install fine and then fail at the first database open, and x64 emulation is Windows 11 only — a Windows 10 ARM64 machine has no fallback. So every binary's PE machine type was read directly from its header, with the x64 publish as a control:

| binary | win-x64 | win-arm64 |
|---|---|---|
| `Hades.Shell.exe` | x64 | **ARM64** |
| `hades.exe` | x64 | **ARM64** |
| `core\Hades.Server.exe` | x64 | **ARM64** |
| `core\e_sqlite3.dll` | x64 | **ARM64** |

That establishes the payload is genuinely native. It does **not** establish that it runs.

---

### Task 19: `install.ps1` and the Mark-of-the-Web measurement

**Files:**
- Create: `install.ps1`

- [x] **Step 1: Measure MotW before writing the script**

Download the MSI three ways and inspect each:

```powershell
curl.exe -L -o msi-curl.msi <url>
Invoke-WebRequest -Uri <url> -OutFile msi-iwr.msi
# and once through a browser
Get-Item msi-*.msi | ForEach-Object { $_.Name; Get-Content $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue }
```

Expected from the research: `curl.exe` (in-box since Windows 10 1803) writes **no** `Zone.Identifier`; a browser does. `Invoke-WebRequest`'s behaviour is contested — **this is the measurement that settles it.** Record all three results.

#### Outcome

| downloader | `Zone.Identifier` |
|---|---|
| `curl.exe` 8.21.0 | **none** |
| `Invoke-WebRequest` | **none** — this settles the contested question |
| `System.Net.WebClient` | **none** |

**With a control**, because "no stream found" and "streams do not work here" look identical: the target filesystem is NTFS, and a hand-written `Zone.Identifier` on a file in the same directory read back correctly. So "clean" means the downloader genuinely wrote no mark, not that the check was incapable of seeing one.

None of the three marks the file, so Step 2 is free to use `curl.exe` for the reason the plan gives — in-box since Windows 10 1803, no PowerShell version sensitivity — rather than because it was the only clean option. A **browser** download was not measured here and is still expected to mark the file; that is the path Step 3 must describe for users.

- [x] **Step 2: Write `install.ps1` using whichever proved clean** (expected: `curl.exe`), mirroring `install.sh`'s structure: version and SHA256 pinned at the top, checksum verified before install, refuses to run as admin, refuses on the wrong architecture, and states plainly what it does and does not do.

#### Outcome

`install.ps1` written, mirroring `install.sh` section for section. Uses `curl.exe` for the reason the plan gives — in-box since Windows 10 1803, insensitive to PowerShell version — with an `Invoke-WebRequest` fallback for builds 14393–17133 where `curl.exe` does not exist. That fallback is a *real* alternative rather than a compromise precisely because Step 1 measured it clean.

Two things `install.sh` does not have to deal with:

- **Two checksums**, one per architecture, because an MSI carries one architecture. Both are the sentinel `REPLACE_AT_RELEASE` and the script **hard-fails** while either is unset — an installer that silently skips verification is worse than one that refuses to run. They get pinned when Task 21 publishes the release.
- **`throw`, never `exit`.** The documented invocation is `irm ... | iex`, and `exit` under `iex` terminates the user's whole PowerShell session rather than just the install. The body is a function; the caller catches and prints.

Verified under Windows PowerShell **5.1** (the worst case — the in-box shell):

| path | result |
|---|---|
| Parses | clean |
| Precondition chain | architecture resolves to `win-x64`, build 26200 accepted, non-admin accepted |
| Run via `iex` | refuses, and **the session survives** |
| Sentinel refusal | fires, naming the architecture it lacked a checksum for |
| **Download → verify → install** | exercised end to end against a `file://` URL pointing at the real MSI: 862 files installed, product registered, temp working directory removed |
| **Checksum mismatch** | one character changed → refused, **installed nothing**, cleaned up |
| **Hades already running** | refused, installed nothing |

The running-shell guard was exercised with a stand-in process carrying the image name `Hades.Shell` rather than by launching the real tray app; `Get-Process` matches on image name, so that is the same test without starting the app and its core.

The machine was returned to its pre-test state: not installed, `%LOCALAPPDATA%\Hades` intact.

Not covered, and not claimable until a release exists: the real HTTPS download path (the `https://` URL and TLS 1.2 pinning were bypassed by the `file://` harness) and the admin refusal, which was not run from an elevated prompt.

- [ ] **Step 3: Verify the SmartScreen experience end to end, both paths**

Browser download → expect the *"Windows protected your PC"* dialog, default button **Don't run**, past which the user must click **More info → Run anyway**, publisher shown as *Unknown*.

`install.ps1` path → expect no interstitial.

**Record both verbatim**, including exact wording, and put the browser-path description into `Documentation/Installing.md`. Users deserve to be told what they will see rather than discovering it.

- [ ] **Step 4: Note the Smart App Control caveat** — on Windows 11 machines with SAC enabled (clean installs only), unsigned code is blocked outright with no override. If you can test on such a machine, do; if not, say so.

#### Outcome — Steps 3 and 4 partially blocked

**Half of Step 3 is done.** The `install.ps1` path produces **no interstitial**: Step 1 measured that none of the three command-line downloaders writes a `Zone.Identifier`, and the end-to-end run in Step 2 installed the MSI with no dialog of any kind.

**The browser half is blocked on Task 21** — it needs a real published release URL to download from, and no Windows release exists yet. Verifying it against a locally-copied file would test Windows' MotW handling, not the release, and the plan asks for the wording users will actually see. Do it when the release is published, and record the dialog wording verbatim into `Documentation/Installing.md`.

**Step 4 cannot be tested here.** Smart App Control is enabled only on clean Windows 11 installs; this machine has it **off** — `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState = 0` (0 off, 1 enforcement, 2 evaluation). The caveat is recorded from documentation, in `Documentation/ReleasePipeline.md` §9.3 and in `install.ps1`'s own header — both state plainly that this script cannot help on a SAC machine and does not pretend to. **Untested, and labelled as such.**

---

### Task 20: Version lockstep gate

`Documentation/ReleasePipeline.md` §2 lists the sites that must move together. That table has already failed twice in one release cycle — `plugin.json` sat at `0.1.0` through the entire 2.0.0 cycle because nothing compared it to anything. Two MSIs and an `install.ps1` make the table longer.

**Files:**
- Create: `scripts/check-version-lockstep.sh`
- Modify: `Documentation/ReleasePipeline.md`

- [x] **Step 1: Read §2's table** and enumerate every current site, including the new Windows ones.

- [x] **Step 2: Write the script** — takes the intended version, greps each site, prints a table of site → found version, exits non-zero on any mismatch. Generalises the existing `plugin.json`-vs-tag gate in `release.yml` to every row.

- [x] **Step 3: Prove it fails**

```bash
bash scripts/check-version-lockstep.sh 2.1.0
```
Deliberately bump one site to a wrong value, re-run, confirm it names that site and exits non-zero, then revert. A gate nobody has watched fail is not a gate.

- [x] **Step 4: Update §2's table** with the new sites.

#### Outcome

`scripts/check-version-lockstep.sh` written. Eight sites in three groups, because treating them alike would be a bug:

**Lockstep with the release** — `HadesTools.cs` `ServerVersion`, `build-app.sh` `CFBundleShortVersionString`, `install.sh` `VERSION`, `install.ps1` `$Version`, `plugin.json` `version`.

**The Unity plugin's own line** — `HadesBoot.PluginVersion` is *not* compared to the release version. It is deliberately independent, and the skew between plugin and app is something the code reasons about (`PluginVersionSkewTests`). What the gate checks is that its two hand-copied test mirrors still agree with it — those are literals someone typed, each carrying a comment saying "keep in sync", which is exactly what rots unwatched.

**Release-only** — the installer checksums: `install.sh`'s `SHA256` and both of `install.ps1`'s. They cannot be right before the artifacts exist, so the gate is red during ordinary development by design, and you cannot release without pinning them.

`CFBundleVersion` (currently `5`) is a build counter, not a marketing version, so it is not compared.

Step 3, three separate proofs rather than one:

| provoked | gate said | exit |
|---|---|---|
| Nothing — run against `2.0.0` | exactly 3 failures: `install.ps1 $Version` (2.1.0) and the two unpinned checksums. Every other row green | 1 |
| Run against `2.1.0` | 4 sites still at 2.0.0, correctly named | 1 |
| `RealAppPluginVersion` 1.4.0 → 1.4.1 | named `CharonStatusTests.cs` specifically | 1 |
| **`ServerVersion` renamed** to `ServerRelease` | **`NOT FOUND`**, not `ok` | 1 |

That last one matters most: an extraction that silently matches nothing would make the whole gate decorative while looking green. Both provoked edits were reverted **byte-identically** (md5 compared) and the working tree confirmed clean.

Running it against `2.0.0` also serves as positive evidence the extraction machinery works — every non-Windows row resolved to a real value, so the green rows are green because they match, not because nothing was read.

---

### Task 21: Release pipeline — build and attach both MSIs

**Files:**
- Modify: `.github/workflows/release.yml`

- [x] **Step 1: Read `release.yml` in full**, including its comments about the mistakes it already encodes — the plugin repo being tagged by accident, the 61 MB DMG published to the wrong repo.

- [x] **Step 2: Add the lockstep gate** as an early step, so a mismatch fails before anything is built or pushed.

- [x] **Step 3: Add a `windows-latest` job** that builds both MSIs and attaches them to the release with `gh release upload`.

Building **and attaching** from CI is the point: the previous manual attach step is exactly where the documented mistakes happened.

- [ ] **Step 4: Dry-run it** using the workflow's existing `workflow_dispatch` dry-run input. Confirm both MSIs are produced and the upload is attempted without publishing.

- [x] **Step 5: Note the asymmetry** — the DMG is still built locally on the Mac while the MSIs come from CI. Two artifacts with two provenances is a drift risk. Record it in `ReleasePipeline.md` as known debt, with moving the DMG into CI as the fix.

#### Outcome

`release.yml` restructured from one job into four: `params` → `lockstep` → (`release`, `windows`) in parallel.

`params` exists because `TAG`/`DRY_RUN` were computed inside the release job — correct while that was the only job, but the gate and the Windows build both need them now, and three copies of that `if` would be three places for a dry run to quietly stop being dry. `lockstep` gates both downstream jobs, so a mismatch fails **before** anything is built, pushed or uploaded.

The `windows` job installs .NET 10 and WiX 7, builds both MSIs, prints paste-ready checksums, uploads them as workflow artifacts **before** attempting any release upload (so a 15-minute build is not lost to a failed attach), then attaches them with `gh release upload --clobber` and **asserts the assets are actually present afterwards** — a release page looking complete while carrying zero assets is a mistake §8.4 records happening for real.

Two decisions the plan did not specify, both recorded rather than made silently:

- **The release does not exist at tag-push time.** §8.4 has the maintainer create it by hand afterwards, so `gh release upload` alone would always fail. The job creates it as a **draft** if missing — a draft is not public, so CI cannot publish anything on its own, and the maintainer still writes the notes and presses publish. What it removes is the manual attach, which is the step the documented mistakes actually happened in.
- **A checksum deadlock, found while wiring this up.** `install.ps1` pins a SHA256 per architecture, but the MSIs are built by the tag run itself and an MSI is not byte-reproducible, so their hashes cannot be known before the tag exists. The gate would block the tag; the tag is what produces the values. Resolved by giving the script a `--skip-checksums` flag that **CI alone** uses, with the maintainer running the full gate before publishing. The residual window is documented as debt in `ReleasePipeline.md` §9.5 along with three possible fixes — it wants a maintainer's decision, not a default.

Verified locally: the YAML parses, every `needs` resolves to a real job, `params` exports all three outputs, the two dry-run branches are complementary (`!=` / `==` on the same value, so never both and never neither), and no stale `inputs.` reference survives outside `params`.

**Step 4 is blocked and deliberately not ticked.** A `workflow_dispatch` dry run has to execute on GitHub, which needs these commits pushed — and git is the user's to run. Everything it would exercise is in place; run it before tagging, and expect it to build `0.0.0` in place of `0.0.0-dryrun` because an MSI ProductVersion must be numeric.

---

### Task 22: Documentation

**Files:**
- Modify: `README.md`, `LIMITATIONS.md`, `Documentation/Architecture.md`, `Documentation/Installing.md`, `Documentation/ReleasePipeline.md`

- [x] **Step 1: `README.md`** — platform badges, prerequisites, and the **beta** label for Windows.

- [x] **Step 2: `LIMITATIONS.md`** — the Maturity section currently says *"macOS is the ONLY tested platform."* Update it, and add §9.1's environmental classes: long paths, OneDrive placeholders, antivirus locking WAL files, AppLocker/WDAC blocking `%LOCALAPPDATA%`, non-default Unity Hub drives.

- [x] **Step 3: `Documentation/Architecture.md`** — §2.2 describes only the Mac shell; §8 only the DMG. Both need the Windows half. Follow that document's own convention: where it drifts from the code, the code is right.

- [x] **Step 4: `Documentation/Installing.md`** — the Windows path, and §8.2's honest SmartScreen description from Task 19's measurement.

- [x] **Step 5: `Documentation/ReleasePipeline.md`** — the Windows build steps and the §2 table updates from Task 20.

- [x] **Step 6: Update Spec #5's status header** to record that steps 4–6 are implemented, and correct anything implementation disproved. Steps 1–3 corrected the spec four times; expect the same here, and treat it as the spec working rather than failing.

#### Outcome

All six files updated. Windows is labelled **beta** everywhere it appears, and the ARM64 build is labelled **never executed** in README, LIMITATIONS and Installing rather than in only one of them.

**`README.md`** — Windows badge; "standalone macOS menu-bar app" → a desktop app that is a menu-bar app on macOS and a tray app on Windows; per-platform Prerequisites, Installation and Uninstalling; the architecture diagram's shell box now shows both shells and the storage line shows both roots; a PATH-needs-a-new-terminal troubleshooting row; and the signing section split into macOS (quarantine) and Windows (Mark-of-the-Web) halves.

**`LIMITATIONS.md`** — a new "Windows (beta)" section carrying §9.1's classes **individually named** rather than as a general disclaimer: long paths, OneDrive placeholders, antivirus and WAL locking, AppLocker/WDAC blocking `%LOCALAPPDATA%`, non-default Unity Hub drives, path case-insensitivity, and the unexecuted ARM64 build. Each is marked untested where it is. `hades diagnose` is pointed at as the mitigation §9.1 itself proposes.

> The plan says this file "currently says *macOS is the ONLY tested platform*". **It does not** — the actual Maturity text reads "not yet across many projects, Unity versions, or platforms". The real text was updated; the quoted text was never there.

**`Documentation/Architecture.md`** — §2.2 became "The shells", with the Windows half describing what the code actually does, read from the code rather than from the plan: `CoreSupervisor` sharing the Mac's decision logic behind `ICoreProcessHost`; the Job Object replacing the reaper (and *why* — the kernel is the thing still alive after the app dies); `CREATE_SUSPENDED` → assign → `ResumeThread` closing the spawn→assign race, with the note that `System.Diagnostics.Process` cannot express `CREATE_SUSPENDED` at all; `CREATE_NO_WINDOW` recorded as a real user-reported bug rather than a theoretical one; and `Local\` vs `Global\` for the mutex. New §8.1 covers the MSI: two architectures, the layout and why it is not arbitrary, per-user and why that means no UAC, the data root surviving uninstall, and the registry-read OS floor.

**`Documentation/Installing.md`** — per-platform Requirements and Building; the install section now leads with the principle both platforms share (the channel matters more than the file) before splitting into Gatekeeper and SmartScreen halves; Windows Options A/B, the post-install PATH caveat, and uninstall. Heading levels were also corrected — the macOS Option A/B were `###` under a `###` platform heading.

**`Documentation/ReleasePipeline.md`** — §8.3 now states plainly that the MSIs are **not** built by hand during a release (CI builds and attaches them), and §8.4 gained the checksum-pinning step with the gate run that closes it.

**Spec #5** — status moved to "Implemented — slices 1–6", plus a "What implementation corrected" table. Two measured corrections: the `LaunchCondition`-on-`VersionNT` claim (wrong, and it shipped a broken first MSI), and §8.2's contested `Invoke-WebRequest` question, now **settled** — re-measured under Windows PowerShell **5.1** specifically, because the spec's contested case was 5.1 and a PowerShell 7 result would not have answered it. Two further items resolved rather than corrected: §4.1's arm64 branch (shipped labelled untested) and §9.1's classes (shipped as named unknowns).

---

### Interlude — a core bug the Windows hand-runs exposed (2026-09-01)

Not part of Slices 4–6, and not caused by them. Found while preflighting Run 3, which is about
watching the tray icon change state - and the icon was pinned to `indexing`, permanently.

**What was wrong.** `hades status` on a freshly started core, against a project with a complete
42 MB graph:

```
icon:     indexing
headline: Indexing project_aurora…
indexState:   indexing        indexStatus: not yet indexed
nodeCount:    28838           edgeCount:   60962
```

Nothing was indexing. Measured: zero disk I/O, and the 13% CPU turned out to be **entirely** the
shell's own polling (1.91s per 15s with the shell up, 0.00s with it stopped). It had been in this
state for 33 minutes and would have stayed there forever.

**Why.** Three compounding decisions, none of them Windows-specific - `git blame` dates the code to
2026-08-05, so this shipped in 2.0.0 and the Mac menu bar showed the same false "Indexing…":

1. `ProjectIndexState` had two members, and `Indexing` was derived from `LastIndexedUtc is null`.
   The enum's own doc comment called this "an honest proxy… not a live progress signal nothing in
   ObservationService exposes yet" - a stand-in that outlived its premise.
2. `LastIndexedUtc` came from `_lastIndexed`, an **in-memory** dictionary. Every restart therefore
   answered "never indexed" for every project, which (1) then rendered as "indexing".
3. **The root cause:** `SyncChanges` returned early on `if (!sweep.AnythingChanged)` *before*
   recording anything. The five-minute periodic sweep verified the graph was current over and over
   and recorded none of it, so the state could never heal on its own.

Note that (2) and (3) hid each other. Fixing only the persistence would still have left a project
nobody edits without a timestamp; fixing only the early return would still have lost it on restart.

**What changed.** The two facts are now separate and come from separate sources:

- **"Has an index ever completed"** is `UnityProject.LastIndexedUtc`, persisted in `project.json`
  beside the graph it describes, and written on every completed index *including* a sweep that finds
  nothing - because a sweep finding nothing has positively verified the graph matches disk.
- **"Is one running right now"** is `OperationRegistry.IsRunningFor(productGuid)`, asked of work
  that actually exists. `OperationRecord` gained a `Subject`, and `index`/`rebuild` now set it.
- `ProjectIndexState` gained `NeverIndexed`. Both shells decode unknown enum values to their own
  `unknown` case, so an older build degrades rather than failing to parse.
- `_lastIndexed` kept its *other* job under a clearer name, `_indexedThisProcess`: it is
  `EnsureIndexed`'s per-process single-flight guard and must **not** survive a restart, or a fresh
  core would never re-sweep a project whose files changed while Hades was closed. Persisting the
  one field naively would have introduced that regression.

**A live operation wins over the stored timestamp**, which the old shape could not express at all:
a rebuild of an already-indexed project had a non-null timestamp and so showed no progress signal -
the exact moment a user most wants one.

**Four tests were pinning the bug** and asserted the wrong behaviour as correct
(`NeverIndexed_IndexStateIsIndexing_StatusIsExactLiteral`,
`NeverIndexed_RowStatusIsExactLiteral_AndIconStateIsIndexing`,
`IndexingOnOneProject_OutranksAttachedOnAnother`, and a `SingleConditionCases` row whose parameter
was literally named `neverIndexed` mapping to `ControlIconState.Indexing`). Same shape as the `lsof`
tests Task 20 found: a green suite actively defending a defect.

**Verified live**, not just in tests, on the installed MSI:

| | before | after |
|---|---|---|
| fresh launch | `indexing` / "Indexing project_aurora…" forever | `idle` / "No Unity Editor attached", row "indexed 6s ago" |
| after restart | same false indexing | timestamp read off disk, still honest |
| during a real rebuild | `idle` (a non-null timestamp meant "indexed") | `indexing`, row "indexing…", live "Assets: 275 of 615 files" |
| rebuild done (38s) | — | back to `idle`, "indexed 10s ago" |

The migration self-heals: a pre-upgrade `project.json` has no timestamp, and the first sweep after
launch wrote one within **6 seconds**.

Core suite **1,956 passed / 0 failed** (`--filter "Platform!=Unix"`); Windows **204 + 11**.

**Still unverified:** the Swift half. `ProjectIndexState.neverIndexed` was added to
`Mac/HadesControl/Sources/HadesControl/DTOs.swift`, but nothing on this machine can compile or run
Swift, so the Mac build and its DTO decoding tests have not been exercised. Nothing in the Mac app
switches on this enum (only `indexStatus`, a string, is rendered), so the change is additive - but
that is reasoning, not a test run.

**One unexplained failure**, recorded rather than explained away:
`MaterialApplyTests.PartialFailure_WirePerOperationFailure_MapsIndexOpAndError_StillOneWireCall`
failed once in a full-solution run and then passed in isolation and in three consecutive full runs
of `Hades.Server.Tests` (869/869 each). It is unrelated to anything here by inspection, and it is the
second sighting of the intermittent failure this plan already records.


---

## Verification checklist

> **Sign-off status.** Slice 6 is ticked below from verification performed directly against this
> machine, with the evidence in each task's Outcome section. **Slice 4 is now fully ticked too**
> (2026-09-02): its last two rows — all seven tray icon states seen live, and the tray menu read out
> of the accessibility tree — were closed by forcing `error` with a real vanished project folder and
> `unknown` with a stub core the shell adopted. **Slice 5 remains unticked**, and deliberately so:
> its CLI row says "on Windows **and** macOS", and the macOS half needs a Mac. Ticking a slice whose
> own hand-run has not happened would make this checklist decorative — which is the failure mode the
> plan spent Task 20 preventing elsewhere.
>
> **Updated 2026-09-01.** Done since this note was written: Task 5 Step 6 (Run 3, lease toast and the
> overflow-chevron case), Task 8 Step 8 (Run 5, Charon — four defects found and fixed), Task 9 Step 8
> (Run 4, Asphodel), Task 7 Step 8 and Task 12 Step 8. **Three remain, and every one of them is
> blocked on something this machine cannot provide:**
>
> - **Task 10 Step 10** — launch-at-login. The registry mechanism is already verified against the
>   real hive; what is left needs an actual reboot.
> - **Task 13 Step 6's macOS half** — the Windows half is done; the Mac half needs a Mac.
> - **Task 16 Step 5** — `install.sh`/`uninstall.sh`. **No longer the untested surface it was.**
>   Both were executed unmodified on 2026-09-02 under a macOS-command harness (see Task 16's second
>   Outcome), across 21 scenarios including the full install → uninstall round trip, and two real
>   defects were found and fixed. What remains needs a Mac: the real `hdiutil`, `ditto`, `codesign`,
>   Gatekeeper and `SMAppService` behaviours, none of which a stub can stand in for.
>
> Several individual checklist rows below are, however, verifiable on Windows today without a reboot
> or a Mac — the supervision ones especially (orphaned core, adopted-core survival). They are unticked
> because nobody has run them, not because they are blocked.

Slice 4:
- [x] `dotnet test --filter "Platform!=Unix"` green in `Windows/` — including the 3 Job Object tests, executing for the first time. **205 passed, 0 failed** (194 `Hades.Shell.Tests` + 11 `Hades.Supervision.Tests`). The three Job Object tests were then re-run *by name* rather than inferred from a green total, because a filter that silently excludes them looks identical to a pass: `Closing_the_job_terminates_a_healthy_member_process`, `Process_is_already_a_job_member_before_it_runs_its_first_instruction`, `Healthy_child_keeps_running_while_the_job_handle_stays_open` — all passed, the last taking 2s, which is it genuinely spawning FakeCore and waiting rather than stubbing
- [x] `TokenFileWriterTests.RestrictsToTheOwnerOnWindows` passes on Windows. Its two `Trait(Platform, Unix)` siblings fail if the filter is omitted — confirmed deliberately, so that "green" here means the trait gating works rather than that the Unix tests silently vanished
- [x] Tray icon shows all six states, legible at 16×16 — **all SEVEN seen live, 2026-09-02.**
  - `idle` (solid grey) and `notRunning` (hollow grey) are the pair that most needed proving, since
    both are the same hue and are told apart only by solid-versus-outline. Captured back to back in
    the real notification area, first on 2026-09-01 and again during this run.
  - `indexing` (blue), `attached` (green) and `leaseHeld` (amber) were seen during Runs 3 and 5.
  - **`error` (red) was forced for real, not simulated**: a throwaway Unity project was registered,
    then its folder was renamed away, which is the one `ControlIconState.Error` condition
    `SummaryEndpoint` detects on its own. The headline correctly took the MULTI-project branch —
    `1 of 2 projects needs attention` rather than naming the one project — because a second project
    was registered, which is the `AttentionSummary` path rather than the single-project sentence.
  - **The rename first FAILED with "Access is denied" while Hades was running**, and that is worth
    recording rather than working around silently: the core's `ProjectWatcher` holds a handle on the
    project directory, and Windows will not rename a directory that is open. Stopping Hades and
    retrying succeeded immediately, which is what identifies the watcher as the cause rather than a
    permission problem. A user who moves a project folder must quit Hades first — on Windows only;
    macOS allows the rename with the handle open.
  - **`unknown` (purple) needed a core this shell cannot understand**, since a matched core can never
    produce it (Task 20 keeps them in lockstep) — which is exactly the forward-compatibility case the
    state exists for. A stub HTTP server answered `/control/ping` so the supervisor would adopt it,
    then reported `"iconState": "quantumEntangled"`. The shell pinged, **adopted it, spawned no real
    core**, rendered the stub's headline and project row, and drew the `unknown` icon — so
    `UnknownFallbackConverter` was exercised end to end against a live shell rather than in a unit
    test. The stub's request log is what proves the shell called it, rather than only the probe.
  - A contact sheet of all seven `.ico` files rendered at their **true 16×16 frame** (requested
    explicitly with `new Icon(path, 16, 16)`, so it is the 16×16 image and not a downscaled 256)
    confirms every state is separable side by side: grey solid, grey outline, blue, green, amber,
    red, purple.
- [x] Tray menu carries lease + Release, supervision states, project rows, ownership footer — read
      out of the **accessibility tree** via UI Automation rather than from a screenshot, so the
      contents are the strings the app actually publishes, 2026-09-02.
  - **Project rows**: each project contributes two disabled items — its name, then its status line
    (`Project path not found — check that the volume is mounted.` / `No Editor attached · indexed
    51s ago`). The headline sits above them.
  - **Ownership footer, BOTH variants**: `Started by this app — quitting stops it` after the shell
    spawned its own core, and `Adopted — quitting Hades leaves it running` after `hades serve`
    started one first and the shell adopted it. Confirmed against process parentage each time — one
    core, parented to `hades.exe` rather than to the shell, is what makes it adoption rather than a
    second spawn.
  - **Supervision state**: killing the adopted core collapsed the menu to `Hades is not running` +
    `Open Hades` + `Quit Hades` — no project rows and no ownership footer, which is right, because
    there is no core to ask and nothing to own.
  - **Lease + Release** were verified during Run 3 (a real Unity Editor took and released a
    script-editing lease) and were NOT re-run here: no Editor was attached, so the menu correctly
    carried no lease line.
  - Two measurement notes, since both cost a wrong reading first. (1) The tray icon is exposed to
    UIA as a **Button named "Hades"** — but so is its **tooltip**, and matching on name alone
    selected the tooltip and sent a right-click into the window behind it. The reader now requires
    `ControlType.Button` AND a bounding rectangle inside the taskbar. (2) Re-pointing the discovery
    file at the stub was not enough on its own: the supervisor only probes for adoption at startup,
    so the menu kept saying `Hades is not running` while the summary poll was already succeeding
    against the stub. Restarting the shell is what made it adopt.
  - **Accessibility gap, not blocking**: the tray icon's accessible name is the bare string `Hades`
    in every state. A screen-reader user gets no indication of whether it is idle, indexing,
    attached, holding a lease, or in error — all seven states announce identically. The information
    exists (the menu's first item is the headline) but is only reachable by opening the menu.
- [x] Closing the window hides it; only Quit exits — verified live 2026-09-01: WM_CLOSE to the main window left the process running with no visible main window, and did not disturb the adopted core.
- [x] **End Task on the app leaves no orphaned core** — verified live 2026-09-01. Nothing running; started the shell, which SPAWNED a core (confirmed: the core's parent process was the shell); hard-terminated the shell so OnExit never ran; the core was gone. That is the Job Object backstop, not the graceful path.
- [x] Adopted core survives app quit — verified live 2026-09-01, in its strongest form. Started a core with `hades serve`, started the shell (it ADOPTED: one core, same pid, parent NOT the shell), then hard-terminated the shell. The core survived AND was still serving — an MCP `tools/list` against it returned 32 tools. Hard termination is a stronger test than a graceful quit here, because it is exactly the case the Job Object could wrongly capture; the graceful half is already covered by `CoreSupervisorTests.Stop_Does_Not_Kill_An_Adopted_Core`.

Slice 5:
- [x] Onboarding completes in four steps; no copy claims five — **walked end to end on the shipped
      MSI, 2026-09-02**, with every step's copy read out of the accessibility tree rather than off a
      screenshot. Two real defects found and fixed.
  - **Step 1 Install** — `Step 1 of 4`, and the only step with no action panel, which is exactly what
    `Assert.Equal([OnboardingStep.Install], withoutAnAction)` pins. The reworded copy holds: *"There
    are four steps, and you can stop after the third with a fully working setup - the last one is an
    upgrade."* The Mac's "five steps … stop after the fourth" is gone.
  - **Step 2 Claude Code** — the live check ran against the real core and answered *"Hades is running
    and serving 32 tools. Whether Claude Code has picked them up is something only Claude Code can
    tell you."* The honesty the plan insisted on survives in the shipped binary, not just in the test.
  - **Step 3 Projects** — added a throwaway project through the real folder picker; result *"Added and
    indexed."*, and the list then showed both projects with index status and node counts.
  - **Step 4 Unity Plugin** — `Step 4 of 4`, Next relabels to **Finish**, and the list carries one row
    per project each with its OWN button. Installing into the throwaway flipped **that row only** to
    "✓ Installed" (31 files landed on disk) and left `project_aurora`'s row a button, untouched —
    which is the question defect 2 of the 2026-08-30 walk was raised about.
  - **Finish wrote `onboarding.json`**, and a relaunch showed no onboarding window. **Skip writes it
    too** — checked separately, since the rule is "not written until finish *or* skip".

  **Defect 1 — "1 nodes".** The Projects step rendered `<Run Text="{Binding NodeCount}" /><Run
  Text="nodes" />`, so a freshly added single-script project displayed and announced *"1 nodes"*.
  Identical to the `1 calls` defect fixed on the Charon side, in the only other place the shell
  renders a count. Fixed with a `NodeCountConverter` carrying a static `Format`, matching
  `CallCountConverter`. Four cases pinned, and the guard was **watched failing** before being trusted:
  reintroducing the bug reddened exactly the `count: 1` case and nothing else, then the source was
  restored and compared. Note the singular was NOT re-observed on screen afterwards — by then the
  throwaway had the 31-file plugin in it and indexed to 48 nodes — so the boundary rests on the unit
  test, which is why that test exists.

  **Defect 2 — both onboarding lists announced garbage to a screen reader.** The Projects rows read
  out the whole record: `ProjectRow { Name = ..., Path = ..., ProductGuid = ..., NodeCount = ...,
  Warnings = System.Collections.Generic.List`1[...] }`. The plugin rows were worse — `PluginRow` is a
  class, not a record, so they announced the bare type name `Hades.Shell.Onboarding.PluginRow`, not
  even naming the project. An `ItemsControl`'s item peer falls back to the bound object's
  `ToString()`, and neither list set `AutomationProperties.Name`.

  This is **the same defect already fixed in `MemoryView` and `TracesView` earlier in this port** —
  both of those files carry a comment describing this exact record dump. The onboarding window was
  simply missed. Fixed the same way, with `ItemContainerStyle` on `ContentPresenter`; the rows now
  announce `hades-onboard-probe, indexed 0s ago, 48 nodes` and the project's name respectively,
  verified by re-reading the automation tree against a rebuilt shell.

  **Not a defect, checked before reporting it as one:** the Projects step's list is empty when the
  step opens even with projects already registered. `AddedProjectsList.ItemsSource` is assigned only
  inside the add handler — it lists what THIS run added, by design. The pre-fetched list defect 3
  of the 2026-08-30 walk fixed is the *plugin* step's, which does populate on entry, and did.

  **An unexplained core state seen once, and deliberately not claimed as a shipped bug.** While
  verifying the fixes with a locally built Debug shell against the data root the MSI had been using,
  `project_aurora` reported `indexState: indexing` with `indexStatus: "not yet indexed"` over a
  28,838-node graph, held that for minutes with the core near-idle and `indexOperationId` empty, and
  the ordering was inverted from what the fields imply: `project.json` *did* carry
  `LastIndexedUtc: 07:26:21`, while the freshly indexed throwaway had no such field yet reported
  "indexed 3m ago". A clean restart on the shipped MSI cleared it completely — both projects then
  read `indexed 18s ago`. That is the same family as the Interlude's stuck-index bug, but the run
  that produced it also mixed a Debug core with an MSI-written data root and removed a project
  mid-flight, so **whether this is reachable in a shipped build is unknown**; it is recorded here and
  as a task rather than asserted as a regression.
- [ ] Every CLI command works on Windows **and** macOS
  - **The Windows half is done (2026-09-01); the row stays unticked because it says "and macOS".**
    All twelve commands exercised against the installed MSI, with the mutating ones pointed at
    throwaway projects rather than the real one: `serve` (via the supervision checks above),
    `diagnose`, `status`, `projects`, `add-project`, `remove-project`, `rebuild`, `operation`,
    `install-plugin`, `traces`, `memory`, `release`, plus the no-arg usage banner. Error paths were
    run too, not just happy ones: `release` and `remove-project` on unknown ids both print the
    core's own message and exit 1.
  - `remove-project` was checked against its own promise rather than its return value: after
    removal the project folder, all 33 of its files, and its `graph.db` in app storage were all
    still present. It deregisters and destroys nothing.
  - **One defect found and fixed by this sweep** — see Task 8's Interlude for the index-state work
    it belongs to. `hades add-project` on a SMALL project answered `not yet indexed`, while the same
    command on a 2,500-file project answered `indexing…`. The cause was ordering inside
    `BuildSnapshotAsync`: it sampled the last-indexed timestamp BEFORE asking whether an index was
    running, so an index that finished between the two reads produced a null timestamp AND a false
    running flag — resolving to NeverIndexed for a project that had just finished indexing. The two
    samples are now taken in the other order, which makes the same race benign (not running, but a
    fresh timestamp, reads as Indexed). Re-tested afterwards: the small project reports `indexing…`.
    `ProjectsResolveTests.FinishedBetweenSamples_ReadsAsIndexed_NotNeverIndexed` pins it.
- [x] `hades serve` core is adopted by the shell — verified live 2026-09-01: exactly one Hades.Server after the shell started, carrying the pid `hades serve` created, with a parent that was not the shell. A spawn would have produced a second core.
- [x] `hades diagnose` is useful with no core running — verified live 2026-09-01. Exit 0 (not running is a state, not an error), 20 lines: OS build, runtime, both architectures, edition with the note that the registry ProductName says "Windows 10" on Windows 11, release, long-path status, the storage root, and whether control.token is present AND parses. It then names why the core is unreachable, port included: `core: not running (No connection could be made... 127.0.0.1:56300)`. The 64-character bearer token does NOT appear anywhere in the output — checked against the real token, not just trusted to `Diagnose_NeverPrintsTheBearerToken`.
- [x] Unity Editor attaches from a real Windows project — done during Run 3 (2026-09-01): Unity 6000.3.2f1, pid 9176, attached to `D:\Fork\project_aurora`, reported `attached=true busy=false` through `hades_charon_status`, then took and released a real script-editing lease. The plugin found and read `editor.token` under Unity's own Mono to do it, which is also what closes the token-path row above.
- [x] Plugin and core agree on the token path — **measured, not assumed**, 2026-09-01. `HadesConnectionFile.DefaultPath` computes `LocalApplicationDataHadesditor.token`; resolved through the same .NET API it calls, that is `C:SERSMIKEKAPPDATAocalhadesditor.token`, which is byte-identical to the path the running core had actually written 89 bytes to. the file carries exactly the `port` + `token` shape the plugin expects, and both sides honour `hades_home` symmetrically. the end-to-end proof is stronger still: in run 3 a real unity editor attached and held a lease, which is only possible if the plugin found and read this file under unity's mono rather than the app's .net.

Slice 6:
- [x] Per-user MSI installs with no UAC prompt — unelevated `/qn` returned 0; `AssignmentType = 0`
- [x] `hades` on PATH in a new terminal — resolved and ran through a PATH rebuilt from the registry, exactly one entry, placed last. *A new terminal was not opened by hand; the session was reconstructed from `HKCU\Environment` + machine PATH, which is what a new session reads.*
- [x] Upgrade replaces in place; uninstall leaves the data root alone — 2.1.1 → one product registered, `RemoveExistingProducts` ran; **7 files in `%LOCALAPPDATA%\Hades` before uninstall, 7 after**
  - **Re-verified end to end on 2026-09-01, on the shipped MSI, with byte-level proof.** The earlier
    check counted files before and after; this one hashed them. Uninstall (`msiexec /x /qn`, exit 0)
    removed the program directory, the Start menu shortcut AND its folder, the PATH entry, and the
    ARP registration — and left the data root untouched: all six files present at identical sizes,
    with `graph.db` (42,291,200 B) and `traces.db` **SHA256-identical** to before. The verbose
    uninstall log never references the data root at all.
  - Reinstalling restored 862 files, the shortcut, the PATH entry and exactly ONE ARP registration.
    The app then started and found its preserved state: `project_aurora`, `indexState: indexed`,
    **28,838 nodes / 60,962 edges** — and `graph.db` was still byte-identical even after the core
    reopened it. A user who uninstalls and reinstalls does not re-index.
- [x] MotW behaviour measured for all three download paths — `curl.exe`, `Invoke-WebRequest` (under Windows PowerShell 5.1, the contested case), `System.Net.WebClient`: none wrote a `Zone.Identifier`, against a control that did. **A browser download was not measured** and is the one path still assumed
- [x] Lockstep script proven to fail on a mismatch — three ways, including a *renamed* constant reporting `NOT FOUND` rather than `ok`
- [ ] Both MSIs built and attached by CI — **built, not yet attached by CI.** Both build locally and the `windows` job is written, but its dry run has to execute on GitHub (Task 21 Step 4)
- [x] arm64 either executed on real hardware, or shipped explicitly labelled untested — **the second branch.** Labelled untested in `README.md`, `LIMITATIONS.md`, `Documentation/Installing.md` and `ReleasePipeline.md` §9.1
  - **Rebuilt 2026-09-01** so the artifact matches the current source (it had been built 08-31, before
    the index-state, Charon, accessibility and settings-binding work). 97.3 MB, 861 files. Verified
    genuinely native rather than silently x64 by reading the PE machine field directly:
    `Hades.Shell.exe`, `hades.exe` and `core\Hades.Server.exe` all report **ARM64**, as does the
    native `e_sqlite3.dll` — against the x64 build's `x64` for the same file. The MSI's own summary
    template is `Arm64;0` (x64's is `x64;0`), which is what refuses a wrong-architecture install.
    Still never EXECUTED: no arm64 hardware here.
