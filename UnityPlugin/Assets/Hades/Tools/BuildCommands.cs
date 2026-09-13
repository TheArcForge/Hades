// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// DESIGN-03 (docs/design/DESIGN-03-build-pipeline.md), Lane C of the roadmap's Phase 1. One
    /// file, carrying five things - matching PlayModeCommands.cs's own "carries several classes
    /// because they are one concern" convention (see that file's own class doc comment):
    ///
    /// <see cref="BuildCommands"/>    - the two wire handlers (project.build_start/build_targets)
    /// <see cref="BuildBridge"/>      - the seam over the real BuildPipeline.BuildPlayer call
    /// <see cref="BuildStatusStore"/> - the status-file schema, atomic write, and TryRead
    /// <see cref="BuildSession"/>     - the boot nonce (SessionState) and the intra-session
    ///                                  '_buildInFlight' liveness static (DESIGN-08 §12)
    /// <see cref="BuildWatchdog"/>    - the EditorApplication.update hook that reconciles a
    ///                                  non-terminal status file to 'abandoned', per tick
    ///                                  (DESIGN-08 O2 - not boot-only)
    ///
    /// <para><b>Why a file, not SessionState, for the build's own record (DESIGN-03 §4.2).</b>
    /// Every other Hades long-running operation in this plugin (play sessions, bakes) survives
    /// entirely in SessionState, because a domain reload is the only thing that can interrupt them
    /// and SessionState survives one. A build is different: it pins Unity's main thread for
    /// minutes, so <see cref="Core.Editors.EditorProxy"/> refuses every OTHER wire call - including
    /// a status poll - for the duration (see that class's own busy check). The core must therefore
    /// answer 'status' entirely from disk, with no wire round trip at all - see
    /// <c>Core/src/Hades.Server/Mcp/ProjectBuildTool.cs</c>, which never sends 'project.build_status'
    /// because no such wire method exists.</para>
    ///
    /// <para><b>The mutation guard this file's commands are already decided against
    /// (CommandTable.AllowedWhilePlaying).</b> Both entries already exist there, seeded by Lane B:
    /// 'project.build_start' is an explicit refuse (<see cref="PlayModeCommands.BuildStartRefusedWhilePlayingMessage"/>)
    /// and 'project.build_targets' is allow-listed (a read). This file does not touch that
    /// dictionary - see CommandTable's own doc comment for why re-adding either entry here would be
    /// a duplicate, not a fix.</para>
    /// </summary>
    public static class BuildCommands
    {
        /// <summary>A queued build's delayCall must fire well within this window in the ordinary
        /// case; if the status file is still 'queued' past it, the delayCall was almost certainly
        /// eaten by a domain reload within this same Editor session (nonce still matches - a
        /// genuine crash/restart is caught by the nonce check instead) - DESIGN-03 §4.4's
        /// "queued-grace" row. Same value as <see cref="Runtime.ReloadGate.DefaultTtl"/>, coincidentally -
        /// this is not that TTL, just a similarly human-scale window.</summary>
        public static readonly TimeSpan QueuedGraceWindow = TimeSpan.FromSeconds(30);

        /// <summary>Seam over the real <c>BuildPipeline.BuildPlayer</c> call - public for the same
        /// InternalsVisibleTo reason <see cref="ProjectCommands.StartTestRun"/> is, and swappable
        /// for the same testing reason, in a stronger form: a REAL BuildPlayer invoked from inside
        /// an already-running EditMode test would pin the very main thread running that test for
        /// minutes (DESIGN-03 §11.2). Tests substitute a fake here and MUST restore it in TearDown -
        /// see that field's own doc comment for the hygiene requirement this follows.</summary>
        public static Func<BuildRequest, BuildOutcome> RunBuild = BuildBridge.Run;

        // ---- DESIGN-03 §5.2 preflight seams ----
        //
        // Not among EditorPlayModeApi's six named seams (DESIGN-01 §6.1), but required by the same
        // testability argument: measured directly against a real batchmode Editor, EditorApplication
        // .isCompiling reads true for a meaningful window while THIS PLUGIN'S OWN scratch project is
        // still settling after import, which made every "the build should proceed" plugin test flake
        // solid-red against the real property, not merely occasionally. Same swappable-public-static-
        // field convention as ProjectCommands.TriggerRecompile/StartTestRun/EditorPlayModeApi -
        // restore in TearDown.
        public static Func<bool> IsCompiling = () => EditorApplication.isCompiling;
        public static Func<bool> ScriptCompilationFailed = () => EditorUtility.scriptCompilationFailed;
        public static Func<bool> IsPlayingOrWillChangePlaymode = () => EditorApplication.isPlayingOrWillChangePlaymode;

        // ---------------------------------------------------------------- project.build_start

        /// <summary>DESIGN-08 §6's recommendation over a hand-rolled gate check: wraps this
        /// handler's bounded, single-tick prep (every preflight in DESIGN-03 §5.2, plus writing the
        /// status file) in <see cref="LeaseScope.Run"/>, exactly like <see cref="PlayModeCommands.Enter"/>
        /// does for 'play.enter'. The lease protects nothing here (a same-target BuildPlayer call
        /// cannot be interrupted by a reload - DESIGN-03 §7.1) - acquiring it anyway is how this
        /// handler finds out someone else (almost always an in-progress script_editing_session)
        /// holds it, with the exact wording <see cref="LeaseScope.Run"/> already produces. The
        /// actual <c>BuildPipeline.BuildPlayer</c> call happens on a LATER tick
        /// (<see cref="EditorApplication.delayCall"/>), strictly after this lease has already been
        /// released - see <see cref="DoStart"/>'s own doc comment.</summary>
        internal static JsonValue Start(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "project.build_start", () => DoStart(@params));

        /// <summary>The bounded prep: every DESIGN-03 §5.2 preflight, in order, refusing before
        /// writing anything or scheduling anything; then writes 'build-status.json' with
        /// state:'queued' and schedules the real build for the next tick. Returns well inside the
        /// 30s <c>EditorProxy.CommandTimeout</c> - the actual build runs later, on its own tick,
        /// after this call (and the lease <see cref="Start"/> wraps it in) has already returned.</summary>
        internal static JsonValue DoStart(JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params, "target", "profile", "outputPath", "development");

            var targetParam = JsonParams.OptionalString(@params, "target");
            var profileParam = JsonParams.OptionalString(@params, "profile");
            var outputPathParam = JsonParams.OptionalString(@params, "outputPath");
            var developmentValue = JsonParams.OptionalValue(@params, "development");
            var development = JsonParams.OptionalBool(@params, "development", false);

            if (string.IsNullOrEmpty(targetParam) && string.IsNullOrEmpty(profileParam))
            {
                throw new ArgumentException(
                    "'project.build_start' requires either 'target' (a BuildTarget name) or 'profile' "
                    + "(a project-relative path to a Build Profile asset).");
            }

            if (!string.IsNullOrEmpty(profileParam) && !string.IsNullOrEmpty(targetParam))
            {
                throw new ArgumentException(
                    "'project.build_start': 'target' and 'profile' are mutually exclusive - a Build "
                    + "Profile already supplies its own target. Omit 'target' when passing 'profile'.");
            }

            if (!string.IsNullOrEmpty(profileParam) && developmentValue != null)
            {
                throw new ArgumentException(
                    "'project.build_start': 'development' and 'profile' are mutually exclusive - the "
                    + "profile's own build options apply. Omit 'development' when passing 'profile'.");
            }

            UnityEditor.Build.Profile.BuildProfile profile = null;
            string canonicalProfilePath = null;
            BuildTarget target;

            if (!string.IsNullOrEmpty(profileParam))
            {
                canonicalProfilePath = AssetPathGuard.RequireWellFormedProjectPath(profileParam, "project.build_start");
                profile = AssetDatabase.LoadAssetAtPath<UnityEditor.Build.Profile.BuildProfile>(canonicalProfilePath);
                if (profile == null)
                {
                    throw new ArgumentException(
                        "'project.build_start': no Build Profile asset found at '" + canonicalProfilePath + "'.");
                }
                target = ReadProfileBuildTarget(profile);
            }
            else if (!Enum.TryParse(targetParam, out target) || !Enum.IsDefined(typeof(BuildTarget), target))
            {
                throw new ArgumentException(
                    "'project.build_start': '" + targetParam + "' is not a recognised BuildTarget name. "
                    + "Call project_build with action 'targets' to see what this Editor can build.");
            }

            // ---- DESIGN-03 §5.2 preflight, in order, before anything is written or scheduled ----

            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (!IsSupported(group, target))
            {
                throw new InvalidOperationException(
                    "'" + target + "' is not buildable by this Editor - the matching module is not "
                    + "installed. Install it from Unity Hub (Installs -> this version -> Add Modules). "
                    + BuildableTargetsClause());
            }

            if (target != EditorUserBuildSettings.activeBuildTarget)
            {
                throw new InvalidOperationException(
                    "'" + target + "' is not the Editor's active build target - that is '"
                    + EditorUserBuildSettings.activeBuildTarget + "'. Building '" + target + "' would "
                    + "switch the Editor's active platform and reimport every asset for it (minutes), "
                    + "and it would stay switched after the build. Hades will not do that silently. "
                    + "Switch platform in Unity (File -> Build Profiles), or call project_build with "
                    + "target='" + EditorUserBuildSettings.activeBuildTarget + "'.");
            }

            if (IsCompiling())
                throw new InvalidOperationException("Scripts are still compiling. Wait, then retry.");

            if (ScriptCompilationFailed())
            {
                throw new InvalidOperationException(
                    "Scripts do not compile. Call project_get_console_log with type='Error' first - a "
                    + "build cannot succeed over a compile error.");
            }

            if (IsPlayingOrWillChangePlaymode())
                throw new InvalidOperationException("Cannot build while in Play Mode. Exit Play Mode, then retry.");

            var bakeRefusal = HadesOwnedBakeInProgressRefusal();
            if (bakeRefusal != null) throw new InvalidOperationException(bakeRefusal);

            // Single-flight - reconciliation (BuildWatchdog) already keeps this file honest per
            // tick, so a non-terminal read here really does mean "still going" (DESIGN-03 §4.4/§8.1).
            var existing = BuildStatusStore.TryRead();
            if (existing != null && !BuildStatusStore.IsTerminal(existing.State))
            {
                throw new InvalidOperationException(
                    "A build (id '" + existing.BuildId + "') is already running for this project. Poll "
                    + "it with project_build action='status', or wait for it to finish.");
            }

            // ---- outputPath ----

            var outputRelative = string.IsNullOrEmpty(outputPathParam)
                ? DefaultOutputRelativePath(target)
                : ValidateOutputPath(outputPathParam);
            var absoluteOutputPath = Path.Combine(
                BuildStatusStore.ProjectRoot, outputRelative.Replace('/', Path.DirectorySeparatorChar));

            var dirtyScenes = CollectDirtyScenePaths();

            // ---- write queued + schedule ----

            var buildId = Guid.NewGuid().ToString("N");
            var bootNonce = BuildSession.CurrentBootNonce;

            var doc = new BuildStatusStore.Document
            {
                Schema = BuildStatusStore.CurrentSchema,
                BuildId = buildId,
                BootNonce = bootNonce,
                State = BuildStatusStore.StateQueued,
                Target = target.ToString(),
                Profile = canonicalProfilePath,
                OutputPath = outputRelative,
                QueuedAtUtcMs = ToUnixTimeMs(DateTime.UtcNow),
                UnityVersion = Application.unityVersion,
                EditorLogPath = Application.consoleLogPath,
                Errors = new List<(string, string, string)>(),
            };
            BuildStatusStore.Write(doc);

            var scenes = EnabledBuildScenePaths();
            EditorApplication.delayCall += () =>
                RunBuildNow(buildId, target, canonicalProfilePath, scenes, outputRelative, absoluteOutputPath, development);

            return JsonValue.NewObject()
                .SetProperty("buildId", JsonValue.String(buildId))
                .SetProperty("state", JsonValue.String(BuildStatusStore.StateQueued))
                .SetProperty("target", JsonValue.String(target.ToString()))
                .SetProperty("outputPath", JsonValue.String(outputRelative))
                .SetProperty("dirtyScenes", ToJsonStringArray(dirtyScenes));
        }

        /// <summary>Runs strictly on a LATER tick than <see cref="DoStart"/> (scheduled via
        /// <see cref="EditorApplication.delayCall"/>), after that handler's own lease has already
        /// been released (DESIGN-08 §6's lease rule: acquire, bounded prep, release, THEN kick off).
        /// Sets 'state':'running' and <see cref="BuildSession.BuildInFlight"/> on the SAME tick
        /// (DESIGN-08 §12's intra-session liveness pairing), calls the real (or, in a test, faked)
        /// <see cref="RunBuild"/>, and always writes a terminal status - success, failure, or a
        /// caught exception - in a finally, so <see cref="BuildSession.BuildInFlight"/> can never be
        /// left true for a build that has actually ended.</summary>
        static void RunBuildNow(string buildId, BuildTarget target, string profilePath, string[] scenes,
            string outputRelative, string absoluteOutputPath, bool development)
        {
            var doc = BuildStatusStore.TryRead();
            if (doc == null || doc.BuildId != buildId || doc.State != BuildStatusStore.StateQueued)
            {
                // The file was removed or overwritten out from under this scheduled continuation
                // (e.g. manual deletion between 'queued' and this tick), or - the narrow case the
                // State check catches - BuildWatchdog's own queued-grace rule already reconciled this
                // SAME build to 'abandoned' because this delayCall took unusually long to fire
                // (nonce still matched, so no reload explains the delay; a genuinely stalled tick
                // does happen). Either way: nothing safe to do here. Never resurrect an already-
                // reconciled record by overwriting it back to 'running'.
                return;
            }

            doc.State = BuildStatusStore.StateRunning;
            doc.StartedAtUtcMs = ToUnixTimeMs(DateTime.UtcNow);
            BuildSession.BuildInFlight = true;
            BuildStatusStore.Write(doc);

            BuildOutcome outcome;
            try
            {
                outcome = RunBuild(new BuildRequest
                {
                    Target = target,
                    ProfileAssetPath = profilePath,
                    Scenes = scenes,
                    AbsoluteOutputPath = absoluteOutputPath,
                    Development = development,
                });
            }
            catch (Exception ex)
            {
                outcome = new BuildOutcome
                {
                    Result = "Failed",
                    ExceptionMessage = ex.Message,
                    Messages = new List<(string, string, string)>(),
                };
            }
            finally
            {
                // Unconditional - the one line that stops an exception anywhere above from leaving
                // BuildSession.BuildInFlight true for a build that has, in fact, already ended.
                BuildSession.BuildInFlight = false;
            }

            doc.State = MapResultToState(outcome.Result);
            doc.FinishedAtUtcMs = ToUnixTimeMs(DateTime.UtcNow);
            doc.TotalErrors = outcome.TotalErrors;
            doc.TotalWarnings = outcome.TotalWarnings;
            doc.OutputSizeBytes = outcome.OutputSizeBytes;

            var messages = outcome.Messages ?? new List<(string, string, string)>();
            doc.ErrorsTruncated = messages.Count > MaxErrorMessages;
            doc.Errors = messages.Take(MaxErrorMessages)
                .Select(m => (m.Item1, m.Item2, Truncate(m.Item3, MaxErrorMessageChars)))
                .ToList();
            doc.EditorLogPath = string.IsNullOrEmpty(outcome.EditorLogPath) ? Application.consoleLogPath : outcome.EditorLogPath;

            if (outcome.ExceptionMessage != null)
            {
                doc.Note = "BuildPlayer threw before producing a report: " + outcome.ExceptionMessage;
            }
            else if (doc.State == BuildStatusStore.StateFailed && doc.Errors.Count == 0)
            {
                doc.Note = "The build failed but reported no structured errors; the full Editor log is at '"
                    + doc.EditorLogPath + "'.";
            }

            BuildStatusStore.Write(doc);
        }

        const int MaxErrorMessages = 50;
        const int MaxErrorMessageChars = 2000;

        static string MapResultToState(string buildResult) => buildResult switch
        {
            "Succeeded" => BuildStatusStore.StateComplete,
            "Cancelled" => BuildStatusStore.StateCancelled,
            _ => BuildStatusStore.StateFailed, // Failed, Unknown, or a caught exception
        };

        static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

        /// <summary>DESIGN-03 §3.4 assumed <c>BuildProfile.buildTarget</c> would be a public
        /// property - measured directly against Unity 6000.3.2f1 (this repo's own pinned probe
        /// version), its getter/setter are actually INTERNAL, so it does not compile from this
        /// plugin assembly (confirmed by a real build failure against the real Editor, not merely
        /// asserted). The type itself, and most of its surface (scenes/scriptingDefines/
        /// overrideGlobalScenes), genuinely are public as the design says - only this one property
        /// is not. The fix is <see cref="SerializedObject"/>/<see cref="SerializedProperty"/> reading
        /// the backing 'm_BuildTarget' field directly - Unity's own supported, public mechanism for
        /// reading a serialized field a type does not expose a C# accessor for, already used
        /// throughout this plugin (see ComponentCommands.cs's SerializedPropertyJson) - NOT a
        /// System.Reflection bridge against an optionally-absent assembly, which is the specific
        /// complexity §3.4 correctly says this design avoids. <see cref="SerializedProperty.intValue"/>
        /// on an Enum-typed property is the RAW underlying stored value (unlike 'enumValueIndex',
        /// which is an index into 'enumNames' and unsafe for a sparse enum like BuildTarget).</summary>
        static BuildTarget ReadProfileBuildTarget(UnityEditor.Build.Profile.BuildProfile profile)
        {
            var so = new SerializedObject(profile);
            var prop = so.FindProperty("m_BuildTarget");
            if (prop == null)
            {
                throw new InvalidOperationException(
                    "'project.build_start': could not read the Build Profile's own target (its serialized "
                    + "'m_BuildTarget' field was not found - this Unity Editor version may have changed the "
                    + "Build Profile format).");
            }
            return (BuildTarget)prop.intValue;
        }

        // ---------------------------------------------------------------- bake x build (DESIGN-08 §7.3)

        /// <summary>DESIGN-06's own <c>Hades.Bake.*</c> SessionState keys (§4.4 - six keys,
        /// including 'Scope'; only three are needed for this read-only ownership check) now live in
        /// the shared <see cref="BakeKeys"/> class (SceneBakeCommands.cs) - ROADMAP.md §4's
        /// shared-surface row: "Preferred mechanism now that both lanes exist: a single shared
        /// BakeKeys constants class in the plugin, referenced by both", replacing this file's own
        /// former private copy of three of the six names. <b>Lane G owns these names; this reader
        /// only consumes them.</b> <see cref="HadesOwnedBakeInProgressRefusal"/> is written
        /// specifically so a missed rename cannot fail silently: an absent JobId is the one case
        /// DESIGN-06 §4.4 itself resolves to "none" (rule 1), so this reader treats it the same way
        /// (and was correctly INERT before Lane G shipped, since nothing had ever written these
        /// keys) - but Terminal/Epoch being unexpectedly absent WHILE JobId is present is never read
        /// as "no bake"; it refuses instead, per this class's own doc comment.</summary>

        /// <summary>Sentinel default distinguishing "key was never set" from "key was set to the
        /// empty string" (a real, meaningful value in DESIGN-06's own model - Terminal="" means
        /// "non-terminal") - <see cref="SessionState.GetString(string,string)"/> cannot tell the two
        /// apart any other way.</summary>
        const string KeyAbsent = "\0hades-bake-key-absent\0";

        /// <summary>Null when a build may proceed; an actionable refusal message otherwise. See
        /// this method's own containing class's doc comment for the "absent means cannot tell,
        /// never no bake" rule this implements, and the field-level doc comment for why an absent
        /// JobId specifically is the one exception (it is DESIGN-06's own "none" case, not a missed
        /// rename).</summary>
        static string HadesOwnedBakeInProgressRefusal()
        {
            var jobId = SessionState.GetString(BakeKeys.JobId, "");
            if (string.IsNullOrEmpty(jobId)) return null; // no bake this session - see doc comment

            var terminal = SessionState.GetString(BakeKeys.Terminal, KeyAbsent);
            if (terminal == KeyAbsent) return CannotTellRefusal();
            if (!string.IsNullOrEmpty(terminal)) return null; // reached a terminal state - not blocking

            var epochRaw = SessionState.GetString(BakeKeys.Epoch, KeyAbsent);
            if (epochRaw == KeyAbsent) return CannotTellRefusal();
            if (!int.TryParse(epochRaw, out var jobEpoch)) return CannotTellRefusal();

            var currentEpoch = SessionState.GetInt(BakeKeys.ReloadEpoch, 0);
            if (jobEpoch != currentEpoch) return null; // unattributable across a reload - not OUR job

            return "A bake started by Hades is currently in progress (job '" + jobId + "') in this "
                + "Editor - building now would race it. Wait for it to finish, then retry.";
        }

        static string CannotTellRefusal() =>
            "Hades cannot tell whether a bake it started is currently in progress in this Editor - its "
            + "own tracked bake state is incomplete (this build tooling may be out of sync with the bake "
            + "tooling in this installation). Refusing to start a build until that is resolved: check the "
            + "Unity console, or retry once you are confident nothing is baking.";

        // ---------------------------------------------------------------- project.build_targets

        /// <summary>Class 4 (live-state read - no lease, same as <see cref="ProjectCommands.GetConsoleLog"/>/
        /// <see cref="ProjectCommands.GetTestResults"/>): asks the live Editor which BuildTargets
        /// THIS installation can actually build, per DESIGN-03 §5.1 - Hades never ships or maintains
        /// a host-OS build matrix of its own.</summary>
        internal static JsonValue Targets(ReloadGate gate, JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params);

            var targets = JsonValue.NewArray();
            foreach (var target in BuildableEnumerationOrder())
            {
                var group = BuildPipeline.GetBuildTargetGroup(target);
                targets.Add(JsonValue.NewObject()
                    .SetProperty("target", JsonValue.String(target.ToString()))
                    .SetProperty("group", JsonValue.String(group.ToString()))
                    .SetProperty("supported", JsonValue.Bool(IsSupported(group, target))));
            }

            return JsonValue.NewObject()
                .SetProperty("hostPlatform", JsonValue.String(Application.platform.ToString()))
                .SetProperty("targets", targets);
        }

        /// <summary>Every <see cref="BuildTarget"/> member NOT carrying <see cref="ObsoleteAttribute"/>
        /// and not the <see cref="BuildTarget.NoTarget"/> sentinel - reflection over the enum's own
        /// fields, never a hardcoded skip-list (DESIGN-03 §5.1 item 1). Unity's enum carries several
        /// non-obsolete members that alias the SAME underlying value as an obsolete one (e.g.
        /// 'LinuxHeadlessSimulation' vs the obsolete 'CloudRendering', both 41) - skipping only the
        /// obsolete ones naturally keeps exactly the canonical, current name for each.</summary>
        static IEnumerable<BuildTarget> BuildableEnumerationOrder()
        {
            foreach (BuildTarget target in Enum.GetValues(typeof(BuildTarget)))
            {
                if (target == BuildTarget.NoTarget) continue;
                var field = typeof(BuildTarget).GetField(target.ToString());
                if (field != null && field.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                yield return target;
            }
        }

        /// <summary>Defensive: some enum members that are not (or no longer) buildable on any host
        /// can throw from inside <c>IsBuildTargetSupported</c> rather than simply returning false -
        /// caught here so one bad target cannot take down the whole 'targets'/'buildable targets'
        /// listing.</summary>
        static bool IsSupported(BuildTargetGroup group, BuildTarget target)
        {
            try { return BuildPipeline.IsBuildTargetSupported(group, target); }
            catch { return false; }
        }

        /// <summary>DESIGN-03 §5.3's closing note: "the refusal itself carries the discovery result -
        /// the agent does not need a second action='targets' round trip after a miss." Used only by
        /// the module-not-installed refusal - the cross-target refusal deliberately does NOT repeat
        /// this list (§5.3's own example message does not carry it either).</summary>
        static string BuildableTargetsClause() =>
            "Buildable targets right now: " + string.Join(", ", BuildableEnumerationOrder().Where(t => IsSupported(BuildPipeline.GetBuildTargetGroup(t), t))) + ".";

        // ---------------------------------------------------------------- output path / scenes / dirty scenes

        /// <summary>DESIGN-03 §6: project-relative only; refused outside the project root or inside
        /// 'Assets/', 'Packages/', 'Library/', or 'ProjectSettings/' - a player written into 'Assets/'
        /// would be imported as assets, which is genuinely destructive. Mirrors the containment idiom
        /// <see cref="AppPaths"/> establishes on the core side (GetFullPath both sides, then
        /// Ordinal StartsWith) - this plugin-side check is defence in depth alongside the core's own
        /// (Core/src/Hades.Server/Mcp/ProjectBuildTool.cs), which refuses a bad path before any wire
        /// call is even sent.</summary>
        static string ValidateOutputPath(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentException("'project.build_start': 'outputPath' must not be empty.");
            if (outputPath.Trim() != outputPath)
            {
                throw new ArgumentException(
                    "'project.build_start': 'outputPath' has leading or trailing whitespace: '" + outputPath + "'.");
            }

            var root = Path.GetFullPath(BuildStatusStore.ProjectRoot);
            var rootWithSep = root + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, outputPath));

            if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "'project.build_start': 'outputPath' must be a project-relative path inside this "
                    + "project - refused '" + outputPath + "'.");
            }

            var relative = full.Substring(rootWithSep.Length).Replace(Path.DirectorySeparatorChar, '/');

            foreach (var forbidden in ForbiddenOutputRoots)
            {
                if (relative == forbidden.TrimEnd('/') || relative.StartsWith(forbidden, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "'project.build_start': 'outputPath' must not resolve inside '" + forbidden.TrimEnd('/')
                        + "' - a player written there would be destructive (e.g. imported as an asset). Refused '"
                        + outputPath + "'.");
                }
            }

            return relative;
        }

        static readonly string[] ForbiddenOutputRoots = { "Assets/", "Packages/", "Library/", "ProjectSettings/" };

        /// <summary>DESIGN-03 §6: defaults to 'Build/&lt;target&gt;/' - already excluded from both
        /// indexing (<c>ProjectWalker.ExcludedDirectoryNames</c>) and file-watching
        /// (<c>ProjectWatcher.IsIgnored</c>) on the core side, so a multi-gigabyte player written
        /// here produces zero graph nodes and zero reindex nudges. The leaf name/extension is the
        /// PLATFORM's convention, never the host's - only the plugin knows it (§6: "the core never
        /// appends an extension"). Deliberately covers only the common desktop/mobile/web targets by
        /// name; anything else falls back to a bare product-name leaf with no extension, which is
        /// always a safe, if occasionally imprecise, default a caller can override with 'outputPath'.</summary>
        static string DefaultOutputRelativePath(BuildTarget target)
        {
            var name = SanitizeFileName(PlayerSettings.productName);
            var leaf = target switch
            {
                BuildTarget.StandaloneOSX => name + ".app",
                BuildTarget.StandaloneWindows or BuildTarget.StandaloneWindows64 => name + ".exe",
                BuildTarget.Android => name + ".apk",
                _ => name,
            };
            return "Build/" + target + "/" + leaf;
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Player";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        static string[] EnabledBuildScenePaths() =>
            EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

        /// <summary>DESIGN-03 §7.3: disclose, never save. Every open scene with unsaved changes, so
        /// the caller knows the build reflects the last SAVED state, not what is currently open -
        /// this handler never calls anything that would write a scene to disk (the F12 rule,
        /// restated for builds - see <see cref="ProjectCommands.RunTests"/>'s own doc comment for
        /// the original fix this mirrors).</summary>
        static List<string> CollectDirtyScenePaths()
        {
            var result = new List<string>();
            var count = UnityEditor.SceneManagement.EditorSceneManager.sceneCount;
            for (var i = 0; i < count; i++)
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.GetSceneAt(i);
                if (!scene.isDirty) continue;
                result.Add(string.IsNullOrEmpty(scene.path) ? "(unsaved, untitled scene)" : scene.path);
            }
            return result;
        }

        static JsonValue ToJsonStringArray(IEnumerable<string> values)
        {
            var array = JsonValue.NewArray();
            foreach (var v in values) array.Add(JsonValue.String(v));
            return array;
        }

        static long ToUnixTimeMs(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    /// <summary>Plain input to <see cref="BuildCommands.RunBuild"/> - deliberately not a
    /// <see cref="JsonValue"/> (this is an in-process seam, not a wire boundary) and deliberately
    /// not the real <see cref="BuildPlayerOptions"/>/<see cref="UnityEditor.BuildPlayerWithProfileOptions"/>
    /// Unity types (a test substituting <see cref="BuildCommands.RunBuild"/> should not need to
    /// construct either).</summary>
    public struct BuildRequest
    {
        public BuildTarget Target;

        /// <summary>Null when this build was requested by bare 'target' (BuildBridge builds via
        /// <see cref="BuildPlayerOptions"/>); a canonical project-relative asset path when a Build
        /// Profile was requested (BuildBridge builds via <see cref="UnityEditor.BuildPlayerWithProfileOptions"/>
        /// instead, re-loading the profile asset from this path).</summary>
        public string ProfileAssetPath;

        /// <summary>Only consulted for the bare-'target' path - a Build Profile supplies its own
        /// scene list.</summary>
        public string[] Scenes;

        public string AbsoluteOutputPath;
        public bool Development;
    }

    /// <summary>Plain output from <see cref="BuildCommands.RunBuild"/> - already reduced to exactly
    /// what <see cref="BuildCommands.RunBuildNow"/> needs to write into the status file, so a fake
    /// substituted in a test needs to construct no real Unity build type either.</summary>
    public struct BuildOutcome
    {
        /// <summary>One of <see cref="BuildResult"/>'s own member names ("Succeeded", "Failed",
        /// "Cancelled", "Unknown"), or "Failed" when <see cref="ExceptionMessage"/> is set (the
        /// build threw before producing any report at all).</summary>
        public string Result;

        public int TotalErrors;
        public int TotalWarnings;
        public long OutputSizeBytes;

        /// <summary>{step, type ("Error"/"Exception"/"Assert"), content} - DESIGN-03 §12 layer 2,
        /// uncapped here; <see cref="BuildCommands.RunBuildNow"/> applies the 50-message/2000-char
        /// cap when writing the status file.</summary>
        public List<(string Step, string Type, string Content)> Messages;

        public string EditorLogPath;

        /// <summary>Set only when <c>BuildPipeline.BuildPlayer</c> itself threw, rather than
        /// returning a <see cref="BuildReport"/> - DESIGN-03 §12's "zero messages" failure mode
        /// this exists to still report SOMETHING actionable for.</summary>
        public string ExceptionMessage;
    }

    /// <summary>The one real implementation of <see cref="BuildCommands.RunBuild"/> - calls the
    /// actual <c>BuildPipeline.BuildPlayer</c>, via a Build Profile when one was supplied
    /// (<c>BuildPlayerWithProfileOptions</c> - DESIGN-03 §3.4, no reflection bridge needed:
    /// <c>UnityEditor.Build.Profile.BuildProfile</c> lives in <c>UnityEditor.CoreModule</c>, a core
    /// Editor assembly this plugin already references despite <c>Hades.asmdef</c>'s empty
    /// "references" array) or via the bare <c>BuildPlayerOptions</c> overload otherwise.</summary>
    public static class BuildBridge
    {
        public static BuildOutcome Run(BuildRequest request)
        {
            BuildReport report;

            if (!string.IsNullOrEmpty(request.ProfileAssetPath))
            {
                var profile = AssetDatabase.LoadAssetAtPath<UnityEditor.Build.Profile.BuildProfile>(request.ProfileAssetPath);
                report = BuildPipeline.BuildPlayer(new UnityEditor.BuildPlayerWithProfileOptions
                {
                    buildProfile = profile,
                    locationPathName = request.AbsoluteOutputPath,
                    options = request.Development ? BuildOptions.Development : BuildOptions.None,
                });
            }
            else
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = request.Scenes,
                    locationPathName = request.AbsoluteOutputPath,
                    target = request.Target,
                    targetGroup = BuildPipeline.GetBuildTargetGroup(request.Target),
                    options = request.Development ? BuildOptions.Development : BuildOptions.None,
                });
            }

            return FromReport(report);
        }

        static BuildOutcome FromReport(BuildReport report)
        {
            var messages = new List<(string, string, string)>();
            var steps = report != null ? report.steps : null;
            if (steps != null)
            {
                foreach (var step in steps)
                {
                    if (step.messages == null) continue;
                    foreach (var message in step.messages)
                    {
                        if (message.type != LogType.Error && message.type != LogType.Exception && message.type != LogType.Assert)
                            continue;
                        messages.Add((step.name, message.type.ToString(), message.content));
                    }
                }
            }

            var summary = report != null ? report.summary : default;
            return new BuildOutcome
            {
                Result = report != null ? summary.result.ToString() : "Unknown",
                TotalErrors = summary.totalErrors,
                TotalWarnings = summary.totalWarnings,
                OutputSizeBytes = (long)summary.totalSize,
                Messages = messages,
                EditorLogPath = Application.consoleLogPath,
            };
        }
    }

    /// <summary>
    /// The status-file schema (DESIGN-03 §4.2) and its atomic write (§4.2.2). Written by the
    /// plugin (this class), read by the core (<c>Core/src/Hades.Server/Mcp/ProjectBuildTool.cs</c>,
    /// via <c>System.Text.Json</c> - the one deliberately duplicated contract this design accepts,
    /// same posture as the wire contract's own Contract/ duplication) and, for reconciliation, by
    /// this same plugin (<see cref="BuildWatchdog"/>).
    /// </summary>
    public static class BuildStatusStore
    {
        public const int CurrentSchema = 1;

        public const string StateQueued = "queued";
        public const string StateRunning = "running";
        public const string StateComplete = "complete"; // DESIGN-08 §5: 'complete', not 'succeeded'
        public const string StateFailed = "failed";
        public const string StateCancelled = "cancelled";
        public const string StateAbandoned = "abandoned";

        public static bool IsTerminal(string state) =>
            state == StateComplete || state == StateFailed || state == StateCancelled || state == StateAbandoned;

        /// <summary>Same convention as <c>HadesBoot.BuildHello</c>/<c>AssetPathGuard.Canonicalize</c>
        /// - no <see cref="Environment.SpecialFolder"/> anywhere, so this is immune to the
        /// .NET-vs-Mono <c>SpecialFolder.ApplicationData</c> disagreement <c>HadesConnectionFile</c>'s
        /// own doc comment describes (DESIGN-03 §4.2).</summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        /// <summary>'Library/' is already excluded from indexing and file-watching on the core side
        /// (DESIGN-03 §4.2) - writing here causes zero graph churn.</summary>
        public static string StatusPath => Path.Combine(ProjectRoot, "Library", "Hades", "build-status.json");

        public sealed class Document
        {
            public int Schema;
            public string BuildId;
            public string BootNonce;
            public string State;
            public string Target;
            public string Profile;
            public string OutputPath;
            public long? QueuedAtUtcMs;
            public long? StartedAtUtcMs;
            public long? FinishedAtUtcMs;
            public string UnityVersion;
            public string EditorLogPath;
            public int? TotalErrors;
            public int? TotalWarnings;
            public long? OutputSizeBytes;
            public List<(string Step, string Type, string Content)> Errors = new List<(string, string, string)>();
            public bool ErrorsTruncated;
            public string Note;
        }

        public static JsonValue ToJson(Document d)
        {
            var errors = JsonValue.NewArray();
            foreach (var e in d.Errors)
            {
                errors.Add(JsonValue.NewObject()
                    .SetProperty("step", JsonValue.String(e.Step ?? ""))
                    .SetProperty("type", JsonValue.String(e.Type ?? ""))
                    .SetProperty("content", JsonValue.String(e.Content ?? "")));
            }

            return JsonValue.NewObject()
                .SetProperty("schema", JsonValue.Integer(d.Schema))
                .SetProperty("buildId", JsonValue.String(d.BuildId))
                .SetProperty("bootNonce", JsonValue.String(d.BootNonce))
                .SetProperty("state", JsonValue.String(d.State))
                .SetProperty("target", JsonValue.String(d.Target))
                .SetProperty("profile", d.Profile != null ? JsonValue.String(d.Profile) : JsonValue.Null)
                .SetProperty("outputPath", JsonValue.String(d.OutputPath))
                .SetProperty("queuedAtUtcMs", d.QueuedAtUtcMs.HasValue ? JsonValue.Integer(d.QueuedAtUtcMs.Value) : JsonValue.Null)
                .SetProperty("startedAtUtcMs", d.StartedAtUtcMs.HasValue ? JsonValue.Integer(d.StartedAtUtcMs.Value) : JsonValue.Null)
                .SetProperty("finishedAtUtcMs", d.FinishedAtUtcMs.HasValue ? JsonValue.Integer(d.FinishedAtUtcMs.Value) : JsonValue.Null)
                .SetProperty("unityVersion", JsonValue.String(d.UnityVersion ?? ""))
                .SetProperty("editorLogPath", JsonValue.String(d.EditorLogPath ?? ""))
                .SetProperty("totalErrors", d.TotalErrors.HasValue ? JsonValue.Integer(d.TotalErrors.Value) : JsonValue.Null)
                .SetProperty("totalWarnings", d.TotalWarnings.HasValue ? JsonValue.Integer(d.TotalWarnings.Value) : JsonValue.Null)
                .SetProperty("outputSizeBytes", d.OutputSizeBytes.HasValue ? JsonValue.Integer(d.OutputSizeBytes.Value) : JsonValue.Null)
                .SetProperty("errors", errors)
                .SetProperty("errorsTruncated", JsonValue.Bool(d.ErrorsTruncated))
                .SetProperty("note", d.Note != null ? JsonValue.String(d.Note) : JsonValue.Null);
        }

        /// <summary>Tolerant of missing/extra fields - a schema this plugin does not recognise (from
        /// the future, or hand-edited) still parses whatever it can; <see cref="TryRead"/>'s own
        /// caller is what decides whether an unexpected <see cref="Document.Schema"/> matters.</summary>
        public static Document FromJson(JsonValue v)
        {
            var d = new Document
            {
                Schema = (int)OptInt(v, "schema", CurrentSchema),
                BuildId = OptString(v, "buildId"),
                BootNonce = OptString(v, "bootNonce"),
                State = OptString(v, "state"),
                Target = OptString(v, "target"),
                Profile = OptString(v, "profile"),
                OutputPath = OptString(v, "outputPath"),
                QueuedAtUtcMs = OptLong(v, "queuedAtUtcMs"),
                StartedAtUtcMs = OptLong(v, "startedAtUtcMs"),
                FinishedAtUtcMs = OptLong(v, "finishedAtUtcMs"),
                UnityVersion = OptString(v, "unityVersion"),
                EditorLogPath = OptString(v, "editorLogPath"),
                TotalErrors = OptInt(v, "totalErrors", null),
                TotalWarnings = OptInt(v, "totalWarnings", null),
                OutputSizeBytes = OptLong(v, "outputSizeBytes"),
                ErrorsTruncated = v.TryGetProperty("errorsTruncated", out var et) && et != null && et.Kind == JsonValueKind.Boolean && et.AsBoolean(),
                Note = OptString(v, "note"),
            };

            d.Errors = new List<(string, string, string)>();
            if (v.TryGetProperty("errors", out var errors) && errors != null && errors.Kind == JsonValueKind.Array)
            {
                foreach (var e in errors.Items)
                    d.Errors.Add((OptString(e, "step") ?? "", OptString(e, "type") ?? "", OptString(e, "content") ?? ""));
            }

            return d;
        }

        static string OptString(JsonValue v, string key) =>
            v != null && v.TryGetProperty(key, out var p) && p != null && p.Kind == JsonValueKind.String ? p.AsString() : null;

        static long? OptLong(JsonValue v, string key) =>
            v != null && v.TryGetProperty(key, out var p) && p != null && p.Kind == JsonValueKind.Integer ? p.AsInteger() : (long?)null;

        static int? OptInt(JsonValue v, string key, int? fallback) =>
            v != null && v.TryGetProperty(key, out var p) && p != null && p.Kind == JsonValueKind.Integer ? (int)p.AsInteger() : fallback;

        /// <summary>Never throws - a missing file, a mid-write partial document, or unparsable JSON
        /// all read as "not ready yet, retry", the same <c>HadesConnectionFile.TryRead</c> posture
        /// (DESIGN-03 §4.2.2). No explicit <c>FileShare</c> needed on this (plugin-side) read: this
        /// process is the only writer, and every write is atomic (see <see cref="Write"/>) - the
        /// core's OWN reader is the one that needs <c>FileShare.ReadWrite | Delete</c>, since it is
        /// a different process reading a file this one owns.</summary>
        public static Document TryRead()
        {
            try
            {
                if (!File.Exists(StatusPath)) return null;
                var text = File.ReadAllText(StatusPath);
                return MiniJson.TryParse(text, out var value, out _) && value != null ? FromJson(value) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>DESIGN-03 §4.2.2: <c>File.Replace</c> is atomic-on-rename on both NTFS and APFS
        /// and IS in netstandard2.1; it requires the destination to exist, so the first-ever write
        /// falls back to a plain <c>File.Move</c> (whose 3-argument overwrite overload is NOT in
        /// netstandard2.1, which is exactly why this two-branch shape is needed rather than one
        /// unconditional call).</summary>
        public static void Write(Document d)
        {
            var dir = Path.GetDirectoryName(StatusPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tempPath = StatusPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, MiniJson.Write(ToJson(d)));

            if (File.Exists(StatusPath))
                File.Replace(tempPath, StatusPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, StatusPath);
        }
    }

    /// <summary>
    /// The boot nonce (DESIGN-03 §4.4) and the intra-session build-liveness static (DESIGN-08 §12's
    /// addition to §4.4). Both exist for the same reason: a non-terminal status file must never be
    /// a PERMANENT lockout after an Editor crash, or after this Editor's own managed continuation
    /// dies without a domain reload to explain it.
    /// </summary>
    public static class BuildSession
    {
        const string BootNonceKey = "Hades.Build.BootNonce";

        /// <summary>DESIGN-08 §12: a PLAIN static, deliberately not <see cref="SessionState"/> - a
        /// domain reload wipes it, which is exactly the signal <see cref="BuildWatchdog.Tick"/>
        /// needs: "the file says 'running', the nonce still matches (same Editor process), but this
        /// build's own managed continuation is gone" can only mean a reload happened since the file
        /// was last written 'running', because nothing else in this design can clear a plain static
        /// without also bumping <see cref="Hades.Runtime.HadesBoot"/>'s own SessionState-persisted
        /// state. Same "wiped by a reload = the signal itself" reasoning
        /// <see cref="Tools.PlayModeCommands"/>' <c>PlayModeSession</c> mirror field documents, and
        /// the same reasoning DESIGN-06 §4.4 gives its own '_sawRunning' static.
        ///
        /// Public - not internal - for the same "no InternalsVisibleTo between this assembly and
        /// Hades.Tests.Editor" reason every other test seam in this plugin is (see e.g.
        /// <see cref="ProjectCommands.StartTestRun"/>'s own doc comment) - BuildCommandsTests sets
        /// this directly to prove <see cref="BuildWatchdog"/>'s intra-session reconciliation without
        /// a real domain reload.</summary>
        public static bool BuildInFlight;

        /// <summary>Lazily created once per real Editor process and cached in
        /// <see cref="SessionState"/>, which survives every domain reload within that process and is
        /// cleared only by a genuine restart - DESIGN-03 §4.4's discriminator. Reading this from
        /// <see cref="BuildWatchdog.Install"/> as early as possible (this Editor's very first tick)
        /// means the nonce exists before any 'project.build_start' call could possibly race its
        /// creation.</summary>
        public static string CurrentBootNonce
        {
            get
            {
                var existing = SessionState.GetString(BootNonceKey, "");
                if (!string.IsNullOrEmpty(existing)) return existing;

                var fresh = Guid.NewGuid().ToString("N");
                SessionState.SetString(BootNonceKey, fresh);
                return fresh;
            }
        }
    }

    /// <summary>
    /// The <see cref="EditorApplication.update"/> hook that reconciles a non-terminal status file
    /// to 'abandoned' - PER TICK (DESIGN-08 O2), not boot-only, mirroring
    /// <see cref="Tools.PlayModeCommands"/>' own <c>PlayModeWatchdog</c>. Boot-only reconciliation
    /// would miss the intra-session case (DESIGN-08 §12: 'running', matching nonce,
    /// <see cref="BuildSession.BuildInFlight"/> false) entirely, since nothing about that case
    /// involves a reload for a boot-only pass to run against.
    /// </summary>
    public static class BuildWatchdog
    {
        static bool _installed;

        /// <summary>Idempotent - called from <see cref="Hades.Runtime.HadesBoot"/>'s static ctor,
        /// which re-runs after every domain reload (see that class's own doc comment) - same
        /// convention as <c>PlayModeWatchdog.Install</c>.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            _ = BuildSession.CurrentBootNonce; // create it as early in this session as possible

            EditorApplication.update += Tick;
            Tick(); // boot is simply the first tick - see this class's own doc comment.
        }

        /// <summary>Runs the DESIGN-03 §4.4 reconciliation table exactly once. Public so tests can
        /// drive it directly and deterministically, without a real domain reload or a real 30s
        /// wait.</summary>
        public static void Tick()
        {
            var doc = BuildStatusStore.TryRead();
            if (doc == null || BuildStatusStore.IsTerminal(doc.State)) return;

            var currentNonce = BuildSession.CurrentBootNonce;
            if (doc.BootNonce != currentNonce)
            {
                Abandon(doc, "The Unity Editor that started this build is gone - most likely a crash or "
                    + "force-quit while it was in progress. Retry.");
                return;
            }

            if (doc.State == BuildStatusStore.StateRunning && !BuildSession.BuildInFlight)
            {
                Abandon(doc, "This build's own tracked activity is gone from this Editor session (most "
                    + "likely a script recompile interrupted it) even though the Editor itself did not "
                    + "restart. Retry.");
                return;
            }

            if (doc.State == BuildStatusStore.StateQueued && doc.QueuedAtUtcMs.HasValue)
            {
                var queuedAt = DateTimeOffset.FromUnixTimeMilliseconds(doc.QueuedAtUtcMs.Value).UtcDateTime;
                if (DateTime.UtcNow - queuedAt > BuildCommands.QueuedGraceWindow)
                {
                    Abandon(doc, "The Unity Editor never started this build - a script recompile most "
                        + "likely interrupted it before it began. Retry.");
                }
            }
        }

        static void Abandon(BuildStatusStore.Document doc, string note)
        {
            doc.State = BuildStatusStore.StateAbandoned;
            doc.FinishedAtUtcMs = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
            doc.Note = note;
            BuildStatusStore.Write(doc);
        }
    }
}
