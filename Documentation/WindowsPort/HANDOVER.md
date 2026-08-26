# Windows Port — Handover

**Written:** 2026-08-26, on the development Mac, for picking the work up on the Windows machine.
**State:** Spec #5 steps 1–3 implemented and green. Steps 4–6 planned, not started.

---

## Start here

1. Read `Spec-5-Windows-Support.md` §1 (what was measured) and §2 (the architectural rule).
2. Read `Plan-Steps-4-6-TODO.md` — **Task 1 is the first thing to run.**
3. Everything in `Plan-Steps-1-3-DONE.md` is already implemented; keep it for the reasoning, not as work.

**Why these three files live here rather than in `docs/`:** `docs/` is gitignored (`.gitignore:56`), so nothing in it survives a clone. These are mirrors. `Documentation/Architecture.md` exists for the same reason and says so in its own header. If you edit the originals under `docs/`, re-copy them here or the Windows machine will read stale ones.

---

## The one thing to run first

Two pieces of code have **never executed anywhere**. They compile, they are reviewed, and that is all:

- `Windows/Hades.Supervision/JobObject.cs` and `ProcessLauncher.cs` — the Job Object and `CreateProcess` P/Invoke
- `Core/src/Hades.Core/Storage/TokenFileWriter.cs` → `WriteWindows` — the atomic token DACL

```powershell
cd <repo>\Windows
dotnet test --filter "Platform!=Unix"     # expect 11 passing: 8 known-good + 3 never-run

cd <repo>\Core
dotnet test --filter "Platform!=Unix"     # runs the Windows DACL test for the first time
```

Expect problems here. That is the point — Plan Task 1 is written around triaging them, with the likely causes listed in order.

---

## What is done

| Area | State |
|---|---|
| Core runs on Windows | `LocalApplicationData` storage root, atomic token DACL, `explorer.exe` reveal, Windows Unity Hub path |
| Test platform gating | Traits + `--filter`, because **xUnit 2.9.3 has no dynamic skip** (`Assert.Skip` does not exist — verified three ways) |
| `Hades.Control.Client` | 39 DTOs, reflection conformance test, 42 generated golden fixtures shared with the Swift client |
| `hades` CLI | Moved onto the shared client, no longer references `Hades.Core` |
| `Windows/Hades.Supervision` | `CoreSupervisor` (platform-neutral, 8 tests pass on macOS), `JobObject`, `ProcessLauncher`, `FakeCore` |
| Boundary guard | Three layers, each proven to bite with a real violation |
| CI | macOS job + `windows-latest` job covering both solutions |

**Verified green on the Mac:** Core 1891 tests, Swift 81 tests, Windows supervision logic 8/8.

**Not yet pushed.** The `windows-latest` CI job has never run. Your first push is the first execution of any Windows-only code.

---

## Things that will bite you if nobody tells you

**1. The suite was flaky; it is fixed; do not undo the fix.**
Every editor test fixture had shrunk `ProjectService.CharonProbeTimeout` to 300ms (production default 1.5s). Any test whose scenario needed the busy probe to *succeed* was silently depending on a real TCP round-trip finishing in 300ms — under parallel load it did not, `EditorProxy` correctly reported "busy", and the test failed asserting an unrelated message. Raised to 5s in all 8 fixtures. That took `Hades.Core.Tests` from failing every run to 882/882 across eight runs, **and cut suite runtime ~5×** (Server 3m → 35s) because expired probes had been dominating it.

The rule: never shrink a timeout in a fixture where the tested scenario needs that operation to *succeed*. Make it generous, and let tests that want a timeout get one by not answering at all.

**2. One residual flake, ~1 run in 5.** A single `Hades.Server.Tests` test fails with a 30-second `TimeoutException`. **Not confined to one class** — seen in `EditorProjectToolsTests` and `SceneApplyTests`, i.e. any test using the `AnswerBusyProbeThen*Async` helpers. The responder waits its full guard for a real command the tool never sent, so the proxy aborted after the probe. Passes in isolation; capping parallelism to 4 did not fix it. May be a genuine `EditorProxy` race.

**Triage Windows CI by that signature, not by class name.** A lone 30s timeout in a class using those helpers is the known flake. Anything else is real.

**3. `hades.exe` is the CLI's assembly name.** `Hades.Cli.csproj` sets `<AssemblyName>hades</AssemblyName>`, so the file is `hades.dll` / `hades.exe`, not `Hades.Cli.*`. This surprises anyone grepping for it.

**4. `Windows/` uses `.slnx`.** .NET 10's `dotnet new sln` produces the newer format by default. `dotnet build` / `dotnet test` pick it up with no explicit path (verified). `Windows/.gitignore` un-ignores it explicitly, because the repo root ignores `*.sln` for Unity's sake.

**5. Do not let a client reference the core.** Three build guards will stop you, and each has been watched failing:
- MSBuild target on `Hades.Shell` (via `Windows/Directory.Build.props`), `Hades.Cli`, `Hades.Control.Client`
- `ArchitectureTests` — loads built assemblies via `MetadataLoadContext`
- `BannedApiAnalyzers` — bans `SqliteConnection` and `Assembly.LoadFrom`

Measured, and **opposite to what the spec first claimed**: MSBuild expands `@(ProjectReference)` to the full *transitive* closure, so Layer 1 catches indirect references; but Roslyn strips *unused* references from metadata, so Layer 2 only sees references actually used in code.

---

## Two real bugs found along the way

**`ControlClient`'s constructor is a trap.** It rewrites `BaseAddress` and `Authorization` on the `HttpClient` it is given — but `HttpClient` throws once it has sent its first request. Sharing one across `ControlClient` constructions breaks after the first ping. That is not a test artifact: **every real core restart yields a fresh ephemeral port**, so this would have silently broken all pinging after the first successful one in production. `CoreSupervisor` now uses a short-lived client per ping. If you add more `ControlClient` consumers, do not hand them a shared `HttpClient`.

**Fixture corpora pin different things.** An earlier plan tried to make the 44 Swift decode tests read the generated corpus. 43 broke — not from drift, but because the Swift fixtures were captured from a *real server* and assert semantic content (`callCount == 7`, `port 9999`), while generated exemplars are synthetic and pin wire *shape*. Both corpora now coexist, each owning what it is good at, and a separate 11-test Swift suite decodes the generated corpus for shape. **Do not merge them.**

---

## Where the code is

```
Core/src/Hades.Control.Client/     shared control-API client (net10.0, platform-neutral)
Core/src/Hades.Cli/                the `hades` CLI, now a client
Core/tests/Fixtures/control-api/   42 generated golden fixtures (both clients read these)
Windows/Hades.Supervision/         CoreSupervisor + Job Object + ProcessLauncher
Windows/FakeCore/                  test fixture; runs on macOS AND Windows
Mac/HadesControl/                  the Swift client — the behavioural reference for the WPF shell
```

The macOS shell (`Mac/HadesApp/`) is the **specification for behaviour**. When Plan steps 4–6 say "read the reference", they mean these — the Windows idiom differs, but *what each surface shows* does not.

---

## What is not done

Steps 4–6, in `Plan-Steps-4-6-TODO.md`: the WPF shell (22 tasks), onboarding, the Unity plugin's Windows arm, the CLI's remaining commands plus `hades diagnose`, two MSIs, and the release pipeline.

**Task 11's hand-run is the honest checkpoint.** It verifies the whole supervision contract on real hardware — including End Task leaving no orphaned core, which has never been verified anywhere. Slices 5 and 6 rest on assumptions only that gate can confirm, so re-read them after it rather than treating them as settled.

---

## Three things to measure, not assume

The spec calls these out because guessing them is how this project got bitten before:

1. **The Unity plugin's token path.** `HadesConnectionFile.cs` exists *because* Unity's Mono and .NET 10 resolved `SpecialFolder.ApplicationData` to different directories on the same machine. Probe both runtimes on Windows before writing the branch (Plan Task 15 has the script).
2. **Mark-of-the-Web**, across all three download paths — `curl.exe`, `Invoke-WebRequest`, and a browser. `curl.exe` is expected clean; `Invoke-WebRequest` is contested. This decides what `install.ps1` uses.
3. **Whether `SystemStatusFlag` reflects Windows 11 24H2's plugged-in "energy saver."** If it does not, drop the Settings row rather than show it wrong.

---

## Git state

Nothing was committed during this work — it is all staged in one changeset. The suggested commit message is in the session notes; adjust freely.

Build artifacts are excluded: `Windows/.gitignore` was added in this changeset, without which 94 `bin/obj` files would have been committed.
