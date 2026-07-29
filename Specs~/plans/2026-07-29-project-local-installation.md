# Project-Local Hades Installation Implementation Plan

> **For agentic workers:** execute this plan task-by-task using the `execute-plan` skill. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a Unity project able to hold its entire Hades installation inside its own workspace, by default, with a user-visible local/global toggle.

**Architecture:** The hub's rendezvous directory (currently hardcoded to `$HOME/.arcforge/hades-hub/`) moves behind a three-rung resolver — `HADES_HUB_DIR` env var, then `<projectRoot>/.arcforge/hades-hub/` when scope is local, then `$HOME` as fallback — implemented once in C# (`HadesPaths`) and once in TypeScript (`hub-dir.ts`). The launcher passes its resolved directory to the hub via the spawn environment, so the hub never re-derives it. All Hades settings move from machine-global Unity `EditorPrefs` into a per-project `.arcforge/config.local.yaml`, read by C# and (one key only) by the launcher.

**Tech Stack:** C# (Unity 6000.0+ Editor assembly, NUnit, Newtonsoft.Json), TypeScript (Node 20, esbuild, vitest).

**Spec:** `Specs~/project-local-installation.md`

## Global Constraints

- Unity floor is `6000.0` (`package.json`). Editor-only assembly `ArcForge.Hades.Editor`; tests in `ArcForge.Hades.Tests.Editor`.
- C# namespaces: production `ArcForge.Hades.Editor.Core` / `.MCP`; tests `ArcForge.Hades.Editor.Tests`.
- **Zero new runtime dependencies** on either side. C# may use only Newtonsoft.Json and BCL. The launcher bundle must stay dependency-free.
- The launcher must remain a **single self-contained esbuild bundle**. `Bridge~/tests/launcher/bundle.test.ts` enforces: `dist/index.js` exists, contains **no relative imports**, and `dist/` holds only `index.js`. New launcher modules must be plain relative-imported siblings so esbuild inlines them.
- Config file dialect is flat `key: value` only — no nesting, no YAML library on either side.
- Config keys are `snake_case`. Defaults, verbatim from spec §4.2: `hub_scope: local`, `skills_scope: local`, `desktop_integration: true`, `mcp_port: 0`, `mcp_enabled: true`, `mcp_auto_start: true`, `mcp_log_level: 1`, `domain_reload_strategy: auto`, `reload_timeout_seconds: 120`, `charon_enabled: true`, `charon_retention_days: 30`, `charon_max_size_mb: 500`.
- Missing config file, missing key, and unparseable value **all** fall back to defaults, silently. A missing file is the normal state for a fresh clone and must never log a warning.
- Never show a modal dialog when `Application.isBatchMode` is true — take the default path instead. CI and `-batchmode` builds must never block.
- Bridge tests: `cd Bridge~ && npm test`. Bridge build: `cd Bridge~ && npm run build`.
- **Unity C# tests are not in CI** (`.github/workflows/ci.yml` runs Node tests only). Run them via Unity's Test Runner (`Window > General > Test Runner > EditMode > Run All`) inside a host Unity project that has this package installed. Every task below states the exact test class and method names to run.
- Unity generates a `.meta` file for every new `.cs` file. After Unity imports, `git add` the `.meta` files alongside the sources — the repo tracks them.

---

## File Structure

| File | Responsibility |
|---|---|
| `Editor/Core/AtomicFile.cs` (new) | One job: write a file atomically via tmp + move. Kills the duplicate copy currently private inside `MCPClientConfig`. |
| `Editor/Core/HadesConfig.cs` (new) | Read/write `.arcforge/config.local.yaml`. Flat `key: value` parse, typed getters with fallbacks, atomic save. Knows nothing about which keys exist. |
| `Editor/Core/HadesPaths.cs` (new) | The hub-dir resolution chain (spec §4.1) plus `.arcforge` path helpers. Pure resolver function so tests never touch real `$HOME`. |
| `Editor/Core/HadesSettings.cs` (modify) | Unchanged public API, re-backed onto `HadesConfig`. Adds `HubScope`, `SkillsScope`, `DesktopIntegration`. Owns EditorPrefs import. |
| `Editor/Core/HadesPreferences.cs` (new) | `SettingsProvider` at Project Settings → Hades, plus the `Hades/Settings…` menu item. UI only. |
| `Editor/Core/LegacyHubNotice.cs` (new) | The one-time informational notice about the now-unused global hub dir (spec §4.7). |
| `Editor/Core/MCPClientConfig.cs` (modify) | Package path resolution fix; launcher copy + `.mcp.json` follow the resolved hub dir; skills scope; desktop-integration gate. |
| `Editor/MCP/HubClient.cs` (modify) | `HubDir` sourced from `HadesPaths` instead of a `$HOME` constant. |
| `Editor/Core/HadesBootstrap.cs` (modify) | Boot wiring: settings migration before anything reads settings; legacy notice deferred. |
| `Editor/Asphodel/Inference/InferenceConfig.cs` (modify) | Reuse `HadesConfig`'s parser. Still reads `config.yaml`, not `config.local.yaml`. |
| `Bridge~/launcher/src/project-path.ts` (modify) | Add `findProjectRoot` returning `string \| null`; `resolveProjectPath` becomes its wrapper. |
| `Bridge~/launcher/src/hub-dir.ts` (new) | TS twin of the resolution chain + a `hub_scope`-only config reader. Dependency-injected file reads so tests need no temp dirs. |
| `Bridge~/launcher/src/index.ts` (modify) | Use the resolver; pass `HADES_HUB_DIR` into the hub spawn env. |
| `Bridge~/hub/src/index.ts` (modify) | Read `HADES_HUB_DIR`, `$HOME` fallback only. No re-derivation. |

Tests: `Tests/Editor/Core/HadesConfigTests.cs`, `Tests/Editor/Core/HadesPathsTests.cs`, `Tests/Editor/Core/HadesSettingsTests.cs`, `Bridge~/tests/launcher/hub-dir.test.ts`.

---

## Task 1: Atomic file write helper

**Files:**
- Create: `Editor/Core/AtomicFile.cs`
- Modify: `Editor/Core/MCPClientConfig.cs:379-390` (replace private `AtomicWrite` with a call into the new helper)

**Interfaces:**
- Consumes: nothing.
- Produces: `ArcForge.Hades.Editor.Core.AtomicFile.Write(string filePath, string content)` — creates parent directories, writes via `filePath + ".tmp"`, then replaces the target.

- [ ] **Step 1: Create the helper**

`Editor/Core/AtomicFile.cs`:

```csharp
using System.IO;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Writes a file atomically: content lands in a sibling .tmp file first, then replaces the
    /// target. A crash mid-write leaves the previous file intact rather than a truncated one.
    /// Extracted from MCPClientConfig so HadesConfig can share it rather than copy it.
    /// </summary>
    public static class AtomicFile
    {
        public static void Write(string filePath, string content)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = filePath + ".tmp";
            File.WriteAllText(tmpPath, content);
            if (File.Exists(filePath))
                File.Delete(filePath);
            File.Move(tmpPath, filePath);
        }
    }
}
```

- [ ] **Step 2: Point `MCPClientConfig` at it**

In `Editor/Core/MCPClientConfig.cs`, delete the private `AtomicWrite` method (currently lines 379-390) and replace its body with a forwarder so the four existing call sites need no edit:

```csharp
        static void AtomicWrite(string filePath, string content)
            => AtomicFile.Write(filePath, content);
```

- [ ] **Step 3: Verify it compiles**

In Unity: let the editor recompile. Expected: no errors in the Console, no new warnings from `MCPClientConfig` or `AtomicFile`.

- [ ] **Step 4: Commit**

```bash
git add Editor/Core/AtomicFile.cs Editor/Core/AtomicFile.cs.meta Editor/Core/MCPClientConfig.cs
git commit -m "refactor: extract AtomicFile.Write from MCPClientConfig"
```

---

## Task 2: HadesConfig — project-local flat config file

**Files:**
- Create: `Editor/Core/HadesConfig.cs`
- Test: `Tests/Editor/Core/HadesConfigTests.cs`

**Interfaces:**
- Consumes: `AtomicFile.Write` (Task 1).
- Produces:
  - `HadesConfig.FileName` → `"config.local.yaml"`
  - `static HadesConfig HadesConfig.Load(string arcforgeDir)`
  - `static Dictionary<string,string> HadesConfig.Parse(string[] lines)` (internal, for tests)
  - `bool Exists { get; }`
  - `string GetString(string key, string fallback)`
  - `bool GetBool(string key, bool fallback)`
  - `int GetInt(string key, int fallback)`
  - `void Set(string key, string value)` / `Set(string key, bool value)` / `Set(string key, int value)`
  - `void Save()`

- [ ] **Step 1: Write failing tests**

`Tests/Editor/Core/HadesConfigTests.cs`:

```csharp
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class HadesConfigTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "hades_config_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        void WriteConfig(string contents)
            => File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), contents);

        [Test]
        public void Exists_IsFalse_WhenFileMissing()
        {
            var config = HadesConfig.Load(_dir);
            Assert.IsFalse(config.Exists);
        }

        [Test]
        public void Getters_ReturnFallbacks_WhenFileMissing()
        {
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual("local", config.GetString("hub_scope", "local"));
            Assert.AreEqual(true, config.GetBool("mcp_enabled", true));
            Assert.AreEqual(120, config.GetInt("reload_timeout_seconds", 120));
        }

        [Test]
        public void Getters_ReturnFallbacks_WhenKeyAbsent()
        {
            WriteConfig("mcp_port: 51234\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual("local", config.GetString("hub_scope", "local"));
        }

        [Test]
        public void Getters_ReadStoredValues()
        {
            WriteConfig("hub_scope: global\nmcp_port: 51234\ncharon_enabled: false\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual("global", config.GetString("hub_scope", "local"));
            Assert.AreEqual(51234, config.GetInt("mcp_port", 0));
            Assert.AreEqual(false, config.GetBool("charon_enabled", true));
        }

        [Test]
        public void Getters_ReturnFallbacks_OnUnparseableValues()
        {
            WriteConfig("mcp_port: banana\ncharon_enabled: maybe\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual(7, config.GetInt("mcp_port", 7));
            Assert.AreEqual(true, config.GetBool("charon_enabled", true));
        }

        [Test]
        public void Parse_SkipsBlankAndCommentLines()
        {
            var values = HadesConfig.Parse(new[]
            {
                "# hub_scope: global",
                "",
                "   ",
                "mcp_port: 42"
            });
            Assert.AreEqual(1, values.Count);
            Assert.AreEqual("42", values["mcp_port"]);
        }

        [Test]
        public void Parse_IgnoresLinesWithoutAColon()
        {
            var values = HadesConfig.Parse(new[] { "garbage", "mcp_port: 42" });
            Assert.AreEqual(1, values.Count);
        }

        [Test]
        public void GetBool_IsCaseInsensitive()
        {
            WriteConfig("mcp_enabled: FALSE\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual(false, config.GetBool("mcp_enabled", true));
        }

        [Test]
        public void Save_ThenLoad_RoundTripsAllTypes()
        {
            var config = HadesConfig.Load(_dir);
            config.Set("hub_scope", "global");
            config.Set("mcp_enabled", false);
            config.Set("mcp_port", 51234);
            config.Save();

            var reloaded = HadesConfig.Load(_dir);
            Assert.IsTrue(reloaded.Exists);
            Assert.AreEqual("global", reloaded.GetString("hub_scope", "local"));
            Assert.AreEqual(false, reloaded.GetBool("mcp_enabled", true));
            Assert.AreEqual(51234, reloaded.GetInt("mcp_port", 0));
        }

        [Test]
        public void Save_CreatesTheDirectory_WhenMissing()
        {
            var nested = Path.Combine(_dir, "nested", ".arcforge");
            var config = HadesConfig.Load(nested);
            config.Set("hub_scope", "local");
            config.Save();
            Assert.IsTrue(File.Exists(Path.Combine(nested, HadesConfig.FileName)));
        }

        [Test]
        public void Save_PreservesUnknownKeys()
        {
            WriteConfig("some_future_key: keepme\n");
            var config = HadesConfig.Load(_dir);
            config.Set("mcp_port", 1);
            config.Save();

            var reloaded = HadesConfig.Load(_dir);
            Assert.AreEqual("keepme", reloaded.GetString("some_future_key", ""));
        }
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Unity Test Runner → EditMode → run class `HadesConfigTests`.
Expected: compile error — `HadesConfig` does not exist.

- [ ] **Step 3: Write the implementation**

`Editor/Core/HadesConfig.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Per-developer, project-local settings at &lt;projectRoot&gt;/.arcforge/config.local.yaml.
    ///
    /// Deliberately a flat `key: value` dialect — the same subset InferenceConfig already parsed.
    /// That keeps two constraints satisfiable at once: no YAML dependency in the Editor assembly,
    /// and the Node launcher can read the one key it needs (hub_scope) in ~15 lines without
    /// pulling a parser into its zero-dependency bundle.
    ///
    /// Every getter falls back silently. A missing file is the normal state of a fresh clone,
    /// not an error worth logging.
    /// </summary>
    public class HadesConfig
    {
        public const string FileName = "config.local.yaml";

        const string Header =
            "# Hades per-developer settings for this project. Machine-specific; gitignored.\n" +
            "# Edit via Unity: Project Settings > Hades (or the Hades/Settings... menu).\n";

        readonly Dictionary<string, string> _values;
        readonly string _filePath;

        HadesConfig(string filePath, Dictionary<string, string> values)
        {
            _filePath = filePath;
            _values = values;
        }

        public static HadesConfig Load(string arcforgeDir)
        {
            var filePath = Path.Combine(arcforgeDir, FileName);
            return new HadesConfig(filePath, Parse(ReadLines(filePath)));
        }

        public bool Exists => File.Exists(_filePath);

        public string FilePath => _filePath;

        static string[] ReadLines(string filePath)
        {
            try
            {
                return File.Exists(filePath) ? File.ReadAllLines(filePath) : new string[0];
            }
            catch
            {
                // Unreadable file is treated exactly like a missing one: fall back to defaults.
                return new string[0];
            }
        }

        /// <summary>
        /// Flat `key: value` parse. Blank lines, comment lines, and lines with no colon are
        /// skipped. Last occurrence of a duplicated key wins.
        /// </summary>
        internal static Dictionary<string, string> Parse(string[] lines)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = trimmed.Substring(0, colonIdx).Trim();
                var value = trimmed.Substring(colonIdx + 1).Trim();
                if (key.Length == 0) continue;

                values[key] = value;
            }

            return values;
        }

        public string GetString(string key, string fallback)
            => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

        public bool GetBool(string key, bool fallback)
        {
            if (!_values.TryGetValue(key, out var v)) return fallback;
            if (bool.TryParse(v, out var parsed)) return parsed;
            return fallback;
        }

        public int GetInt(string key, int fallback)
        {
            if (!_values.TryGetValue(key, out var v)) return fallback;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return fallback;
        }

        public void Set(string key, string value) => _values[key] = value ?? "";

        public void Set(string key, bool value) => Set(key, value ? "true" : "false");

        public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Writes every known key back, sorted for a stable diff. Keys this version does not
        /// recognise are preserved — a newer Hades must not lose settings when an older one saves.
        /// </summary>
        public void Save()
        {
            var keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append(Header);
            foreach (var key in keys)
                sb.Append(key).Append(": ").Append(_values[key]).Append('\n');

            AtomicFile.Write(_filePath, sb.ToString());
        }
    }
}
```

Note `bool.TryParse` accepts `"FALSE"`/`"False"` case-insensitively, which satisfies `GetBool_IsCaseInsensitive`.

- [ ] **Step 4: Run tests, verify they pass**

Unity Test Runner → EditMode → class `HadesConfigTests`. Expected: 11/11 pass.

- [ ] **Step 5: Commit**

```bash
git add Editor/Core/HadesConfig.cs Editor/Core/HadesConfig.cs.meta \
        Tests/Editor/Core/HadesConfigTests.cs Tests/Editor/Core/HadesConfigTests.cs.meta
git commit -m "feat: add HadesConfig, project-local flat settings file"
```

---

## Task 3: HadesPaths — the hub directory resolution chain

**Files:**
- Create: `Editor/Core/HadesPaths.cs`
- Test: `Tests/Editor/Core/HadesPathsTests.cs`

**Interfaces:**
- Consumes: `PathSandbox.ProjectRoot` (existing, `Editor/Core/PathSandbox.cs:15`).
- Produces:
  - `enum HadesScope { Local = 0, Global = 1 }`
  - `HadesPaths.EnvHubDir` → `"HADES_HUB_DIR"`
  - `static string HadesPaths.ResolveHubDir(string envOverride, HadesScope scope, string projectRoot, string homeDir)` — pure, no I/O
  - `static string HadesPaths.GlobalHubDir(string homeDir)`
  - `static string HadesPaths.ArcforgeDir { get; }` — `<projectRoot>/.arcforge`
  - `static string HadesPaths.HubDir { get; }` — live resolution for production callers
  - `static string HadesPaths.GlobalHubDirForMachine { get; }`

- [ ] **Step 1: Write failing tests**

`Tests/Editor/Core/HadesPathsTests.cs`:

```csharp
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class HadesPathsTests
    {
        const string Home = "/Users/tester";
        const string Project = "/Work/MyGame";

        static string Expected(params string[] parts) => Path.Combine(parts);

        [Test]
        public void EnvOverride_WinsOverEverything()
        {
            var result = HadesPaths.ResolveHubDir("/custom/hub", HadesScope.Local, Project, Home);
            Assert.AreEqual("/custom/hub", result);
        }

        [Test]
        public void EnvOverride_WinsEvenInGlobalScope()
        {
            var result = HadesPaths.ResolveHubDir("/custom/hub", HadesScope.Global, Project, Home);
            Assert.AreEqual("/custom/hub", result);
        }

        [Test]
        public void EnvOverride_IsIgnored_WhenWhitespaceOnly()
        {
            var result = HadesPaths.ResolveHubDir("   ", HadesScope.Local, Project, Home);
            Assert.AreEqual(Expected(Project, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void EnvOverride_IsTrimmed()
        {
            var result = HadesPaths.ResolveHubDir("  /custom/hub  ", HadesScope.Local, Project, Home);
            Assert.AreEqual("/custom/hub", result);
        }

        [Test]
        public void LocalScope_ResolvesInsideTheProject()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Local, Project, Home);
            Assert.AreEqual(Expected(Project, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void GlobalScope_ResolvesUnderHome()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Global, Project, Home);
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void LocalScope_FallsBackToHome_WhenProjectRootIsUnknown()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Local, null, Home);
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void LocalScope_FallsBackToHome_WhenProjectRootIsEmpty()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Local, "", Home);
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void GlobalHubDir_IsHomeArcforgeHadesHub()
        {
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), HadesPaths.GlobalHubDir(Home));
        }
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Unity Test Runner → EditMode → class `HadesPathsTests`.
Expected: compile error — `HadesPaths` / `HadesScope` do not exist.

- [ ] **Step 3: Write the implementation**

`Editor/Core/HadesPaths.cs`:

```csharp
using System;
using System.IO;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>Where Hades keeps a given kind of state: inside the project, or under $HOME.</summary>
    public enum HadesScope
    {
        Local = 0,
        Global = 1
    }

    /// <summary>
    /// Resolves the hub rendezvous directory.
    ///
    /// The hub needs a location that Unity, the launcher, and the hub itself can each compute with
    /// zero configuration, because that is where hub.json (port + pid) is published. $HOME was the
    /// original choice for exactly that reason. The Unity project root satisfies the same property
    /// — Unity knows it directly, and the launcher finds it by walking up for
    /// ProjectSettings/ProjectVersion.txt — so it works equally well while staying in the workspace.
    ///
    /// Chain (spec §4.1):
    ///   1. HADES_HUB_DIR env var          — explicit override, and the seam that makes this testable
    ///   2. &lt;projectRoot&gt;/.arcforge/hades-hub — the default
    ///   3. $HOME/.arcforge/hades-hub      — legacy, and the fallback when projectRoot is unknown
    ///
    /// Rung 3 is not vestigial: a launcher whose cwd is a `file:`-referenced package repo OUTSIDE
    /// the Unity project cannot see the project's hub dir, and must fall through to the shared hub
    /// so Registry.findByProjectPath's manifestPackages match still routes it.
    /// </summary>
    public static class HadesPaths
    {
        public const string EnvHubDir = "HADES_HUB_DIR";
        public const string ArcforgeDirName = ".arcforge";
        public const string HubDirName = "hades-hub";

        /// <summary>Pure resolver — no environment reads, no filesystem probing. See class docs.</summary>
        public static string ResolveHubDir(string envOverride, HadesScope scope,
            string projectRoot, string homeDir)
        {
            if (!string.IsNullOrEmpty(envOverride))
            {
                var trimmed = envOverride.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            if (scope == HadesScope.Local && !string.IsNullOrEmpty(projectRoot))
                return Path.Combine(projectRoot, ArcforgeDirName, HubDirName);

            return GlobalHubDir(homeDir);
        }

        public static string GlobalHubDir(string homeDir)
            => Path.Combine(homeDir ?? "", ArcforgeDirName, HubDirName);

        public static string ArcforgeDir
            => Path.Combine(PathSandbox.ProjectRoot, ArcforgeDirName);

        public static string HomeDir
            => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string GlobalHubDirForMachine => GlobalHubDir(HomeDir);

        /// <summary>Live resolution for production callers. Reads env + this project's settings.</summary>
        public static string HubDir => ResolveHubDir(
            Environment.GetEnvironmentVariable(EnvHubDir),
            new HadesSettings().HubScope,
            PathSandbox.ProjectRoot,
            HomeDir);
    }
}
```

The `HubDir` property references `HadesSettings.HubScope`, which Task 4 adds. Until then the assembly will not compile — that is expected and is why Task 4 follows immediately. To keep this task independently committable, temporarily hardcode the scope:

```csharp
        public static string HubDir => ResolveHubDir(
            Environment.GetEnvironmentVariable(EnvHubDir),
            HadesScope.Local,   // replaced with settings lookup in Task 4
            PathSandbox.ProjectRoot,
            HomeDir);
```

- [ ] **Step 4: Run tests, verify they pass**

Unity Test Runner → EditMode → class `HadesPathsTests`. Expected: 9/9 pass.

- [ ] **Step 5: Commit**

```bash
git add Editor/Core/HadesPaths.cs Editor/Core/HadesPaths.cs.meta \
        Tests/Editor/Core/HadesPathsTests.cs Tests/Editor/Core/HadesPathsTests.cs.meta
git commit -m "feat: add HadesPaths hub-dir resolution chain"
```

---

## Task 4: Re-back HadesSettings onto HadesConfig

**Files:**
- Modify: `Editor/Core/HadesSettings.cs` (full rewrite of the storage layer; public API preserved)
- Modify: `Editor/Core/HadesPaths.cs` (swap the hardcoded `HadesScope.Local` for the real settings lookup)
- Test: `Tests/Editor/Core/HadesSettingsTests.cs`

**Interfaces:**
- Consumes: `HadesConfig.Load` / getters / `Set` / `Save` (Task 2), `HadesPaths.ArcforgeDir` and `HadesScope` (Task 3).
- Produces: `HadesSettings` with its **existing** members unchanged in name and type — `Port` (int), `Enabled` (bool), `AutoStart` (bool), `LogLevel` (int), `DomainReloadStrategy` (`ReloadStrategy`), `ReloadTimeoutSeconds` (int), `CharonEnabled` (bool), `CharonRetentionDays` (int), `CharonMaxSizeMb` (int) — plus new `HubScope` (`HadesScope`), `SkillsScope` (`HadesScope`), `DesktopIntegration` (bool). Constructors: `HadesSettings()` and `HadesSettings(HadesConfig)`. Statics: `HasLegacyEditorPrefs()`, `ImportFromEditorPrefs(HadesConfig)`, `EnsureMigrated()`.

Preserving the existing member names is load-bearing: `MCPServer.cs:21,55,90`, `CharonInitializer.cs:18`, and `Tests/Editor/MCPServerIntegrationTests.cs:31,38,46,65` all construct `new HadesSettings()` and must keep compiling untouched.

- [ ] **Step 1: Write failing tests**

`Tests/Editor/Core/HadesSettingsTests.cs`:

```csharp
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class HadesSettingsTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "hades_settings_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        HadesSettings NewSettings() => new HadesSettings(HadesConfig.Load(_dir));

        [Test]
        public void Defaults_MatchSpec()
        {
            var s = NewSettings();
            Assert.AreEqual(HadesScope.Local, s.HubScope);
            Assert.AreEqual(HadesScope.Local, s.SkillsScope);
            Assert.AreEqual(true, s.DesktopIntegration);
            Assert.AreEqual(0, s.Port);
            Assert.AreEqual(true, s.Enabled);
            Assert.AreEqual(true, s.AutoStart);
            Assert.AreEqual(1, s.LogLevel);
            Assert.AreEqual(ReloadStrategy.Auto, s.DomainReloadStrategy);
            Assert.AreEqual(120, s.ReloadTimeoutSeconds);
            Assert.AreEqual(true, s.CharonEnabled);
            Assert.AreEqual(30, s.CharonRetentionDays);
            Assert.AreEqual(500, s.CharonMaxSizeMb);
        }

        [Test]
        public void Setters_PersistAcrossReload()
        {
            var s = NewSettings();
            s.HubScope = HadesScope.Global;
            s.SkillsScope = HadesScope.Global;
            s.DesktopIntegration = false;
            s.Port = 51234;
            s.Enabled = false;
            s.AutoStart = false;
            s.LogLevel = 3;
            s.DomainReloadStrategy = ReloadStrategy.Manual;
            s.ReloadTimeoutSeconds = 45;
            s.CharonEnabled = false;
            s.CharonRetentionDays = 7;
            s.CharonMaxSizeMb = 0;

            var reloaded = NewSettings();
            Assert.AreEqual(HadesScope.Global, reloaded.HubScope);
            Assert.AreEqual(HadesScope.Global, reloaded.SkillsScope);
            Assert.AreEqual(false, reloaded.DesktopIntegration);
            Assert.AreEqual(51234, reloaded.Port);
            Assert.AreEqual(false, reloaded.Enabled);
            Assert.AreEqual(false, reloaded.AutoStart);
            Assert.AreEqual(3, reloaded.LogLevel);
            Assert.AreEqual(ReloadStrategy.Manual, reloaded.DomainReloadStrategy);
            Assert.AreEqual(45, reloaded.ReloadTimeoutSeconds);
            Assert.AreEqual(false, reloaded.CharonEnabled);
            Assert.AreEqual(7, reloaded.CharonRetentionDays);
            Assert.AreEqual(0, reloaded.CharonMaxSizeMb);
        }

        [Test]
        public void HubScope_ParsesTheStringForm()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), "hub_scope: global\n");
            Assert.AreEqual(HadesScope.Global, NewSettings().HubScope);
        }

        [Test]
        public void HubScope_FallsBackToLocal_OnGarbage()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), "hub_scope: sideways\n");
            Assert.AreEqual(HadesScope.Local, NewSettings().HubScope);
        }

        [Test]
        public void HubScope_IsCaseInsensitive()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), "hub_scope: GLOBAL\n");
            Assert.AreEqual(HadesScope.Global, NewSettings().HubScope);
        }

        [Test]
        public void DomainReloadStrategy_ParsesTheStringForm()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName),
                "domain_reload_strategy: manual\n");
            Assert.AreEqual(ReloadStrategy.Manual, NewSettings().DomainReloadStrategy);
        }

        [Test]
        public void ImportFromEditorPrefs_CopiesLegacyValues()
        {
            UnityEditor.EditorPrefs.SetInt("Hades_MCP_Port", 51999);
            UnityEditor.EditorPrefs.SetBool("Hades_MCP_CharonEnabled", false);
            try
            {
                var config = HadesConfig.Load(_dir);
                HadesSettings.ImportFromEditorPrefs(config);
                config.Save();

                var s = NewSettings();
                Assert.AreEqual(51999, s.Port);
                Assert.AreEqual(false, s.CharonEnabled);
            }
            finally
            {
                UnityEditor.EditorPrefs.DeleteKey("Hades_MCP_Port");
                UnityEditor.EditorPrefs.DeleteKey("Hades_MCP_CharonEnabled");
            }
        }

        [Test]
        public void HasLegacyEditorPrefs_IsTrue_WhenAnyLegacyKeyExists()
        {
            UnityEditor.EditorPrefs.SetInt("Hades_MCP_Port", 51999);
            try
            {
                Assert.IsTrue(HadesSettings.HasLegacyEditorPrefs());
            }
            finally
            {
                UnityEditor.EditorPrefs.DeleteKey("Hades_MCP_Port");
            }
        }
    }
}
```

`HasLegacyEditorPrefs_IsTrue_...` asserts only the positive case on purpose: the negative case cannot be asserted reliably because the developer running the tests may legitimately have legacy prefs set on their machine.

- [ ] **Step 2: Run tests, verify they fail**

Unity Test Runner → EditMode → class `HadesSettingsTests`.
Expected: compile error — `HadesSettings` has no `HadesConfig` constructor and no `HubScope`.

- [ ] **Step 3: Write the implementation**

Replace the whole of `Editor/Core/HadesSettings.cs`:

```csharp
using System;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    public enum ReloadStrategy
    {
        Auto = 0,
        Manual = 1
    }

    /// <summary>
    /// Hades settings for THIS project. Storage is .arcforge/config.local.yaml, not EditorPrefs.
    ///
    /// EditorPrefs is global per Unity install, so two projects on one machine previously shared
    /// port, log level, and Charon retention. The public surface here is unchanged so existing
    /// callers (MCPServer, CharonInitializer) need no edit; only the backing store moved.
    /// </summary>
    public class HadesSettings
    {
        // Legacy EditorPrefs keys, read once during migration and never again.
        const string LegacyPrefix = "Hades_MCP_";
        static readonly string[] LegacyKeys =
        {
            LegacyPrefix + "Port",
            LegacyPrefix + "Enabled",
            LegacyPrefix + "AutoStart",
            LegacyPrefix + "LogLevel",
            LegacyPrefix + "ReloadStrategy",
            LegacyPrefix + "ReloadTimeout",
            LegacyPrefix + "CharonEnabled",
            LegacyPrefix + "CharonRetentionDays",
            LegacyPrefix + "CharonMaxSizeMb"
        };

        const string KeyHubScope = "hub_scope";
        const string KeySkillsScope = "skills_scope";
        const string KeyDesktopIntegration = "desktop_integration";
        const string KeyPort = "mcp_port";
        const string KeyEnabled = "mcp_enabled";
        const string KeyAutoStart = "mcp_auto_start";
        const string KeyLogLevel = "mcp_log_level";
        const string KeyReloadStrategy = "domain_reload_strategy";
        const string KeyReloadTimeout = "reload_timeout_seconds";
        const string KeyCharonEnabled = "charon_enabled";
        const string KeyCharonRetentionDays = "charon_retention_days";
        const string KeyCharonMaxSizeMb = "charon_max_size_mb";

        readonly HadesConfig _config;

        public HadesSettings() : this(HadesConfig.Load(HadesPaths.ArcforgeDir)) { }

        public HadesSettings(HadesConfig config)
        {
            _config = config;
        }

        public HadesScope HubScope
        {
            get => ParseScope(_config.GetString(KeyHubScope, "local"));
            set => SetAndSave(KeyHubScope, ScopeToString(value));
        }

        public HadesScope SkillsScope
        {
            get => ParseScope(_config.GetString(KeySkillsScope, "local"));
            set => SetAndSave(KeySkillsScope, ScopeToString(value));
        }

        public bool DesktopIntegration
        {
            get => _config.GetBool(KeyDesktopIntegration, true);
            set => SetAndSave(KeyDesktopIntegration, value);
        }

        public int Port
        {
            get => _config.GetInt(KeyPort, 0);
            set => SetAndSave(KeyPort, value);
        }

        public bool Enabled
        {
            get => _config.GetBool(KeyEnabled, true);
            set => SetAndSave(KeyEnabled, value);
        }

        public bool AutoStart
        {
            get => _config.GetBool(KeyAutoStart, true);
            set => SetAndSave(KeyAutoStart, value);
        }

        public int LogLevel
        {
            get => _config.GetInt(KeyLogLevel, 1);
            set => SetAndSave(KeyLogLevel, value);
        }

        public ReloadStrategy DomainReloadStrategy
        {
            get => string.Equals(_config.GetString(KeyReloadStrategy, "auto"), "manual",
                       StringComparison.OrdinalIgnoreCase)
                ? ReloadStrategy.Manual
                : ReloadStrategy.Auto;
            set => SetAndSave(KeyReloadStrategy,
                value == ReloadStrategy.Manual ? "manual" : "auto");
        }

        public int ReloadTimeoutSeconds
        {
            get => _config.GetInt(KeyReloadTimeout, 120);
            set => SetAndSave(KeyReloadTimeout, value);
        }

        public bool CharonEnabled
        {
            get => _config.GetBool(KeyCharonEnabled, true);
            set => SetAndSave(KeyCharonEnabled, value);
        }

        public int CharonRetentionDays
        {
            get => _config.GetInt(KeyCharonRetentionDays, 30);
            set => SetAndSave(KeyCharonRetentionDays, value);
        }

        // Hard size cap for traces.db. Time-based retention alone let the trace DB grow into
        // the multi-GB range on a large, heavily-used project; this is the backstop. 0 disables.
        public int CharonMaxSizeMb
        {
            get => _config.GetInt(KeyCharonMaxSizeMb, 500);
            set => SetAndSave(KeyCharonMaxSizeMb, value);
        }

        void SetAndSave(string key, string value) { _config.Set(key, value); _config.Save(); }
        void SetAndSave(string key, bool value) { _config.Set(key, value); _config.Save(); }
        void SetAndSave(string key, int value) { _config.Set(key, value); _config.Save(); }

        static HadesScope ParseScope(string raw)
            => string.Equals(raw, "global", StringComparison.OrdinalIgnoreCase)
                ? HadesScope.Global
                : HadesScope.Local;

        static string ScopeToString(HadesScope scope)
            => scope == HadesScope.Global ? "global" : "local";

        // ---- Migration (spec §4.7) ----

        public static bool HasLegacyEditorPrefs()
        {
            foreach (var key in LegacyKeys)
                if (EditorPrefs.HasKey(key)) return true;
            return false;
        }

        /// <summary>
        /// Copies legacy EditorPrefs values into <paramref name="config"/>. Does not save —
        /// the caller decides when to write. EditorPrefs keys are left in place, because another
        /// project on this machine may not have migrated yet.
        /// </summary>
        public static void ImportFromEditorPrefs(HadesConfig config)
        {
            config.Set(KeyPort, EditorPrefs.GetInt(LegacyPrefix + "Port", 0));
            config.Set(KeyEnabled, EditorPrefs.GetBool(LegacyPrefix + "Enabled", true));
            config.Set(KeyAutoStart, EditorPrefs.GetBool(LegacyPrefix + "AutoStart", true));
            config.Set(KeyLogLevel, EditorPrefs.GetInt(LegacyPrefix + "LogLevel", 1));
            config.Set(KeyReloadStrategy,
                EditorPrefs.GetInt(LegacyPrefix + "ReloadStrategy", 0) == 1 ? "manual" : "auto");
            config.Set(KeyReloadTimeout, EditorPrefs.GetInt(LegacyPrefix + "ReloadTimeout", 120));
            config.Set(KeyCharonEnabled, EditorPrefs.GetBool(LegacyPrefix + "CharonEnabled", true));
            config.Set(KeyCharonRetentionDays,
                EditorPrefs.GetInt(LegacyPrefix + "CharonRetentionDays", 30));
            config.Set(KeyCharonMaxSizeMb, EditorPrefs.GetInt(LegacyPrefix + "CharonMaxSizeMb", 500));
        }

        /// <summary>
        /// Creates .arcforge/config.local.yaml if absent, offering a one-time import of legacy
        /// EditorPrefs values. The file is always created so the prompt never repeats. Silent
        /// (defaults only) in batch mode — nothing may block a headless editor.
        /// </summary>
        public static void EnsureMigrated()
        {
            var config = HadesConfig.Load(HadesPaths.ArcforgeDir);
            if (config.Exists) return;

            var import = HasLegacyEditorPrefs()
                && !Application.isBatchMode
                && EditorUtility.DisplayDialog(
                    "Hades — Import Settings?",
                    "Hades found existing settings stored globally in this Unity install.\n\n" +
                    "Import them into this project?\n\n" +
                    "Settings are now stored per-project in .arcforge/config.local.yaml.",
                    "Import", "Use Defaults");

            if (import) ImportFromEditorPrefs(config);

            config.Save();
        }
    }
}
```

- [ ] **Step 4: Wire the real scope into HadesPaths**

In `Editor/Core/HadesPaths.cs`, replace the Task 3 placeholder:

```csharp
        public static string HubDir => ResolveHubDir(
            Environment.GetEnvironmentVariable(EnvHubDir),
            new HadesSettings().HubScope,
            PathSandbox.ProjectRoot,
            HomeDir);
```

- [ ] **Step 5: Run tests, verify they pass**

Unity Test Runner → EditMode → **Run All**. Expected: `HadesSettingsTests` 8/8 pass, and `HadesConfigTests`, `HadesPathsTests`, `MCPServerIntegrationTests`, `HubClientTests` all still pass. `MCPServerIntegrationTests` passing unchanged is the proof that the public API was preserved.

- [ ] **Step 6: Commit**

```bash
git add Editor/Core/HadesSettings.cs Editor/Core/HadesPaths.cs \
        Tests/Editor/Core/HadesSettingsTests.cs Tests/Editor/Core/HadesSettingsTests.cs.meta
git commit -m "feat: store Hades settings per-project instead of in EditorPrefs"
```

---

## Task 5: Reuse the shared parser in InferenceConfig

**Files:**
- Modify: `Editor/Asphodel/Inference/InferenceConfig.cs:16-64`

**Interfaces:**
- Consumes: `HadesConfig.Parse` (Task 2).
- Produces: no API change. `InferenceConfig.LoadFromDirectory(string arcforgeDir)` keeps reading `config.yaml` (team-shared), **not** `config.local.yaml`.

Behaviour change, intentional and strictly safer: comment lines are now skipped. Previously a line like `# enabled: false` parsed to the key `"# enabled"`, which matched nothing but was sloppy.

- [ ] **Step 1: Replace the hand-rolled loop with the shared parser**

In `Editor/Asphodel/Inference/InferenceConfig.cs`, replace the body of `LoadFromDirectory` from the `foreach (var line in ...)` loop onward:

```csharp
        public static InferenceConfig LoadFromDirectory(string arcforgeDir)
        {
            var config = new InferenceConfig();
            var configPath = System.IO.Path.Combine(arcforgeDir, "config.yaml");
            if (!System.IO.File.Exists(configPath)) return config;

            // Same flat `key: value` dialect HadesConfig reads — shared so the two config files
            // can never drift in how they parse. Note this reads config.yaml (team-shared,
            // git-tracked), NOT config.local.yaml (per-developer).
            var values = Core.HadesConfig.Parse(System.IO.File.ReadAllLines(configPath));

            foreach (var pair in values)
            {
                var key = pair.Key;
                var value = pair.Value;

                switch (key)
                {
                    case "enabled":
                        if (bool.TryParse(value, out var e)) config.Enabled = e;
                        break;
                    case "promotion_confidence_threshold":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var pct))
                            config.PromotionConfidenceThreshold = pct;
                        break;
                    case "promotion_sample_minimum":
                        if (int.TryParse(value, out var psm)) config.PromotionSampleMinimum = psm;
                        break;
                    case "deferred_cooldown_days":
                        if (int.TryParse(value, out var dcd)) config.DeferredCooldownDays = dcd;
                        break;
                    case "max_trace_lookback_days":
                        if (int.TryParse(value, out var mtl)) config.MaxTraceLookbackDays = mtl;
                        break;
                    case "acceptance_rate":
                        if (bool.TryParse(value, out var ar)) config.AcceptanceRateEnabled = ar;
                        break;
                    case "topic_cluster":
                        if (bool.TryParse(value, out var tc)) config.TopicClusterEnabled = tc;
                        break;
                    case "time_of_day":
                        if (bool.TryParse(value, out var tod)) config.TimeOfDayEnabled = tod;
                        break;
                    case "failure_correlation":
                        if (bool.TryParse(value, out var fc)) config.FailureCorrelationEnabled = fc;
                        break;
                }
            }

            return config;
        }
```

`HadesConfig.Parse` is `internal`, and both files live in the `ArcForge.Hades.Editor` assembly, so this resolves.

- [ ] **Step 2: Run the Asphodel tests**

Unity Test Runner → EditMode → run the `Tests/Editor/Asphodel` group. Expected: all pass, no behaviour change.

- [ ] **Step 3: Commit**

```bash
git add Editor/Asphodel/Inference/InferenceConfig.cs
git commit -m "refactor: share the flat config parser with InferenceConfig"
```

---

## Task 6: Point HubClient at the resolved hub dir

**Files:**
- Modify: `Editor/MCP/HubClient.cs:22-27`

**Interfaces:**
- Consumes: `HadesPaths.HubDir` (Tasks 3, 4).
- Produces: no API change. `HubJsonPath` and `PendingDir` now follow the resolved hub dir.

The existing `HubClientTests` all pass explicit paths (`ReadHubInfo(path)`, `WriteBreadcrumb(pendingDir, ...)`), so they are unaffected and act as the regression guard.

- [ ] **Step 1: Replace the `$HOME` constant with the resolver**

In `Editor/MCP/HubClient.cs`, replace lines 22-27:

```csharp
        // Resolved per call rather than cached in a static readonly: the hub scope is a
        // user-facing setting, so the directory can change within an editor session.
        static string HubDir => Core.HadesPaths.HubDir;

        static string HubJsonPath => Path.Combine(HubDir, "hub.json");
        static string PendingDir => Path.Combine(HubDir, "pending");
```

Remove the now-unused `using System;` only if nothing else in the file needs it — `Exception` is used in `WriteBreadcrumb` and `PostToHub`, so **keep** it.

- [ ] **Step 2: Run tests**

Unity Test Runner → EditMode → class `HubClientTests`. Expected: 7/7 pass.

- [ ] **Step 3: Commit**

```bash
git add Editor/MCP/HubClient.cs
git commit -m "fix: resolve HubClient hub dir via HadesPaths"
```

---

## Task 7: Fix package path resolution (git-URL UPM installs)

**Files:**
- Modify: `Editor/Core/MCPClientConfig.cs:303-317` (`FindPackageSkillsDir`), `:338-350` (`FindPackageLauncherDir`)

**Interfaces:**
- Produces: `static string MCPClientConfig.PackageRoot()` — the installed package's resolved root, or the dev repo root as fallback.

This is spec §1.2 and §4.5. Both current methods guess `<project>/Packages/com.arcforge.hades`, which exists only for an *embedded* package. The documented install (Package Manager → Add package from git URL) resolves to `Library/PackageCache/com.arcforge.hades@<hash>`, so both return `null` and the launcher copy plus skills install silently no-op. `PackageInfo.FindForAssembly(...).resolvedPath` covers embedded, registry, git, and local-disk installs alike — the pattern already used at `GraphBuilder.cs:858` and `CharonDashboard.cs:75`.

- [ ] **Step 1: Add the shared resolver**

Add to `Editor/Core/MCPClientConfig.cs`:

```csharp
        /// <summary>
        /// The installed package root, whatever the install channel — embedded (Packages/),
        /// registry, git URL, or local disk all resolve through PackageInfo. Falls back to the
        /// project root, which is correct when running Hades from a source checkout.
        ///
        /// Do NOT reintroduce a hardcoded "Packages/com.arcforge.hades" guess: a git-URL install
        /// lands in Library/PackageCache/com.arcforge.hades@&lt;hash&gt; and the guess silently
        /// misses, leaving the launcher uncopied and no .mcp.json written.
        /// </summary>
        static string PackageRoot()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo
                    .FindForAssembly(typeof(MCPClientConfig).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath)
                    && Directory.Exists(info.resolvedPath))
                    return info.resolvedPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Package path resolution failed: {ex.Message}");
            }

            return PathSandbox.ProjectRoot;
        }
```

- [ ] **Step 2: Rewrite both finders on top of it**

Replace `FindPackageSkillsDir` and `FindPackageLauncherDir` entirely:

```csharp
        static string FindPackageSkillsDir()
        {
            var skillsDir = Path.Combine(PackageRoot(), "skills");
            return Directory.Exists(skillsDir) ? skillsDir : null;
        }

        static string FindPackageLauncherDir()
        {
            var launcherDir = Path.Combine(PackageRoot(), "Bridge~", "launcher");
            return Directory.Exists(launcherDir) ? launcherDir : null;
        }
```

- [ ] **Step 3: Verify against a real git-URL install**

This cannot be unit-tested — `PackageInfo` needs a real Unity package context.

1. In a scratch Unity project, Package Manager → **+** → *Add package from git URL* → `https://github.com/TheArcForge/Hades.git` (or *Add package from disk* pointing at this working tree, which also exercises `PackageInfo`).
2. Open the project and watch the Console.

Expected: no `[Hades] Failed to copy launcher` warning, and `<hubDir>/launcher.js` exists. Before this fix, neither the launcher copy nor `.mcp.json` appeared at all.

- [ ] **Step 4: Commit**

```bash
git add Editor/Core/MCPClientConfig.cs
git commit -m "fix: resolve package root via PackageInfo, not a Packages/ path guess

A git-URL UPM install resolves to Library/PackageCache/com.arcforge.hades@<hash>,
so the hardcoded Packages/com.arcforge.hades guess always missed and the launcher
copy plus .mcp.json write silently no-opped on the documented install path."
```

---

## Task 8: Launcher copy, hub-path.json, and .mcp.json follow the hub dir

**Files:**
- Modify: `Editor/Core/MCPClientConfig.cs:27-62` (`EnsureStableLauncher`), `:319-336` (`WriteHubPath`)

**Interfaces:**
- Consumes: `HadesPaths.HubDir` (Tasks 3, 4), `PackageRoot()` (Task 7).
- Produces: no signature change. `EnsureStableLauncher` now writes into the resolved hub dir.

`WriteProjectMcpJson` needs **no** change: it already writes whatever launcher path it is handed, and `.mcp.json` is already gitignored (`.gitignore:53`) and already machine-specific.

- [ ] **Step 1: Resolve the hub dir instead of hardcoding `$HOME`**

In `Editor/Core/MCPClientConfig.cs`, replace lines 29-31 inside `EnsureStableLauncher`:

```csharp
            var hubDir = HadesPaths.HubDir;
```

Delete the now-unused `Environment.GetFolderPath(...)` expression and the `".arcforge", "hades-hub"` literals. The rest of the method — the bundle-invariant comment at lines 44-49, the copy, the `WriteHubPath` call — stays as is.

Update the summary comment on the method:

```csharp
        /// <summary>
        /// Copies the launcher to &lt;hubDir&gt;/launcher.js and writes hub-path.json beside it.
        /// hubDir is resolved by HadesPaths — the project's .arcforge/hades-hub by default.
        /// Returns the stable launcher path, or null if it can't be resolved.
        /// </summary>
```

- [ ] **Step 2: Verify `WriteHubPath` needs no change**

`WriteHubPath(packageLauncherDir, hubDir)` already takes `hubDir` as a parameter and derives the hub entry from `packageLauncherDir`. With Task 7 in place, `packageLauncherDir` is the real resolved package path, so `hubEntry` correctly points at `<packageRoot>/Bridge~/hub/dist/index.js` — inside `Library/PackageCache/`, i.e. inside the workspace. No edit needed. Confirm by reading the method.

- [ ] **Step 3: Verify in the editor**

Restart Unity in a project with the package installed. Expected:
- `<projectRoot>/.arcforge/hades-hub/launcher.js` exists
- `<projectRoot>/.arcforge/hades-hub/hub-path.json` contains a `hubEntry` under the package root
- `<projectRoot>/.mcp.json` `args[0]` is the path from the first bullet
- `~/.arcforge/hades-hub/` is **not** created on a machine that never had it

- [ ] **Step 4: Commit**

```bash
git add Editor/Core/MCPClientConfig.cs
git commit -m "feat: write the stable launcher into the resolved hub dir"
```

---

## Task 9: Skills scope

**Files:**
- Modify: `Editor/Core/MCPClientConfig.cs:12-21` (`OnServerStart`), `:268-301` (`InstallSkillsForDesktop`)

**Interfaces:**
- Consumes: `HadesSettings.SkillsScope` (Task 4).
- Produces: `static void MCPClientConfig.InstallSkills(HadesScope scope)`, replacing `InstallSkillsForDesktop()`.

Local target is `<projectRoot>/.claude/skills/hades-<name>/SKILL.md` — Claude Code reads project-scoped skills. Global target is unchanged (`~/.claude/skills/`), required for Claude Desktop, which does not.

- [ ] **Step 1: Load settings once in `OnServerStart` and thread them through**

Replace `OnServerStart`:

```csharp
        public static void OnServerStart(int port)
        {
            var launcherPath = EnsureStableLauncher();
            if (launcherPath == null) return;

            var settings = new HadesSettings();

            if (settings.DesktopIntegration)
                UpdateClaudeDesktopConfig(launcherPath);

            WriteProjectMcpJson(launcherPath);
            WriteProjectClaudeMd();
            InstallSkills(settings.SkillsScope);
        }
```

The `settings.DesktopIntegration` gate is Task 10's concern but lands here because it is the same three lines; Task 10 adds its verification.

- [ ] **Step 2: Rewrite the skills installer**

Replace `InstallSkillsForDesktop`:

```csharp
        /// <summary>
        /// Installs Hades skills so a Claude client can discover them. Runs on every startup to
        /// keep them in sync with the installed package version.
        ///
        /// Local scope targets &lt;projectRoot&gt;/.claude/skills/, which Claude Code reads —
        /// nothing leaves the workspace. Global scope targets ~/.claude/skills/, which is the only
        /// location Claude Desktop reads.
        /// </summary>
        static void InstallSkills(HadesScope scope)
        {
            try
            {
                var skillsRoot = scope == HadesScope.Global
                    ? Path.Combine(HadesPaths.HomeDir, ".claude", "skills")
                    : Path.Combine(PathSandbox.ProjectRoot, ".claude", "skills");

                var packageSkillsDir = FindPackageSkillsDir();
                if (packageSkillsDir == null) return;

                foreach (var skillDir in Directory.GetDirectories(packageSkillsDir))
                {
                    var skillName = Path.GetFileName(skillDir);
                    var skillFile = Path.Combine(skillDir, "SKILL.md");
                    if (!File.Exists(skillFile)) continue;

                    var targetDir = Path.Combine(skillsRoot, "hades-" + skillName);
                    if (!Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    File.Copy(skillFile, Path.Combine(targetDir, "SKILL.md"), true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to install skills: {ex.Message}");
            }
        }
```

`FindPackageSkillsDir` (Task 7) already returns `null` unless the directory exists, so the old `|| !Directory.Exists(...)` check is redundant.

- [ ] **Step 3: Verify in the editor**

1. Default settings, restart Unity. Expected: 22 `hades-*` directories under `<projectRoot>/.claude/skills/`. Run `claude` from the project dir and confirm the skills list includes them.
2. Project Settings → Hades → Skills scope → Global (available after Task 12; until then set `skills_scope: global` in `.arcforge/config.local.yaml` by hand), restart Unity. Expected: they appear under `~/.claude/skills/`.

- [ ] **Step 4: Commit**

```bash
git add Editor/Core/MCPClientConfig.cs
git commit -m "feat: install skills into the project by default, global by setting"
```

---

## Task 10: Desktop integration gate

**Files:**
- Modify: `Editor/Core/MCPClientConfig.cs:64-66` (doc comment on `UpdateClaudeDesktopConfig`)

**Interfaces:**
- Consumes: `HadesSettings.DesktopIntegration` (Task 4). The call-site gate landed in Task 9 Step 1.

Spec §4.6. An existing `mcpServers.hades` entry is deliberately **left in place** when the setting is turned off: removing an entry Hades does not exclusively own is riskier than leaving a harmless one that points at a launcher which starts a hub on demand.

- [ ] **Step 1: Document the gate on the method**

Replace the summary comment above `UpdateClaudeDesktopConfig`:

```csharp
        /// <summary>
        /// Writes/updates claude_desktop_config.json so Claude Desktop (Chat/Cowork) can reach
        /// the hub.
        ///
        /// This is the one Hades write that CANNOT be project-local: Claude Desktop is a single
        /// global application with exactly one config file. Gated by the `desktop_integration`
        /// setting (default on) so a user who never opens Desktop can have Hades write nothing
        /// outside the workspace. Turning the setting off does not remove an existing entry —
        /// Hades does not exclusively own this file, and a stale entry is harmless.
        /// </summary>
```

- [ ] **Step 2: Verify both states**

1. Default (`desktop_integration` absent or `true`): restart Unity, confirm `mcpServers.hades` in `~/Library/Application Support/Claude/claude_desktop_config.json` has the current launcher path.
2. Set `desktop_integration: false` in `.arcforge/config.local.yaml`. Note the file's mtime (`ls -l "$HOME/Library/Application Support/Claude/claude_desktop_config.json"`), restart Unity, confirm the mtime is unchanged and the pre-existing entry is still present.

- [ ] **Step 3: Commit**

```bash
git add Editor/Core/MCPClientConfig.cs
git commit -m "feat: gate the Claude Desktop config write behind desktop_integration"
```

---

## Task 11: One-time legacy hub dir notice

**Files:**
- Create: `Editor/Core/LegacyHubNotice.cs`

**Interfaces:**
- Consumes: `HadesPaths.HubDir`, `HadesPaths.GlobalHubDirForMachine` (Tasks 3, 4).
- Produces: `static void LegacyHubNotice.MaybeShow()` and `static bool LegacyHubNotice.ShouldShow(string resolvedHubDir, string globalHubDir, bool alreadyShown, bool globalDirExists)` (pure, testable).

Spec §4.7. Nothing in the old directory is moved or deleted: `launcher.js` and `hub-path.json` are regenerated every startup, while `hub.json`/`hub.lock`/`pending/` are live runtime state of a hub that may be serving another project right now. Hades also cannot know whether another project still needs it.

The shown-flag stays in **EditorPrefs** on purpose — the fact it records (this machine's shared folder is now unused) is machine-global. In `config.local.yaml` the notice would re-appear once per project for a single shared folder.

- [ ] **Step 1: Write the implementation with its decision function split out**

`Editor/Core/LegacyHubNotice.cs`:

```csharp
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Tells the user once that ~/.arcforge/hades-hub/ is no longer used by this project.
    ///
    /// Nothing is moved or deleted, deliberately. launcher.js and hub-path.json are regenerated
    /// on every startup, and hub.json / hub.lock / pending/ are the live runtime state of a hub
    /// process that may be serving a different project — moving them would break its discovery.
    /// Hades also cannot tell whether another project on this machine still depends on the folder,
    /// so deleting it is not Hades' call.
    /// </summary>
    public static class LegacyHubNotice
    {
        // Machine-global on purpose: the fact recorded is machine-global. Stored per project,
        // this notice would fire once per project for one shared folder.
        internal const string ShownKey = "Hades_LegacyHubNoticeShown";

        static readonly StringComparison PathComparison =
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        /// <summary>Pure decision function — no dialogs, no filesystem, no prefs.</summary>
        internal static bool ShouldShow(string resolvedHubDir, string globalHubDir,
            bool alreadyShown, bool globalDirExists)
        {
            if (alreadyShown) return false;
            if (!globalDirExists) return false;
            if (string.IsNullOrEmpty(resolvedHubDir) || string.IsNullOrEmpty(globalHubDir))
                return false;

            // Still using the global dir — there is nothing to tell them.
            return !string.Equals(Normalize(resolvedHubDir), Normalize(globalHubDir),
                PathComparison);
        }

        static string Normalize(string path)
            => path.Replace('\\', '/').TrimEnd('/');

        public static void MaybeShow()
        {
            if (Application.isBatchMode) return;

            try
            {
                var globalHubDir = HadesPaths.GlobalHubDirForMachine;

                if (!ShouldShow(HadesPaths.HubDir, globalHubDir,
                        EditorPrefs.GetBool(ShownKey, false),
                        Directory.Exists(globalHubDir)))
                    return;

                var ok = EditorUtility.DisplayDialog(
                    "Hades — Hub Moved Into This Project",
                    "Hades now keeps its hub inside this project:\n\n" +
                    "    .arcforge/hades-hub/\n\n" +
                    "The old shared folder is no longer used by this project:\n\n" +
                    $"    {globalHubDir}\n\n" +
                    "Other Unity projects may still be using it. It is safe to delete only " +
                    "once every project has been updated.",
                    "OK", "Open Folder");

                // Recorded either way — the notice is informational and must fire only once.
                EditorPrefs.SetBool(ShownKey, true);

                if (!ok) EditorUtility.RevealInFinder(globalHubDir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Legacy hub notice failed: {ex.Message}");
            }
        }
    }
}
```

`EditorUtility.DisplayDialog` returns `true` for the first button ("OK") and `false` for the second ("Open Folder").

- [ ] **Step 2: Write tests for the decision function**

Append to `Tests/Editor/Core/HadesPathsTests.cs` (same concern, same file — the resolver and what the resolver's outcome triggers):

```csharp
    public class LegacyHubNoticeTests
    {
        const string GlobalDir = "/Users/tester/.arcforge/hades-hub";
        const string LocalDir = "/Work/MyGame/.arcforge/hades-hub";

        [Test]
        public void ShouldShow_IsTrue_WhenLocalAndGlobalDirExists()
        {
            Assert.IsTrue(LegacyHubNotice.ShouldShow(LocalDir, GlobalDir, false, true));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenAlreadyShown()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(LocalDir, GlobalDir, true, true));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenGlobalDirAbsent()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(LocalDir, GlobalDir, false, false));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenStillUsingTheGlobalDir()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(GlobalDir, GlobalDir, false, true));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenGlobalDirDiffersOnlyByTrailingSlash()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(GlobalDir + "/", GlobalDir, false, true));
        }
    }
```

Add `using ArcForge.Hades.Editor.Core;` if not already present at the top of the file (it is, from Task 3).

- [ ] **Step 3: Run tests, verify they pass**

Unity Test Runner → EditMode → class `LegacyHubNoticeTests`. Expected: 5/5 pass.

- [ ] **Step 4: Commit**

```bash
git add Editor/Core/LegacyHubNotice.cs Editor/Core/LegacyHubNotice.cs.meta \
        Tests/Editor/Core/HadesPathsTests.cs
git commit -m "feat: one-time notice that the global hub dir is no longer used"
```

---

## Task 12: Bootstrap wiring

**Files:**
- Modify: `Editor/Core/HadesBootstrap.cs:28-59`

**Interfaces:**
- Consumes: `HadesSettings.EnsureMigrated()` (Task 4), `LegacyHubNotice.MaybeShow()` (Task 11).
- Produces: two new entries in `HadesBootstrap.BootTrace` — `"Settings"` first, `"LegacyHubNotice"` in the deferred tick.

Ordering matters. `CharonInitializer` (boot step 1) constructs `new HadesSettings()` at `CharonInitializer.cs:18`, so migration must complete before it runs — otherwise Charon reads defaults and the user's imported values are ignored for that session. The notice goes in the **deferred** tick instead, so its modal dialog cannot delay the MCP server becoming reachable, which the existing class comment names as the boot priority.

`Tests/Editor/Core/HadesBootstrapTests.cs` asserts on `BootTrace`. Read it before editing and update its expectations to include the new steps.

- [ ] **Step 1: Read the existing ordering test**

```bash
cat Tests/Editor/Core/HadesBootstrapTests.cs
```

Note which assertions reference step names or ordering, so Step 3 can update them.

- [ ] **Step 2: Add the boot steps**

In `Editor/Core/HadesBootstrap.cs`, add `Settings` as the first step in `Boot()`:

```csharp
                BootTrace.Clear();
                // FIRST: Charon (next step) constructs HadesSettings, so the project-local
                // settings file must exist and any EditorPrefs import must be done before it.
                Step("Settings",       () => HadesSettings.EnsureMigrated());
                Step("Charon",         () => CharonInitializer.Initialize());
                Step("GraphDb",        () => Graph.GraphInitializer.EnsureDatabase());
                Step("Asphodel",       () => Asphodel.AsphodeInitializer.Initialize());
                Step("MCPServer",      () => MCPServer.StartFromBootstrap());
                Step("GraphEvents",    () => Graph.Updates.GraphUpdateHandler.InitializeFromBootstrap());
                Step("PackageWatcher", () => Graph.Updates.PackageChangeDetector.Initialize());
```

Then extend the deferred tick so the notice's modal dialog cannot delay server reachability:

```csharp
        static void RunStartupSyncOnce()
        {
            AppNapGuard.Acquire();
            try { Graph.Updates.GraphUpdateHandler.RunStartupSync(); }
            finally { AppNapGuard.Release(); }

            // Deferred deliberately: this can show a modal dialog, and the boot path's priority
            // is getting the MCP server reachable first.
            Step("LegacyHubNotice", () => LegacyHubNotice.MaybeShow());
        }
```

- [ ] **Step 3: Update the ordering test**

In `Tests/Editor/Core/HadesBootstrapTests.cs`, add `"Settings"` as the expected first element and assert it precedes `"Charon"`. Follow whatever assertion style the file already uses. If it asserts an exact sequence, insert `"Settings"` at the front; if it asserts relative order, add:

```csharp
        [Test]
        public void Settings_BootsBeforeCharon()
        {
            var trace = HadesBootstrap.BootTrace;
            Assert.Less(trace.IndexOf("Settings"), trace.IndexOf("Charon"),
                "Charon constructs HadesSettings, so settings migration must run first.");
        }
```

- [ ] **Step 4: Run tests, verify they pass**

Unity Test Runner → EditMode → **Run All**. Expected: all pass, including the updated `HadesBootstrapTests`.

- [ ] **Step 5: Commit**

```bash
git add Editor/Core/HadesBootstrap.cs Tests/Editor/Core/HadesBootstrapTests.cs
git commit -m "feat: migrate settings before Charon boots; defer the legacy hub notice"
```

---

## Task 13: Settings UI — Project Settings page and menu item

**Files:**
- Create: `Editor/Core/HadesPreferences.cs`

**Interfaces:**
- Consumes: every `HadesSettings` property (Task 4).
- Produces: a `SettingsProvider` at `Project/Hades`, and the `Hades/Settings…` menu item (priority 300, after the existing Asphodel items at 200-201).

Spec §4.3. This is the decision-1 requirement: the local/global toggle must be reachable from the Unity Editor menu.

- [ ] **Step 1: Write the settings provider**

`Editor/Core/HadesPreferences.cs`:

```csharp
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Project Settings > Hades. Every value here is stored per-project in
    /// .arcforge/config.local.yaml, so two Unity projects on one machine no longer share them.
    /// </summary>
    public static class HadesPreferences
    {
        const string SettingsPath = "Project/Hades";

        static readonly string[] ScopeLabels = { "Local (this project)", "Global (shared)" };
        static readonly int[] ScopeValues = { (int)HadesScope.Local, (int)HadesScope.Global };

        [MenuItem("Hades/Settings...", priority = 300)]
        public static void Open() => SettingsService.OpenProjectSettings(SettingsPath);

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Hades",
                keywords = new[] { "hades", "hub", "mcp", "charon", "skills", "scope" },
                guiHandler = _ => Draw()
            };
        }

        static void Draw()
        {
            var settings = new HadesSettings();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Installation Scope", EditorStyles.boldLabel);

            var hubScope = (HadesScope)EditorGUILayout.IntPopup(
                new GUIContent("Hub",
                    "Local keeps hub.json in this project's .arcforge/hades-hub. " +
                    "Global shares one hub across every project on this machine."),
                (int)settings.HubScope, ScopeLabels, ScopeValues);
            if (hubScope != settings.HubScope) settings.HubScope = hubScope;

            EditorGUILayout.HelpBox(
                "Changing the hub scope takes effect on the next Claude Code session — the " +
                "launcher reads this setting when it starts.", MessageType.Info);

            var skillsScope = (HadesScope)EditorGUILayout.IntPopup(
                new GUIContent("Skills",
                    "Local installs into this project's .claude/skills (Claude Code reads it). " +
                    "Global installs into ~/.claude/skills, which Claude Desktop requires."),
                (int)settings.SkillsScope, ScopeLabels, ScopeValues);
            if (skillsScope != settings.SkillsScope) settings.SkillsScope = skillsScope;

            var desktop = EditorGUILayout.Toggle(
                new GUIContent("Claude Desktop Integration",
                    "Writes ~/Library/Application Support/Claude/claude_desktop_config.json. " +
                    "This file cannot be project-local. Turn off to keep every Hades write " +
                    "inside the workspace."),
                settings.DesktopIntegration);
            if (desktop != settings.DesktopIntegration) settings.DesktopIntegration = desktop;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("MCP Server", EditorStyles.boldLabel);

            var enabled = EditorGUILayout.Toggle("Enabled", settings.Enabled);
            if (enabled != settings.Enabled) settings.Enabled = enabled;

            var autoStart = EditorGUILayout.Toggle("Auto Start", settings.AutoStart);
            if (autoStart != settings.AutoStart) settings.AutoStart = autoStart;

            var port = EditorGUILayout.IntField(
                new GUIContent("Port", "0 lets the OS assign an ephemeral port."), settings.Port);
            if (port != settings.Port) settings.Port = Mathf.Clamp(port, 0, 65535);

            var logLevel = EditorGUILayout.IntSlider("Log Level", settings.LogLevel, 0, 3);
            if (logLevel != settings.LogLevel) settings.LogLevel = logLevel;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Domain Reload", EditorStyles.boldLabel);

            var strategy = (ReloadStrategy)EditorGUILayout.EnumPopup(
                "Strategy", settings.DomainReloadStrategy);
            if (strategy != settings.DomainReloadStrategy) settings.DomainReloadStrategy = strategy;

            var timeout = EditorGUILayout.IntField("Timeout (s)", settings.ReloadTimeoutSeconds);
            if (timeout != settings.ReloadTimeoutSeconds)
                settings.ReloadTimeoutSeconds = Mathf.Max(1, timeout);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Charon (Observability)", EditorStyles.boldLabel);

            var charon = EditorGUILayout.Toggle("Enabled", settings.CharonEnabled);
            if (charon != settings.CharonEnabled) settings.CharonEnabled = charon;

            var retention = EditorGUILayout.IntField("Retention (days)", settings.CharonRetentionDays);
            if (retention != settings.CharonRetentionDays)
                settings.CharonRetentionDays = Mathf.Max(0, retention);

            var maxSize = EditorGUILayout.IntField(
                new GUIContent("Max Size (MB)", "Hard cap on traces.db. 0 disables the cap."),
                settings.CharonMaxSizeMb);
            if (maxSize != settings.CharonMaxSizeMb)
                settings.CharonMaxSizeMb = Mathf.Max(0, maxSize);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Storage", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                System.IO.Path.Combine(HadesPaths.ArcforgeDir, HadesConfig.FileName),
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.SelectableLabel(HadesPaths.HubDir, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }
    }
}
```

Each `if (x != settings.X)` guard matters: every setter writes the file, so assigning unconditionally would rewrite `config.local.yaml` on every OnGUI repaint.

- [ ] **Step 2: Verify in the editor**

1. **Hades → Settings…** opens Project Settings with Hades selected.
2. Change Hub to `Global (shared)`. Confirm `.arcforge/config.local.yaml` now contains `hub_scope: global`.
3. Change it back to Local. Confirm the file says `hub_scope: local`.
4. Toggle every other control once; confirm each round-trips after closing and reopening the window.
5. Open the window and idle on it for a few seconds without touching anything. Confirm `config.local.yaml`'s mtime does **not** change (proves the repaint guards work).

- [ ] **Step 3: Commit**

```bash
git add Editor/Core/HadesPreferences.cs Editor/Core/HadesPreferences.cs.meta
git commit -m "feat: add Project Settings > Hades with hub and skills scope toggles"
```

---

## Task 14: findProjectRoot — distinguish "found" from "fell back"

**Files:**
- Modify: `Bridge~/launcher/src/project-path.ts`
- Test: `Bridge~/tests/launcher/project-path.test.ts` (append; existing tests must stay untouched)

**Interfaces:**
- Produces: `findProjectRoot(cwd: string): string | null`. `resolveProjectPath(cwd: string): string` becomes a wrapper over it and keeps its exact current behaviour.

Rung 2 of the resolution chain requires knowing whether a Unity project was actually found. `resolveProjectPath` currently returns `cwd` on failure, conflating "the project root is cwd" with "no project found". Splitting the two keeps the existing function and its tests intact.

- [ ] **Step 1: Write failing tests**

Append to `Bridge~/tests/launcher/project-path.test.ts` — add `findProjectRoot` to the existing import, then add a new describe block:

```ts
import { resolveProjectPath, findProjectRoot } from "../../launcher/src/project-path.js";
```

```ts
describe("findProjectRoot", () => {
  const made: string[] = [];

  function makeProject(): string {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "hades-unity-"));
    made.push(root);
    fs.mkdirSync(path.join(root, "ProjectSettings"));
    fs.writeFileSync(
      path.join(root, "ProjectSettings", "ProjectVersion.txt"),
      "m_EditorVersion: 6000.0.0f1\n"
    );
    return root;
  }

  afterEach(() => {
    for (const d of made.splice(0)) fs.rmSync(d, { recursive: true, force: true });
  });

  it("returns the project root when cwd IS the project root", () => {
    const root = makeProject();
    expect(findProjectRoot(root)).toBe(root);
  });

  it("walks up from a subdirectory", () => {
    const root = makeProject();
    const sub = path.join(root, "Assets", "Scripts");
    fs.mkdirSync(sub, { recursive: true });
    expect(findProjectRoot(sub)).toBe(root);
  });

  it("returns null when no Unity project is found", () => {
    expect(findProjectRoot("/")).toBeNull();
  });

  it("returns null for a directory tree with no ProjectVersion.txt", () => {
    const plain = fs.mkdtempSync(path.join(os.tmpdir(), "hades-plain-"));
    made.push(plain);
    expect(findProjectRoot(plain)).toBeNull();
  });
});
```

- [ ] **Step 2: Run tests, verify they fail**

```bash
cd Bridge~ && npx vitest run tests/launcher/project-path.test.ts
```

Expected: FAIL — `findProjectRoot is not a function`.

- [ ] **Step 3: Write the implementation**

Replace the body of `Bridge~/launcher/src/project-path.ts`, keeping the existing file header comment:

```ts
import fs from "node:fs";
import path from "node:path";

/**
 * Walks UP from `cwd` looking for a Unity project (marked by
 * `ProjectSettings/ProjectVersion.txt`). Returns null when none is found — e.g. cwd is "/" or
 * sits outside any project.
 *
 * Callers that need to distinguish "found a project" from "gave up" must use this rather than
 * resolveProjectPath: the hub-dir resolution chain only uses the project-local hub when a real
 * project root was found.
 */
export function findProjectRoot(cwd: string): string | null {
  let dir = cwd;
  for (let i = 0; i < 40; i++) {
    if (fs.existsSync(path.join(dir, "ProjectSettings", "ProjectVersion.txt"))) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break; // reached the filesystem root
    dir = parent;
  }
  return null;
}

/**
 * Resolves the Unity project root the launcher belongs to, falling back to `cwd` when no project
 * is found — in that case the hub's single-instance fallback routes the call when only one Unity
 * is open. This is the value sent as the X-Hades-Project header.
 */
export function resolveProjectPath(cwd: string): string {
  return findProjectRoot(cwd) ?? cwd;
}
```

- [ ] **Step 4: Run tests, verify they pass**

```bash
cd Bridge~ && npx vitest run tests/launcher/project-path.test.ts
```

Expected: PASS — 7 tests (3 pre-existing `resolveProjectPath` + 4 new).

- [ ] **Step 5: Commit**

```bash
git add Bridge~/launcher/src/project-path.ts Bridge~/tests/launcher/project-path.test.ts
git commit -m "feat: add findProjectRoot returning null when no Unity project is found"
```

---

## Task 15: hub-dir.ts — the TypeScript resolution chain

**Files:**
- Create: `Bridge~/launcher/src/hub-dir.ts`
- Test: `Bridge~/tests/launcher/hub-dir.test.ts`

**Interfaces:**
- Produces:
  - `ENV_HUB_DIR` → `"HADES_HUB_DIR"`
  - `CONFIG_FILE_NAME` → `"config.local.yaml"`
  - `readHubScope(arcforgeDir: string, readFile: ReadFile): "local" | "global"`
  - `resolveHubDir(opts: { env: NodeJS.ProcessEnv; projectRoot: string | null; readFile: ReadFile }): string`
  - `defaultReadFile(p: string): string | null`
  - `type ReadFile = (p: string) => string | null`

Must mirror `HadesPaths.ResolveHubDir` (Task 3) rung for rung. File reads are injected so tests need no temp directories and no real `$HOME`.

- [ ] **Step 1: Write failing tests**

`Bridge~/tests/launcher/hub-dir.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import path from "node:path";
import {
  resolveHubDir,
  readHubScope,
  ENV_HUB_DIR,
  CONFIG_FILE_NAME,
} from "../../launcher/src/hub-dir.js";

const HOME = "/Users/tester";
const PROJECT = "/Work/MyGame";
const GLOBAL = path.join(HOME, ".arcforge", "hades-hub");
const LOCAL = path.join(PROJECT, ".arcforge", "hades-hub");

/** A readFile stub: maps absolute path -> contents. Anything else reads as missing. */
function files(map: Record<string, string> = {}) {
  return (p: string) => (p in map ? map[p] : null);
}

const CONFIG_PATH = path.join(PROJECT, ".arcforge", CONFIG_FILE_NAME);

describe("readHubScope", () => {
  const arcforge = path.join(PROJECT, ".arcforge");

  it("defaults to local when the config file is missing", () => {
    expect(readHubScope(arcforge, files())).toBe("local");
  });

  it("defaults to local when the key is absent", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "mcp_port: 51234\n" }))).toBe("local");
  });

  it("reads global", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: global\n" }))).toBe("global");
  });

  it("reads local explicitly", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: local\n" }))).toBe("local");
  });

  it("is case-insensitive", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: GLOBAL\n" }))).toBe("global");
  });

  it("ignores comment lines", () => {
    expect(
      readHubScope(arcforge, files({ [CONFIG_PATH]: "# hub_scope: global\n" }))
    ).toBe("local");
  });

  it("defaults to local on an unrecognised value", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: sideways\n" }))).toBe("local");
  });

  it("tolerates CRLF line endings", () => {
    expect(
      readHubScope(arcforge, files({ [CONFIG_PATH]: "mcp_port: 1\r\nhub_scope: global\r\n" }))
    ).toBe("global");
  });

  it("ignores lines without a colon", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "garbage\n" }))).toBe("local");
  });
});

describe("resolveHubDir", () => {
  it("prefers the env override over everything", () => {
    const dir = resolveHubDir({
      env: { HOME, [ENV_HUB_DIR]: "/custom/hub" },
      projectRoot: PROJECT,
      readFile: files(),
    });
    expect(dir).toBe("/custom/hub");
  });

  it("trims the env override", () => {
    const dir = resolveHubDir({
      env: { HOME, [ENV_HUB_DIR]: "  /custom/hub  " },
      projectRoot: PROJECT,
      readFile: files(),
    });
    expect(dir).toBe("/custom/hub");
  });

  it("ignores a whitespace-only env override", () => {
    const dir = resolveHubDir({
      env: { HOME, [ENV_HUB_DIR]: "   " },
      projectRoot: PROJECT,
      readFile: files(),
    });
    expect(dir).toBe(LOCAL);
  });

  it("defaults to the project-local hub dir", () => {
    const dir = resolveHubDir({ env: { HOME }, projectRoot: PROJECT, readFile: files() });
    expect(dir).toBe(LOCAL);
  });

  it("uses the global dir when hub_scope is global", () => {
    const dir = resolveHubDir({
      env: { HOME },
      projectRoot: PROJECT,
      readFile: files({ [CONFIG_PATH]: "hub_scope: global\n" }),
    });
    expect(dir).toBe(GLOBAL);
  });

  it("falls back to the global dir when no project root was found", () => {
    const dir = resolveHubDir({ env: { HOME }, projectRoot: null, readFile: files() });
    expect(dir).toBe(GLOBAL);
  });

  it("uses USERPROFILE when HOME is absent", () => {
    const dir = resolveHubDir({
      env: { USERPROFILE: HOME },
      projectRoot: null,
      readFile: files(),
    });
    expect(dir).toBe(GLOBAL);
  });

  it("falls back to the local dir on a malformed config file", () => {
    const dir = resolveHubDir({
      env: { HOME },
      projectRoot: PROJECT,
      readFile: files({ [CONFIG_PATH]: "  not: [valid\n" }),
    });
    expect(dir).toBe(LOCAL);
  });
});
```

- [ ] **Step 2: Run tests, verify they fail**

```bash
cd Bridge~ && npx vitest run tests/launcher/hub-dir.test.ts
```

Expected: FAIL — cannot resolve `../../launcher/src/hub-dir.js`.

- [ ] **Step 3: Write the implementation**

`Bridge~/launcher/src/hub-dir.ts`:

```ts
import fs from "node:fs";
import path from "node:path";

export const ENV_HUB_DIR = "HADES_HUB_DIR";
export const CONFIG_FILE_NAME = "config.local.yaml";

const ARCFORGE_DIR_NAME = ".arcforge";
const HUB_DIR_NAME = "hades-hub";
const HUB_SCOPE_KEY = "hub_scope";

export type HubScope = "local" | "global";
export type ReadFile = (filePath: string) => string | null;

export interface ResolveHubDirOptions {
  env: NodeJS.ProcessEnv;
  /** Project root, or null when no Unity project was found. See findProjectRoot. */
  projectRoot: string | null;
  readFile: ReadFile;
}

/** Reads a file, returning null for anything unreadable — missing, permission denied, a dir. */
export function defaultReadFile(filePath: string): string | null {
  try {
    return fs.readFileSync(filePath, "utf8");
  } catch {
    return null;
  }
}

/**
 * Reads just `hub_scope` out of .arcforge/config.local.yaml.
 *
 * Deliberately a hand-rolled reader for a single key rather than a YAML dependency: the launcher
 * ships as a zero-dependency esbuild bundle. Mirrors HadesConfig.Parse on the C# side — flat
 * `key: value`, blank/comment/colonless lines skipped. Anything unexpected yields "local", which
 * is the documented default.
 */
export function readHubScope(arcforgeDir: string, readFile: ReadFile): HubScope {
  const raw = readFile(path.join(arcforgeDir, CONFIG_FILE_NAME));
  if (raw === null) return "local";

  for (const line of raw.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;

    const colonIdx = trimmed.indexOf(":");
    if (colonIdx <= 0) continue;

    if (trimmed.slice(0, colonIdx).trim() !== HUB_SCOPE_KEY) continue;

    return trimmed.slice(colonIdx + 1).trim().toLowerCase() === "global" ? "global" : "local";
  }

  return "local";
}

/**
 * Resolves the hub rendezvous directory — where hub.json (port + pid) is published.
 *
 * Must stay rung-for-rung identical to HadesPaths.ResolveHubDir in the Unity assembly:
 *   1. HADES_HUB_DIR env var
 *   2. <projectRoot>/.arcforge/hades-hub   when hub_scope is local
 *   3. $HOME/.arcforge/hades-hub           otherwise, and when projectRoot is unknown
 *
 * Rung 3 is load-bearing, not legacy dead weight: a launcher whose cwd is a `file:`-referenced
 * package repo OUTSIDE the Unity project cannot see the project's hub dir, and must reach the
 * shared hub so Registry.findByProjectPath's manifestPackages match still routes it.
 */
export function resolveHubDir(opts: ResolveHubDirOptions): string {
  const override = opts.env[ENV_HUB_DIR];
  if (override && override.trim()) return override.trim();

  const home = opts.env.HOME ?? opts.env.USERPROFILE ?? "";
  const globalDir = path.join(home, ARCFORGE_DIR_NAME, HUB_DIR_NAME);

  if (!opts.projectRoot) return globalDir;

  const arcforgeDir = path.join(opts.projectRoot, ARCFORGE_DIR_NAME);
  if (readHubScope(arcforgeDir, opts.readFile) === "global") return globalDir;

  return path.join(arcforgeDir, HUB_DIR_NAME);
}
```

- [ ] **Step 4: Run tests, verify they pass**

```bash
cd Bridge~ && npx vitest run tests/launcher/hub-dir.test.ts
```

Expected: PASS — 17 tests (9 readHubScope + 8 resolveHubDir).

- [ ] **Step 5: Commit**

```bash
git add Bridge~/launcher/src/hub-dir.ts Bridge~/tests/launcher/hub-dir.test.ts
git commit -m "feat: add TypeScript hub-dir resolution chain"
```

---

## Task 16: Wire the launcher to the resolver

**Files:**
- Modify: `Bridge~/launcher/src/index.ts:9-14` (HUB_DIR), `:40` (PROJECT_PATH), `:89-97` (`startHub`)

**Interfaces:**
- Consumes: `findProjectRoot` (Task 14), `resolveHubDir` / `defaultReadFile` / `ENV_HUB_DIR` (Task 15).
- Produces: the hub child process receives `HADES_HUB_DIR` in its environment.

- [ ] **Step 1: Replace the hardcoded HUB_DIR with the resolver**

In `Bridge~/launcher/src/index.ts`, update the imports:

```ts
import { resolveProjectPath, findProjectRoot } from "./project-path.js";
import { acquireSpawnLock, releaseSpawnLock } from "./spawn-lock.js";
import { resolveHubDir, defaultReadFile, ENV_HUB_DIR } from "./hub-dir.js";
```

Then replace lines 9-14 (the `HUB_DIR` constant), keeping `HUB_JSON_PATH` and `HUB_ENTRY` in their current order so `findHubEntry()` still runs after `HUB_DIR` is initialised:

```ts
const PROJECT_ROOT = findProjectRoot(process.cwd());
const HUB_DIR = resolveHubDir({
  env: process.env,
  projectRoot: PROJECT_ROOT,
  readFile: defaultReadFile,
});
const HUB_JSON_PATH = path.join(HUB_DIR, "hub.json");
const HUB_ENTRY = findHubEntry();
```

Delete the old standalone `const PROJECT_PATH = resolveProjectPath(process.cwd());` at line 40 and replace it with the already-computed root:

```ts
const PROJECT_PATH = PROJECT_ROOT ?? process.cwd();
```

This is exactly `resolveProjectPath(process.cwd())` without walking the tree twice. `resolveProjectPath` stays exported and tested in `project-path.ts` as the documented single-call form; if your linter flags the now-unused import, drop `resolveProjectPath` from the import list in this file only — do **not** delete the function.

- [ ] **Step 2: Pass the resolved dir to the hub**

In `startHub()`, replace the `env` line so the hub never has to re-derive the directory:

```ts
function startHub(): void {
  process.stderr.write("[hades-launcher] Starting hub...\n");
  const child = spawn("node", [HUB_ENTRY], {
    detached: true,
    stdio: "ignore",
    // Hand the resolved dir down explicitly. If the hub re-derived it from $HOME it could
    // disagree with this launcher and publish hub.json where nobody is looking.
    env: { ...process.env, [ENV_HUB_DIR]: HUB_DIR },
  });
  child.unref();
}
```

- [ ] **Step 3: Typecheck and run the full launcher suite**

```bash
cd Bridge~ && npx vitest run tests/launcher/
```

Expected: PASS. `bundle.test.ts` may fail until Task 18 rebuilds `dist/` — that is expected here.

```bash
cd Bridge~/launcher && npx tsc --noEmit
```

Expected: no output (clean).

- [ ] **Step 4: Commit**

```bash
git add Bridge~/launcher/src/index.ts
git commit -m "feat: resolve the launcher hub dir and pass it to the hub"
```

---

## Task 17: Wire the hub to HADES_HUB_DIR

**Files:**
- Modify: `Bridge~/hub/src/index.ts:10-16`

**Interfaces:**
- Consumes: `HADES_HUB_DIR` from the environment (Task 16).
- Produces: no API change. `HUB_JSON_PATH` and `PENDING_DIR` follow the injected dir.

- [ ] **Step 1: Read the env var, keep `$HOME` as fallback**

In `Bridge~/hub/src/index.ts`, replace lines 10-16:

```ts
// Handed down by the launcher that spawned this hub (see startHub). The $HOME fallback covers a
// hub started by hand or by an older launcher. The hub deliberately does NOT re-derive this from
// a project root: only the launcher knows which project it was invoked for.
const HUB_DIR =
  process.env.HADES_HUB_DIR?.trim() ||
  path.join(
    process.env.HOME ?? process.env.USERPROFILE ?? "",
    ".arcforge",
    "hades-hub"
  );
const HUB_JSON_PATH = path.join(HUB_DIR, "hub.json");
const PENDING_DIR = path.join(HUB_DIR, "pending");
```

- [ ] **Step 2: Typecheck and run the hub suite**

```bash
cd Bridge~/hub && npx tsc --noEmit
cd Bridge~ && npx vitest run tests/hub/
```

Expected: clean typecheck; all hub tests pass.

- [ ] **Step 3: Commit**

```bash
git add Bridge~/hub/src/index.ts
git commit -m "feat: hub reads its directory from HADES_HUB_DIR"
```

---

## Task 18: Rebuild the bundle and confirm the invariants

**Files:**
- Modify: `Bridge~/launcher/dist/index.js`, `Bridge~/hub/dist/*` (build output, tracked in this repo)

**Interfaces:**
- Consumes: everything from Tasks 14-17.
- Produces: rebuilt `dist/` output that `sync-plugin.sh` and the release workflow copy verbatim.

`dist/` is tracked (`Bridge~/hub/dist/*.js` and `Bridge~/launcher/dist/index.js` are in the repo, and `sync-plugin.sh` refuses to run without them), so the rebuild must be committed.

- [ ] **Step 1: Build**

```bash
cd Bridge~ && npm run build
```

Expected: no errors. This runs `tsc` for the hub, `tsc --noEmit` for the launcher, then esbuild bundles the launcher to `dist/index.js`.

- [ ] **Step 2: Run the whole Bridge suite**

```bash
cd Bridge~ && npm test
```

Expected: all suites pass, **including** `bundle.test.ts`. Its three assertions are the guard that matters here: `dist/index.js` exists, contains no relative imports (proving `hub-dir.js` was inlined, not left as a runtime import that the single-file copy would fail on), and `dist/` holds only `index.js`.

If `bundle.test.ts` reports relative imports, the launcher is importing something esbuild could not inline — check that `hub-dir.ts` uses only `node:fs` / `node:path` and has no dynamic `import()`.

- [ ] **Step 3: Confirm the resolver is actually in the bundle**

```bash
grep -c 'hub_scope' Bridge~/launcher/dist/index.js
```

Expected: `1` or more. Zero means the module was tree-shaken out and the launcher is still using the old path.

- [ ] **Step 4: Commit**

```bash
git add Bridge~/launcher/dist/index.js Bridge~/hub/dist
git commit -m "build: rebuild Bridge with project-local hub dir resolution"
```

---

## Task 19: Gitignore the new project-local artifacts

**Files:**
- Modify: `.gitignore`

**Interfaces:** none.

`.arcforge/config.local.yaml` is per-developer and machine-specific. `.arcforge/hades-hub/` is pure runtime state — a port file, a lock, a copied bundle. Note that `.gitignore:56` currently un-ignores `!.arcforge/memory/`, so ordering matters: keep the new rules with the other `.arcforge` entries above that negation.

- [ ] **Step 1: Add the rules**

In `.gitignore`, alongside the existing `.arcforge` block (lines 48-52), add:

```gitignore
.arcforge/config.local.yaml
.arcforge/hades-hub/
```

- [ ] **Step 2: Verify the rules match, and that memory is still tracked**

```bash
git check-ignore -v .arcforge/config.local.yaml .arcforge/hades-hub/hub.json
```

Expected: both lines report a match against the rules just added.

```bash
git check-ignore -v .arcforge/memory/some-file.md
```

Expected: **no** match (exit code 1) — the `!.arcforge/memory/` negation must still win.

- [ ] **Step 3: Commit**

```bash
git add .gitignore
git commit -m "chore: gitignore config.local.yaml and the project-local hub dir"
```

---

## Task 20: End-to-end verification on a real UPM install

**Files:** none — this is spec §6's manual matrix. Task 7's fix means this path was previously broken, so it cannot be skipped.

**Interfaces:** exercises everything above.

- [ ] **Step 1: Fresh install, default settings**

Create a scratch Unity 6000.0+ project. Package Manager → **+** → *Add package from disk* → this working tree's `package.json`. Open the project and watch the Console.

Expected:
- No `[Hades]` warnings.
- `[Hades MCP] Server running on {endpoint}` appears.
- `<projectRoot>/.arcforge/hades-hub/launcher.js` exists.
- `<projectRoot>/.arcforge/hades-hub/hub-path.json` points at the package's `Bridge~/hub/dist/index.js`.
- `<projectRoot>/.mcp.json` `args[0]` equals that `launcher.js` path.
- `<projectRoot>/.arcforge/config.local.yaml` exists.
- 22 `hades-*` dirs under `<projectRoot>/.claude/skills/`.

- [ ] **Step 2: Connection works**

```bash
cd <scratch project> && claude
```

Then run `/hades:status`. Expected: graph node/edge counts and a connected hub.

- [ ] **Step 3: hub.json is project-local**

```bash
cat <scratch project>/.arcforge/hades-hub/hub.json
```

Expected: valid JSON with `port`, `pid`, `startedAt`, and that pid is alive (`ps -p <pid>`).

- [ ] **Step 4: The isolation claim — the headline check**

Set all three isolation settings: Project Settings → Hades → Hub `Local`, Skills `Local`, Claude Desktop Integration **off**.

```bash
find "$HOME/.arcforge" "$HOME/.claude/skills" "$HOME/Library/Application Support/Claude" \
  -newermt '-2 minutes' 2>/dev/null
```

Restart Unity, wait for boot to finish, then re-run that `find`. Expected: **no output** — nothing under `$HOME` was created or modified.

- [ ] **Step 5: Two projects, both local**

Open a second Unity project with the package installed. Expected: two hub processes (`ps aux | grep hades-hub`), each project's `hub.json` holding a different port, and `/hades:status` from each project directory reporting that project's own graph counts.

- [ ] **Step 6: Global mode still works**

In project one: Project Settings → Hades → Hub → `Global (shared)`. Restart Unity, then start a **fresh** `claude` session in that project dir (the launcher reads the setting at process start).

Expected: `~/.arcforge/hades-hub/hub.json` exists, `<projectRoot>/.arcforge/hades-hub/hub.json` is stale or absent, and `/hades:status` still reports the correct project.

- [ ] **Step 7: Env override wins**

```bash
cd <scratch project> && HADES_HUB_DIR=/tmp/hades-test-hub claude
```

Run `/hades:status`. Expected: `/tmp/hades-test-hub/hub.json` is created. Unity must also be pointed there for a full connection — the point of this step is confirming the launcher honours rung 1.

- [ ] **Step 8: Legacy notice fires exactly once**

```bash
mkdir -p "$HOME/.arcforge/hades-hub"
```

In the Unity Console, clear the shown-flag so the notice can fire again: **Hades → Settings…** is not the place for this; use the Console's command line or add a temporary menu item, or simply run in a fresh Unity install. Simplest reliable route: `EditorPrefs.DeleteKey("Hades_LegacyHubNoticeShown")` via a temporary `[MenuItem]`, then restart Unity.

Expected: the notice appears once. Restart again — it does **not** reappear. Restart after a domain reload (edit any script) — it does not reappear.

- [ ] **Step 9: Settings import prompt**

Delete `<projectRoot>/.arcforge/config.local.yaml`, set a legacy pref (`EditorPrefs.SetInt("Hades_MCP_Port", 51999)` via a temporary menu item), restart Unity.

Expected: the import prompt appears. Choose **Import**; confirm `config.local.yaml` contains `mcp_port: 51999`. Restart — the prompt does not reappear.

- [ ] **Step 10: Record the results**

Note any deviation in the PR description. Do not mark this task complete on partial verification — steps 4 and 5 are the two that prove the feature.

---

## Task 21: Documentation

**Files:**
- Modify: `Documentation/getting-started.md`, `Documentation/troubleshooting.md`, `Documentation/arcforge-hades-architecture.md`
- Modify: `CHANGELOG.md`

**Interfaces:** none.

- [ ] **Step 1: Add an "Installation scope" section to getting-started.md**

Insert after Step 1 (the Unity package install). Content to write:

- Hades stores everything for a project inside that project by default: `.arcforge/` (graph, traces, memory, hub, settings), `.mcp.json`, `.claude/skills/`, `CLAUDE.md`.
- Two settings change that, both at **Project Settings → Hades** (or the **Hades → Settings…** menu): **Hub scope** and **Skills scope**, each Local or Global.
- Global hub = one shared hub process for every Unity project on the machine, with state in `~/.arcforge/hades-hub/`. Choose it if you work across a Unity project and a separate `file:`-referenced package repo in the same Claude Code session — a project-local hub is invisible from outside the project directory.
- Global skills = `~/.claude/skills/`. Required for Claude Desktop, which does not read project-scoped skills. Claude Code reads both.
- `HADES_HUB_DIR` overrides the hub directory for a single launcher process, ignoring both the setting and the default.
- Changing hub scope takes effect on the next Claude Code session, because the launcher reads it at startup.
- **The two things that stay outside your workspace**, stated plainly:
  1. `~/Library/Application Support/Claude/claude_desktop_config.json` (Windows: `%APPDATA%\Claude\`) — Claude Desktop has exactly one global config file. Turn off **Claude Desktop Integration** in Project Settings → Hades to stop Hades writing it.
  2. `~/.claude/skills/` — only when Skills scope is Global.
  Fully isolated configuration: Hub `Local`, Skills `Local`, Desktop Integration `off`.

- [ ] **Step 2: Update troubleshooting.md**

- Update the hub-recovery guidance to cover both scopes: the file is at `<projectRoot>/.arcforge/hades-hub/hub.json` in local mode and `~/.arcforge/hades-hub/hub.json` in global mode. Project Settings → Hades shows the resolved path.
- Add: **"MCP tools missing when Claude Code runs from outside the project directory"** → a project-local hub is not discoverable from outside; either `cd` into the project, or switch Hub scope to Global, or set `HADES_HUB_DIR`.
- Add manual cleanup of the legacy global dir: safe to `rm -rf ~/.arcforge/hades-hub` once every project on the machine uses local scope and no hub process is running (`ps aux | grep hades-hub`).

- [ ] **Step 3: Correct architecture.md**

Four edits:
- **§207** ("Hades is fully project-scoped… No shared state between instances") — was true of the data plane only. State that the control plane (hub rendezvous, settings) is now project-scoped too by default, and that global remains available as a setting.
- **§1883** — remove the "no loader… not currently gitignored" note about `config.local.yaml`. Describe the flat `key: value` loader (`HadesConfig`), list the keys from spec §4.2, and note it is gitignored. Also correct the sentence claiming Graph/Charon/MCP settings live in EditorPrefs.
- **§2701** — the launcher path description: project `.mcp.json` now points into the resolved hub dir, project-local by default, not `~/.arcforge/hades-hub/launcher.js`.
- **§2812** — the troubleshooting table row hardcoding `~/.arcforge/hades-hub/hub.json`: make it scope-aware.

- [ ] **Step 4: Add a CHANGELOG entry**

Under a new Unreleased heading, following the file's existing style:

```markdown
### Changed
- Hades now installs project-local by default. The hub rendezvous directory moved from
  `~/.arcforge/hades-hub/` to `<projectRoot>/.arcforge/hades-hub/`, and skills install to
  `<projectRoot>/.claude/skills/`. Both are switchable per project at Project Settings > Hades,
  and `HADES_HUB_DIR` overrides the hub directory outright.
- Settings moved out of Unity EditorPrefs (global per Unity install, so projects shared them)
  into `<projectRoot>/.arcforge/config.local.yaml`. Existing settings can be imported on first
  load via a one-time prompt.
- The Claude Desktop config write is now gated by a `desktop_integration` setting (default on).
  It remains the one Hades write that cannot be project-local.

### Fixed
- Package path resolution used a hardcoded `Packages/com.arcforge.hades` guess, which only
  exists for embedded packages. On the documented git-URL install the package resolves to
  `Library/PackageCache/com.arcforge.hades@<hash>`, so the stable launcher copy and the skills
  install both silently no-opped. Now resolved via `PackageInfo.FindForAssembly`.

### Notes
- `~/.arcforge/hades-hub/` is no longer used in the default configuration. Nothing in it needs
  migrating — the launcher copy is regenerated at startup, and the rest is runtime state. A
  one-time notice points it out; delete it manually once every project has been updated.
```

- [ ] **Step 5: Verify no stale paths remain in docs**

```bash
grep -rn 'arcforge/hades-hub' Documentation/ README.md CHANGELOG.md | grep -v 'projectRoot'
```

Review each hit: every remaining `~/.arcforge/hades-hub` reference must be explicitly about global mode or the legacy folder, not presented as the only location.

- [ ] **Step 6: Commit**

```bash
git add Documentation/getting-started.md Documentation/troubleshooting.md \
        Documentation/arcforge-hades-architecture.md CHANGELOG.md
git commit -m "docs: document project-local installation and scope settings"
```

---

## Self-Review

**Spec coverage.** Every spec section maps to at least one task:

| Spec | Task(s) |
|---|---|
| §1.2 / §4.5 package path bug | 7 |
| §4.1 hub-dir resolution chain | 3 (C#), 15 (TS), 16, 17 |
| §4.2 config file + keys | 2, 4 |
| §4.3 settings surface + menu | 13 |
| §4.4 launcher/skills placement | 8, 9 |
| §4.6 Desktop config + `desktop_integration` | 9 (gate), 10 (docs on the method) |
| §4.7 settings migration | 4, 12 |
| §4.7 legacy hub notice | 11, 12 |
| §5 file list | all; `AtomicFile.cs` added in Task 1, not listed in the spec — a DRY consequence of two writers needing atomic writes |
| §6 automated verification | 2, 3, 4, 11, 14, 15, 18 |
| §6 manual verification | 20 |
| §7 documentation | 21 |
| §8 bundle-invariant risk | 18 |

Gaps closed while reviewing:
- The spec's file list omits `.gitignore` from the task breakdown even though §5 mentions it — Task 19 added.
- The spec does not mention `CHANGELOG.md`; the repo keeps one, so Task 21 Step 4 adds an entry.
- `HadesBootstrapTests` asserts on `BootTrace` and would have broken silently — Task 12 Step 3 updates it.
- `InferenceConfig` reuse (spec §5) had no task — Task 5 added.

**Type consistency.** Checked across tasks: `HadesScope` (Task 3) is the type used by `HadesSettings.HubScope`/`SkillsScope` (4), `InstallSkills(HadesScope)` (9), and `HadesPreferences` (13). `HadesConfig.Parse` is `internal` and consumed only inside the same assembly, by `InferenceConfig` (5) and the tests (2). `ReadFile` (15) is the injected type used by `readHubScope` and `resolveHubDir`, with `defaultReadFile` matching it. `findProjectRoot` returns `string | null` (14) and feeds `ResolveHubDirOptions.projectRoot`, typed `string | null` (15). `HadesPaths.ResolveHubDir(string, HadesScope, string, string)` and `resolveHubDir({env, projectRoot, readFile})` implement the same three rungs with the same precedence — verified against each other's test tables.

**Placeholder scan.** No TBD/TODO, no "add error handling", no "similar to Task N". Every step carries either complete code or an exact command with expected output. Task 3 Step 3's temporary `HadesScope.Local` hardcode is called out explicitly with the task that removes it, rather than left as an implicit gap.

**One known cross-task dependency to respect at execution time:** Task 3 leaves the assembly compiling only because of the temporary hardcode; Task 4 Step 4 removes it. Do not commit Task 3 and stop.
