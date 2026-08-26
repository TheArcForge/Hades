# Hades Windows Support — Design (Spec #5)

**Status:** Design approved (revision 2, post-review), pending implementation plan
**Date:** 2026-08-23 · **Revised:** 2026-08-23 after a four-topic review pass
**Parent:** `2026-08-01-hades-standalone-overview-design.md`
**Siblings:** `2026-08-01-hades-mac-shell-design.md` (Spec #3), `2026-08-01-hades-distribution-design.md` (Spec #4)

**Goal:** Ship Hades on Windows at parity with `Hades.app` — minus the §7 inapplicables, which are things with no Windows referent rather than things cut for cost — **plus** a first-class headless CLI path on both platforms, without the Windows shell becoming a second decision-maker.

**Tech stack:** WPF (`net10.0-windows`, `UseWPF` + `UseWindowsForms`). Client of the core's control API. Spawns and supervises the core inside a Job Object.

Spec #3 §1 already anticipated this document:

> A future Windows shell becomes a view layer against an unchanged API rather than a re-derivation of behaviour.

This spec is the attempt to cash that cheque. §2 is about the one thing that could stop it, and §4 is the part the review pass proved was the most dangerous.

### Revision 2 — what the review changed

A four-topic review (general, correctness, architecture, long-term support) found the spec's *measured* claims sound — every probe was independently re-run and matched — and the *reasoned* ones weak. Material changes, all incorporated below:

| Change | §  | Why |
|---|---|---|
| Job Object preconditions stated; spawn→assign race, handle lifetime, assignment failure, adopted cores | §4 | The guarantee shipped with none of its preconditions. Revision 1 deleted the reaper on the grounds that "the OS handles it" — the exact assumption the reaper exists because macOS violated |
| Ship **both** win-x64 and win-arm64 | §4.1 | Revision 1's "no architecture gate needed" was **wrong** on Windows 10 ARM64: x64 emulation is Windows 11 only. A native arm64 publish was then verified free |
| Tray menu contents enumerated; lease Release given a home | §5.2 | Revision 1 specified the divergence and not the replacement, silently downgrading net #7 of the reload-safety design |
| Launch-at-login reads **two** registry locations | §5.3 | Revision 1 claimed the macOS discipline "ports verbatim". It does not — `StartupApproved\Run` is a second source of truth |
| Atomic create-with-DACL named explicitly | §6.2 | The Unix side creates at 0600 *in the same syscall as the inode*. A naive Windows port reintroduces the race it paid to avoid |
| Golden JSON fixtures added, sourced from the server suite and shared with Swift | §2.2 | Reflection cannot see serializer options, routes, or semantics |
| `Hades.Control.Client` placed in `Core/src`, not `Windows/` | §3 | Revision 1 contradicted itself: it claimed `Core/` was untouched while moving `Hades.Cli` onto a client in the Windows tree |
| "No direct DB / no direct MCP" non-goal restored | §7 | Revision 1 silently dropped it from Spec #3 §6 while claiming a *stronger* boundary |
| Headless CLI promoted to a product surface | §5.4 | Decision B: full parity **and** headless |
| Unsigned position recorded as dated debt | §8.2 | "Symmetric with macOS" is false: macOS unsigned is a stable plateau, Windows unsigned is a slow decline |

One review finding was **rejected on evidence.** The architecture review argued the conformance test is unworkable because "many server records have no `JsonPropertyName`", citing `ProjectSnapshot`. Every `public sealed record` under `Control/` was scripted: exactly six have unattributed properties — `EditorStateSnapshot`, `ProjectStateSnapshot`, `SettingsSnapshot`, `ProjectSnapshot`, `TraceRecordSnapshot`, `OperationRecord` — and **none of the six is a wire type**. They are the plain-data inputs to each endpoint's `Resolve`, plus the operations registry record whose wire counterpart `OperationResult` is separately defined and fully attributed. (They *are* declared `public` — for testability — so "not a wire type" is a statement about their role, not their C# accessibility; §2.2 carve-out 3 gives the mechanical rule that separates them, precisely because a public-type walk would otherwise pick them up.) Every actual wire record is 100% attribute-pinned, which is what makes §2.2 viable. The finding's *secondary* points were correct and are incorporated.

---

## 1. What the research established

Every row measured on the development Mac on 2026-08-23, and independently re-run during the review pass.

| Question | Answer | Evidence |
|---|---|---|
| Does the .NET core build for Windows today? | **Yes, unmodified** | `dotnet publish -c Release -r win-x64 --self-contained true` → `Hades.Server.exe` + `e_sqlite3.dll`, 367 files, 128 MB, 6.6s |
| For ARM64 too? | **Yes, also unmodified** | Same command with `-r win-arm64` → `Hades.Server.exe` + a **native arm64** `e_sqlite3.dll`. SQLitePCLRaw ships win-arm64 |
| Why is it already clean? | The CA1416 platform-compatibility analyzer is on by default for the platform-neutral `net10.0` TFM; `TreatWarningsAsErrors=true` is what makes its findings *fail the build* | Hence the existing `OperatingSystem.IsWindows()` guards around `File.SetUnixFileMode` |
| Can WPF be built on macOS? | **Yes**, with `EnableWindowsTargeting=true` | Probe compiled XAML, `UseWPF` + `UseWindowsForms` together, and self-contained-published a `probe.exe` |
| Can WinUI 3 be built on macOS? | **No** | `XamlCompiler.exe: cannot execute binary file` / `error MSB3073 ... exited with code 126`. Windows-only .NET Framework 4.7.2 binary |
| Is WPF Fluent theming usable? | Yes, but **evaluation-gated** | `error WPF0001: 'ThemeMode' is for evaluation purposes only`. Builds with `WPF0001` suppressed. See §5.5 for the decision this forces |
| Can an MSI be built on macOS? | **No** | `warning WIX0000: The WiX Toolset only supports Windows ... All behavior after this point is undefined` |
| Does WiX cost money? | **No, not for Hades** | OSMF EULA §1: fee applies only to revenue-generating users with annual gross revenue ≥ US$10,000. Accept once via `wix eula` |
| Does the §2 boundary guard work? | **Yes** | Two-project probe: builds clean with a benign reference *and* with no `ProjectReference` at all; fails with the exact intended message when `Hades.Core` is referenced |

The consequence that shaped everything else: **WinUI 3 would have forced 100% of shell development onto the Windows machine.** WPF preserves the intended workflow — build and unit-test on the Mac, run and debug on Windows.

**Recorded non-decision (so it can be revisited honestly):** Avalonia was not evaluated. It builds *and runs* on macOS and would keep a future single-shell consolidation open, at the cost of being a third-party UI framework and of a look that is cross-platform-native rather than genuinely Windows-native. It was excluded by the "best approach per platform, standalone each" decision, not by measurement. If the two-shell tax in §9.1 is ever actually felt, this is the first assumption to re-open.

## 2. The rule, and how it survives being written in C#

**The shell renders, the core decides.** Identical to Spec #3 §1.

On macOS the rule is *mostly* enforced by language: Swift cannot reference `Hades.Core`. On Windows the shell is also .NET, and a single `ProjectReference` would collapse the architecture — permanently, and in the direction that is easier in the moment.

But a `ProjectReference` is only one of the ways this erodes, and Spec #3 knew it: Swift can open SQLite too, which is why Spec #3 §6 carries a non-goal (*"Embedding the graph or trace databases. The shell reads through the control API only"*) and §5 a review gate (*"any behaviour in Swift that is not view state is a defect"*). **Revision 1 dropped both while claiming a stronger mechanism.** Both are restored — §7 and §9 respectively — and the mechanism is layered rather than singular:

**Layer 1 — build-time reference guard.** In `Windows/Directory.Build.targets` (not one `.csproj`, so removing it is a loud, separate diff, and so it covers every project in the tree):

```xml
<Target Name="EnsureShellIsAClient" BeforeTargets="Build">
  <Error Condition="@(ProjectReference->AnyHaveMetadataValue('Filename', 'Hades.Core'))
                 or @(ProjectReference->AnyHaveMetadataValue('Filename', 'Hades.Server'))
                 or @(Reference->AnyHaveMetadataValue('Filename', 'Hades.Core'))
                 or @(Reference->AnyHaveMetadataValue('Filename', 'Hades.Server'))"
         Text="Hades.Shell must not reference Hades.Core or Hades.Server. It is a control-API client by design." />
</Target>
```

**Verified empirically (2026-08-23), not assumed from MSBuild's documentation:** a two-project probe built clean without the reference, built clean with *no* `ProjectReference` items at all (an empty item list does not break the condition), and failed with exactly `error : Hades.Shell must not reference Hades.Core or Hades.Server. It is a control-API client by design.` when the reference was added. The `Reference` clauses above extend the tested syntax to `HintPath`-style references.

**The guard covers three projects, not one — including `Hades.Cli`, which lives in `Core/src`.** §2.1 promotes the CLI to a client of the same API, so it falls under the same rule; but `Windows/Directory.Build.targets` cannot reach `Core/src`. The identical target is therefore declared in **`Core/src/Hades.Cli/Hades.Cli.csproj`** as well. It is deliberately *not* put in a `Core/`-wide `Directory.Build.props`, which would break `Hades.Server` — that project legitimately references `Hades.Core`.

**Layer 2 — artifact-level architecture test.** A test loads the built **`Hades.Shell.dll`, `Hades.Control.Client.dll`, and `Hades.Cli.dll`** and asserts their reference closure contains no `Hades.Core`, `Hades.Server`, or SQLite provider.

**Corrected 2026-08-26 — both layers behave differently from what this section originally claimed, measured by deliberately violating each:**

- Layer 1 is **stronger** than assumed. SDK-style MSBuild expands `@(ProjectReference)` to the full **transitive** project closure by `Build` time, so a reference introduced two projects away still trips it. The original claim that item checks are "structurally blind to transitive references" is false.
- Layer 2 is **narrower** than assumed. Roslyn strips an assembly reference from the compiled `AssemblyRef` table entirely when no code uses a type from it, so a *declared but unused* reference is invisible to any metadata-based check. Layer 2 sees references that are actually **used**; Layer 1 sees references that are **declared**.

That is still a genuine layering — each catches what the other cannot — but for the opposite reasons to the ones first written down. Layer 2 earns its keep on `HintPath`/`Reference` forms, on a renamed project, and as the check that survives someone deleting a `Target` from a csproj. It must load via **`MetadataLoadContext`**, not `Assembly.Load`: this test runs on the macOS CI leg (§9 step 2), where loading a `net10.0-windows` assembly for execution would pull WPF and fail for the wrong reason.

**Layer 3 — banned APIs.** `Microsoft.CodeAnalysis.BannedApiAnalyzers` forbids `Assembly.LoadFrom`/`AppDomain` load APIs and `SqliteConnection` in **the shell, the client, and the CLI**. This matters concretely because §8.1 ships `core\` *inside the shell's own install directory*: `Assembly.LoadFrom("core\\Hades.Core.dll")` is a one-liner MSBuild can never see.

**Layer 4 — the restored review gate.** §9's checklist carries Spec #3 §5's rule verbatim: any behaviour in the shell or client that is not view state is a defect. No mechanism catches logic *drift*; only review does.

### 2.1 Placement, and the `Hades.Cli` bridge

`Hades.Control.Client` is **platform-neutral (`net10.0`) client code consumed by a macOS-shipped CLI.** It is not Windows code and does not live in `Windows/`. It lives at `Core/src/Hades.Control.Client/`, inside `Core/Hades.sln`; `Windows/HadesWindows.sln` references it and nothing else from `Core/`.

`Hades.Cli` today references `Hades.Core`, justified in-source as "AppPaths only", for discovery. Once `Discovery` moves into the client — which it must, since Swift's `Discovery.swift` does exactly this job client-side — that justification evaporates. **The reference is dropped and the CLI comes under the same client-only guard**, leaving `Hades.Control.Client.Tests` as the single sanctioned dual-referencing project, stated as such in its `.csproj`.

### 2.2 DTOs: duplicate, prove by reflection, and pin with golden bytes

There are 58 public wire records/enums under `Core/src/Hades.Server/Control/` (45 excluding migration). Swift duplicates all of them in `DTOs.swift` (1,001 lines) because it has no alternative. `Hades.Control.Client` duplicates them too — but unlike Swift, it can be held to account, in two complementary ways.

**Reflection conformance test.** `Hades.Control.Client.Tests` references both `Hades.Server` and the client, walks **the server's type list** (not the client's — so a new server DTO with no client twin *fails* rather than passing silently), and asserts `JsonPropertyName`, nullability, and required-ness agree field for field. Three explicit, commented carve-outs:

1. **Migration types** (§7), from the named exclusion artifact.
2. **The client's deliberate `unknown` enum case**, which every client enum gets by construction via a shared converter, never per-enum opt-in — the one someone forgets is the one that crashes.
3. **Non-wire types that are nonetheless `public`.** Six records under `Control/` are declared `public sealed record` but never reach the wire: `EditorStateSnapshot`, `ProjectStateSnapshot`, `SettingsSnapshot`, `ProjectSnapshot`, `TraceRecordSnapshot`, and `OperationRecord`. They are the plain-data *inputs* to each endpoint's `Resolve`, and the operations registry's own record — public for testability, not for serialization. A walk over public types will pick them up, so the exclusion must be **mechanical, not a judgement call**: a type participates in the walk only if **at least one of its properties carries `[JsonPropertyName]`**. Every genuine wire record is 100% attribute-pinned (verified: these six are the *only* records under `Control/` with unattributed properties), and every one of these six has zero attributes, so the rule separates them cleanly with no hand-maintained list to rot. The rule is asserted in the test itself — if a future wire record ships unattributed, the walk skips it silently, so the test also fails when a type has *some* but not all properties attributed.

**Golden JSON fixtures.** Reflection cannot see the wire, only the types. `ControlListener.cs:180` sets `DefaultIgnoreCondition = WhenWritingNull`, so a nullable field is **absent**, not `null` — invisible to any reflection test. So the server suite serializes one exemplar of every wire DTO through the **real listener options** into checked-in fixture files, and **both** `Hades.Control.Client.Tests` *and* the Swift `HadesControl` tests decode the same files.

**This upgrades an existing mechanism; it does not create one, and an earlier draft of this spec wrongly claimed otherwise.** `Mac/HadesControl/Tests/HadesControlTests/` already carries **50 fixtures and 44 decode tests covering 25 DTO types**, captured off a real running `Hades.Server` — including a test asserting that `SummaryResult`'s optional lease field is *genuinely absent* rather than null, which is precisely the `WhenWritingNull` behaviour named above. The Swift copy is already well tested. Two things are actually new:

1. **Generated, not captured.** Today's fixtures were produced once by a documented manual procedure (Plan 12 Task 1). Regenerating them on every server-suite run means a DTO change cannot leave a stale fixture silently passing.
2. **One set, both clients.** The .NET client decodes byte-identical inputs rather than its own parallel corpus, so "the two clients agree" is tested rather than assumed.

**Disposition of the existing fixtures — corrected 2026-08-26, after the attempt failed.** An earlier revision of this section said the generated set should **replace** the Swift corpus, with the 44 Swift tests "repointed at it". That was wrong, and trying it proved why: 43 of the 44 broke. The two corpora do not differ by naming, they differ by **purpose**.

- The Swift fixtures were **captured from a real running server**, and its 44 tests assert on *semantic content* — `callCount == 7`, `port 9999`, a UUID-shaped `operationId`, specific remedy text.
- The generated exemplars are **synthetic**, and pin *wire shape* — absent-vs-null under `WhenWritingNull`, property names, casing.

Neither substitutes for the other, so **both corpora stay**, each owning what it is good at:

| Corpus | Pins | Consumers |
|---|---|---|
| Generated, `Core/tests/Fixtures/control-api/` | Wire shape — what reflection structurally cannot see | `Hades.Control.Client.Tests`, plus a small Swift shape test |
| Hand-captured, `Mac/HadesControl/Tests/HadesControlTests/Fixtures/` | Semantic content from a real server | The existing 44 Swift decode tests, unchanged |

The "both clients decode identical bytes" guarantee is still obtained — by **adding** a compact Swift test that decodes the generated corpus and asserts it parses, never by rewriting value-asserting tests to accept synthetic data. The general lesson, worth keeping: a fixture that pins shape and a fixture that pins meaning are different artifacts, and conflating them destroys the second.

**What neither catches, stated so nobody assumes otherwise:** routes, HTTP verbs, query-parameter names, server-owned defaults (the Swift client deliberately omits `limit` so the route default stays the single source of truth), the `{"error": …}` body shape, the 401→`staleToken` contract, units, and the semantics of `success: false`. Those live in review and in `ControlClient.swift`'s doc comments, which the .NET client should cite rather than paraphrase.

**The rejected alternative**, recorded so it can be re-opened: moving the 45 wire records into a shared `Hades.Control.Contract` referenced by server and both .NET clients would eliminate .NET-side drift *by construction*, and is the pattern `Hades.Contract` already uses for the Unity wire. It was rejected as too invasive for working, shipped, macOS-critical code. **If the conformance test ever becomes burdensome, this is the first decision to revisit** — and the fixtures survive either way.

## 3. Layout

```
Core/
  src/Hades.Control.Client/        NEW — net10.0, the .NET twin of Swift's HadesControl
  src/Hades.Cli/                   loses its Hades.Core reference; becomes a product surface (§5.4)
  src/Hades.Server/                three OS-branched areas (§6)
  tests/Hades.Control.Client.Tests/  NEW — the ONLY sanctioned dual-referencing project (§2.1)
  tests/Fixtures/control-api/        NEW — generated golden JSON (§2.2); consumed by the Swift
                                     tests too, which reach it by relative path
  tests/Hades.Control.Client.Tests/ClientCoverage.cs
                                     NEW — the named exclusion artifact (§7): migration types,
                                     shared by the conformance carve-out and the fixture manifest
Mac/                               untouched
Windows/                           NEW — HadesWindows.sln
  Directory.Build.targets            the §2 layer-1 guard
  Hades.Shell/                       net10.0-windows, WPF
  Hades.Supervision/                 net10.0-windows, the Job Object supervisor (§4)
  Hades.Shell.Tests/
  Hades.Supervision.Tests/
UnityPlugin/                       one Windows branch (§6.3)
```

`Windows/` keeps its own solution so that no `net10.0-windows` project ever enters `Core/Hades.sln` — which is what keeps the existing macOS CI job unaffected. Note precisely what that does and does not claim: `Core/Hades.sln` *does* gain `Hades.Control.Client` (platform-neutral, builds everywhere, no `EnableWindowsTargeting` needed). The isolation is about the Windows-only TFM, not about the tree.

Supervision is its own project, mirroring `Mac/HadesSupervision` — and because its tests can only execute on Windows, that boundary is also what lets §9 state honestly which gates run where.

## 4. Supervision

**`HadesCoreReaper` has no Windows counterpart and is not ported.** Its purpose — guarantee a spawned core never outlives the app, even when the app is killed without running cleanup code — is a kernel primitive here.

**The guarantee is real but conditional, and every condition below is load-bearing.** Revision 1 stated it unconditionally. That is the same shape of assumption — "the OS will clean up" — that the reaper exists on macOS because it turned out to have fine print.

**Mechanism:** `CreateJobObject` → `SetInformationJobObject` with `JOBOBJECT_EXTENDED_LIMIT_INFORMATION.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` → spawn → `AssignProcessToJobObject`. When the **last handle** to the job closes, the kernel terminates every process in it.

**Precondition 1 — close the spawn→assign window.** Between spawn and assign: if the shell dies, the core is orphaned; and any process the child spawns *before* assignment is never captured, because assignment does not retroactively adopt existing descendants. The Debug path is the worst case — `dotnet run` forks the real `Hades.Server` plus compiler-server nodes. The fix is `CREATE_SUSPENDED` → `AssignProcessToJobObject` → `ResumeThread`, which **cannot be expressed with `System.Diagnostics.Process`** and requires P/Invoking `CreateProcess`. The implementation plan must do this, not `Process.Start`.

**Precondition 2 — handle lifetime, which cuts both ways.** The shell must keep the job `SafeHandle` rooted for its entire lifetime: a handle eligible for finalization kills a *healthy* core mid-session. Conversely, any *other* surviving handle to the job voids the guarantee entirely — a leaked handle makes the core outlive its own death sentence.

Because Precondition 1 replaces `Process.Start` with a raw `CreateProcess` P/Invoke, **the implementer now owns `bInheritHandles` directly**, and redirecting the core's stdio requires passing `TRUE` with inheritable pipe ends. Blanket inheritance would hand the child every inheritable handle in the process, the job handle potentially among them. Two requirements follow: the job handle is created non-inheritable (`CreateJobObject(NULL, …)`'s default, which must not be overridden), and inheritance is scoped to exactly the pipe handles via `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` in a `STARTUPINFOEX`. The force-kill test catches a leak only indirectly, so this is stated as a construction requirement rather than left to the test.

**Precondition 3 — assignment can fail.** `AssignProcessToJobObject` can return `ERROR_ACCESS_DENIED` even on Windows 8+ where job-hierarchy rules are not satisfiable (some sandboxes, silo/container hosts, corporate launcher wrappers). Nested jobs exist since Windows 8, but "not a hazard" is not absolute. **Stated behaviour: fail loudly and refuse to spawn**, surfacing the condition in the shell rather than running an unsupervised core that can outlive its parent.

**Precondition 4 — adopted cores are never assigned to the job.** Otherwise `Ownership.adopted`'s "never killed by `stop()`" contract is violated on shell exit via kill-on-close.

**The job is the force-quit backstop, not the shutdown path.** Job close is `TerminateProcess` — abrupt — where the Mac reaper does TERM, wait 1s, KILL. `stop()` performs the graceful sequence itself; the job only ever fires when the app did not get to run.

**Everything else in `CoreSupervisor` ports faithfully:** adopt-or-spawn against `/control/ping`; 1/2/4/8/16s capped backoff; `maxRestartAttempts` of 5; and the 3-second `minimumStableUptime`. That last value is not a preference — it encodes a measured bug (Plan 13 Task 8): without it, a core answering one ping then dying got a fresh 5-attempt budget on every death, so the cap never bound. **49 spawn attempts in 75 seconds, observed live.** The `FakeCore` fixture ports with it.

**Tests this section requires**, beyond the ported suite: the graceful-`stop()` path is graceful; the job handle survives GC pressure for the process lifetime; a force-killed harness leaves no surviving core (the analogue of `ReaperForceKillTests`); assignment failure fails loudly; an adopted core survives `stop()`.

### 4.1 Core embedding and architectures

Release publishes the core self-contained into `<install>\core\`, the analogue of `Contents/Resources/HadesServer/`. Debug falls back to `dotnet run --project <repo>/Core/src/Hades.Server --no-launch-profile`, logging loudly that it did, exactly as `AppDelegate.makeConfiguration` does.

**Both `win-x64` and `win-arm64` ship.** Revision 1 claimed no architecture gate was needed because Windows-on-ARM emulates x64. That is **false on Windows 10 ARM64, which emulates x86 only — x64 emulation is Windows 11 and later.** Shipping both RIDs is cheaper than reasoning about emulation, and it also removes the unmeasured emulation-performance question for a long-running Roslyn/SQLite indexing workload — precisely the class emulation penalises, in a product that spent a whole sibling spec on felt latency.

**Stated at the right strength:** what §1 verified is that `dotnet publish -r win-arm64 --self-contained` *succeeds on the Mac* and emits a native arm64 `e_sqlite3.dll` — files on disk, nothing more. **No arm64 binary produced by this project has ever been executed.** Under this project's own evidence discipline that is a publish result, not a runtime result, and it stays labelled that way until step 6 runs it on real ARM64 hardware. If no ARM64 Windows machine is available, the honest options are to ship the arm64 MSI labelled untested, or to ship x64 only and let Windows 11 emulate — **not** to describe an unexecuted binary as verified.

**Packaging consequence:** an MSI carries one architecture. This ships **two MSIs**, `Hades-<version>-x64.msi` and `Hades-<version>-arm64.msi`, each embedding only its own `core\`. Both are built and attached by the same `windows-latest` release job (§8.3), and both carry the same version, so §8.3's lockstep script covers them as two more sites rather than as a special case.

**Supported-OS statement, corrected.** .NET 10's supported-OS list covers Windows 11, Windows Server, and Windows 10 **Enterprise/IoT/LTSC editions only** (1607/1809/21H2) — *not* consumer Windows 10 Home/Pro 22H2. Revision 1's "Windows 10 and 11" read far broader than the list it cited.

The MSI enforces what it actually can: a `LaunchCondition` on `VersionNT`/build number. **Edition is not expressible there**, so the Enterprise/LTSC nuance this paragraph just corrected cannot be enforced by the installer — it is a documented support statement (§10), not a gate. An unsupported Windows 10 edition will install and then fail at runtime in whatever way .NET fails; that is the honest consequence of the distinction and should not be described as prevented.

## 5. The shell

### 5.1 Surfaces

| Mac | Windows | Note |
|---|---|---|
| `NSStatusItem` + `NSPopover` (SwiftUI) | Tray `NotifyIcon` + context menu; main window on double-click | §5.2 |
| Main window: Projects / Charon / Asphodel + Settings | Same three sections + Settings | Sidebar `ListBox` + `ContentControl` |
| Onboarding: install → permissions → claudeCode → projects → unityPlugin | install → claudeCode → projects → unityPlugin | §7 drops `permissions`. **`OnboardingInstallStepView`'s copy hardcodes "five steps, and you can stop after the fourth"** — Swift-authored, not API-served, so the Windows copy must be reworded, not ported |
| `StatusIcon` SF Symbols | Six tray `.ico` files + a Segoe Fluent Icons glyph map | `idle`/`indexing`/`attached`/`leaseHeld`/`error`/`unknown` for the tray; `StatusIcon` carries **four further symbol vocabularies** (row severity, operation state, trace outcome) used across the main window, which need Windows equivalents too |
| `NSOpenPanel` behind `DirectoryPicking` | Folder dialog behind the same protocol shape | The seam isolates the untestable part; it does not make it testable |

**Window close semantics** (unspecified in revision 1, and it interacts with §4): closing the main window **hides** it; only tray **Exit** ends the process — and with it, a spawned core. A naive WPF `MainWindow` close would exit the app, closing the job handle and killing the core mid-index.

**Single instance:** a named mutex; a second launch **activates the existing window** rather than exiting silently.

### 5.2 The tray menu — what replaces the popover

The Mac popover is not a summary; it is the densest safety surface in the shell. Revision 1 replaced it with "a context menu" and never said what was on the menu, which quietly downgraded net #7 of the reload-safety design (Spec #3 §3.1: the lease indicator is *"deliberately prominent… a user must never be confused about why their code stopped compiling"*). A `leaseHeld` icon in a tray that Windows hides behind the overflow chevron by default is a state, not an affordance.

The context menu mirrors `MenuBarContentView` item for item, top to bottom:

1. **Held lease line + `Release`** when a lease is held, pinned at the top, the item disabled unless `lease.releasable` — the direct port of the Mac's `Button("Release")` / `.disabled(!lease.releasable)`.
2. **Supervision state** when not running: `notRunning`, `restarting (attempt N)`, or `failed` rendering the Mac's `"Gave up after N attempts."`
3. **Per-project rows** keyed by `productGuid`, with the core-decided status text rendered verbatim.
4. **Ownership footer**, verbatim from `SupervisionFooterView`: *"Adopted — quitting Hades leaves it running"* / *"Started by this app — quitting stops it"*.
5. **Open Hades** · **Quit Hades**.

Additionally, because a tray icon can be hidden: **a balloon/toast notification when a held lease passes the warning threshold.** The threshold is not a new number invented here — it is the plugin-side value the Mac surface already keys its warning off, and the shell must read it from the same source rather than hard-coding a second copy. This is named here as net #7's port so it cannot later be triaged as polish.

### 5.3 `ShellFacts` divergence

Spec #3 §1's carve-out — *an OS fact about the shell's own process or machine is the shell's* — applies unchanged, with a different OS underneath.

| Fact | Mac | Windows |
|---|---|---|
| Launch at login | `SMAppService.mainApp` | `HKCU\…\CurrentVersion\Run` **plus** `HKCU\…\Explorer\StartupApproved\Run` |
| Low Power Mode | `ProcessInfo.isLowPowerModeEnabled` | `GetSystemPowerStatus` → `SystemStatusFlag` |
| Thermal state | `ProcessInfo.thermalState` | **No analogue — dropped** |

**Launch-at-login has two sources of truth on Windows, and revision 1 got this wrong.** Windows records the *user's* enable/disable decision (made in Task Manager or Settings → Startup Apps) in `StartupApproved\Run`, **not** by removing the Run value. So an app that writes the Run value and re-reads only the Run value will report launch-at-login "on" forever while Windows never launches it. The Mac's italicised discipline — *write, then re-read the OS, and report only what the re-read says* — therefore does **not** port verbatim.

**Read:** enabled only when the Run value exists **and** `StartupApproved\Run` either has no entry for it or holds an enabled state.

**Write, which revision 1 left unspecified:** enabling writes the Run value *and* **deletes** any `StartupApproved\Run` entry for it, rather than trying to author that entry's state bytes — the blob's format is undocumented, and deleting it returns the app to the OS's own default-enabled path, which is the honest way to say "the user has just re-enabled this from inside the app." Disabling removes the Run value and likewise leaves no `StartupApproved` entry behind. Either way the result is re-read from both locations before anything is displayed, so a write the OS declines to honour can never render as success.

**Battery saver is display-only in Settings.** That is the decision, not a fork: it matches how the Mac shell treats Low Power Mode, it is one row rendering one OS value, and nothing else in the app consumes it. The precedent that `launchAtLogin` and `resourceGuards` were removed from the .NET side as hollow constants (Plan 13 Task 7) is what keeps this shell-side rather than a control-API field.

Windows 11 24H2 replaced battery saver with "energy saver", which users can enable while plugged in, including on desktops; whether `SystemStatusFlag` reflects that state is exactly the kind of "should" this project refuses to trust, and it joins the §9 step-5 measurement list. If it turns out not to reflect it, the row is dropped rather than shown wrong.

### 5.4 The headless CLI is now a product surface

`Hades.Cli`'s own header currently reads *"NOT a product deliverable: its purpose is diagnostic."* **That status changes.** Windows ships full parity *and* a supported headless path, and the CLI is that path — on both platforms, since nothing about it is Windows-specific.

It gains, on top of today's `status` / `projects` / `release <leaseId>`: `add-project <path>`, `remove-project`, `rebuild`, `install-plugin`, `traces`, `memory`, and **`diagnose`** — each a thin call against an existing control-API route, holding the existing "deliberately dumb" rule (no logic, render what the core decided).

**`hades diagnose` is load-bearing, not a nicety.** §9.1's mitigation for the entire class of environmental failures CI cannot reach — OneDrive placeholders, antivirus locking, long paths, non-default Hub locations — is that the maintainer can ask a reporter to run one command. It exports an environment and log bundle: OS build and edition, architecture and whether the process is emulated, the resolved storage root, core version and ownership, per-project paths and index state, recent log tail, and the results of the same checks §9's hand-run list covers. For a solo maintainer who cannot reproduce these conditions, this is worth more than additional tests, so it ships in the same step as the rest of the CLI surface rather than being deferred.

**`hades serve` runs the core in the foreground of the calling terminal and exits with it.** The CLI deliberately does **not** offer a supervised `start`: supervision is the shell's job, and a CLI that spawned a detached, unsupervised core would violate the "no hanging state" rule outright. This composes correctly with what already exists — a core started by `hades serve` is simply **adopted** by the shell if one is launched later, which is exactly what `Ownership.adopted` was built for.

**Delivery, on both platforms.** On Windows the MSI puts the install directory on the user's `PATH` (per-user `Environment` element) so `hades` is callable from any terminal, and `hades serve` finds the core at `<install>\core\Hades.Server.exe`.

macOS needs an answer too, and revision 1 promised "both platforms" without giving one. `hades` is published alongside the core into `Hades.app/Contents/Resources/`, and **`install.sh` symlinks it into `/usr/local/bin`** — the one part of the macOS install that touches anything outside `/Applications`, so it is done only when that directory already exists and is writable, and `uninstall.sh` removes the symlink. If it cannot be created, the installer says so and prints the full path rather than failing. `hades serve` resolves the core relative to its own location (`Contents/Resources/HadesServer/Hades.Server`), falling back to `dotnet run` against a source checkout exactly as the shell's Debug path does.

This is a change to shipped macOS code, and it is the only one in this spec.

### 5.5 Fluent theming — decided

`ThemeMode` is evaluation-gated (§1) and .NET 11 previews show no stabilisation work on it. **Decision: Fluent is garnish, not identity.** The shell must look acceptable under the default theme; `ThemeMode` is applied where it helps and the app is never dependent on it.

The suppression is applied at the narrowest possible scope — a `#pragma` at the call site, **not** a project-wide `<NoWarn>WPF0001</NoWarn>`. Revision 1 specified the project-wide form, which would also silence *future* experimental APIs nobody chose to adopt, and which breaches this codebase's own established standard of suppressing at the single call site.

**This forces one implementation constraint worth stating:** `ThemeMode` is set **in code, not in XAML.** Set as a XAML attribute on `Application`, the WPF0001 diagnostic is raised inside the generated `.g.cs`, where no `#pragma` of ours can live — which would leave the project-wide `NoWarn` as the only option, defeating the decision above.

## 6. Changes inside shared code

### 6.1 `ProjectsEndpoint`

- `UnityHubEditorExecutablePath` gains a Windows branch: `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe`. Still the Hub's documented default, not real Hub discovery — out of scope exactly as on macOS. Worth noting the cost is *higher* here: Windows users relocate editors to other drives far more often than Mac users move `/Applications`.
- `RevealInFinder` **keeps its route name.** Renaming `/control/projects/{id}/revealInFinder` would break the shipped Swift client for a cosmetic gain. Only the implementation branches, to `explorer.exe /select,<path>`. **Policy, not precedent** (§9.2): route verbs stay platform-neutral in *name*; platform-specific behaviour lives in the implementation.

### 6.2 Token file protection — atomically, or not at all

Today `ControlAuth.WriteConnectionFile` and `EditorListener` create their token files at mode 0600 **in the same syscall that creates the inode** (`FileStreamOptions.UnixCreateMode`), with a doc comment stating plainly that write-then-chmod is deliberately avoided so the token is never briefly readable. The Windows branch is bare `File.WriteAllText` — no protection at all, which was correct while Windows was unsupported and is a real gap the moment it ships. These tokens authorise project mutations by any local process that can read them.

**A create-then-`SetAccessControl` port would reintroduce exactly the race the Unix side paid to avoid.** The atomic equivalent is in-box and requires no extra package:

```csharp
var security = new FileSecurity();
security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
security.AddAccessRule(new FileSystemAccessRule(
    WindowsIdentity.GetCurrent().User!,
    FileSystemRights.Read | FileSystemRights.Write,
    AccessControlType.Allow));

using var stream = new FileInfo(path).Create(
    FileMode.Create, FileSystemRights.Write, FileShare.None,
    bufferSize: 4096, FileOptions.None, security);
```

`SetAccessRuleProtection(true, false)` is the load-bearing call, not boilerplate: without it the directory's inherited ACEs come along and the explicit DACL adds nothing at all. The rule grants the current user's SID only — read/write, matching 0600's intent. This was compiled during review on the platform-neutral `net10.0` TFM under `TreatWarningsAsErrors` behind a `[SupportedOSPlatform("windows")]` guard: **builds clean, no package reference needed.**

Stated honestly: like `root` on Unix, Administrators can always read the file, and under default Windows ACLs other standard users already cannot reach files in the profile. The DACL hardens non-default setups; it is not what creates the boundary.

### 6.3 The Unity plugin — unproven until measured

`UnityPlugin/Assets/Hades/Transport/HadesConnectionFile.cs` needs a Windows arm. It must stay C# 9 (Unity 6000.3's ceiling) and is embedded as a resource in `Hades.Core.dll`, so changing it changes the core build.

**This file exists because of exactly the hazard the Windows branch reintroduces.** Its doc comment records that Unity's Mono resolves `SpecialFolder.ApplicationData` to `~/.config` while .NET 10 resolves it to `~/Library/Application Support` — same machine, same enum, different answer. On Windows both *should* resolve to `%APPDATA%` (Roaming). "Should" is the word that already cost this project once.

**The branch is not trusted until verified on real hardware** (§9 step 5): a throwaway Editor script prints what Unity's runtime resolves, compared against the path the core actually wrote.

### 6.4 The Windows storage root — decided now, not deferred

`AppPaths.DefaultRoot()` uses `SpecialFolder.ApplicationData`, which on Windows is **Roaming** `%APPDATA%`. That is wrong here, and it cannot be left to a later measurement: §9 step 1 runs `AppPaths` as-is, every later step accretes databases under whatever it returns, and changing it after ship is a data-relocation break. A measurement could not settle it anyway — roaming-profile sync behaviour is not observable on a non-domain-joined developer machine.

**Decision: on Windows the root is `SpecialFolder.LocalApplicationData` — `%LOCALAPPDATA%\Hades`.** Everything under it is either derived and rebuildable (`graph.db`, `traces.db`, `memory-index.db`) or machine-local by nature (`control.token`, `editor.token`, whose ports are meaningless on another machine). None of it should follow a user between machines, and a roaming profile that silently syncs a multi-hundred-megabyte graph is a support incident waiting to happen. macOS is unchanged.

The one authored, irreplaceable thing Hades owns — `memory/*.md` — already lives in the user's own repository under `.arcforge/`, not here, so this decision does not put anything precious in a machine-local directory.

`HadesConnectionFile`'s Windows arm anchors on the same folder, and **step 5's measurement is demoted accordingly**: it verifies that Unity's Mono and .NET 10 agree on the *decided* path, rather than choosing the path.

## 7. Non-goals

| Excluded | Why |
|---|---|
| **Embedding the graph or trace databases; direct MCP calls to 7823.** The shell and CLI read through the control API only | Restored from Spec #3 §6, which revision 1 dropped. The schema is drop-and-recreate on version bump, so a client reading it directly is also a time bomb |
| v1.2 migration (`MigrationEndpoint` UI, `V12Detector`/`V12Importer`/`V12Cleanup`, `LiveMigrationOffering`, the onboarding migration offer) | v1.2 was macOS-only. No Windows user can have an install to migrate from. A wizard step guaranteed to find nothing forever is dead code with a UI |
| The `permissions` onboarding step | macOS TCC folder access has no Windows equivalent. Explaining a prompt that never fires would be a lie |
| Thermal state | §5.3 |
| A supervised `hades start` | §5.4 — supervision is the shell's job |
| Authenticode signing | §8.2 — recorded as dated debt, not symmetry |
| Real Unity Hub discovery | Out of scope on both platforms (§6.1) |
| Linux | Not in scope |

The migration exclusion is a **named, commented artifact** shared by the §2.2 conformance carve-out list and the fixture manifest, so "which client speaks which endpoints" has one home rather than becoming tribal knowledge.

## 8. Distribution

### 8.1 The MSI

**Per-user, installing to `%LOCALAPPDATA%\Programs\Hades\`** — no elevation prompt, matching `install.sh`'s deliberate *"Do not run this with sudo"* stance, and the convention VS Code's user setup already uses. Payload: `Hades.Shell.exe`, `hades.exe` (§5.4), `core\` (§4.1, per-RID), and the §5.1 icons. `MajorUpgrade` handles version bumps; a per-user Add/Remove Programs entry (HKCU) replaces what `uninstall.sh` does by hand — including its promise never to touch a project's own `.arcforge/`. `PATH` gains the install directory. Launch-at-login stays an in-app setting, matching the Mac.

The no-elevation virtue has a flip side worth recording: corporate **AppLocker/WDAC** policies frequently block execution from `%LOCALAPPDATA%` outright.

### 8.2 Unsigned — the actual user experience, and the debt

Shipping unsigned for v1 is an accepted decision. It is **not** symmetric with macOS, and the spec should not claim it is: macOS-unsigned-via-curl is a stable plateau Apple has left alone for years, while Windows-unsigned is a slow decline.

**Browser download** (the majority path): the browser may warn on the download itself; the file carries Mark-of-the-Web; launching it raises SmartScreen's *"Windows protected your PC"* dialog whose default button is **Don't run**, past which the user must find **More info → Run anyway**, with the publisher shown as *Unknown*. Because reputation for unsigned binaries keys on the **file hash**, this **resets on every release, forever** — it never warms up the way a certificate does.

**Scripted download** (the clean path): **`curl.exe`**, in-box since Windows 10 1803, does not write `Zone.Identifier`, so no MotW and no SmartScreen. This is the exact analogue of `install.sh`. The install script specifies `curl.exe` explicitly — *not* `Invoke-WebRequest`: PowerShell 7 appears not to tag downloads, but Windows PowerShell 5.1's behaviour is contested, and `curl.exe` is the one this project will actually verify. **That verification is a step-6 gate, not an assumption** — this paragraph is currently reasoning, and the spec should not carry it in the voice of measurement while every other claim in §1 was executed. (Revision 1 posed this as an open question about `irm`; the framing was wrong twice over — `irm … | iex` never creates a file, so MotW is structurally moot for the *script*, and the question only ever mattered for the MSI it downloads.)

**After install: no UAC prompt at all** — genuinely better than the macOS Gatekeeper story.

**The debt, dated:** on Windows 11 machines with **Smart App Control** enabled (clean installs only), unsigned code is blocked with no override, and that population only grows. Azure Artifact Signing (~$10/month, individual developers eligible, HSM-backed, first-class on the `windows-latest` runner this spec already adds) would make Windows signing *cheaper and more automatable than macOS notarization*. **Recorded as debt with a review date at the first release that draws real Windows users, not as a permanent position.**

### 8.3 CI and release

The existing macOS `dotnet-tests` job is untouched. A new `windows-latest` job runs `Core/`'s suite and `Windows/HadesWindows.sln`. Running the **Core** suite on Windows is valuable independently of the shell — it is what surfaces path, case-sensitivity, and file-mode assumptions.

Two blockers to a green run, both re-counted against the tree on 2026-08-24 because revision 2 stated them wrongly:

- **`UnixFileMode`: one file, not eleven.** Eleven files reference it, but **ten already carry `OperatingSystem.IsWindows()` early-return guards.** The single unguarded file is `Hades.Core.Tests/Observation/IncrementalIndexTests.cs`, which suppresses the platform check with `#pragma warning disable CA1416` instead of guarding — so on Windows `File.SetUnixFileMode` throws `PlatformNotSupportedException` and the test fails outright rather than passing hollowly.
- **Hardcoded dev paths: 15 skippable files, not 20.** Fifteen files early-return when a hardcoded `/Users/mike/…` project is absent — which xUnit reports as **passed**, not skipped. Five further files (`MiniJsonTests`, `V12CleanupTests`, `V12DetectorTests`, `Control/MigrationTests`, `Control/ProjectsTests`) merely use such a path as a *string literal* in fixture content, never probing the filesystem, and need no change at all. Revision 2 conflated the two groups.

**Known residual flake, accepted 2026-08-25.** Roughly one `Hades.Server.Tests` run in five fails a single test with a 30-second `TimeoutException`. **It is not confined to one class** — observed in both `EditorProjectToolsTests` and `SceneApplyTests`, i.e. any test using the shared `AnswerBusyProbeThen*Async` responder helpers in `EditorToolTestBase`. In each case the fake-Unity responder waits its full guard period for a real command the tool never sent, meaning the proxy aborted between the busy probe and the send. The affected classes pass in isolation and the failure did not respond to capping test parallelism, so this may be a genuine `EditorProxy` race the tests are correctly catching rather than a test defect. Triage Windows CI against the SIGNATURE, not the class name: a single 30s `TimeoutException` in a `Hades.Server.Tests` class using that helper is the known flake; anything else is real.

The dominant cause of suite flakiness WAS found and fixed the same day: every editor fixture shrank `ProjectService.CharonProbeTimeout` to 300ms (production default 1.5s), so any test whose scenario required the busy probe to *succeed* silently depended on a real TCP round-trip completing inside 300ms. Under parallel load it did not, `EditorProxy` correctly reported "busy", and the test failed asserting on an unrelated message. Raising those fixtures to 5s took `Hades.Core.Tests` from failing every run to 882/882 across eight consecutive runs, and cut suite runtime roughly fivefold (Server 3m -> 35s) because the expired probes had been dominating it.

**The 15 are converted to real `Skip`s** rather than documented in a comment — the pass count is about to double its audience, and a skip count is a signal where a comment is only documentation.

**The release pipeline gains a lockstep-validation script** run in CI, generalising the existing `plugin.json`-vs-tag gate to every site in `ReleasePipeline.md` §2. This is not speculative hardening: that table has already failed twice in one release cycle. The MSI is built **and attached** by `release.yml` on a `windows-latest` job rather than by hand, eliminating the manual step where the previous mistakes happened. Longer term the DMG build should move into CI too, so both artifacts share provenance instead of one being built locally and one in CI.

## 9. Testing and sequencing

View models are tested headlessly; views are not. WPF view models stay free of `Dispatcher` dependencies so tests need no STA apartment.

**Not headlessly testable, requiring a hand-run pass** — the analogue of the Mac's Task 8: the tray icon, its context menu and the lease toast; the folder dialog; MSI install/uninstall/upgrade; SmartScreen; and the environmental classes in §9.1.

| # | Work | Gate | Where |
|---|---|---|---|
| 1 | Core green on Windows: `IncrementalIndexTests` guard, 15 early-returns → `Skip` (§8.3), token DACL (§6.2), **storage root → `LocalApplicationData` (§6.4)**, OS-branched areas (§6.1) | `dotnet test` passes on `windows-latest`; DACL test proves inheritance is severed | Mac + CI |
| 2 | `Hades.Control.Client`; reflection test with all three carve-outs + generated golden fixtures (§2.2); existing 44 Swift tests repointed; `Hades.Cli` off `Hades.Core`; **guard layers 1–3 across shell, client and CLI** (§2) | Both .NET clients and Swift decode identical generated fixtures; every guard layer demonstrated failing on a deliberate violation; CLI unchanged on macOS | Mac + CI |
| 3 | Supervision (§4): Job Object, `CREATE_SUSPENDED` P/Invoke, `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`, handle lifetime, `FakeCore` | Adopt/backoff/`minimumStableUptime` verifiable on **Mac**; Job Object kill, force-kill, assignment-failure and handle-lifetime tests **only on `windows-latest`** | Mac (logic) + **Windows CI** (mechanism) |
| 4 | Shell: tray + full menu (§5.2), lease toast, main window, `ShellFacts` (§5.3), Fluent (§5.5), tray `.ico` set **and the four further `StatusIcon` vocabularies** (§5.1) | hand-run | **Windows** |
| 5 | Onboarding (4 steps, reworded copy); CLI surface incl. `diagnose` (§5.4); Unity plugin branch; **the §6.3/§6.4 measurements** — plugin vs core path agreement on the decided root, and whether `SystemStatusFlag` reflects 24H2 energy saver | Editor attaches from a real Windows Unity project; OneDrive and non-default-drive Hub scenarios from §9.1 exercised by name | **Windows** |
| 6 | Two MSIs (x64 + arm64, §4.1), `PATH`, macOS `hades` symlink (§5.4), install script (§8.2), release CI + lockstep script (§8.3) | Clean install/uninstall on a fresh machine; `curl.exe` confirmed to leave no MotW; **arm64 MSI executed on real ARM64 hardware, or explicitly shipped labelled untested** | **Windows CI** + **Windows** |

Revision 1 marked step 3 "Mac + CI" with a gate of "spawn / adopt / restart / force-kill pass" — **not one line of the Job Object path can execute on a Mac.** The gate is split above so the claim is honest.

**Review gate, every step** (restored from Spec #3 §5): *any behaviour in the shell or the client that is not view state is a defect.*

### 9.1 Environmental risks CI cannot reach

Named because the testing strategy above genuinely does not cover them, and a solo maintainer cannot reproduce what he cannot see:

- **MAX_PATH.** Real Unity projects (`Library/PackageCache/com.unity.*@x.y.z/…`) routinely exceed 260 characters. .NET is long-path capable; anything shelling out (`explorer.exe /select,`, Hub paths) may not be. Only a real deep project reproduces this.
- **OneDrive-redirected folders.** Documents is OneDrive-redirected by default on consumer Windows 11. Files On-Demand placeholders mean a full index can trigger mass hydration, and placeholder mtime/size behaviour undermines the incremental `file_state` logic. Invisible to CI.
- **Antivirus.** SQLite WAL files locked mid-checkpoint by real-time scanning produce "database is locked" with no repro; file-watcher event storms; spawn heuristics against an unsigned exe in AppData.
- **Case-insensitivity.** `D:\Proj` and `d:\proj` are the same directory; every ordinal path comparison in `ProjectStore.Canonicalize` / `RootsRouter` is a latent duplicate-node or routing-miss bug. Step 1's Windows CI run is the right first move against this class.
- **Firewall prompts** on first loopback bind — usually exempt, cheap to measure, on the step 5 list.

**Mitigations:** Windows ships explicitly labelled **beta**; a `hades diagnose` command exports an environment/log bundle, which for a maintainer who cannot reproduce OneDrive or AV issues is worth more than additional tests; and OneDrive plus a non-default-drive Unity Hub install go on the step 5 hand-run checklist by name.

### 9.2 Contract policy for three clients

With Swift, .NET, and the CLI all consuming one API, two pressures need a written answer:

- **Additive-only changes.** Every client tolerates unknown enum values by construction (§2.2); no client may require a field a prior version omitted.
- **Route verbs stay platform-neutral in name** (§6.1). `revealInFinder`-consumed-by-`explorer.exe` is policy, not a precedent for per-platform endpoints.
- **A new control endpoint lands in both clients, or gets an explicit entry** in the §7 exclusion artifact — the same lockstep discipline `ReleasePipeline.md` already applies to versions.
- **`ControlClient.swift`'s doc comments are the semantic reference.** The .NET client cites them rather than paraphrasing; a second prose copy of the semantics is a third place for them to drift.

## 10. Documentation to update on completion

- `README.md` — platform badges, prerequisites, the beta label
- `LIMITATIONS.md` — "macOS is the ONLY tested platform" under Maturity, plus §9.1's environmental classes
- `Documentation/Architecture.md` — §2.2 describes only the Mac shell; §8 only the DMG
- `Documentation/Installing.md` — the Windows path and §8.2's honest SmartScreen description
- `Documentation/ReleasePipeline.md` — the lockstep table gains the Windows sites; §8.3's validation script
- `Core/src/Hades.Cli/Program.cs` — its "NOT a product deliverable" header is no longer true (§5.4)
