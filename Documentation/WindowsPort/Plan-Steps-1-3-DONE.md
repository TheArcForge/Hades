# Windows Support, Steps 1–3 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Hades .NET core run correctly on Windows, extract a shared control-API client that both the CLI and the future WPF shell consume, and build the Job-Object supervisor that guarantees a spawned core never outlives its parent.

**Architecture:** Three sequential slices of `docs/superpowers/specs/2026-08-23-hades-windows-shell-design.md` (Spec #5). Slice 1 fixes the core's Windows-hostile assumptions (a POSIX-only test, a roaming storage root, unprotected token files, two macOS-only shell-outs). Slice 2 extracts `Hades.Control.Client` — the .NET twin of Swift's `HadesControl` — and pins it against the server with a reflection conformance test plus server-generated golden JSON fixtures shared with the Swift client. Slice 3 builds `Hades.Supervision`, replacing the macOS `HadesCoreReaper` process with a Windows Job Object.

**Tech Stack:** .NET 10 (`net10.0`, plus `net10.0-windows` for supervision), xUnit, central package management via `Core/Directory.Packages.props`, GitHub Actions (`macos-latest` + `windows-latest`).

**Scope note:** This plan covers Spec #5 steps 1–3 only. Steps 4–6 (the WPF shell, onboarding + Unity plugin branch, MSI + release CI) require the Windows machine for their hand-run gates and get their own plan. Slices 1–3 are developable on macOS and verifiable in CI.

---

## Ground truth verified before writing this plan

These were re-counted against the tree on 2026-08-24. **Do not trust earlier numbers** — Spec #5 revision 2 stated two of them wrongly and was corrected.

| Fact | Value |
|---|---|
| Files referencing `UnixFileMode` in tests | 11 |
| …of which already carry `OperatingSystem.IsWindows()` guards | **10** |
| …genuinely needing work | **1** — `Hades.Core.Tests/Observation/IncrementalIndexTests.cs` |
| Test files that early-return on a missing hardcoded `/Users/mike/…` path | **15** — of which **1** (`PluginInstallerTests`) needs only THIS repo and is fixed to run everywhere (Task 3); the other **14 files / 53 tests** genuinely need the developer's Unity projects and keep their early returns |
| Files using such a path only as a string literal (need no change) | 5 — `MiniJsonTests`, `V12CleanupTests`, `V12DetectorTests`, `Control/MigrationTests`, `Control/ProjectsTests` |
| Public records under `Control/` with unattributed properties (all non-wire) | 6 |
| Existing Swift fixtures / decode tests / DTO types | 50 / 44 / 25 |

**The 15 skippable files:**
`Hades.Core.Tests/Editors/PluginInstallerTests.cs`, `Indexing/RealProjectAuroraIndexSmokeTest.cs`, `Indexing/RealProjectBareSequenceReferenceTests.cs`, `Indexing/RealProjectBinaryAssetIndexSmokeTest.cs`, `Indexing/RealProjectIndexSmokeTest.cs`, `Memory/RealProjectMemoryImportSmokeTest.cs`, `Unity/MetaFileReaderTests.cs`, `Unity/RealCorpusReaderTests.cs`, and `Hades.Server.Tests/RealProject{DanglingDependency,Inspection,MemoryTools,ReferenceEvent,Settings,SummaryTool,TypedAsset}SmokeTest.cs`.

---

## File structure

**Slice 1 — Core on Windows** (modify only; no new projects)

| File | Responsibility |
|---|---|
| `Core/src/Hades.Core/Storage/AppPaths.cs` | Storage root; gains a Windows branch to `LocalApplicationData` |
| `Core/src/Hades.Core/Editors/EditorListener.cs` | `editor.token`; Windows branch gains an atomic restricted DACL |
| `Core/src/Hades.Server/Control/ControlAuth.cs` | `control.token`; same |
| `Core/src/Hades.Core/Storage/TokenFileWriter.cs` | **NEW** — the one place a 0600-or-DACL token file is created, shared by the two above |
| `Core/src/Hades.Server/Control/ProjectsEndpoint.cs` | `RevealInFinder` + `UnityHubEditorExecutablePath` Windows branches |
| `Core/tests/Hades.Core.Tests/Observation/IncrementalIndexTests.cs` | Add the missing Windows guard |
| `Core/tests/Hades.Core.Tests/PlatformTraits.cs` | **NEW** — the trait-based platform-gating idiom (xUnit 2.9.3 has no dynamic skip) |
| `Core/tests/Hades.Core.Tests/CorpusAvailabilityTests.cs` | **NEW** — meta-test keeping the 53 corpus-gated tests honest |
| `Core/tests/Hades.Core.Tests/Editors/PluginInstallerTests.cs` | Repo root derived, not hardcoded — so it runs on CI too |
| `.github/workflows/ci.yml` | Add the `windows-latest` job |

**Slice 2 — the shared client**

| File | Responsibility |
|---|---|
| `Core/src/Hades.Control.Client/Discovery.cs` | Reads `control.token`; the .NET twin of Swift `Discovery.swift` |
| `Core/src/Hades.Control.Client/ControlConnection.cs` | Port + token record |
| `Core/src/Hades.Control.Client/ControlClientError.cs` | Error taxonomy incl. `StaleToken` |
| `Core/src/Hades.Control.Client/ControlClient.cs` | Typed HTTP calls |
| `Core/src/Hades.Control.Client/UnknownFallbackConverter.cs` | Generic enum converter — forward compatibility by construction |
| `Core/src/Hades.Control.Client/Dtos/*.cs` | The duplicated wire records, one file per endpoint group |
| `Core/tests/Hades.Control.Client.Tests/ConformanceTests.cs` | Reflection walk of the server's wire types |
| `Core/tests/Hades.Control.Client.Tests/ClientCoverage.cs` | The named exclusion artifact (migration types) |
| `Core/tests/Hades.Control.Client.Tests/FixtureGenerationTests.cs` | Emits the golden corpus |
| `Core/tests/Fixtures/control-api/*.json` | Generated golden fixtures, consumed by .NET **and** Swift |
| `Core/src/Hades.Cli/*` | Moves onto the client; loses its `Hades.Core` reference |

**Slice 3 — supervision**

| File | Responsibility |
|---|---|
| `Windows/Directory.Build.targets` | The boundary guard for the Windows tree |
| `Windows/Hades.Supervision/JobObject.cs` | `CreateJobObject` + kill-on-close, `SafeHandle`-owned |
| `Windows/Hades.Supervision/ProcessLauncher.cs` | `CreateProcess` P/Invoke with `CREATE_SUSPENDED` + scoped handle inheritance |
| `Windows/Hades.Supervision/CoreSupervisor.cs` | Adopt-or-spawn, backoff, ownership — the port of the Swift actor |
| `Windows/Hades.Supervision.Tests/*` | Logic tests (macOS-runnable) + mechanism tests (Windows-only) |
| `Windows/FakeCore/Program.cs` | Test fixture answering `/control/ping` |

---

# SLICE 1 — Core green on Windows

### Task 1: Establish the platform-gating idiom

**Context — verified 2026-08-25, do not re-derive:** xUnit 2.9.3 has **no working dynamic skip**. `Assert.Skip` does not exist (`error CS0117`), `Xunit.SkipException` is not public (`error CS0234`), and throwing the raw `$XunitDynamicSkip$` token is reported as **FAIL**, not skipped. Dynamic skip requires either the third-party `Xunit.SkippableFact` package or xUnit v3. **Neither is being adopted.**

Instead, tests that can only run on one OS are marked with a **trait** and filtered out by the CI job for the other OS. A filtered test is not reported at all — which is strictly more honest than a skip, and needs no dependency.

**Files:**
- Create: `Core/tests/Hades.Core.Tests/PlatformTraits.cs`

- [ ] **Step 1: Document the idiom in one place**

```csharp
namespace Hades.Core.Tests;

/// <summary>
/// Platform gating for tests that can only run on one OS.
///
/// xUnit 2.9.3 has no dynamic skip (verified 2026-08-25: Assert.Skip does not exist,
/// Xunit.SkipException is not public, and the raw DynamicSkipToken is reported as a FAILURE by
/// the v2 runner). Rather than add Xunit.SkippableFact or migrate to xUnit v3, platform-specific
/// tests carry a trait and each CI job filters out the traits it cannot run:
///
///   macOS:   dotnet test --filter "Platform!=Windows"
///   Windows: dotnet test --filter "Platform!=Unix"
///
/// A filtered test is not reported at all - better than the early-return convention used
/// elsewhere in this suite, which xUnit reports as PASSED.
///
/// Usage:  [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
/// </summary>
public static class PlatformTraits
{
    public const string Key = "Platform";
    public const string Windows = "Windows";
    public const string Unix = "Unix";
}
```

- [ ] **Step 2: Build**

Run: `cd Core && dotnet build tests/Hades.Core.Tests`
Expected: Build succeeded.

- [ ] **Step 3: Filter Windows-only traits out of the existing macOS CI job**

Without this, a Windows-only test would RUN on the macOS runner and fail. In `.github/workflows/ci.yml`, change the existing `dotnet-tests` job's final step to:

```yaml
      - name: dotnet test
        working-directory: Core
        # Platform!=Windows excludes tests that can only run on Windows (the token DACL, the Job
        # Object supervisor). A filtered test is not reported at all - see PlatformTraits for why
        # traits rather than skips (xUnit 2.9.3 has no dynamic skip).
        run: dotnet test --filter "Platform!=Windows"
```

- [ ] **Step 4: Verify the filter locally**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "Platform!=Windows"`
Expected: PASS. Today no test carries the trait yet, so this must run the full suite unchanged — proving the filter is a no-op until traits exist, not a silent test-eater.

---

### Task 2: Guard the one POSIX-only test

`IncrementalIndexTests` suppresses CA1416 with a `#pragma` instead of guarding, so on Windows `File.SetUnixFileMode` throws `PlatformNotSupportedException` and the test **fails outright**. Every other `UnixFileMode` test in the suite already carries an `OperatingSystem.IsWindows()` early-return guard; this is the only one that does not.

**Files:**
- Modify: `Core/tests/Hades.Core.Tests/Observation/IncrementalIndexTests.cs`

- [ ] **Step 1: Find the affected test**

```bash
grep -n -B30 "File.SetUnixFileMode(lockedDir, UnixFileMode.None)" \
  Core/tests/Hades.Core.Tests/Observation/IncrementalIndexTests.cs
```

Note the `[Fact]` method containing that call. There is exactly one.

- [ ] **Step 2: Mark it Unix-only with the Task 1 trait**

Change its attribute line from `[Fact]` to:

```csharp
    // File.SetUnixFileMode is a POSIX chmod equivalent and throws PlatformNotSupportedException on
    // Windows. This test exists specifically to simulate a Unix permissions failure, so it is
    // gated by trait rather than early-returned - see PlatformTraits for why traits, not skips.
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
```

Add `using Xunit;` only if the file lacks it (the project has an implicit `Using Include="Xunit"`, so it almost certainly does not need one).

- [ ] **Step 3: Verify it still runs on macOS**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~IncrementalIndex"`
Expected: PASS — macOS runs everything; no filter is applied locally.

- [ ] **Step 4: Verify the Windows filter would exclude it**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~IncrementalIndex&Platform!=Unix"`
Expected: the `UnreadableDirectory`-style test is **not** in the run. This simulates exactly what the Windows job will do.

---

### Task 3: Make the plugin-source test run everywhere, and stop the corpus tests from lying

Two separate problems, deliberately handled differently.

**Problem A — one test is gated for no good reason.** `PluginInstallerTests.Install_MatchesTheRealPluginSourceTreeExactly` requires `/Users/mike/Projects/Hades` — *this repository*. CI has the repository; it is simply checked out somewhere else (`D:\a\Hades\Hades` on a Windows runner). Derived from the test assembly's own location it runs everywhere, including Windows CI. It is worth fixing on its own merits: it is the only guard proving the Unity plugin sources embedded in `Hades.Core.dll` still match the files on disk — the mechanism that lets a notarized `.app` install a working plugin into a stranger's project.

**Problem B — 53 tests genuinely need the developer's own Unity projects** (`Hades-Unity-Client`, `project_aurora`). Those corpora will never exist on a runner, and `project_aurora` is a private production project that should not be. They stay early-returns; a single meta-test stops the pass count from silently overstating coverage.

**Files:**
- Modify: `Core/tests/Hades.Core.Tests/Editors/PluginInstallerTests.cs`
- Create: `Core/tests/Hades.Core.Tests/CorpusAvailabilityTests.cs`

- [ ] **Step 1: Locate the repository root from the test assembly**

In `PluginInstallerTests.cs`, replace the two hardcoded constants (around line 160) and the guard on line 162.

Remove:

```csharp
        const string PluginSourceDir = "/Users/mike/Projects/Hades/UnityPlugin/Assets/Hades";
        const string ContractSourceDir = "/Users/mike/Projects/Hades/Core/src/Hades.Contract/Wire";
        if (!Directory.Exists(PluginSourceDir) || !Directory.Exists(ContractSourceDir)) return;
```

Add, as a member of the test class:

```csharp
    /// <summary>
    /// The repository root, walked up from the test assembly's own location rather than hardcoded
    /// to one developer's checkout path. This test needs THIS repository - which CI always has,
    /// just at its own path (D:\a\Hades\Hades on a Windows runner) - so hardcoding made it skip
    /// everywhere except one machine, for no reason. Anchored on a file that only the repo root
    /// has, so it cannot silently latch onto some other directory.
    /// </summary>
    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hades.sln"))
                                     && !Directory.Exists(Path.Combine(directory.FullName, "UnityPlugin")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
```

and in the test body:

```csharp
        var repositoryRoot = RepositoryRoot();
        var pluginSourceDir = Path.Combine(repositoryRoot, "UnityPlugin", "Assets", "Hades");
        var contractSourceDir = Path.Combine(repositoryRoot, "Core", "src", "Hades.Contract", "Wire");
```

Update the rest of the method to use the two locals instead of the deleted constants. **No guard and no early return** — this test must now fail loudly if the sources are missing, because their absence is a real problem, not a machine difference.

- [ ] **Step 2: Run it**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~PluginInstaller"`
Expected: PASS, 8 tests, **none skipped or hollow**. If `RepositoryRoot()` throws, the walk-up anchor is wrong — print `AppContext.BaseDirectory` and fix the anchor rather than reinstating a hardcoded path.

- [ ] **Step 3: Write the corpus-availability meta-test**

Create `Core/tests/Hades.Core.Tests/CorpusAvailabilityTests.cs`:

```csharp
namespace Hades.Core.Tests;

/// <summary>
/// 53 tests across 14 files run the real indexer against real Unity projects on the developer's
/// own machine (Hades-Unity-Client, project_aurora). They have caught real defects that synthetic
/// fixtures did not - see RealCorpusReaderTests' own note that "Plan 1's two worst defects were
/// both found this way". They cannot run in CI: those corpora are not on a runner, and
/// project_aurora is a private production project that should not be.
///
/// Each guards itself with `if (!Directory.Exists(...)) return;`, which xUnit reports as PASSED -
/// so on a machine without the corpora, the pass count silently overstates what was exercised.
/// xUnit 2.9.3 offers no dynamic skip to express this properly (see PlatformTraits), and neither
/// Xunit.SkippableFact nor an xUnit v3 migration is being adopted for it.
///
/// This test is the honest alternative: a machine that is SUPPOSED to have the corpora says so by
/// setting HADES_REQUIRE_CORPUS=1, and finds out here - once, loudly - if they are missing. CI
/// never sets it, so CI simply does not pretend to cover them.
/// </summary>
public class CorpusAvailabilityTests
{
    static readonly string[] Corpora =
    [
        "/Users/mike/Projects/Hades-Unity-Client",
        "/Users/mike/Projects/project_aurora",
    ];

    [Fact]
    public void RequiredCorporaArePresentWhenTheMachineClaimsThem()
    {
        if (Environment.GetEnvironmentVariable("HADES_REQUIRE_CORPUS") != "1") return;

        var missing = Corpora.Where(c => !Directory.Exists(c)).ToList();

        Assert.True(missing.Count == 0,
            "HADES_REQUIRE_CORPUS=1 but these corpora are absent, so the ~53 real-project tests " +
            "silently no-opped and this run's pass count overstates its coverage: " +
            string.Join(", ", missing));
    }
}
```

- [ ] **Step 4: Verify both states**

```bash
cd Core
dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~CorpusAvailability"
HADES_REQUIRE_CORPUS=1 dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~CorpusAvailability"
```

Expected: PASS both times on the developer's Mac (the corpora exist). To prove the failure path, temporarily add a nonexistent path to `Corpora`, re-run with the variable set, confirm it FAILS with the list, and revert.

- [ ] **Step 5: Leave the other 53 early-returns exactly as they are**

Do **not** modify the 14 corpus-gated files. Their early-return guards are now a documented, deliberate convention rather than an oversight, and `CorpusAvailabilityTests` is what keeps them honest.

---

### Task 4: Move the Windows storage root off the roaming profile

`SpecialFolder.ApplicationData` is **Roaming** `%APPDATA%` on Windows. Leaving it there would sync a multi-hundred-megabyte graph between machines, and changing it after ship is a data-relocation break (Spec #5 §6.4).

**Files:**
- Modify: `Core/src/Hades.Core/Storage/AppPaths.cs`
- Test: `Core/tests/Hades.Core.Tests/Storage/AppPathsTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `Core/tests/Hades.Core.Tests/Storage/AppPathsTests.cs` (create the file if absent, with `namespace Hades.Core.Tests.Storage;` and `using Hades.Core.Storage;`):

```csharp
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void DefaultRootIsMachineLocalOnWindows()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hades");

        Assert.Equal(expected, new AppPaths().Root);
    }

    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
    public void DefaultRootIsApplicationSupportOnMac()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hades");

        Assert.Equal(expected, new AppPaths().Root);
    }
```

- [ ] **Step 2: Run to verify the Mac test passes and the Windows one skips**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~AppPathsTests"`
Expected: the Mac assertion PASSES, the Windows assertion SKIPS. The real proof arrives in Task 9's CI run.

- [ ] **Step 3: Implement the branch**

In `Core/src/Hades.Core/Storage/AppPaths.cs`, replace `DefaultRoot()`:

```csharp
    /// <summary>
    /// macOS/Unix: <c>~/Library/Application Support/Hades</c> via
    /// <see cref="Environment.SpecialFolder.ApplicationData"/>.
    ///
    /// Windows: <c>%LOCALAPPDATA%\Hades</c> — deliberately NOT
    /// <see cref="Environment.SpecialFolder.ApplicationData"/>, which resolves to the ROAMING
    /// profile there. Everything under this root is either derived and rebuildable (graph.db,
    /// traces.db, memory-index.db) or machine-local by nature (control.token, editor.token, whose
    /// ports are meaningless on another machine), so none of it should follow a user between
    /// machines — and a roaming profile silently syncing a multi-hundred-megabyte graph is a
    /// support incident waiting to happen. The one authored, irreplaceable thing Hades owns
    /// (memory/*.md) lives in the user's own repository under .arcforge/, not here.
    /// See Spec #5 §6.4.
    /// </summary>
    static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.ApplicationData),
        "Hades");
```

- [ ] **Step 4: Run the full Core test suite**

Run: `cd Core && dotnet test tests/Hades.Core.Tests`
Expected: PASS, no regressions. Every other test overrides the root via `HADES_HOME` or an explicit constructor argument, so none depends on the default.

- [ ] **Step 5: Commit**

```bash
git add Core/src/Hades.Core/Storage/AppPaths.cs Core/tests/Hades.Core.Tests/Storage/AppPathsTests.cs
git commit -m "fix: use LocalApplicationData for the Windows storage root"
```

---

### Task 5: Extract token-file writing into one place

`ControlAuth.WriteConnectionFile` and `EditorListener.WriteConnectionFile` are two copies of the same careful logic — create at 0600 **in the same syscall as the inode**, then defensively re-chmod. Task 6 adds a Windows DACL branch; adding it twice would guarantee drift. Extract first, then extend once.

**Files:**
- Create: `Core/src/Hades.Core/Storage/TokenFileWriter.cs`
- Create: `Core/tests/Hades.Core.Tests/Storage/TokenFileWriterTests.cs`
- Modify: `Core/src/Hades.Server/Control/ControlAuth.cs`
- Modify: `Core/src/Hades.Core/Editors/EditorListener.cs`

- [ ] **Step 1: Write the failing test**

Create `Core/tests/Hades.Core.Tests/Storage/TokenFileWriterTests.cs`:

```csharp
using Hades.Core.Storage;

namespace Hades.Core.Tests.Storage;

public class TokenFileWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WritesTheContentAndCreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "control.token");

        TokenFileWriter.Write(path, """{"port":1,"token":"t"}""");

        Assert.Equal("""{"port":1,"token":"t"}""", File.ReadAllText(path));
    }

    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
    public void RestrictsToTheOwnerOnUnix()
    {
        var path = Path.Combine(_dir, "control.token");
        TokenFileWriter.Write(path, "x");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
    public void NarrowsAPreExistingWiderFile()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "control.token");
        File.WriteAllText(path, "stale");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        TokenFileWriter.Write(path, "fresh");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~TokenFileWriter"`
Expected: FAIL — `TokenFileWriter` does not exist.

- [ ] **Step 3: Implement, moving the existing logic verbatim**

Create `Core/src/Hades.Core/Storage/TokenFileWriter.cs`:

```csharp
namespace Hades.Core.Storage;

/// <summary>
/// Writes a discovery/token file so that only its owner can read it, from the moment it exists.
///
/// One implementation, two callers (<c>Server.Control.ControlAuth</c> and
/// <see cref="Editors.EditorListener"/>): both write a bearer token that authorises project
/// mutations, both had their own copy of this logic, and a Windows branch added twice would drift.
/// </summary>
public static class TokenFileWriter
{
    public static void Write(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            // Replaced with a restricted DACL in Task 6. Until then this preserves the exact
            // behaviour both call sites had.
            File.WriteAllText(path, contents);
            return;
        }

        WriteUnix(path, contents);
    }

    // Create the inode at 0600 in the SAME syscall that creates it (FileStreamOptions.
    // UnixCreateMode), so the token is never briefly sitting in a file at the wider,
    // umask-determined default mode a plain WriteAllText-then-chmod would leave it at for the
    // instant in between. UnixCreateMode only takes effect when this call actually creates a NEW
    // inode, so SetUnixFileMode still runs unconditionally afterward as a defensive fallback for a
    // pre-existing file at this path that FileMode.Create reuses/truncates instead of replacing.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static void WriteUnix(string path, string contents)
    {
        using (var stream = File.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~TokenFileWriter"`
Expected: PASS (3 tests on macOS).

- [ ] **Step 5: Repoint both callers**

In `Core/src/Hades.Server/Control/ControlAuth.cs`, replace the body of `WriteConnectionFile` after the JSON is serialized with:

```csharp
        TokenFileWriter.Write(path, json);
```

Delete the now-dead directory-creation, `OperatingSystem.IsWindows()` branch, `File.Open`/`FileStreamOptions` block, and trailing `SetUnixFileMode`. Add `using Hades.Core.Storage;` if absent.

In `Core/src/Hades.Core/Editors/EditorListener.cs`, replace the body of `WriteConnectionFile` after `json` is computed with:

```csharp
        TokenFileWriter.Write(_tokenFilePath, json);
```

Delete the same now-dead block there.

- [ ] **Step 6: Run the suites that cover both callers**

Run: `cd Core && dotnet test`
Expected: PASS. `ControlAuthTests` and `EditorListenerTests` both assert the 0600 end state and the reused-inode case; they must still pass unchanged, which is the proof the extraction was behaviour-preserving.

- [ ] **Step 7: Commit**

```bash
git add Core/src/Hades.Core/Storage/TokenFileWriter.cs \
        Core/tests/Hades.Core.Tests/Storage/TokenFileWriterTests.cs \
        Core/src/Hades.Server/Control/ControlAuth.cs \
        Core/src/Hades.Core/Editors/EditorListener.cs
git commit -m "refactor: extract token-file writing to one implementation"
```

---

### Task 6: Protect token files on Windows with an atomic restricted DACL

Today the Windows branch is bare `File.WriteAllText` — no protection at all. A create-then-`SetAccessControl` port would reintroduce exactly the window the Unix side pays to avoid (Spec #5 §6.2).

**Files:**
- Modify: `Core/src/Hades.Core/Storage/TokenFileWriter.cs`
- Modify: `Core/tests/Hades.Core.Tests/Storage/TokenFileWriterTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `TokenFileWriterTests`:

```csharp
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void RestrictsToTheOwnerOnWindows()
    {
        var path = Path.Combine(_dir, "control.token");
        TokenFileWriter.Write(path, "x");

        var security = new FileInfo(path).GetAccessControl();

        // Protection severed inheritance: the parent's ACEs must NOT have come along, or the
        // explicit rule below adds nothing at all.
        Assert.True(security.AreAccessRulesProtected);

        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true,
                            typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToList();

        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        Assert.All(rules, rule => Assert.Equal(me, rule.IdentityReference));
        Assert.Contains(rules, rule =>
            rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.Read));
    }
```

- [ ] **Step 2: Run on macOS to verify it skips**

Run: `cd Core && dotnet test tests/Hades.Core.Tests --filter "FullyQualifiedName~TokenFileWriter"`
Expected: 3 pass, 1 skip. **The real verification is Task 9's Windows CI run** — this cannot be proven on macOS, and that limitation is the point of the Windows job.

- [ ] **Step 3: Implement the Windows branch**

In `TokenFileWriter.cs`, replace the `if (OperatingSystem.IsWindows())` block with `WriteWindows(path, contents); return;` and add:

```csharp
    /// <summary>
    /// The Windows equivalent of <see cref="WriteUnix"/>'s atomic 0600: create the file with its
    /// final DACL already applied, in one call, rather than creating it under the directory's
    /// inherited ACL and narrowing it afterward — which would reintroduce precisely the window
    /// UnixCreateMode exists to close.
    ///
    /// SetAccessRuleProtection(true, false) is the load-bearing call, not boilerplate: without it
    /// the parent directory's inherited ACEs come along and the explicit rule below adds nothing.
    ///
    /// Stated honestly: as root can on Unix, Administrators can always read this file, and under
    /// default Windows ACLs other standard users already cannot reach files inside the profile.
    /// This hardens non-default setups; it is not what creates the boundary.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void WriteWindows(string path, string contents)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.Read | FileSystemRights.Write,
            AccessControlType.Allow));

        using var stream = new FileInfo(path).Create(
            FileMode.Create, FileSystemRights.Write, FileShare.None,
            bufferSize: 4096, FileOptions.None, security);

        var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
    }
```

Add to the top of the file:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;
```

- [ ] **Step 4: Verify it compiles under warnings-as-errors**

Run: `cd Core && dotnet build`
Expected: Build succeeded, 0 warnings. If CA1416 fires, the `[SupportedOSPlatform("windows")]` attribute is missing or misplaced. **No package reference is needed** — `FileSystemAclExtensions` is in-box on `net10.0`; this was compile-verified during spec review.

- [ ] **Step 5: Run the full suite**

Run: `cd Core && dotnet test`
Expected: PASS on macOS with the Windows test skipped.

- [ ] **Step 6: Commit**

```bash
git add Core/src/Hades.Core/Storage/TokenFileWriter.cs \
        Core/tests/Hades.Core.Tests/Storage/TokenFileWriterTests.cs
git commit -m "feat: restrict token files to the owner on Windows via an atomic DACL"
```

---

### Task 7: Branch `RevealInFinder` for Windows

**Files:**
- Modify: `Core/src/Hades.Server/Control/ProjectsEndpoint.cs:658`
- Test: `Core/tests/Hades.Server.Tests/Control/ProjectsTests.cs`

- [ ] **Step 1: Modify the EXISTING test — do not add a new one**

`ProjectsTests.cs:1083` already contains `RevealInFinder_PathExists_InvokesOpenDashRWithTheProjectPath`, which hard-asserts `open` and `-R`. **That test fails on Windows as written**, so this task is a modification, not an addition. Replace its assertions (lines 1093–1094) so the whole test reads:

```csharp
    [Fact]
    public async Task RevealInFinder_PathExists_InvokesThePlatformFileManager()
    {
        _projects.Adopt(_projectRoot);
        string? capturedExecutable = null;
        IReadOnlyList<string>? capturedArgs = null;
        bool Fake(string exe, IReadOnlyList<string> args) { capturedExecutable = exe; capturedArgs = args; return true; }

        var response = ProjectsEndpoint.RevealInFinder(_projects, ProjectGuid, Fake);
        var json = await ResultBodyAsync(response);

        if (OperatingSystem.IsWindows())
        {
            // explorer.exe takes the selection as ONE comma-joined argument, not two.
            Assert.Equal("explorer.exe", capturedExecutable);
            Assert.Equal([$"/select,{RealPath(_projectRoot)}"], capturedArgs);
        }
        else
        {
            Assert.Equal("open", capturedExecutable);
            Assert.Equal(["-R", RealPath(_projectRoot)], capturedArgs);
        }

        Assert.True(json.GetProperty("success").GetBoolean());
    }
```

Leave `RevealInFinder_PathMissing_FailsCleanly_NeverInvokesTheLauncher` (line 1099) untouched — it asserts the launcher is never invoked, which is platform-independent.

- [ ] **Step 2: Run to confirm macOS is unaffected**

Run: `cd Core && dotnet test tests/Hades.Server.Tests --filter "FullyQualifiedName~RevealInFinder"`
Expected: PASS (2 tests). The Windows branch is unproven until Task 9's CI run — which is exactly where a wrong `explorer.exe` argument shape will surface.

- [ ] **Step 3: Implement**

In `ProjectsEndpoint.RevealInFinder`, replace the single `launch("open", ["-R", project.Path])` with:

```csharp
        // The route keeps its macOS-flavoured name deliberately: renaming
        // /control/projects/{id}/revealInFinder would break the shipped Swift client for a
        // cosmetic gain. Route verbs stay platform-neutral in NAME; platform-specific behaviour
        // lives here. See Spec #5 §6.1 and §9.2.
        //
        // explorer.exe takes the selection as one comma-joined argument, not two.
        var launched = OperatingSystem.IsWindows()
            ? launch("explorer.exe", [$"/select,{project.Path}"])
            : launch("open", ["-R", project.Path]);
```

- [ ] **Step 4: Run and commit**

Run: `cd Core && dotnet test tests/Hades.Server.Tests`
Expected: PASS.

```bash
git add Core/src/Hades.Server/Control/ProjectsEndpoint.cs Core/tests/Hades.Server.Tests/Control/ProjectsTests.cs
git commit -m "feat: reveal a project in Explorer on Windows"
```

**Note:** `explorer.exe` returns a non-zero exit code even on success. `DefaultProcessLauncher` reports whether the process *started*, not its exit code, so this is already correct — do not "fix" it by checking the exit code.

---

### Task 8: Branch the Unity Hub editor path for Windows

**Files:**
- Modify: `Core/src/Hades.Server/Control/ProjectsEndpoint.cs:718`
- Test: `Core/tests/Hades.Server.Tests/Control/ProjectsTests.cs`

- [ ] **Step 1: Write the failing test**

`UnityHubEditorExecutablePath` is currently a private static. Make it `internal static` and add `[assembly: InternalsVisibleTo("Hades.Server.Tests")]` if the assembly lacks it (check `Core/src/Hades.Server/` for an existing `AssemblyInfo` or an `InternalsVisibleTo` in the csproj first — `Hades.Core` already uses this pattern for `Hades.Server`).

```csharp
    [Fact]
    public void UnityHubPathFollowsThePlatformConvention()
    {
        var path = ProjectsEndpoint.UnityHubEditorExecutablePath("6000.0.30f1");

        if (OperatingSystem.IsWindows())
            Assert.Equal(@"C:\Program Files\Unity\Hub\Editor\6000.0.30f1\Editor\Unity.exe", path);
        else
            Assert.Equal("/Applications/Unity/Hub/Editor/6000.0.30f1/Unity.app/Contents/MacOS/Unity", path);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd Core && dotnet test tests/Hades.Server.Tests --filter "FullyQualifiedName~UnityHubPath"`
Expected: FAIL — the method is private, or returns the macOS path on both.

- [ ] **Step 3: Implement**

```csharp
    /// <summary>Unity Hub's own default per-version install location on each platform - see this
    /// class's own "design decisions" note on why this convention, rather than real Hub discovery,
    /// is what backs <see cref="OpenInUnity"/>. The cost of the convention is higher on Windows,
    /// where users relocate editors to another drive far more often than Mac users move
    /// /Applications - so a miss here is expected more often, and OpenInUnity's existing
    /// "not found at the default location, open from Unity Hub instead" message carries it.</summary>
    internal static string UnityHubEditorExecutablePath(string version) =>
        OperatingSystem.IsWindows()
            ? $@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Unity.exe"
            : $"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity";
```

- [ ] **Step 4: Run and commit**

Run: `cd Core && dotnet test tests/Hades.Server.Tests`
Expected: PASS.

```bash
git add Core/src/Hades.Server/Control/ProjectsEndpoint.cs Core/tests/Hades.Server.Tests/Control/ProjectsTests.cs
git commit -m "feat: resolve the Unity Hub editor path on Windows"
```

---

### Task 9: Add the Windows CI job — the gate for Slice 1

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add the job**

Append to `.github/workflows/ci.yml`, mirroring the existing `dotnet-tests` job:

```yaml
  dotnet-tests-windows:
    name: App (.NET) Tests — Windows
    # The point of this job is precisely what the macOS job cannot see: path handling,
    # case-insensitivity, and the Windows file-security branch added in Spec #5 §6.2. It is also
    # the ONLY place the LocalApplicationData storage root (§6.4) and the token DACL are ever
    # actually executed.
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      # Same isolation as the macOS job, and for the same reason: only Hades.Server.Tests has a
      # [ModuleInitializer] safety net, so the other three projects would otherwise write into the
      # runner's real application-data directory.
      - name: Set up an isolated HADES_HOME
        shell: bash
        run: |
          echo "HADES_HOME=$RUNNER_TEMP/hades-home" >> "$GITHUB_ENV"
          mkdir -p "$RUNNER_TEMP/hades-home"

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: Core/global.json

      # Honesty note, matching the macOS job's: 15 test files require a developer-local Unity
      # project that is absent here. Since this plan's Task 3, they report as SKIPPED rather than
      # passed, so this job's pass count no longer overstates what it exercised - read the skip
      # count alongside it.
      - name: dotnet test
        working-directory: Core
        # Platform!=Unix excludes the handful of tests that assert POSIX-only behaviour
        # (File.SetUnixFileMode and friends). A filtered test is not reported at all - see
        # PlatformTraits for why traits rather than skips.
        run: dotnet test --filter "Platform!=Unix"
```

- [ ] **Step 2: Push the branch and watch the run**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: run the .NET suite on Windows"
git push -u origin HEAD
gh run watch
```

Expected: **the first run will probably fail.** That is the job doing its job. Triage each failure into one of:
- a genuine Windows bug in the core → fix it, add a regression test, commit
- a test asserting POSIX behaviour → guard it as in Task 1
- a path-separator or case-sensitivity assumption → fix the production code, not the test

- [ ] **Step 3: Iterate until green**

Re-run after each fix. **Do not proceed to Slice 2 until this job is green** — everything downstream assumes a core that actually works on Windows.

- [ ] **Step 4: Record what the run revealed**

Add any newly discovered Windows-specific bug classes to `docs/superpowers/specs/2026-08-23-hades-windows-shell-design.md` §9.1, which exists to carry exactly this list.

```bash
git add docs/superpowers/specs/2026-08-23-hades-windows-shell-design.md
git commit -m "docs: record Windows-specific findings from the first CI run"
```

---

# SLICE 2 — `Hades.Control.Client`

### Task 10: Create the project and its test project

**Files:**
- Create: `Core/src/Hades.Control.Client/Hades.Control.Client.csproj`
- Create: `Core/tests/Hades.Control.Client.Tests/Hades.Control.Client.Tests.csproj`
- Modify: `Core/Hades.sln`

- [ ] **Step 1: Create the library csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    The .NET twin of Mac/HadesControl (Swift). Platform-neutral net10.0 on purpose: this is
    consumed by Hades.Cli, which ships on macOS, and by the Windows WPF shell. It is NOT Windows
    code and does not live under Windows/. See Spec #5 §2.1.

    It must never reference Hades.Core or Hades.Server. That rule is enforced for real in Task 20;
    this comment is not the mechanism.
  -->
  <PropertyGroup>
    <RootNamespace>Hades.Control.Client</RootNamespace>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Create the test csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <!-- The ONLY project in the repo sanctioned to reference both sides of the client/core
         boundary. It does so to PROVE they agree (ConformanceTests) and to generate the golden
         fixture corpus from the real server types - never to let the client borrow server code.
         See Spec #5 §2.1. -->
    <ProjectReference Include="..\..\src\Hades.Control.Client\Hades.Control.Client.csproj" />
    <ProjectReference Include="..\..\src\Hades.Server\Hades.Server.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add both to the solution**

```bash
cd Core
dotnet sln add src/Hades.Control.Client/Hades.Control.Client.csproj
dotnet sln add tests/Hades.Control.Client.Tests/Hades.Control.Client.Tests.csproj
dotnet build
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Core/Hades.sln Core/src/Hades.Control.Client Core/tests/Hades.Control.Client.Tests
git commit -m "chore: scaffold Hades.Control.Client and its test project"
```

---

### Task 11: Port `Discovery`

**Files:**
- Create: `Core/src/Hades.Control.Client/ControlConnection.cs`
- Create: `Core/src/Hades.Control.Client/Discovery.cs`
- Create: `Core/tests/Hades.Control.Client.Tests/DiscoveryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Hades.Control.Client;

namespace Hades.Control.Client.Tests;

public class DiscoveryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ReadsPortAndToken()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "control.token"), """{"port":54321,"token":"abc"}""");

        var connection = Discovery.Read(_root);

        Assert.NotNull(connection);
        Assert.Equal(54321, connection.Port);
        Assert.Equal("abc", connection.Token);
    }

    [Fact]
    public void ReturnsNullWhenTheFileIsAbsent()
    {
        Assert.Null(Discovery.Read(_root));
    }

    [Fact]
    public void ReturnsNullOnMalformedContent()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "control.token"), "not json");

        Assert.Null(Discovery.Read(_root));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~Discovery"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

`ControlConnection.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Hades.Control.Client;

/// <summary>Where the running core's control API can be reached, and the bearer token every
/// request must carry. Mirrors Swift's <c>ControlConnection</c> and the server's
/// <c>ControlConnectionInfo</c> - same JSON shape, same discovery file.</summary>
public sealed record ControlConnection
{
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
}
```

`Discovery.cs`:

```csharp
using System.Text.Json;

namespace Hades.Control.Client;

/// <summary>
/// Reads the control API's discovery file - never a hardcoded port. A missing file means Hades is
/// not running, which is an ordinary state, not an error: every failure here returns null rather
/// than throwing. Mirrors Swift's Discovery.swift exactly.
/// </summary>
public static class Discovery
{
    /// <param name="root">The application-data root. Callers that want the default should pass
    /// the same value the core resolves - see the CLI, which reads HADES_HOME or falls back.</param>
    public static ControlConnection? Read(string root)
    {
        try
        {
            var path = Path.Combine(root, "control.token");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ControlConnection>(File.ReadAllText(path));
        }
        catch
        {
            // Missing, unreadable, malformed, or caught mid-write by the core: all mean "not
            // usable right now", never a condition worth surfacing as a client error.
            return null;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~Discovery"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Core/src/Hades.Control.Client Core/tests/Hades.Control.Client.Tests
git commit -m "feat: add control-API discovery to the shared client"
```

---

### Task 12: Add the unknown-enum fallback converter

Forward compatibility by construction: a newer core adding an `iconState` case must never crash an older client. Swift gets this from `ControlEnum.unknownFallback`; .NET needs a converter, applied generically so no one can forget it per-enum.

**Files:**
- Create: `Core/src/Hades.Control.Client/UnknownFallbackConverter.cs`
- Create: `Core/tests/Hades.Control.Client.Tests/UnknownFallbackConverterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Control.Client;

namespace Hades.Control.Client.Tests;

public class UnknownFallbackConverterTests
{
    [JsonConverter(typeof(UnknownFallbackConverter<Sample>))]
    public enum Sample { Unknown, Idle, Indexing }

    static readonly JsonSerializerOptions Options = new();

    [Fact]
    public void DecodesAKnownValue()
    {
        Assert.Equal(Sample.Indexing, JsonSerializer.Deserialize<Sample>("\"indexing\"", Options));
    }

    [Fact]
    public void DecodesAnUnknownValueToTheFallbackInsteadOfThrowing()
    {
        Assert.Equal(Sample.Unknown, JsonSerializer.Deserialize<Sample>("\"teleporting\"", Options));
    }

    [Fact]
    public void IsCaseInsensitiveLikeTheWire()
    {
        Assert.Equal(Sample.Idle, JsonSerializer.Deserialize<Sample>("\"Idle\"", Options));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~UnknownFallback"`
Expected: FAIL — converter does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hades.Control.Client;

/// <summary>
/// Decodes a closed string enum, mapping any unrecognised value to the enum's <c>Unknown</c>
/// member instead of throwing - the .NET equivalent of Swift's <c>ControlEnum.unknownFallback</c>.
///
/// Every control-API enum in this client uses this converter, without exception: a newer core
/// adding a case must degrade, never crash an older client, and the enum someone forgets to opt
/// in is exactly the one that will crash. Requiring an <c>Unknown</c> member (enforced by the
/// static constructor below) is what makes "apply it everywhere" a mechanical rule rather than a
/// judgement call.
/// </summary>
public sealed class UnknownFallbackConverter<T> : JsonConverter<T> where T : struct, Enum
{
    static readonly T Fallback;

    static UnknownFallbackConverter()
    {
        if (!Enum.TryParse("Unknown", out Fallback))
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} must declare an 'Unknown' member to use UnknownFallbackConverter.");
        }
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return raw is not null && Enum.TryParse<T>(raw, ignoreCase: true, out var value) ? value : Fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~UnknownFallback"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Core/src/Hades.Control.Client/UnknownFallbackConverter.cs \
        Core/tests/Hades.Control.Client.Tests/UnknownFallbackConverterTests.cs
git commit -m "feat: decode unknown control-API enum values to a fallback"
```

---

### Task 13: Port the DTOs

**Files:**
- Create: `Core/src/Hades.Control.Client/Dtos/Summary.cs`, `Projects.cs`, `Editors.cs`, `Traces.cs`, `Memory.cs`, `Settings.cs`, `Operations.cs`, `Errors.cs`
- Reference while porting: `Core/src/Hades.Server/Control/*.cs` and `Mac/HadesControl/Sources/HadesControl/DTOs.swift`

- [ ] **Step 1: Enumerate exactly what to port**

```bash
cd Core/src/Hades.Server/Control
grep -hn "^public sealed record \|^public enum " *.cs | sort
```

Port every wire record and enum **except** the 13 in `MigrationEndpoint.cs` (excluded per Spec #5 §7) and the six non-wire types named in the plan's ground-truth table.

- [ ] **Step 2: Port them, one file per endpoint group**

For each record, copy the property list verbatim, keeping `[JsonPropertyName]` exactly as the server declares it. Example, from `SettingsEndpoint.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.McpPortSetting. Field-for-field agreement is enforced by
/// ConformanceTests, not by discipline.</summary>
public sealed record McpPortSetting
{
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("inUse")] public required bool InUse { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

public sealed record LogLevelSetting
{
    [JsonPropertyName("level")] public required string Level { get; init; }
}

public sealed record SettingsResult
{
    [JsonPropertyName("mcpPort")] public required McpPortSetting McpPort { get; init; }
    [JsonPropertyName("logLevel")] public required LogLevelSetting LogLevel { get; init; }
}
```

Every ported enum gets an `Unknown` member and the converter:

```csharp
[JsonConverter(typeof(UnknownFallbackConverter<ControlIconState>))]
public enum ControlIconState { Unknown, Idle, Indexing, Attached, LeaseHeld, Error }
```

**Do not add helper methods, computed properties, or convenience constructors.** These types combine nothing, format nothing, and derive nothing — the same rule `DTOs.swift` states in its own header. A helper here is the first crack in "the shell renders, the core decides".

- [ ] **Step 3: Build**

Run: `cd Core && dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add Core/src/Hades.Control.Client/Dtos
git commit -m "feat: port the control-API wire DTOs to the .NET client"
```

---

### Task 14: The conformance test

**Files:**
- Create: `Core/tests/Hades.Control.Client.Tests/ClientCoverage.cs`
- Create: `Core/tests/Hades.Control.Client.Tests/ConformanceTests.cs`

- [ ] **Step 1: Write the exclusion artifact**

```csharp
namespace Hades.Control.Client.Tests;

/// <summary>
/// The single, named record of which parts of the control API the .NET client deliberately does
/// not cover. Shared by ConformanceTests and the golden-fixture generator so "which client speaks
/// which endpoints" has one home instead of becoming tribal knowledge (Spec #5 §7).
///
/// Any new control endpoint lands in BOTH clients, or gets an entry here with a reason.
/// </summary>
public static class ClientCoverage
{
    /// <summary>v1.2 migration. macOS-only by construction: v1.2 never shipped on Windows, so no
    /// Windows user can have an install to migrate from (Spec #5 §7). The Swift client covers
    /// these; the .NET client deliberately does not.</summary>
    public static bool IsExcluded(Type serverType) =>
        serverType.Name.StartsWith("Migration", StringComparison.Ordinal);
}
```

- [ ] **Step 2: Write the conformance test**

```csharp
using System.Reflection;
using System.Text.Json.Serialization;

namespace Hades.Control.Client.Tests;

/// <summary>
/// Proves the client's duplicated wire records still agree with the server's, field for field.
///
/// The walk is driven from the SERVER's type list, never the client's: driven from the client,
/// a brand-new server DTO with no client twin would pass silently, which is precisely the drift
/// this exists to catch.
/// </summary>
public class ConformanceTests
{
    /// <summary>A server type participates only if at least one property carries
    /// [JsonPropertyName]. Six public records under Control/ are never serialized - the plain-data
    /// inputs to each endpoint's Resolve, plus the operations registry record - and a public-type
    /// walk would otherwise pick them up. Verified 2026-08-24: these six are the ONLY records
    /// under Control/ with unattributed properties, and every genuine wire record is 100%
    /// attribute-pinned, so this rule separates them with no hand-maintained list to rot.</summary>
    static bool IsWireType(Type t) =>
        t.GetProperties().Any(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null);

    /// <summary>
    /// The walk applies all three carve-outs Spec #5 §2.2 names:
    ///  1. Migration types - via ClientCoverage.IsExcluded.
    ///  2. The client's deliberate `unknown` enum member - handled by NOT walking enums at all
    ///     (`t.IsClass`). The client's enums are intentionally a superset of the server's, so
    ///     comparing case sets would fail by design; forward compatibility is instead proven
    ///     directly by UnknownFallbackConverterTests, and the enum's WIRE behaviour is pinned by
    ///     the golden fixtures, which carry real enum values through the real serializer.
    ///  3. Non-wire public records - via IsWireType.
    /// </summary>
    static IEnumerable<Type> ServerWireTypes() =>
        typeof(Hades.Server.Control.SettingsResult).Assembly
            .GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "Hades.Server.Control")
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(IsWireType)
            .Where(t => !ClientCoverage.IsExcluded(t));

    [Fact]
    public void EveryServerWireTypeHasAClientTwin()
    {
        var clientTypes = typeof(Hades.Control.Client.Dtos.SettingsResult).Assembly
            .GetTypes().Where(t => t.IsPublic).ToDictionary(t => t.Name);

        var missing = ServerWireTypes().Select(t => t.Name).Where(n => !clientTypes.ContainsKey(n)).ToList();

        Assert.True(missing.Count == 0,
            $"Server wire types with no client twin: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryWirePropertyAgreesFieldForField()
    {
        var clientTypes = typeof(Hades.Control.Client.Dtos.SettingsResult).Assembly
            .GetTypes().Where(t => t.IsPublic).ToDictionary(t => t.Name);
        var failures = new List<string>();

        foreach (var serverType in ServerWireTypes())
        {
            if (!clientTypes.TryGetValue(serverType.Name, out var clientType)) continue;

            var serverProps = WireNames(serverType);
            var clientProps = WireNames(clientType);

            foreach (var (name, serverProp) in serverProps)
            {
                if (!clientProps.TryGetValue(name, out var clientProp))
                {
                    failures.Add($"{serverType.Name}.{name}: missing on the client");
                    continue;
                }

                if (IsNullable(serverProp) != IsNullable(clientProp))
                    failures.Add($"{serverType.Name}.{name}: nullability differs");

                if (IsRequired(serverProp) != IsRequired(clientProp))
                    failures.Add($"{serverType.Name}.{name}: required-ness differs");
            }

            foreach (var name in clientProps.Keys.Except(serverProps.Keys))
                failures.Add($"{serverType.Name}.{name}: present on the client, absent on the server");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>A partially-attributed type would be skipped by IsWireType's "any" rule and drift
    /// unnoticed, so it is a failure in its own right, not a silent pass.</summary>
    [Fact]
    public void NoServerWireTypeIsOnlyPartiallyAttributed()
    {
        var offenders = ServerWireTypes()
            .Where(t => t.GetProperties()
                .Any(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is null))
            .Select(t => t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Wire types with unattributed properties (IsWireType would misclassify these): " +
            $"{string.Join(", ", offenders)}");
    }

    static Dictionary<string, PropertyInfo> WireNames(Type t) =>
        t.GetProperties()
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name);

    static bool IsNullable(PropertyInfo p) =>
        new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable;

    static bool IsRequired(PropertyInfo p) =>
        p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null;
}
```

- [ ] **Step 2b: Run it**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~Conformance"`
Expected: **likely FAIL initially**, listing real mismatches from Task 13. Fix the *client* DTOs until green — the server is the source of truth here, never the other way round.

- [ ] **Step 3: Commit**

```bash
git add Core/tests/Hades.Control.Client.Tests
git commit -m "test: pin the .NET client's DTOs against the server's wire types"
```

---

### Task 15: Generate the golden fixture corpus

Reflection cannot see the wire. `ControlListener.cs:180` sets `DefaultIgnoreCondition = WhenWritingNull`, so a nullable field is **absent**, not null — invisible to Task 14. Fixtures close that gap and are shared with Swift.

**Files:**
- Create: `Core/tests/Hades.Control.Client.Tests/FixtureGenerationTests.cs`
- Create: `Core/tests/Fixtures/control-api/*.json` (generated)

- [ ] **Step 1: Write the generator**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hades.Control.Client.Tests;

/// <summary>
/// Emits one exemplar of every wire DTO, serialized through the SAME JsonSerializerOptions
/// ControlListener configures, into Core/tests/Fixtures/control-api/.
///
/// Generated on every run rather than captured once by hand: the existing Swift corpus
/// (Mac/HadesControl/Tests/HadesControlTests/Fixtures, 50 files) was produced by a documented
/// manual procedure, so a DTO change could leave a stale fixture passing. Generation makes that
/// impossible. Both this project and the Swift tests decode these same bytes, so "the two clients
/// agree" is tested rather than assumed. See Spec #5 §2.2.
/// </summary>
public class FixtureGenerationTests
{
    /// <summary>Exactly what ControlListener configures - see ControlListener.cs:180. If that
    /// ever changes, this must change with it, or the fixtures stop representing the wire.</summary>
    static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "control-api");

    [Fact]
    public void GenerateTheCorpus()
    {
        var dir = Path.GetFullPath(FixtureDir);
        Directory.CreateDirectory(dir);

        foreach (var (name, value) in Exemplars.All())
        {
            var json = JsonSerializer.Serialize(value, value.GetType(), WireOptions);
            File.WriteAllText(Path.Combine(dir, $"{name}.json"), json);
        }

        Assert.NotEmpty(Directory.GetFiles(dir, "*.json"));
    }

    [Fact]
    public void EveryFixtureRoundTripsIntoItsClientType()
    {
        var dir = Path.GetFullPath(FixtureDir);
        Assert.True(Directory.Exists(dir), "Run GenerateTheCorpus first — it populates this directory.");

        foreach (var (name, value) in Exemplars.All())
        {
            var path = Path.Combine(dir, $"{name}.json");
            Assert.True(File.Exists(path), $"Missing fixture: {name}.json");

            var clientType = typeof(Hades.Control.Client.Dtos.SettingsResult).Assembly
                .GetTypes().Single(t => t.IsPublic && t.Name == value.GetType().Name);

            var decoded = JsonSerializer.Deserialize(File.ReadAllText(path), clientType);
            Assert.NotNull(decoded);
        }
    }
}
```

- [ ] **Step 2: Write the exemplars**

Create `Core/tests/Hades.Control.Client.Tests/Exemplars.cs`. One entry per non-excluded server wire type, each populated with values that exercise the tricky cases — **at least one exemplar per type with every nullable field null**, since absent-vs-null is the behaviour fixtures exist to pin:

```csharp
namespace Hades.Control.Client.Tests;

/// <summary>One exemplar per wire DTO, built from the SERVER's types so the fixture is what the
/// server would really send. Nullable fields are deliberately left null in at least one exemplar
/// per type: WhenWritingNull means they must be ABSENT from the JSON, and that absence is exactly
/// what a reflection test cannot see.</summary>
public static class Exemplars
{
    public static IEnumerable<(string Name, object Value)> All()
    {
        yield return ("settings", new Hades.Server.Control.SettingsResult
        {
            McpPort = new Hades.Server.Control.McpPortSetting
            {
                Port = 7823, InUse = false, Message = "Bound to 127.0.0.1:7823.",
            },
            LogLevel = new Hades.Server.Control.LogLevelSetting { Level = "Information" },
        });

        // The single most important fixture in the corpus: SummaryResult.Lease is nullable, so
        // WhenWritingNull means the key must be ABSENT from the JSON, not present as null. That is
        // exactly the behaviour Task 14's reflection test structurally cannot see. Property names
        // and types come from SummaryEndpoint.cs's own declaration - read it while writing this.
        yield return ("summary_no_lease", new Hades.Server.Control.SummaryResult
        {
            // ...every required property of SummaryResult, with Lease left null
        });
    }
}
```

**Completing the set is mechanical, not a judgement call**, and the compiler does most of the work: every wire record's properties are `required`, so a missing one is a build error and a fully compiling `Exemplars.cs` is itself proof that each exemplar is complete. Do not invent property names — read each record's declaration in `Core/src/Hades.Server/Control/`.

Work from the authoritative list the conformance test already computes:

```bash
cd Core
dotnet test tests/Hades.Control.Client.Tests \
  --filter "FullyQualifiedName~EveryServerWireTypeHasAClientTwin" -v n
```

For **every** type that test walks, add one `yield return`. Where a type has nullable properties, add a **second** exemplar with those properties null, named `<name>_absent` — absent-vs-null is the whole reason this corpus exists.

Step 3 below fails until every walked type has an exemplar, so the loop terminates on its own.

- [ ] **Step 3: Run the generator**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~FixtureGeneration"`
Expected: PASS; `Core/tests/Fixtures/control-api/` now contains one `.json` per wire type.

- [ ] **Step 4: Inspect one by hand to confirm nulls are absent, not null**

```bash
cat Core/tests/Fixtures/control-api/summary_no_lease.json
```

Expected: **no `"lease": null`** — the key is simply not there. If it is present as null, `WireOptions` does not match `ControlListener`'s and must be fixed before proceeding; this is the whole point of the corpus.

- [ ] **Step 5: Commit the generator and the corpus**

```bash
git add Core/tests/Hades.Control.Client.Tests Core/tests/Fixtures/control-api
git commit -m "test: generate golden control-API fixtures from the server's own types"
```

---

### Task 16: Repoint the Swift tests at the generated corpus

**Files:**
- Modify: `Mac/HadesControl/Package.swift`
- Modify: `Mac/HadesControl/Tests/HadesControlTests/Support/Fixtures.swift`
- Delete: `Mac/HadesControl/Tests/HadesControlTests/Fixtures/*.json` (after the repoint is green)

- [ ] **Step 1: Point SwiftPM at the shared corpus**

The generated corpus lives outside the Swift package, and SwiftPM resources must live inside it. Create a symlink so one set of bytes serves both clients:

```bash
cd Mac/HadesControl/Tests/HadesControlTests
mv Fixtures Fixtures.hand-captured
ln -s ../../../../Core/tests/Fixtures/control-api Fixtures
```

- [ ] **Step 2: Run the Swift suite**

```bash
cd Mac/HadesControl && swift test
```

Expected: **failures** wherever a hand-captured fixture name has no generated counterpart. For each, either rename the generated exemplar to match, or add the missing exemplar in `Exemplars.cs` — the generated corpus is the target, and any hand-captured fixture without a counterpart is a coverage gap to close, not a file to keep.

- [ ] **Step 3: Iterate until the 44 existing Swift tests pass against generated bytes**

Their hand-written assertions are the valuable part and must be kept unchanged. Only the inputs become generated.

- [ ] **Step 4: Delete the hand-captured corpus**

```bash
rm -rf Mac/HadesControl/Tests/HadesControlTests/Fixtures.hand-captured
cd Mac/HadesControl && swift test
```

Expected: PASS, 44 tests.

- [ ] **Step 5: Commit**

```bash
git add Mac/HadesControl
git commit -m "test: decode the generated control-API corpus from the Swift client too"
```

---

### Task 17: Build the typed `ControlClient`

**Files:**
- Create: `Core/src/Hades.Control.Client/ControlClientError.cs`
- Create: `Core/src/Hades.Control.Client/ControlClient.cs`
- Create: `Core/tests/Hades.Control.Client.Tests/ControlClientTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;

namespace Hades.Control.Client.Tests;

public class ControlClientTests
{
    sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? SeenAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    static ControlClient Make(StubHandler handler) =>
        new(new ControlConnection { Port = 1234, Token = "tok" }, new HttpClient(handler));

    [Fact]
    public async Task PresentsTheBearerToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"mcpPort":{"port":7823,"inUse":false,"message":"ok"},"logLevel":{"level":"Information"}}""");

        await Make(handler).GetSettingsAsync();

        Assert.Equal("Bearer tok", handler.SeenAuthorization);
    }

    [Fact]
    public async Task DecodesASuccessfulResponse()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"mcpPort":{"port":7823,"inUse":false,"message":"ok"},"logLevel":{"level":"Information"}}""");

        var settings = await Make(handler).GetSettingsAsync();

        Assert.Equal(7823, settings.McpPort.Port);
    }

    [Fact]
    public async Task Raises_StaleToken_On401()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":"bad token"}""");

        var error = await Assert.ThrowsAsync<ControlClientException>(
            () => Make(handler).GetSettingsAsync());

        Assert.Equal(ControlClientError.StaleToken, error.Error);
    }

    [Fact]
    public async Task CarriesTheServersOwnMessageOnOtherFailures()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"error":"Unknown project 'x'."}""");

        var error = await Assert.ThrowsAsync<ControlClientException>(
            () => Make(handler).GetSettingsAsync());

        Assert.Equal(ControlClientError.Server, error.Error);
        Assert.Equal("Unknown project 'x'.", error.Message);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~ControlClientTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the error taxonomy**

```csharp
namespace Hades.Control.Client;

/// <summary>Every way a control-API call can fail to hand back a decoded DTO. Mirrors Swift's
/// ControlClientError.</summary>
public enum ControlClientError
{
    /// <summary>HTTP 401: the token this client presented is stale, almost always because the core
    /// restarted and wrote a fresh discovery file. The only recovery is to call Discovery.Read
    /// again and build a new client - retrying with the same token fails identically every time.
    /// A distinct case, not a status code the caller must compare, so it is actionable in the
    /// type.</summary>
    StaleToken,

    /// <summary>A non-2xx, non-401 response. The message is the server's own "error" field when
    /// the body carried one - never text invented client-side.</summary>
    Server,

    /// <summary>The body did not decode into the expected DTO shape.</summary>
    Decoding,

    /// <summary>No response to check the status of: the core is not running, the port is stale,
    /// the request timed out.</summary>
    Transport,
}

public sealed class ControlClientException(ControlClientError error, string message)
    : Exception(message)
{
    public ControlClientError Error { get; } = error;
}
```

- [ ] **Step 4: Implement the client**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Hades.Control.Client.Dtos;

namespace Hades.Control.Client;

/// <summary>
/// A thin, typed client over the core's control API. The .NET twin of Swift's ControlClient, and
/// held to the same contract: no retry policy, no caching, no derived state. It renders what the
/// core decided and nothing else.
/// </summary>
public sealed class ControlClient
{
    readonly HttpClient _http;

    public ControlClient(ControlConnection connection, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri($"http://127.0.0.1:{connection.Port}");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
    }

    public Task<SummaryResult> GetSummaryAsync(CancellationToken ct = default) =>
        SendAsync<SummaryResult>(HttpMethod.Get, "/control/summary", ct);

    public Task<ProjectsResult> GetProjectsAsync(CancellationToken ct = default) =>
        SendAsync<ProjectsResult>(HttpMethod.Get, "/control/projects", ct);

    public Task<SettingsResult> GetSettingsAsync(CancellationToken ct = default) =>
        SendAsync<SettingsResult>(HttpMethod.Get, "/control/settings", ct);

    public Task<ActionResult> ReleaseLeaseAsync(string leaseId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"/control/leases/{leaseId}/release", ct);

    async Task<T> SendAsync<T>(HttpMethod method, string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(new HttpRequestMessage(method, path), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ControlClientException(ControlClientError.Transport, ex.Message);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ControlClientException(ControlClientError.StaleToken,
                "The control-API token is stale. Re-read the discovery file.");

        if (!response.IsSuccessStatusCode)
            throw new ControlClientException(ControlClientError.Server, ServerMessage(body) ?? body);

        try
        {
            return JsonSerializer.Deserialize<T>(body)
                   ?? throw new ControlClientException(ControlClientError.Decoding, "Body decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new ControlClientException(ControlClientError.Decoding, ex.Message);
        }
    }

    /// <summary>Every Control endpoint's error responses carry an "error" field; surfacing it
    /// verbatim is what keeps the client from inventing text the core did not author.</summary>
    static string? ServerMessage(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~ControlClientTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add Core/src/Hades.Control.Client Core/tests/Hades.Control.Client.Tests
git commit -m "feat: add the typed control-API client"
```

---

### Task 18: Move `Hades.Cli` onto the client

**Files:**
- Modify: `Core/src/Hades.Cli/Hades.Cli.csproj`
- Modify: `Core/src/Hades.Cli/Program.cs`
- Modify: `Core/src/Hades.Cli/Commands.cs`
- Delete: `Core/src/Hades.Cli/Discovery.cs`

- [ ] **Step 1: Confirm the CLI's current behaviour so the move can be proven behaviour-preserving**

```bash
cd Core && dotnet run --project src/Hades.Cli -- status > /tmp/hades-cli-before.txt 2>&1; cat /tmp/hades-cli-before.txt
```

Record the output. If Hades is not running, the expected output is the "No Hades control API found …" line — that is a valid baseline too.

- [ ] **Step 2: Swap the project reference**

In `Hades.Cli.csproj`, replace the `Hades.Core` reference:

```xml
  <ItemGroup>
    <!-- Hades.Core is deliberately NOT referenced. It used to be, for AppPaths only, to find the
         discovery file - that job now belongs to Hades.Control.Client, exactly as it belongs to
         Discovery.swift on the Swift side. A production project referencing both sides of the
         client/core boundary is precisely the bridge the shell is forbidden (Spec #5 §2.1). -->
    <ProjectReference Include="..\Hades.Control.Client\Hades.Control.Client.csproj" />
  </ItemGroup>
```

- [ ] **Step 3: Rewrite the discovery half of `Program.cs`**

```csharp
using Hades.Control.Client;

// The application-data root, resolved without Hades.Core: HADES_HOME wins, else the same
// per-platform default AppPaths.DefaultRoot() computes (Spec #5 §6.4). Kept in step with the core
// by the fixture/conformance suite, not by a shared reference.
var root = Environment.GetEnvironmentVariable("HADES_HOME")
           ?? Path.Combine(
               Environment.GetFolderPath(OperatingSystem.IsWindows()
                   ? Environment.SpecialFolder.LocalApplicationData
                   : Environment.SpecialFolder.ApplicationData),
               "Hades");

var connection = Discovery.Read(root);

if (connection is null)
{
    Console.Error.WriteLine(
        $"No Hades control API found at {Path.Combine(root, "control.token")} — is Hades running?");
    return 1;
}

var client = new ControlClient(connection);

return await DispatchAsync(args, client);
```

Update `DispatchAsync` and `Commands.*` to take `ControlClient` instead of `HttpClient`, replacing raw `JsonElement` parsing with the typed DTOs. Keep the "deliberately dumb" rule: print what the core decided, compute nothing.

- [ ] **Step 4: Delete the superseded discovery**

```bash
rm Core/src/Hades.Cli/Discovery.cs
```

- [ ] **Step 5: Build and compare behaviour**

```bash
cd Core && dotnet build
dotnet run --project src/Hades.Cli -- status > /tmp/hades-cli-after.txt 2>&1
diff /tmp/hades-cli-before.txt /tmp/hades-cli-after.txt
```

Expected: no differences. The CLI is macOS-shipped today; this move must not change what it prints.

- [ ] **Step 6: Run the CLI's own tests**

Run: `cd Core && dotnet test tests/Hades.Cli.Tests`
Expected: PASS. These exercise the CLI against a real loopback `ControlListener`, so they are the real proof.

- [ ] **Step 7: Commit**

```bash
git add Core/src/Hades.Cli
git commit -m "refactor: move the CLI onto the shared control client"
```

---

# SLICE 3 — Job Object supervision

### Task 19: Scaffold the Windows tree

**Files:**
- Create: `Windows/HadesWindows.sln`
- Create: `Windows/Hades.Supervision/Hades.Supervision.csproj`
- Create: `Windows/Hades.Supervision.Tests/Hades.Supervision.Tests.csproj`
- Create: `Windows/FakeCore/FakeCore.csproj`

- [ ] **Step 1: Create the projects**

`Windows/Hades.Supervision/Hades.Supervision.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Core\src\Hades.Control.Client\Hades.Control.Client.csproj" />
  </ItemGroup>

</Project>
```

`EnableWindowsTargeting` is what lets this compile on the development Mac (verified during spec research). `AllowUnsafeBlocks` is needed for the `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` marshalling in Task 22.

The test and `FakeCore` projects follow the same shape; the test project references `Hades.Supervision` plus the usual xUnit set. `FakeCore` is `<OutputType>Exe</OutputType>` and needs no references.

- [ ] **Step 2: Create the solution**

```bash
cd Windows
dotnet new sln -n HadesWindows
dotnet sln add Hades.Supervision/Hades.Supervision.csproj
dotnet sln add Hades.Supervision.Tests/Hades.Supervision.Tests.csproj
dotnet sln add FakeCore/FakeCore.csproj
dotnet build
```

Expected: Build succeeded on macOS.

- [ ] **Step 3: Confirm `Core/Hades.sln` is unaffected**

Run: `cd Core && dotnet build`
Expected: Build succeeded. **No `net10.0-windows` project may enter `Core/Hades.sln`** — that isolation is what keeps the macOS CI job working (Spec #5 §3).

- [ ] **Step 4: Commit**

```bash
git add Windows
git commit -m "chore: scaffold the Windows solution and supervision projects"
```

---

### Task 20: The boundary guard — all three layers

**Files:**
- Create: `Windows/Directory.Build.targets`
- Modify: `Core/src/Hades.Cli/Hades.Cli.csproj`

- [ ] **Step 1: Write the guard**

`Windows/Directory.Build.targets`:

```xml
<Project>

  <!--
    "The shell renders, the core decides" (Spec #5 §2). On macOS that rule is mostly enforced by
    language - Swift cannot reference Hades.Core. On Windows the shell is also .NET, so one
    ProjectReference would collapse the architecture permanently, in the direction that is easier
    in the moment.

    Declared here rather than in a single csproj so it covers every project in this tree and so
    removing it is a loud, separate diff. Modeled on the EnsureHeadless target in
    Hades.Core.csproj, which holds the core to the same kind of structural promise.

    Verified empirically (2026-08-23): a two-project probe built clean with a benign reference AND
    with no ProjectReference items at all, and failed with exactly this message when Hades.Core
    was referenced.
  -->
  <Target Name="EnsureShellIsAClient" BeforeTargets="Build">
    <Error Condition="@(ProjectReference->AnyHaveMetadataValue('Filename', 'Hades.Core'))
                   or @(ProjectReference->AnyHaveMetadataValue('Filename', 'Hades.Server'))
                   or @(Reference->AnyHaveMetadataValue('Filename', 'Hades.Core'))
                   or @(Reference->AnyHaveMetadataValue('Filename', 'Hades.Server'))"
           Text="$(MSBuildProjectName) must not reference Hades.Core or Hades.Server. It is a control-API client by design (Spec #5 §2)." />
  </Target>

</Project>
```

- [ ] **Step 2: Add the same target to `Hades.Cli.csproj`**

The CLI lives in `Core/src`, which `Windows/Directory.Build.targets` cannot reach, and Task 18 just promoted it to a client. Paste the identical `<Target>` into `Core/src/Hades.Cli/Hades.Cli.csproj`.

**Do not put it in a `Core/`-wide `Directory.Build.props`** — `Hades.Server` references `Hades.Core` legitimately and would break.

- [ ] **Step 3: Prove the guard fires**

```bash
cd Windows
# Temporarily add a forbidden reference
sed -i '' 's|</PropertyGroup>|</PropertyGroup>\n  <ItemGroup><ProjectReference Include="..\\..\\Core\\src\\Hades.Core\\Hades.Core.csproj" /></ItemGroup>|' Hades.Supervision/Hades.Supervision.csproj
dotnet build Hades.Supervision/Hades.Supervision.csproj 2>&1 | grep "must not reference"
```

Expected: the error text appears. Then revert:

```bash
git checkout Hades.Supervision/Hades.Supervision.csproj
dotnet build
```

Expected: Build succeeded.

Repeat the same add-revert probe against `Core/src/Hades.Cli/Hades.Cli.csproj`.

- [ ] **Step 4: Commit Layer 1**

```bash
git add Windows/Directory.Build.targets Core/src/Hades.Cli/Hades.Cli.csproj
git commit -m "build: fail the build if a client references the core"
```

- [ ] **Step 5: Add Layer 2 — the artifact-level architecture test**

MSBuild item checks are structurally blind to *transitive* references (if `Hades.Control.Client` ever referenced `Hades.Core`, the shell would inherit core types and Layer 1 would stay silent), to `<Reference>` with a `HintPath`, and to a renamed project. Layer 2 inspects the built artifacts instead.

Create `Core/tests/Hades.Control.Client.Tests/ArchitectureTests.cs`:

```csharp
using System.Reflection;

namespace Hades.Control.Client.Tests;

/// <summary>
/// Layer 2 of the boundary defence (Spec #5 §2). Layer 1 (MSBuild) sees only DIRECT
/// ProjectReference/Reference items on the projects that declare the target; this sees what the
/// compiled assembly actually depends on, which is what catches a transitive reference, a
/// HintPath'd DLL, and a renamed project.
///
/// MetadataLoadContext, not Assembly.Load: this test runs on the macOS CI leg, and once
/// Hades.Shell.dll (net10.0-windows) joins the list in the steps 4-6 plan, loading it for
/// EXECUTION would drag in WPF and fail for entirely the wrong reason.
/// </summary>
public class ArchitectureTests
{
    static readonly string[] Forbidden =
        ["Hades.Core", "Hades.Server", "Microsoft.Data.Sqlite", "SQLitePCLRaw.core"];

    public static TheoryData<string> ClientAssemblies() => new()
    {
        // Hades.Shell.dll is deliberately absent: it does not exist until Spec #5 step 4, and it
        // joins this list in that plan. Everything that exists today is covered.
        typeof(Hades.Control.Client.Discovery).Assembly.Location,
        Path.Combine(
            Path.GetDirectoryName(typeof(Hades.Control.Client.Discovery).Assembly.Location)!,
            "hades.dll"),
    };

    [Theory]
    [MemberData(nameof(ClientAssemblies))]
    public void AClientNeverDependsOnTheCore(string assemblyPath)
    {
        Assert.True(File.Exists(assemblyPath), $"Expected this assembly in the test output: {assemblyPath}");

        var runtimeAssemblies = Directory.GetFiles(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll");
        var resolver = new PathAssemblyResolver(
            runtimeAssemblies.Concat(
                Directory.GetFiles(Path.GetDirectoryName(assemblyPath)!, "*.dll")));

        using var context = new MetadataLoadContext(resolver);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        var violations = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => Forbidden.Contains(name))
            .ToList();

        Assert.True(violations.Count == 0,
            $"{Path.GetFileName(assemblyPath)} references {string.Join(", ", violations)}. " +
            "Clients reach the core over the control API only (Spec #5 §2).");
    }
}
```

`hades.dll` is the CLI's assembly name (`Hades.Cli.csproj` sets `<AssemblyName>hades</AssemblyName>`). Add a `ProjectReference` to `Hades.Cli` from the test project so it lands in the same output directory:

```xml
    <ProjectReference Include="..\..\src\Hades.Cli\Hades.Cli.csproj" />
```

- [ ] **Step 6: Run Layer 2 and prove it fails on a real violation**

Run: `cd Core && dotnet test tests/Hades.Control.Client.Tests --filter "FullyQualifiedName~Architecture"`
Expected: PASS (2 theory cases).

Then prove it bites — temporarily add `<ProjectReference Include="..\Hades.Core\Hades.Core.csproj" />` to `Hades.Control.Client.csproj`, re-run, and confirm the failure names `Hades.Core`. **Note this is a violation Layer 1 cannot see**, since the guard target lives on `Hades.Shell` and `Hades.Cli`, not on the client — which is precisely why Layer 2 exists. Revert:

```bash
git checkout Core/src/Hades.Control.Client/Hades.Control.Client.csproj
```

- [ ] **Step 7: Add Layer 3 — banned APIs**

Layers 1 and 2 both watch *references*. Neither sees `Assembly.LoadFrom("core/Hades.Core.dll")` or a raw `SqliteConnection` opened against `graph.db` — and Spec #5 §8.1 ships `core\` *inside the shell's own install directory*, so that path is a one-liner away.

Add to `Core/Directory.Packages.props`:

```xml
    <PackageVersion Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="4.14.0" />
```

Add to `Hades.Control.Client.csproj` and `Hades.Cli.csproj` (and, in the steps 4–6 plan, `Hades.Shell.csproj`):

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <AdditionalFiles Include="$(MSBuildThisFileDirectory)..\..\BannedSymbols.txt" />
  </ItemGroup>
```

Create `Core/BannedSymbols.txt`:

```
T:Microsoft.Data.Sqlite.SqliteConnection;Clients read through the control API, never the databases directly (Spec #5 §7).
M:System.Reflection.Assembly.LoadFrom(System.String);The shell ships core/ inside its own install directory; loading from it bypasses the client boundary (Spec #5 §2).
M:System.Reflection.Assembly.LoadFile(System.String);See Assembly.LoadFrom.
```

- [ ] **Step 8: Prove Layer 3 bites**

Temporarily add to any file in `Hades.Control.Client`:

```csharp
var _ = System.Reflection.Assembly.LoadFrom("core/Hades.Core.dll");
```

Run: `cd Core && dotnet build`
Expected: **build error RS0030**, naming the banned symbol and the reason text. Revert the temporary line and rebuild clean.

- [ ] **Step 9: Commit Layers 2 and 3**

```bash
git add Core/Directory.Packages.props Core/BannedSymbols.txt \
        Core/src/Hades.Control.Client Core/src/Hades.Cli \
        Core/tests/Hades.Control.Client.Tests
git commit -m "build: add artifact-level and banned-API boundary guards"
```

---

### Task 21: The `FakeCore` fixture

Supervision tests must not depend on `dotnet` being installed or fast to cold-start. `FakeCore` speaks just enough of the control API to exercise adopt/spawn/restart.

**Files:**
- Create: `Windows/FakeCore/Program.cs`

- [ ] **Step 1: Implement**

```csharp
// A minimal stand-in for Hades.Server: answers GET /control/ping with a token check, writes the
// same control.token discovery file the real core does, and nothing else. Exists so
// CoreSupervisor's adopt/spawn/restart logic can be exercised without depending on `dotnet` being
// installed or fast to cold-start. Mirrors Mac/HadesSupervision/Sources/FakeCore.
//
// Args: <hadesHome> [--die-after-ms N] [--never-answer]
using System.Net;
using System.Text.Json;

var home = args[0];
var dieAfterMs = GetIntArg("--die-after-ms");
var neverAnswer = args.Contains("--never-answer");

var token = Guid.NewGuid().ToString("N");
var listener = new HttpListener();
var port = FreePort();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();

Directory.CreateDirectory(home);
File.WriteAllText(Path.Combine(home, "control.token"),
    JsonSerializer.Serialize(new { port, token }));

if (dieAfterMs is { } ms)
{
    _ = Task.Run(async () => { await Task.Delay(ms); Environment.Exit(0); });
}

while (true)
{
    var context = await listener.GetContextAsync();
    if (neverAnswer) continue;

    var authorized = context.Request.Headers["Authorization"] == $"Bearer {token}";
    context.Response.StatusCode = authorized ? 200 : 401;
    context.Response.Close();
}

int? GetIntArg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : null;
}

static int FreePort()
{
    using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var chosen = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return chosen;
}
```

- [ ] **Step 2: Build**

Run: `cd Windows && dotnet build FakeCore/FakeCore.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Windows/FakeCore
git commit -m "test: add the FakeCore supervision fixture"
```

---

### Task 22: `JobObject` and `ProcessLauncher`

The two preconditions that a naive implementation silently violates (Spec #5 §4).

**Files:**
- Create: `Windows/Hades.Supervision/JobObject.cs`
- Create: `Windows/Hades.Supervision/ProcessLauncher.cs`
- Create: `Windows/Hades.Supervision.Tests/JobObjectTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using Hades.Supervision;

namespace Hades.Supervision.Tests;

public class JobObjectTests
{
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void KillsTheChildWhenTheJobHandleCloses()
    {
        int pid;
        using (var job = new JobObject())
        {
            var process = ProcessLauncher.LaunchSuspended("cmd.exe", "/c timeout /t 60", null);
            job.Assign(process.ProcessHandle);
            ProcessLauncher.Resume(process);
            pid = process.ProcessId;

            Assert.False(Process.GetProcessById(pid).HasExited);
        } // job disposed -> last handle closed -> kernel kills the tree

        Thread.Sleep(500);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void AssignsBeforeTheChildCanSpawnGrandchildren()
    {
        using var job = new JobObject();
        var process = ProcessLauncher.LaunchSuspended("cmd.exe", "/c echo hi", null);

        // The whole point of CREATE_SUSPENDED: nothing has executed yet, so nothing can have
        // escaped the job. Assignment does NOT retroactively capture existing descendants.
        job.Assign(process.ProcessHandle);
        ProcessLauncher.Resume(process);

        Assert.True(process.ProcessId > 0);
    }
}
```

- [ ] **Step 2: Run on macOS to confirm both skip**

Run: `cd Windows && dotnet test Hades.Supervision.Tests --filter "FullyQualifiedName~JobObject"`
Expected: 2 skipped. **These can only be proven on Windows CI (Task 24)** — that is exactly why Spec #5 §9 splits step 3's gate.

- [ ] **Step 3: Implement `JobObject`**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hades.Supervision;

/// <summary>
/// A Windows Job Object configured to kill every process in it when its LAST handle closes.
///
/// This replaces the entire HadesCoreReaper process the macOS shell needs: when the app dies by
/// any means - including Task Manager's End Task, where no code inside the app gets to run - the
/// kernel closes its handles and terminates the job. No helper process, no getppid() polling, no
/// process-group arithmetic.
///
/// TWO PRECONDITIONS, both load-bearing (Spec #5 §4):
///  1. This object must stay ROOTED for the app's entire lifetime. A handle eligible for
///     finalization kills a HEALTHY core mid-session. Hold it in a field, never a local.
///  2. No OTHER handle to the job may survive. CreateJobObject(NULL, ...) returns a
///     non-inheritable handle, which is safe by default - ProcessLauncher must not widen that
///     (see its own scoped-inheritance note).
///
/// The job is the FORCE-QUIT BACKSTOP, not the shutdown path: job close is TerminateProcess,
/// abrupt, where CoreSupervisor.StopAsync performs a graceful sequence first.
/// </summary>
public sealed class JobObject : IDisposable
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JobObjectLimitKillOnJobClose = 0x2000;

    readonly SafeFileHandle _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Adds a process to the job. Throws on failure rather than degrading: AssignProcessToJobObject
    /// can return ERROR_ACCESS_DENIED even on Windows 8+ where job-hierarchy rules are not
    /// satisfiable (some sandboxes, silo/container hosts, corporate launcher wrappers). The stated
    /// behaviour is to fail loudly and refuse to spawn - an unsupervised core that can outlive its
    /// parent is worse than no core (Spec #5 §4, precondition 3).
    /// </summary>
    public void Assign(SafeFileHandle processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new InvalidOperationException(
                $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}. " +
                "Hades will not spawn an unsupervised core.");
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeFileHandle job, int infoClass, IntPtr info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
}
```

Mark the class `public sealed partial class JobObject` for `LibraryImport` source generation.

- [ ] **Step 4: Implement `ProcessLauncher`**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hades.Supervision;

public sealed record LaunchedProcess(
    SafeFileHandle ProcessHandle, SafeFileHandle ThreadHandle, int ProcessId);

/// <summary>
/// Launches a process SUSPENDED so it can be assigned to a Job Object before it executes a single
/// instruction.
///
/// This exists because System.Diagnostics.Process CANNOT express CREATE_SUSPENDED, and without it
/// there is a real window between spawn and assign in which (a) the parent dying orphans the
/// child - the exact failure the macOS reaper exists to prevent - and (b) any grandchild the child
/// spawns escapes the job forever, because assignment does NOT retroactively capture existing
/// descendants. The Debug path makes this the common case, not the corner case: `dotnet run` forks
/// the real Hades.Server plus compiler-server nodes. See Spec #5 §4, precondition 1.
///
/// On handle inheritance (precondition 2): because this owns CreateProcess directly, it also owns
/// bInheritHandles. Redirecting the core's stdio requires passing TRUE with inheritable pipe ends,
/// and blanket inheritance would hand the child every inheritable handle in the process - the job
/// handle potentially among them, which would make the core survive its own death sentence.
/// Inheritance is therefore scoped to exactly the pipe handles via PROC_THREAD_ATTRIBUTE_HANDLE_LIST
/// in a STARTUPINFOEX. Until stdio redirection is actually wired, this passes bInheritHandles=FALSE,
/// which is trivially safe - do not change that to TRUE without adding the attribute list.
/// </summary>
public static partial class ProcessLauncher
{
    const uint CreateSuspended = 0x00000004;
    const uint CreateUnicodeEnvironment = 0x00000400;

    public static LaunchedProcess LaunchSuspended(string executable, string arguments, string? workingDirectory)
    {
        var startupInfo = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
        var commandLine = $"\"{executable}\" {arguments}";

        if (!CreateProcessW(
                lpApplicationName: null,
                lpCommandLine: ref commandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: CreateSuspended | CreateUnicodeEnvironment,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out var info))
        {
            throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        }

        return new LaunchedProcess(
            new SafeFileHandle(info.hProcess, ownsHandle: true),
            new SafeFileHandle(info.hThread, ownsHandle: true),
            info.dwProcessId);
    }

    public static void Resume(LaunchedProcess process)
    {
        if (ResumeThread(process.ThreadHandle) == unchecked((uint)-1))
            throw new InvalidOperationException($"ResumeThread failed: {Marshal.GetLastWin32Error()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? lpApplicationName, ref string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeFileHandle thread);
}
```

- [ ] **Step 5: Build on macOS**

Run: `cd Windows && dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add Windows/Hades.Supervision Windows/Hades.Supervision.Tests
git commit -m "feat: add the Job Object and suspended-launch primitives"
```

---

### Task 23: `CoreSupervisor`

Port the Swift actor's logic faithfully — including the value that encodes a measured bug.

**Files:**
- Create: `Windows/Hades.Supervision/CoreSupervisor.cs`
- Create: `Windows/Hades.Supervision.Tests/CoreSupervisorTests.cs`
- Reference: `Mac/HadesSupervision/Sources/HadesSupervision/CoreSupervisor.swift`

- [ ] **Step 1: Write the failing tests (these run on macOS — they are pure logic)**

```csharp
using Hades.Supervision;

namespace Hades.Supervision.Tests;

public class CoreSupervisorTests
{
    [Fact]
    public void BackoffDoublesAndCapsAtSixteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), CoreSupervisorConfiguration.DefaultBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(2), CoreSupervisorConfiguration.DefaultBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(4), CoreSupervisorConfiguration.DefaultBackoff(3));
        Assert.Equal(TimeSpan.FromSeconds(8), CoreSupervisorConfiguration.DefaultBackoff(4));
        Assert.Equal(TimeSpan.FromSeconds(16), CoreSupervisorConfiguration.DefaultBackoff(5));
        Assert.Equal(TimeSpan.FromSeconds(16), CoreSupervisorConfiguration.DefaultBackoff(9));
    }

    [Fact]
    public void MinimumStableUptimeDefaultsToThreeSeconds()
    {
        // Not a preference. Without it, a core that answers one ping then dies gets a FRESH
        // 5-attempt budget on every death, so maxRestartAttempts never binds - measured live at
        // 49 spawn attempts in 75 seconds (Plan 13 Task 8).
        Assert.Equal(TimeSpan.FromSeconds(3), new CoreSupervisorConfiguration().MinimumStableUptime);
    }

    [Fact]
    public void DefaultMaxRestartAttemptsIsFive()
    {
        Assert.Equal(5, new CoreSupervisorConfiguration().MaxRestartAttempts);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd Windows && dotnet test Hades.Supervision.Tests --filter "FullyQualifiedName~CoreSupervisorTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the configuration and state types**

```csharp
namespace Hades.Supervision;

/// <summary>Whether this app started the current core, or found one already running. Adopted is
/// the load-bearing case: the app does not own that core's lifecycle, so quitting must never kill
/// it - and it is NEVER assigned to the Job Object, or kill-on-close would violate that contract
/// on exit (Spec #5 §4, precondition 4).</summary>
public enum Ownership { Adopted, Spawned }

public abstract record SupervisorState
{
    public sealed record NotStarted : SupervisorState;
    public sealed record Starting : SupervisorState;
    public sealed record Running(Ownership Ownership) : SupervisorState;
    public sealed record Restarting(int Attempt) : SupervisorState;
    public sealed record Failed(int Attempts) : SupervisorState;
}

public sealed record CoreSupervisorConfiguration
{
    public string? Home { get; init; }
    public string CoreExecutable { get; init; } = "";
    public string CoreArguments { get; init; } = "";
    public int MaxRestartAttempts { get; init; } = 5;
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PingPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan AdoptionProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a spawned core must stay running before its death is treated as a FRESH
    /// problem (attempt budget resets) rather than a continuation of the already-diagnosed one
    /// (budget keeps depleting). Three seconds is comfortably past any plausible "answered a ping
    /// while already doomed" window (measured at ~100ms) while short enough that a core which
    /// genuinely recovers is not kept on a depleting budget for long.</summary>
    public TimeSpan MinimumStableUptime { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>1s, 2s, 4s, 8s, 16s - doubling, capped at 16s.</summary>
    public static TimeSpan DefaultBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(16, 1 << Math.Max(0, attempt - 1)));
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `cd Windows && dotnet test Hades.Supervision.Tests --filter "FullyQualifiedName~CoreSupervisorTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Implement `CoreSupervisor` itself**

Port `CoreSupervisor.swift`'s `start()` / `refresh()` / `stop()` / `handleCoreProcessExit` faithfully:

- `StartAsync`: `Discovery.Read` + a `/control/ping` probe within `AdoptionProbeTimeout`. If it answers → `Running(Adopted)`, **and no Job Object assignment**. Otherwise spawn.
- Spawn: `new JobObject()` held in a field (precondition 1), `ProcessLauncher.LaunchSuspended`, `job.Assign`, `ProcessLauncher.Resume`, then poll `/control/ping` every `PingPollInterval` up to `PingTimeout`.
- On unexpected exit of a spawned core: if it ran longer than `MinimumStableUptime`, reset the attempt counter; else keep depleting. Then `Restarting(attempt)` with `DefaultBackoff`, up to `MaxRestartAttempts`, then `Failed(attempts)`.
- `StopAsync`: for `Spawned`, the **graceful** sequence first — request exit, wait up to 1s, then terminate. The job is the backstop for the case where the app never got to run this at all. For `Adopted`, do nothing.

- [ ] **Step 6: Build and run the macOS-runnable tests**

Run: `cd Windows && dotnet test`
Expected: PASS for the logic tests; the Job Object tests skip.

- [ ] **Step 7: Commit**

```bash
git add Windows/Hades.Supervision Windows/Hades.Supervision.Tests
git commit -m "feat: port CoreSupervisor to Windows with Job Object ownership"
```

---

### Task 24: Windows CI for the Windows solution — the gate for Slice 3

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Extend the Windows job**

Add a step to `dotnet-tests-windows`:

```yaml
      # The Windows solution is NOT part of Core/Hades.sln (Spec #5 §3) and must be built
      # separately. This is the ONLY place the Job Object path ever executes - not one line of it
      # can run on the development Mac, which is why Spec #5 §9 splits step 3's gate.
      - name: dotnet test (Windows solution)
        working-directory: Windows
        run: dotnet test
```

- [ ] **Step 2: Add the force-kill test — the analogue of `ReaperForceKillTests`**

```csharp
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void AForceKilledParentLeavesNoSurvivingCore()
    {
        // A harness process that creates a job, spawns a long-lived child into it, prints the
        // child's pid, and then waits. Killing the HARNESS (not the child) is what proves the
        // kernel - not any code of ours - cleans up: on SIGKILL-equivalent termination, nothing
        // inside the harness gets to run.
        var harness = Process.Start(new ProcessStartInfo("dotnet",
            $"run --project ../FakeAppHarness") { RedirectStandardOutput = true })!;

        var childPid = int.Parse(harness.StandardOutput.ReadLine()!);
        Assert.False(Process.GetProcessById(childPid).HasExited);

        harness.Kill(entireProcessTree: false);
        Thread.Sleep(1000);

        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childPid));
    }
```

Create `Windows/FakeAppHarness/Program.cs` accordingly — a minimal process that embeds a `JobObject`, launches `cmd.exe /c timeout /t 60` into it, writes the child pid to stdout, and blocks forever. Add it to the solution.

- [ ] **Step 3: Push and watch**

```bash
git add .github/workflows/ci.yml Windows
git commit -m "ci: run the Windows solution's supervision tests"
git push
gh run watch
```

Expected: the Job Object tests, previously skipped on macOS, now **execute and pass**. If `KillsTheChildWhenTheJobHandleCloses` fails, precondition 1 or 2 is violated — check that the `JobObject` is not being finalized early and that no handle leaked to the child.

- [ ] **Step 4: Confirm the gate**

Both CI jobs green:
- `App (.NET) Tests` (macOS) — unchanged, still passing
- `App (.NET) Tests — Windows` — `Core/` suite **and** `Windows/` suite passing

That is the completion gate for Slices 1–3. **Steps 4–6 of Spec #5 begin on the Windows machine and belong to a separate plan.**

---

## Verification checklist

- [ ] `cd Core && dotnet test` passes on macOS
- [ ] `cd Windows && dotnet build` succeeds on macOS
- [ ] `cd Mac/HadesControl && swift test` passes, decoding the generated corpus
- [ ] Both CI jobs green
- [ ] `dotnet run --project Core/src/Hades.Cli -- status` prints what it printed before Task 18
- [ ] The boundary guard demonstrably fails the build for `Hades.Shell`-tree projects **and** `Hades.Cli`
- [ ] Skip counts, not inflated pass counts, are reported for machine-dependent tests
- [ ] No `net10.0-windows` project appears in `Core/Hades.sln`
