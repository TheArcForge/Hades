# Windows Port — Handover back to the Mac

**Written:** 2026-09-02, on the Windows machine, for picking the remaining work up on the Mac.
**Branch:** `feature/windows-support`, pushed and up to date at `293f5c0`.
**State:** Slices 4 and 6 of the verification checklist are fully ticked. **Two rows remain, and one of them is yours.**

This is the mirror of `HANDOVER.md`, which went the other way in August. Read that one only for history — everything it lists as "not done" is done.

---

## Start here: three pieces of macOS code have never been compiled or executed

They were written on a machine with no Mac. They are reviewed, they are consistent with everything around them, and that is the entire extent of the assurance behind them.

| File | What changed | Assurance so far |
|---|---|---|
| `Mac/HadesControl/Sources/HadesControl/DTOs.swift` | added `case neverIndexed` to `ProjectIndexState` | **never passed through a Swift compiler** |
| `Mac/HadesApp/scripts/build-app.sh` | publishes `Hades.Cli` into `Contents/Resources/HadesCli/` | **never executed** |
| `install.sh`, `uninstall.sh` | `hades` symlink create/remove, plus two bug fixes | executed only under a stubbed harness on Windows |

**Run this first.** If `DTOs.swift` does not compile, nothing else on this list matters:

```bash
swift build --package-path Mac/HadesControl
swift test  --package-path Mac/HadesControl
swift test  --package-path Mac/HadesApp
swift test  --package-path Mac/HadesSupervision
```

The `neverIndexed` addition is believed safe, and here is the reasoning so you can check it rather than trust it: `ControlEnum`'s shared `init(from:)` does `self = Self(rawValue: raw) ?? Self.unknownFallback`, so an unrecognised wire value decodes to `.unknown` instead of throwing; and **nothing in the repository `switch`es over `ProjectIndexState`** — the only non-test file that even names the type is `DTOs.swift` itself, because the UI renders the `indexStatus` *string* verbatim. Both facts were checked by grep from Windows. Confirm them with a compiler.

---

## The two remaining checklist rows

### 1. Task 13 Step 6 — the CLI's macOS half *(this row is unticked because of the word "macOS")*

The Windows half is done: all twelve commands were exercised against the installed MSI with a real core, including error paths. Nothing about the CLI is Windows-specific by design, but that is exactly the kind of claim this project does not accept on reasoning alone.

```bash
Mac/HadesApp/scripts/build-app.sh Release     # publishes the CLI into the bundle
```

Then exercise each command. Point the mutating ones at a **throwaway** project, not a real one:

`serve` · `diagnose` · `status` · `projects` · `add-project` · `remove-project` · `rebuild` · `operation <id>` · `install-plugin` · `traces` · `memory` · `release` · plus the no-argument usage banner

Two specifics worth repeating from the Windows sweep:

- `operation <id>` is not in the plan's original list but `rebuild` requires it — rebuild answers an operation id and returns, so without it the CLI can start a rebuild and never report on it.
- `remove-project` was checked against its **promise**, not its return value: after removal, the project folder, all its files, and its `graph.db` in app storage were all still present. It deregisters and destroys nothing. Verify the same way here.

### 2. Task 16 Step 5 — `install.sh` / `uninstall.sh` for real

**Read `Plan-Steps-4-6-TODO.md`, Task 16's second Outcome section before starting.** These were run unmodified under a harness that stubbed the twelve macOS-only commands, across 21 scenarios, and it found two real defects that are now fixed. What it could not touch is everything that makes these scripts macOS scripts.

Already covered by the harness — do not spend the day re-deriving it:

- `--dry-run` removes nothing (proved with a SHA256 manifest before and after)
- all six `TARGETS` are removed on a real run, and five deliberate bystanders survive — including `~/Library/Preferences/unity.DefaultCompany.ArcForge.plist`, which is **Unity's** file for a company named ArcForge and is the reason the script's header forbids globbing on "arcforge"
- all three `/usr/local/bin/hades` ownership branches: removed when it is a symlink into the bundle, left alone with a stated reason when it is a real file or points elsewhere
- an app that refuses to quit aborts with exit 1 having removed **nothing**
- every `install.sh` guard fires: non-macOS, `sudo`, Intel, macOS 13, already-running, download failure, checksum mismatch, DMG without a `Hades.app`, `ditto` failure
- the full round trip, including `uninstall.sh --dry-run` seeing the symlink `install.sh` itself created

**Still unknown, and only a Mac can answer:** whether `hdiutil` mounts the real DMG, whether `ditto` preserves the bundle, whether `codesign --deep` survives the copy, whether Gatekeeper stays quiet on a `curl`-fetched DMG, whether `osascript` really removes an `SMAppService` login item, and whether the published `shasum` matches. The harness's happy-path checksum was satisfied by a stub returning the expected value — only the **mismatch** direction used a genuine SHA-256.

The step as written:

```bash
Mac/HadesApp/scripts/build-app.sh Release
Mac/HadesApp/scripts/build-dmg.sh Release       # see --help; unsigned is the default path
bash install.sh                                  # or the curl | bash form
hades status                                     # from a FRESH terminal
bash uninstall.sh --dry-run                      # the symlink must be listed
bash uninstall.sh
```

`VERSION` and `SHA256` at the top of `install.sh` still point at v2.0.0. For a local hand-run you will need to either bump them to the DMG you just built or drive the script against a local file; **do not leave a hand-edited checksum committed.**

---

## The core bug this branch fixes, which was always a Mac bug too

Worth verifying on the Mac specifically, because it is the most user-visible change in the whole branch and it was found on Windows by accident.

Every project reported **"Indexing…" forever** after each launch — a spinner and "Indexing X…" in the menu bar over a finished graph with nothing running. Present since 2026-08-05. Three compounding causes, all fixed:

1. `LastIndexedUtc` was per-process, so after any restart every project looked mid-index. It is now persisted on `UnityProject`.
2. The no-change sweep path never recorded a completed index, so a project that needed no work never stopped looking busy.
3. `ProjectsEndpoint.BuildSnapshotAsync` sampled the timestamp *before* asking whether an index was running, so an index finishing between the two reads resolved to `NeverIndexed`. The order is now reversed, which makes the same race benign.

`ProjectIndexState` gained `NeverIndexed` so "never indexed" and "indexing right now" stop sharing a representation — that is the change that reaches `DTOs.swift`.

**On the Mac, confirm:** launch with an already-indexed project and check the menu bar does not claim to be indexing; add a fresh project and check it reads as not-yet-indexed rather than indexing; and check a project mid-index actually says indexing.

---

## Things that will bite you if nobody tells you

**1. `.gitattributes` has no rule for `*.sh`.** With `* text=auto` and `core.eol` defaulting to native, shell scripts check out **CRLF on Windows** — measured at 203 and 181 CRs. The committed blobs are clean LF (verified after the commit), so `raw.githubusercontent.com` serves LF and `curl | bash` is fine. But there is no rule enforcing it. A task chip exists to add `*.sh text eol=lf`. If you ever see `$'\r': command not found` from these scripts, this is why.

**2. macOS ships bash 3.2.57 as `/bin/bash`,** and `curl … | bash` uses whatever `bash` resolves to. Both installers were scanned for bash-4-only syntax (`mapfile`, `readarray`, `declare -A`, `${x,,}`, `${x^^}`, `coproc`, `|&`) and GNU-only flags (`readlink -f`, `sed -i `, `grep -P`) — none present. Keep it that way.

**3. The two defects that were fixed are subtle enough to be re-introduced by a well-meaning edit:**

- `install.sh` line ~107 ends `| head -1 || true)`. That `|| true` is **load-bearing**. Without it, `set -euo pipefail` aborts the script *at the assignment* whenever `grep` finds no `/Volumes` line, so the `die` on the very next line — which exists solely to explain that failure — can never run. Measured: a failing `hdiutil attach` exited 1 having printed nothing after `==> Mounting the disk image`.
- `uninstall.sh` uses `${t/#$HOME/\~}` with an **escaped** tilde. The replacement half of `${var/pattern/string}` undergoes tilde expansion, so a bare `~` expands straight back to `$HOME` and the substitution silently prints the full path it was written to shorten. Proved by setting `HOME=/ZZZ` and watching the output follow it.

**4. Do not "fix" the Mac onboarding's "five steps" copy.** The Mac genuinely has five steps; Windows has four because `permissions` is macOS TCC folder access with no Windows equivalent. The Windows text was reworded, not ported. `OnboardingWindow` on Windows builds "Step N of 4" from `AllSteps.Length` rather than writing the number in prose — the Mac still writes it in prose, which is what went stale in the first place. Worth considering, not required.

**5. Three tests in `Hades.Server.Tests` are flaky under parallel load**, not two as the plan originally said. `InspectToolTests`, `AckGapTests.VerifierConfirmsNotApplied_ThrowsClearRetrySafeError`, and — added 2026-09-02 — `EditorsProgramWiringTests.ControlListener_ReleaseAction_ReachesTheRealAttachedEditor_ThroughTheSharedEditorProxy`. All boot real wiring and are timing-sensitive. Triage by that signature. A green full-solution run means the machine was quiet, not that they are fixed.

---

## What NOT to redo

All of this was verified on real Windows hardware and is recorded with its evidence in the plan:

- all **seven** tray icon states seen live, including `error` forced with a real vanished project folder and `unknown` forced with a stub core the shell adopted
- the tray menu read out of the accessibility tree in three supervision states, both ownership footers
- onboarding walked end to end on the shipped MSI, all four steps, finishing with a working setup
- uninstall/reinstall round trip with byte-level proof that `graph.db` (42,291,200 B) and `traces.db` survive SHA256-identical
- MSI per-user install with no UAC, `hades` on PATH, upgrade in place, both architectures built
- Unity Editor attach, lease take and release, MCP `tools/list` returning 32 tools

**2,222 tests pass** with `--filter "Platform!=Unix"` (Core 1,957 + Windows 265).

---

## Still open after your two rows

- **Task 21 Step 4** — `release.yml` `workflow_dispatch` with `dry_run: true`, which builds both MSIs. Needs a run on GitHub; the guards were re-read and it cannot publish anything (`git push --dry-run` for the plugin repo, and the release-attach step is skipped when dry).
- **Task 19 Steps 3–4** — Mark-of-the-Web and SmartScreen; needs a *published* release and a Smart App Control machine. Neither a Mac nor CI covers it.
- **Launch-at-login at logon** — reproduced across four logons on Windows and concluded **not a Hades defect**: the machine honours no newly added `Run` entry at all, proven with a `notepad.exe` control. Needs different Windows hardware, not a Mac.
- **`TheArcForge/hades-plugin`** still describes Hades as a "macOS app". Different repo, separate push.
- Six task chips, including tray accessible-name announcing only "Hades" in all seven states, and an unexplained stuck `indexState` seen once and explicitly **not** claimed as a shipped bug.

---

## Git

**Mike handles git himself.** The one commit on this branch was made with his explicit one-time approval and is already pushed. Do the work in the working tree and leave it uncommitted unless he asks in that session.

Note for whoever runs this on the Mac: pushing from the Windows machine is refused — `Permission to TheArcForge/Hades.git denied to mikeKuharuk`, HTTP 403. `TheArcForge` is an organization and the repo is public, so read succeeds for anyone and proves nothing about the credential. Two GitHub credentials are stored there: Git Credential Manager's, and the Fork GUI client's separate OAuth token — which is most likely the one that actually has write access.
