// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Globalization;
using System.Reflection;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// DESIGN-06 (docs/design/DESIGN-06-baking-and-subsystems.md), Lane G of the roadmap's Phase 2 -
    /// the roadmap's own "highest-risk lane". One wire command, <c>scene.bake</c> (action
    /// start|status|cancel), backing the app's <c>scene_bake</c> MCP tool
    /// (Hades.Server.Mcp.SceneBakeTool). One file, carrying everything this design needs, matching
    /// BuildCommands.cs/PlayModeCommands.cs' own "one file, several classes, one concern" convention:
    ///
    /// <see cref="BakeKeys"/>        - every DESIGN-06 SessionState key name, in one place (owned
    ///                                 here; BuildCommands.cs's bake x build preflight reads the same
    ///                                 constants - see that file's own doc comment and ROADMAP.md §4).
    /// <see cref="SceneBakeCommands"/> - the wire handler.
    /// <see cref="RunningState"/>    - the three-valued oracle result (Running/NotRunning/Indeterminate).
    /// <see cref="BakeTargetSeams"/> - the <c>IsRunning(scope)</c> seam: swappable per-target Func
    ///                                 fields, written against the seam from the FIRST implementation
    ///                                 (lighting/occlusion), with navmesh as the seam's SECOND
    ///                                 implementation - see that class's own doc comment.
    /// <see cref="NavMeshBridge"/>   - the reflection bridge over the optional com.unity.ai.navigation
    ///                                 package's NavMeshAssetManager.
    /// <see cref="BakeJobStore"/>    - the single-slot, SessionState-backed job record (§4.4).
    /// <see cref="BakeWatchdog"/>    - the EditorApplication.update tick observer AND (folded into the
    ///                                 same idempotent-once-per-reload Install(), so HadesBoot needs
    ///                                 only ONE new line - see ROADMAP.md §4's HadesBoot row) the
    ///                                 process-wide reload-epoch increment.
    /// <see cref="CapabilityProbe"/> - DESIGN-06 §6 Layer 2/3: an execution-free "can this target run
    ///                                 at all in this AppDomain" check, carried home in the Hello
    ///                                 handshake (see HadesBoot.BuildHello).
    ///
    /// <para><b>The single most important structural requirement (the task brief, verbatim).</b> The
    /// job store, tick observer and poll rules are written against the <c>bool IsRunning(string[]
    /// scope)</c> seam from the very first implementation - lighting/occlusion are the seam's FIRST
    /// implementation (<c>_ =&gt; Lightmapping.isRunning</c> / <c>_ =&gt; StaticOcclusionCulling.isRunning</c>,
    /// scope written empty); navmesh is the SECOND implementation of an interface that already exists.
    /// Realised here as <see cref="BakeTargetSeams.Resolve"/> - three-valued from the start (a global
    /// boolean can never itself be "indeterminate", but the SEAM'S return type must already allow for
    /// a scoped predicate that can), so navmesh's divergence (DESIGN-06 §4.4 "Two oracle shapes") is a
    /// second switch-case, never a rewrite of the store/observer/poll rules above it.</para>
    ///
    /// <para><b>The lease decision (§4.3).</b> A bake holds no <see cref="ReloadGate"/> lease for its
    /// duration - only 'start'/'cancel's own BOUNDED prep is lease-protected (mirroring
    /// project.run_tests/project.build_start's own shape: acquire, bounded work, release, and - for
    /// 'start' only - the actual kick-off happens strictly AFTER that release, never inside it, so a
    /// held lease can never fight itself). A domain reload therefore cancels an in-progress bake, and
    /// the epoch mechanism below is what lets Hades say so honestly rather than reporting a phantom
    /// 'complete'.</para>
    ///
    /// <para><b>Never synthesize a terminal state from the absence of evidence (§4.4's governing
    /// principle).</b> <see cref="BakeJobStore.Terminal"/> is written exactly twice: by
    /// <see cref="BakeWatchdog.Tick"/>, on a POSITIVELY OBSERVED running-&gt;not-running transition
    /// within the job's own epoch ("complete"); and by 'cancel'/'status' themselves for the two cases
    /// they can each determine with certainty ("cancelled:request"; "cancelled:reload", only once an
    /// epoch mismatch AND "not running" are BOTH true). Every other shape - not yet observed running,
    /// an epoch mismatch with something still running, or a navmesh <c>Resolve</c> failure - answers
    /// 'indeterminate' and writes nothing. An agent told 'indeterminate' re-polls or asks; an agent
    /// told 'complete' proceeds to build on a NavMesh that may not exist.</para>
    /// </summary>
    internal static class SceneBakeCommands
    {
        static readonly string[] ValidActions = { "start", "status", "cancel" };
        static readonly string[] ValidTargets = { "lighting", "occlusion", "navmesh" };

        internal static JsonValue Bake(ReloadGate gate, JsonValue @params)
        {
            var action = JsonParams.RequireString(@params, "action", "scene.bake");
            switch (action)
            {
                case "start":
                    return LeaseScope.Run(gate, "scene.bake", () => DoStart(@params));
                case "status":
                    return DoStatus(@params);
                case "cancel":
                    return LeaseScope.Run(gate, "scene.bake", () => DoCancel(@params));
                default:
                    throw new ArgumentException(
                        "scene.bake: 'action' must be one of " + string.Join(", ", ValidActions) + ", got '" + action + "'.");
            }
        }

        // ---------------------------------------------------------------- start

        internal static JsonValue DoStart(JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params, "action", "target");
            var target = JsonParams.RequireString(@params, "target", "scene.bake:start");
            if (Array.IndexOf(ValidTargets, target) < 0)
            {
                throw new ArgumentException(
                    "scene.bake: 'target' must be one of " + string.Join(", ", ValidTargets) + ", got '" + target + "'.");
            }

            // DESIGN-06 §6.3 - a successful call reporting an unavailable capability, never a thrown
            // TypeLoadException. Only navmesh needs this: lighting/occlusion are built into
            // UnityEditor with no package dependency at all.
            if (target == "navmesh" && !BakeTargetSeams.NavMeshTypesAreResolvable())
                return UnavailableResult(target, NavMeshBridge.UnavailableDetail());

            var currentEpoch = BakeReloadEpoch.Current;

            // Three-branch refusal table (§4.4 "Related edge, same root"). First: is OUR OWN current
            // job (same target does not need to match - only one job can ever be in flight at a time,
            // single-slot store) still running, in the epoch we started it in?
            if (BakeJobStore.HasJob && string.IsNullOrEmpty(BakeJobStore.Terminal) && BakeJobStore.JobEpoch == currentEpoch)
            {
                var (ownState, _) = BakeTargetSeams.Resolve(BakeJobStore.CurrentTarget, BakeJobStore.CurrentScope);
                if (ownState == RunningState.Running || ownState == RunningState.Indeterminate)
                {
                    throw new InvalidOperationException(
                        "A bake Hades started (job '" + BakeJobStore.CurrentJobId + "', target '" + BakeJobStore.CurrentTarget
                        + "') is already in progress in this Editor. Poll scene_bake with action 'status' "
                        + "and jobId '" + BakeJobStore.CurrentJobId + "', or wait for it to finish.");
                }
            }

            // Second: a bake Hades did NOT start, currently running for THIS target. Only lighting and
            // occlusion expose a global boolean this can even be asked of - navmesh has no such
            // signal (DESIGN-06 §4.4 "Two oracle shapes"), so this check is a documented no-op there.
            if ((target == "lighting" && BakeTargetSeams.LightingIsRunning())
                || (target == "occlusion" && BakeTargetSeams.OcclusionIsRunning()))
            {
                throw new InvalidOperationException(
                    "A bake Hades did not start is already running in this Editor. Wait for it to finish, "
                    + "or cancel it in Unity - Hades will not cancel a bake it does not own.");
            }

            // Third (implicit): not running - proceed, overwrite the slot.
            var (started, scope, error) = BakeTargetSeams.Start(target);
            if (!started)
            {
                throw new InvalidOperationException(
                    error ?? ("scene.bake: failed to start a '" + target + "' bake - see the Unity console."));
            }

            var jobId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            BakeJobStore.MarkStarted(jobId, target, scope, now, currentEpoch);
            BakeWatchdog.NotifyJobStarted();

            // DESIGN-08 §5 / ROADMAP.md: "DESIGN-06 drops 'started'" from the status vocabulary - a
            // fresh job's own status is computed through the SAME resolver 'status' itself uses,
            // rather than a hardcoded literal, so the response is honest even in the (common) race
            // where isRunning has not flipped true on the very next tick yet (§4.4's second hole).
            return ResolveStatus(jobId, target, scope, now, currentEpoch, "");
        }

        // ---------------------------------------------------------------- status

        internal static JsonValue DoStatus(JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params, "action", "jobId");
            var requestedJobId = JsonParams.OptionalString(@params, "jobId");

            if (!BakeJobStore.HasJob) return NoneResult();

            var currentJobId = BakeJobStore.CurrentJobId;
            if (!string.IsNullOrEmpty(requestedJobId) && requestedJobId != currentJobId)
                return UnknownResult(requestedJobId, currentJobId);

            return ResolveStatus(currentJobId, BakeJobStore.CurrentTarget, BakeJobStore.CurrentScope,
                BakeJobStore.StartedAtUtc, BakeJobStore.JobEpoch, BakeJobStore.Terminal);
        }

        /// <summary>DESIGN-06 §4.4's five-rule resolution order, first match wins - shared by 'start'
        /// (against the record it just wrote) and 'status' (against whatever is currently recorded).
        /// The two writes this method can itself perform (rule 2's verbatim replay aside) are exactly
        /// the two the class doc comment names as certain: rule 3's 'cancelled:reload', the ONLY
        /// place this method mutates state.</summary>
        static JsonValue ResolveStatus(string jobId, string target, string[] scope, DateTime? startedAtUtc, int jobEpoch, string terminal)
        {
            // Rule 2: terminal is sticky, written once, never recomputed.
            if (!string.IsNullOrEmpty(terminal))
                return TerminalResult(jobId, target, startedAtUtc, terminal);

            var currentEpoch = BakeReloadEpoch.Current;

            // Rule 3: an epoch mismatch means a domain reload happened since this job started - this
            // job is unattributable. Never 'complete' from here in either branch.
            if (jobEpoch != currentEpoch)
            {
                var (state, missing) = BakeTargetSeams.Resolve(target, scope);
                if (state == RunningState.NotRunning)
                {
                    BakeJobStore.MarkTerminal("cancelled:reload");
                    return TerminalResult(jobId, target, startedAtUtc, "cancelled:reload");
                }

                // Running or Indeterminate: "most likely the user's" (Running) or "cannot resolve the
                // recorded scope" (Indeterminate) - either way, write nothing (§4.4's own "deliberately
                // non-sticky" rule) and let a later poll resolve it once the picture is clearer.
                return IndeterminateResult(jobId, target, startedAtUtc,
                    "A bake is running, but a domain reload occurred since this job started, so Hades "
                    + "cannot confirm it is this one - it is most likely the user's. Poll again; once it "
                    + "stops, this resolves to 'cancelled'."
                    + (missing != null ? " (Also could not resolve recorded surface '" + missing + "'.)" : ""));
            }

            // Rule 4/5: same epoch (ours).
            var (runState, missingSurface) = BakeTargetSeams.Resolve(target, scope);
            if (runState == RunningState.Running)
            {
                return RunningResult(jobId, target, startedAtUtc,
                    BakeTargetSeams.Progress(target, scope), BakeTargetSeams.Backend(target));
            }

            if (runState == RunningState.Indeterminate)
            {
                return IndeterminateResult(jobId, target, startedAtUtc,
                    "A recorded surface for this navmesh bake no longer resolves (deleted, or its scene "
                    + "was closed) - Hades cannot confirm whether it is still baking, so this is never "
                    + "reported as a completion signal."
                    + (missingSurface != null ? " Missing: '" + missingSurface + "'." : ""));
            }

            // Rule 5: not running, no completion observed - never 'complete', per the class doc
            // comment. 'complete' is written only by BakeWatchdog.Tick's own positively-observed
            // transition.
            return IndeterminateResult(jobId, target, startedAtUtc,
                "The bake is not currently running and no completion was observed - either it has only "
                + "just been queued and has not started this tick (poll again shortly), or it ended "
                + "without this plugin observing the transition.");
        }

        // ---------------------------------------------------------------- cancel

        internal static JsonValue DoCancel(JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params, "action", "jobId");
            var requestedJobId = JsonParams.OptionalString(@params, "jobId");

            if (!BakeJobStore.HasJob) return NoneResult();

            var currentJobId = BakeJobStore.CurrentJobId;
            if (!string.IsNullOrEmpty(requestedJobId) && requestedJobId != currentJobId)
                return UnknownResult(requestedJobId, currentJobId);

            var target = BakeJobStore.CurrentTarget;
            var scope = BakeJobStore.CurrentScope;
            var startedAtUtc = BakeJobStore.StartedAtUtc;
            var terminal = BakeJobStore.Terminal;

            // Idempotent - closing something already terminal is a safe no-op, same contract as
            // ReloadGate.Release/EndScriptEditing.
            if (!string.IsNullOrEmpty(terminal))
                return TerminalResult(currentJobId, target, startedAtUtc, terminal);

            var currentEpoch = BakeReloadEpoch.Current;
            var jobEpoch = BakeJobStore.JobEpoch;

            if (jobEpoch != currentEpoch)
            {
                // §10.18: navmesh's cancel is handle-only and UNVERIFIED to survive a domain reload -
                // refuse with an actionable message, never silently no-op, until that is verified.
                if (target == "navmesh")
                {
                    throw new InvalidOperationException(
                        "Hades can no longer cancel this bake - the Editor reloaded since it started; "
                        + "cancel it in the Navigation window.");
                }

                var (state, _) = BakeTargetSeams.Resolve(target, scope);
                if (state == RunningState.NotRunning)
                {
                    BakeJobStore.MarkTerminal("cancelled:reload");
                    return TerminalResult(currentJobId, target, startedAtUtc, "cancelled:reload");
                }

                throw new InvalidOperationException(
                    "A bake Hades did not start is running in this Editor - the Editor reloaded since "
                    + "this job started, so Hades cannot confirm it is the same one. Hades will not "
                    + "cancel a bake it does not own.");
            }

            var (runState, _) = BakeTargetSeams.Resolve(target, scope);
            if (runState != RunningState.Running)
            {
                // Nothing running right now to cancel - report the honest current state rather than
                // pretending a cancel action happened.
                return ResolveStatus(currentJobId, target, scope, startedAtUtc, jobEpoch, terminal);
            }

            var cancelError = BakeTargetSeams.Cancel(target, scope);
            if (cancelError != null) throw new InvalidOperationException(cancelError);

            BakeJobStore.MarkTerminal("cancelled:request");
            return TerminalResult(currentJobId, target, startedAtUtc, "cancelled:request");
        }

        // ---------------------------------------------------------------- result builders

        static JsonValue NoneResult() =>
            JsonValue.NewObject()
                .SetProperty("status", JsonValue.String("none"))
                .SetProperty("detail", JsonValue.String("No bake has been started this session. Call scene_bake with action 'start' first."));

        static JsonValue UnknownResult(string requestedJobId, string currentJobId) =>
            JsonValue.NewObject()
                .SetProperty("status", JsonValue.String("unknown"))
                .SetProperty("jobId", JsonValue.String(requestedJobId))
                .SetProperty("detail", JsonValue.String(
                    "No bake with jobId '" + requestedJobId + "' is known. The most recently started job's "
                    + "id is '" + currentJobId + "' - omit 'jobId' to act on it, or call scene_bake with "
                    + "action 'start' to begin a new bake."));

        static JsonValue UnavailableResult(string target, string detail) =>
            JsonValue.NewObject()
                .SetProperty("target", JsonValue.String(target))
                .SetProperty("status", JsonValue.String("unavailable"))
                .SetProperty("detail", JsonValue.String(detail));

        static JsonValue RunningResult(string jobId, string target, DateTime? startedAtUtc, float? progress, string backend)
        {
            var result = BaseResult(jobId, target, startedAtUtc, "running",
                "A '" + target + "' bake is currently running.");
            if (progress.HasValue) result.SetProperty("progress", JsonValue.Float(progress.Value));
            if (backend != null) result.SetProperty("backend", JsonValue.String(backend));
            return result;
        }

        static JsonValue IndeterminateResult(string jobId, string target, DateTime? startedAtUtc, string detail) =>
            BaseResult(jobId, target, startedAtUtc, "indeterminate", detail);

        static JsonValue TerminalResult(string jobId, string target, DateTime? startedAtUtc, string terminal)
        {
            var (status, detail) = DescribeTerminal(target, terminal);
            var result = BaseResult(jobId, target, startedAtUtc, status, detail);

            // DESIGN-06 §4.4 stores no separate "finished" timestamp (the design's own six-key
            // budget) - durationSeconds is therefore measured to the moment this is READ, not to the
            // (unrecorded) moment the transition actually happened. Documented here rather than
            // silently approximated - the same "an honest approximation beats a silent one" standard
            // ProjectSummary.AppliedDefines holds itself to.
            if (startedAtUtc.HasValue)
                result.SetProperty("durationSeconds", JsonValue.Float((float)(DateTime.UtcNow - startedAtUtc.Value).TotalSeconds));

            if (target == "lighting")
            {
                var backend = BakeTargetSeams.Backend(target);
                if (backend != null) result.SetProperty("backend", JsonValue.String(backend));
            }

            if (status == "failed") result.SetProperty("error", JsonValue.String(detail));
            return result;
        }

        static (string Status, string Detail) DescribeTerminal(string target, string terminal)
        {
            if (terminal == "complete")
                return ("complete", "The '" + target + "' bake ran to completion without this plugin observing "
                    + "an abort - not a guarantee the output asset was independently verified.");
            if (terminal == "cancelled:reload")
                return ("cancelled", "This bake was cancelled by a domain reload - Hades holds no reload lock "
                    + "across a bake (the alternative would block the user's own recompiles for the bake's "
                    + "entire duration), so a reload always ends any bake in progress.");
            if (terminal == "cancelled:request")
                return ("cancelled", "This bake was cancelled by an explicit scene_bake action='cancel' call.");
            if (terminal != null && terminal.StartsWith("failed:", StringComparison.Ordinal))
                return ("failed", terminal.Substring("failed:".Length));

            return ("failed", "Unrecognised terminal state '" + terminal + "'.");
        }

        static JsonValue BaseResult(string jobId, string target, DateTime? startedAtUtc, string status, string detail)
        {
            var result = JsonValue.NewObject()
                .SetProperty("jobId", JsonValue.String(jobId))
                .SetProperty("target", JsonValue.String(target))
                .SetProperty("status", JsonValue.String(status))
                .SetProperty("detail", JsonValue.String(detail));

            if (startedAtUtc.HasValue)
                result.SetProperty("startedAtUtcMs", JsonValue.Integer(ToUnixTimeMs(startedAtUtc.Value)));

            return result;
        }

        static long ToUnixTimeMs(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    /// <summary>DESIGN-06 §4.4's three-valued oracle result. <see cref="NotRunning"/> is the only
    /// genuine completion signal; <see cref="Indeterminate"/> must NEVER be treated as one - see
    /// SceneBakeCommands' own class doc comment.</summary>
    public enum RunningState
    {
        Running,
        NotRunning,
        Indeterminate,
    }

    /// <summary>
    /// Every DESIGN-06 SessionState key name, in one place. DESIGN-06 (this file) owns these names -
    /// it is the writer; DESIGN-03's bake x build preflight (BuildCommands.cs,
    /// <c>HadesOwnedBakeInProgressRefusal</c>) is the one other reader. ROADMAP.md §4: "a rename Lane
    /// C misses fails silently - the reader finds absent keys, concludes 'no bake', and never fires
    /// ... Preferred mechanism now that both lanes exist: a single shared BakeKeys constants class in
    /// the plugin, referenced by both." A rename here now fails BuildCommands.cs to COMPILE instead.
    ///
    /// Public - not internal - for the same "no InternalsVisibleTo between this assembly and
    /// Hades.Tests.Editor" convention every other plugin-side test seam in this codebase follows
    /// (see e.g. CommandTable.Handlers' own doc comment). Phase 3 integration correction: this was
    /// internal, which meant BuildCommandsTests.cs (a separate assembly) could not reference it at
    /// all and had to hardcode the key names as string literals instead - exactly the staleness risk
    /// the class exists to close. Widening to public changes no behaviour.
    /// </summary>
    public static class BakeKeys
    {
        public const string JobId = "Hades.Bake.JobId";
        public const string Target = "Hades.Bake.Target";
        public const string StartedTicks = "Hades.Bake.StartedTicks";
        public const string Epoch = "Hades.Bake.Epoch";
        public const string Terminal = "Hades.Bake.Terminal";
        public const string Scope = "Hades.Bake.Scope";

        /// <summary>Process-wide, incremented once per domain reload - see
        /// <see cref="BakeWatchdog.Install"/>. NOT under the "Hades.Bake." prefix (the other six keys
        /// belong to one job; this one outlives every job in the process).</summary>
        public const string ReloadEpoch = "Hades.ReloadEpoch";
    }

    /// <summary>
    /// The single-slot, SessionState-backed job record (DESIGN-06 §4.4). Mirrors
    /// <c>TestRunResultStore</c> in PERSISTENCE (SessionState survives the domain reload a reload can
    /// cause) and diverges from it in ORACLE - see this file's own class doc comment for why. Every
    /// value is stored as a STRING (even <see cref="JobEpoch"/>, an integer) - the same convention
    /// <c>PlayModeSession</c> already establishes, so every key erases uniformly through
    /// <see cref="SessionState.EraseString"/> with no per-type Erase* call to keep in sync. Safe to
    /// touch from a command handler / <see cref="BakeWatchdog"/> tick because both always run on the
    /// main thread via MainThreadPump - the identical guarantee <see cref="ReloadGate"/>'s own
    /// SessionState calls rely on.
    /// </summary>
    public static class BakeJobStore
    {
        public static bool HasJob => !string.IsNullOrEmpty(SessionState.GetString(BakeKeys.JobId, ""));
        public static string CurrentJobId => SessionState.GetString(BakeKeys.JobId, "");
        public static string CurrentTarget => SessionState.GetString(BakeKeys.Target, "");

        /// <summary>"" while non-terminal; once written, sticky - see
        /// <see cref="MarkTerminal"/>.</summary>
        public static string Terminal => SessionState.GetString(BakeKeys.Terminal, "");

        public static int JobEpoch
        {
            get
            {
                var raw = SessionState.GetString(BakeKeys.Epoch, "");
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) ? epoch : -1;
            }
        }

        /// <summary>Newline-joined identities of what this job started baking - empty for
        /// lighting/occlusion (scene-global by nature); for navmesh, the GlobalObjectId strings of
        /// the NavMeshSurface components <see cref="NavMeshBridge.Start"/> passed to
        /// StartBakingSurfaces. See <see cref="BakeTargetSeams.Resolve"/> for how this is consumed.</summary>
        public static string[] CurrentScope
        {
            get
            {
                var raw = SessionState.GetString(BakeKeys.Scope, "");
                return string.IsNullOrEmpty(raw) ? Array.Empty<string>() : raw.Split('\n');
            }
        }

        public static DateTime? StartedAtUtc
        {
            get
            {
                var raw = SessionState.GetString(BakeKeys.StartedTicks, "");
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                    ? new DateTime(ticks, DateTimeKind.Utc)
                    : (DateTime?)null;
            }
        }

        /// <summary>Overwrites the single slot unconditionally - the caller (SceneBakeCommands.DoStart)
        /// has already run the §4.4 refusal table and decided overwriting is safe.</summary>
        public static void MarkStarted(string jobId, string target, string[] scope, DateTime nowUtc, int epoch)
        {
            SessionState.SetString(BakeKeys.JobId, jobId);
            SessionState.SetString(BakeKeys.Target, target);
            SessionState.SetString(BakeKeys.StartedTicks, nowUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            SessionState.SetString(BakeKeys.Epoch, epoch.ToString(CultureInfo.InvariantCulture));
            SessionState.SetString(BakeKeys.Terminal, "");
            SessionState.SetString(BakeKeys.Scope, scope == null || scope.Length == 0 ? "" : string.Join("\n", scope));
        }

        /// <summary>Sticky - a no-op once <see cref="Terminal"/> is already non-empty, so every caller
        /// (BakeWatchdog.Tick's own 'complete', SceneBakeCommands' 'cancelled:*') can call this
        /// unconditionally without checking first, and "terminal is written once and never
        /// recomputed" (§4.4) holds by construction rather than by caller discipline.</summary>
        public static void MarkTerminal(string terminal)
        {
            if (!string.IsNullOrEmpty(Terminal)) return;
            SessionState.SetString(BakeKeys.Terminal, terminal);
        }

        /// <summary>Test-only reset. Public visibility follows this whole file's "no
        /// InternalsVisibleTo between this assembly and Hades.Tests.Editor" convention (see
        /// ProjectCommands.TriggerRecompile's own doc comment) - internal is used throughout the rest
        /// of this file for symmetry with SceneApplyCommands/ScriptableObjectApplyCommands, but this
        /// one method needs the same public escape hatch <see cref="TestRunResultStore.Reset"/> uses.</summary>
        public static void Reset()
        {
            SessionState.EraseString(BakeKeys.JobId);
            SessionState.EraseString(BakeKeys.Target);
            SessionState.EraseString(BakeKeys.StartedTicks);
            SessionState.EraseString(BakeKeys.Epoch);
            SessionState.EraseString(BakeKeys.Terminal);
            SessionState.EraseString(BakeKeys.Scope);
        }
    }

    /// <summary>
    /// The process-wide reload-epoch counter (DESIGN-06 §4.4) - the OWNERSHIP TOKEN, not merely a
    /// cancellation marker: a job is attributable to a currently-running bake only while its own
    /// recorded <see cref="BakeJobStore.JobEpoch"/> equals <see cref="Current"/>. Stored via
    /// <see cref="SessionState.GetInt"/>/<see cref="SessionState.SetInt"/> (unlike every other
    /// DESIGN-06 key, which stores as a string) because BuildCommands.cs's own
    /// <c>HadesOwnedBakeInProgressRefusal</c> already reads this exact key that way - matching its
    /// existing storage format is what keeps that reader correct, not a stylistic choice.
    /// </summary>
    public static class BakeReloadEpoch
    {
        public static int Current => SessionState.GetInt(BakeKeys.ReloadEpoch, 0);

        public static void Bump() => SessionState.SetInt(BakeKeys.ReloadEpoch, Current + 1);

        /// <summary>Test-only reset - production code never needs to un-bump this counter.</summary>
        public static void ResetForTests() => SessionState.EraseInt(BakeKeys.ReloadEpoch);
    }

    /// <summary>
    /// The <c>bool IsRunning(string[] scope)</c> seam (realised as the three-valued
    /// <see cref="Resolve"/>, per this file's own class doc comment), plus <c>Start</c>/<c>Progress</c>/
    /// <c>Cancel</c>/<c>Backend</c> - one field per target, every one a swappable public static
    /// delegate, same convention as <c>ProjectCommands.TriggerRecompile</c>/<c>StartTestRun</c>: a
    /// test that reassigns one MUST restore it in TearDown.
    ///
    /// <b>Lighting/occlusion - the seam's FIRST implementation.</b> A process-global boolean,
    /// scope ignored and written empty (§4.4 "Two oracle shapes"). <b>NavMesh - the SECOND
    /// implementation of the interface these two already established</b> - a scoped predicate over
    /// the job's own recorded identities via <see cref="NavMeshBridge"/>, never a stored handle (a
    /// domain reload destroys a handle; SessionState-backed identities survive it, and the epoch
    /// mechanism already handles "did a reload happen" uniformly for every target - see
    /// <see cref="NavMeshBridge"/>'s own class doc comment for why this does not need its own,
    /// second ownership rule).
    /// </summary>
    public static class BakeTargetSeams
    {
        public static Func<bool> LightingBakeAsync = () => Lightmapping.BakeAsync();
        public static Func<bool> LightingIsRunning = () => Lightmapping.isRunning;
        public static Func<float> LightingBuildProgress = () => Lightmapping.buildProgress;
        public static Action LightingCancelAction = () => Lightmapping.Cancel();

        /// <summary>DESIGN-06 §10.2/P12 (half-resolved): the CONFIGURED lightmapper
        /// (Lightmapping.lightingSettings.lightmapper), not the effective one - whether the GPU
        /// lightmapper's CPU fallback is detectable at all is H5, unresolved. Labelled "configured"
        /// in Hades.Server.Mcp.SceneBakeTool's own description, not merely in this comment.</summary>
        public static Func<string> LightingConfiguredBackend = () =>
        {
            var settings = Lightmapping.lightingSettings;
            return settings != null ? settings.lightmapper.ToString() : null;
        };

        public static Func<bool> OcclusionGenerateInBackground = () => StaticOcclusionCulling.GenerateInBackground();
        public static Func<bool> OcclusionIsRunning = () => StaticOcclusionCulling.isRunning;
        public static Action OcclusionCancelAction = () => StaticOcclusionCulling.Cancel();

        public static Func<(bool Started, string[] Scope, string Error)> NavMeshStart = NavMeshBridge.Start;
        public static Func<string[], (RunningState State, string MissingIdentity)> NavMeshResolve = NavMeshBridge.Resolve;
        public static Action<string[]> NavMeshCancelAction = NavMeshBridge.Cancel;

        /// <summary>The execution-free capability gate 'start' checks before ever calling
        /// <see cref="NavMeshStart"/> - a swappable seam (like every other field here) so a test can
        /// prove BOTH the 'unavailable' branch AND the "proceeds to Start" branch deterministically,
        /// regardless of whether com.unity.ai.navigation actually happens to be installed in whatever
        /// project this plugin's own EditMode suite runs inside.</summary>
        public static Func<bool> NavMeshTypesAreResolvable = NavMeshBridge.TypesAreResolvable;

        public static (bool Started, string[] Scope, string Error) Start(string target)
        {
            switch (target)
            {
                case "lighting":
                {
                    var ok = LightingBakeAsync();
                    return ok
                        ? (true, Array.Empty<string>(), null)
                        : (false, null, "Lightmapping.BakeAsync() returned false - the bake did not start (check the Unity console).");
                }
                case "occlusion":
                {
                    var ok = OcclusionGenerateInBackground();
                    return ok
                        ? (true, Array.Empty<string>(), null)
                        : (false, null, "StaticOcclusionCulling.GenerateInBackground() returned false - the bake did not start (check the Unity console).");
                }
                case "navmesh":
                    return NavMeshStart();
                default:
                    return (false, null, "Unknown bake target '" + target + "'.");
            }
        }

        public static (RunningState State, string MissingIdentity) Resolve(string target, string[] scope)
        {
            switch (target)
            {
                case "lighting": return (LightingIsRunning() ? RunningState.Running : RunningState.NotRunning, null);
                case "occlusion": return (OcclusionIsRunning() ? RunningState.Running : RunningState.NotRunning, null);
                case "navmesh": return NavMeshResolve(scope ?? Array.Empty<string>());
                default: return (RunningState.NotRunning, null);
            }
        }

        /// <summary>Null when the target reports none - occlusion exposes no progress fraction, and
        /// navmesh's would need the live per-operation handle (destroyed by any reload); rather than
        /// resurrect the "handle outlives a reload" hole this design spent §4.4 closing twice already,
        /// navmesh's progress is deliberately, always null in this version - see
        /// <see cref="NavMeshBridge"/>'s own class doc comment.</summary>
        public static float? Progress(string target, string[] scope) =>
            target == "lighting" && LightingIsRunning() ? (float?)LightingBuildProgress() : null;

        public static string Backend(string target) => target == "lighting" ? LightingConfiguredBackend() : null;

        /// <summary>Null on success; an actionable message on failure. Never throws - every target
        /// implementation catches its own reflection/engine-call failures.</summary>
        public static string Cancel(string target, string[] scope)
        {
            switch (target)
            {
                case "lighting": LightingCancelAction(); return null;
                case "occlusion": OcclusionCancelAction(); return null;
                case "navmesh":
                    try { NavMeshCancelAction(scope ?? Array.Empty<string>()); return null; }
                    catch (Exception ex) { return "Failed to cancel the navmesh bake: " + ex.Message; }
                default: return "Unknown bake target '" + target + "'.";
            }
        }
    }

    /// <summary>
    /// Reflection bridge over the optional com.unity.ai.navigation package's Editor-side bake
    /// manager (NavMeshAssetManager) - DESIGN-06 §4.4 "Two oracle shapes", VERIFIED (source,
    /// com.unity.ai.navigation v2.0.9, per ROADMAP.md P13): the modern navmesh bake path has NO
    /// global <c>isRunning</c> - baking is per-operation through NavMeshAssetManager's public
    /// <c>StartBakingSurfaces(Object[])</c> / <c>IsSurfaceBaking(NavMeshSurface)</c>.
    ///
    /// <para><b>UNVERIFIED IN THIS REPOSITORY: the exact namespace of NavMeshAssetManager/
    /// NavMeshSurface.</b> No Unity installation is referenced anywhere in this repo (DESIGN-06 §10's
    /// own standing caveat), so this class cannot compile against either type, and cannot even
    /// hardcode a fully-qualified name with confidence - the type has moved namespaces across package
    /// releases. Resolved by SIMPLE NAME across every loaded assembly instead
    /// (<see cref="UnityTypeSearch.FindAll"/>, the exact mechanism ScriptableObjectApplyCommands/
    /// AssetManageCommands already use for user script types) - broader than a fully-qualified
    /// lookup, but safe: this is an execution-free TYPE lookup, exactly
    /// <c>TestRunnerBridge.TypesAreResolvable</c>'s own "safe, execution-free check that the LOOKUP
    /// itself works" contract, and every caller here degrades to 'unavailable'/'indeterminate' if it
    /// ever resolves to nothing - never a TypeLoadException. If a future Editor install proves the
    /// namespace, replace the simple-name search with a direct <c>Type.GetType</c> call and DELETE
    /// this caveat - do not merely add a second, competing lookup on top of it.</para>
    ///
    /// <para><b>Identity, not a handle.</b> <see cref="Start"/> records each surface's
    /// <c>GlobalObjectId</c> (a Unity Editor primitive, always compile-time available - no reflection
    /// needed here) rather than the live component reference: a handle is a managed object a domain
    /// reload destroys, reintroducing the exact "keys outlive the truth" asymmetry DESIGN-06 §4.4
    /// closed twice already. <see cref="Resolve"/> re-resolves each identity fresh on every call via
    /// <c>GlobalObjectId.GlobalObjectIdentifierToObjectSlow</c> - a failure to resolve (the GameObject
    /// was deleted, or its scene was closed) is reported as <see cref="RunningState.Indeterminate"/>,
    /// never as "not running" (§4.4's three-valued predicate, the hole a naive <c>Any(...)</c> would
    /// otherwise introduce).</para>
    ///
    /// <para><b>Cancel</b> uses the always-present engine type <c>UnityEngine.AI.NavMeshBuilder</c>
    /// (core module, referenced directly - no reflection, and NOT the obsolete
    /// <c>UnityEditor.AI.NavMeshBuilder</c> legacy type DESIGN-06 explicitly says not to build on),
    /// reading each surface's own <c>navMeshData</c> property via reflection (the property itself
    /// belongs to the optional package).</para>
    /// </summary>
    internal static class NavMeshBridge
    {
        public static bool TypesAreResolvable() => ManagerType() != null && SurfaceType() != null;

        public static string UnavailableDetail() =>
            "com.unity.ai.navigation is not installed (or its NavMeshAssetManager/NavMeshSurface types "
            + "could not be resolved) in this project - install the 'AI Navigation' package via Package "
            + "Manager to use scene_bake with target 'navmesh'.";

        public static (bool Started, string[] Scope, string Error) Start()
        {
            var surfaceType = SurfaceType();
            var managerType = ManagerType();
            if (surfaceType == null || managerType == null) return (false, null, UnavailableDetail());

            var surfaces = UnityEngine.Object.FindObjectsByType(surfaceType, FindObjectsSortMode.None);
            if (surfaces == null || surfaces.Length == 0)
            {
                return (false, null,
                    "No NavMeshSurface components were found in the currently loaded scene(s) - add one "
                    + "(GameObject > AI > NavMesh Surface) before baking.");
            }

            object manager;
            MethodInfo startMethod;
            try
            {
                manager = InstanceOf(managerType);
                startMethod = managerType.GetMethod("StartBakingSurfaces", BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                return (false, null, "Could not resolve NavMeshAssetManager: " + ex.Message);
            }

            if (manager == null) return (false, null, "NavMeshAssetManager.instance was null.");
            if (startMethod == null)
            {
                return (false, null,
                    "NavMeshAssetManager has no public StartBakingSurfaces(Object[]) method in this "
                    + "installed package version.");
            }

            try
            {
                startMethod.Invoke(manager, new object[] { surfaces });
            }
            catch (Exception ex)
            {
                return (false, null, "StartBakingSurfaces threw: " + (ex.InnerException ?? ex).Message);
            }

            var scope = new string[surfaces.Length];
            for (var i = 0; i < surfaces.Length; i++)
                scope[i] = GlobalObjectId.GetGlobalObjectIdSlow((UnityEngine.Object)surfaces[i]).ToString();

            return (true, scope, null);
        }

        public static (RunningState State, string MissingIdentity) Resolve(string[] scope)
        {
            if (scope == null || scope.Length == 0) return (RunningState.NotRunning, null);

            var managerType = ManagerType();
            if (managerType == null) return (RunningState.Indeterminate, null); // package vanished mid-session

            object manager;
            MethodInfo isBakingMethod;
            try
            {
                manager = InstanceOf(managerType);
                isBakingMethod = managerType.GetMethod("IsSurfaceBaking", BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                return (RunningState.Indeterminate, null);
            }

            if (manager == null || isBakingMethod == null) return (RunningState.Indeterminate, null);

            var anyBaking = false;
            foreach (var identity in scope)
            {
                if (!GlobalObjectId.TryParse(identity, out var gid))
                    return (RunningState.Indeterminate, identity);

                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (resolved == null)
                    return (RunningState.Indeterminate, identity);

                bool baking;
                try { baking = (bool)isBakingMethod.Invoke(manager, new object[] { resolved }); }
                catch { return (RunningState.Indeterminate, identity); }

                if (baking) anyBaking = true;
            }

            return (anyBaking ? RunningState.Running : RunningState.NotRunning, null);
        }

        public static void Cancel(string[] scope)
        {
            if (scope == null || scope.Length == 0) return;

            foreach (var identity in scope)
            {
                if (!GlobalObjectId.TryParse(identity, out var gid)) continue;

                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as Component;
                if (resolved == null) continue;

                var navMeshDataProp = resolved.GetType().GetProperty("navMeshData", BindingFlags.Public | BindingFlags.Instance);
                if (navMeshDataProp?.GetValue(resolved) is UnityEngine.AI.NavMeshData navMeshData)
                    UnityEngine.AI.NavMeshBuilder.Cancel(navMeshData);
            }
        }

        static Type ManagerType()
        {
            var matches = UnityTypeSearch.FindAll("NavMeshAssetManager", typeof(ScriptableObject), out _);
            return matches.Count == 1 ? matches[0] : null;
        }

        static Type SurfaceType()
        {
            var matches = UnityTypeSearch.FindAll("NavMeshSurface", typeof(Component), out _);
            return matches.Count == 1 ? matches[0] : null;
        }

        static object InstanceOf(Type type)
        {
            var prop = type.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null);
        }
    }

    /// <summary>
    /// The <see cref="EditorApplication.update"/> tick observer (DESIGN-06 §4.4: "who writes
    /// Terminal='complete'. Not the poll - the poll is exactly the thing that cannot tell") -
    /// PER TICK, mirroring <c>PlayModeWatchdog</c>/<c>BuildWatchdog</c>'s own "boot is simply the
    /// first tick" shape. <see cref="Install"/> ALSO bumps <see cref="BakeReloadEpoch"/> - folded in
    /// here (rather than a second HadesBoot line) because both need the exact same "exactly once per
    /// domain reload" guard <c>_installed</c> already provides (a plain static, wiped by every
    /// reload), so HadesBoot needs only the one appended line this class doc comment names -
    /// ROADMAP.md §4's HadesBoot row: "G: reload-epoch increment".
    ///
    /// <see cref="_sawRunning"/> is a PLAIN static, deliberately - not SessionState. Any domain reload
    /// that wipes it also bumps the epoch, and rule 3 (SceneBakeCommands.ResolveStatus) fires before
    /// anything could consult it - the identical reasoning <c>ConsoleLogBuffer</c> gives for the
    /// opposite choice, and the identical reasoning DESIGN-06 §4.4 itself gives for this exact field.
    /// </summary>
    public static class BakeWatchdog
    {
        static bool _installed;
        static bool _sawRunning;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            // DESIGN-06 §4.4: "The epoch costs one line, because the hook already exists" - this IS
            // that line, and it runs exactly once per domain reload for the same reason
            // PlayModeWatchdog.Install/BuildWatchdog.Install's own bodies do: _installed is a plain
            // static, wiped by every reload, so this guard's body is fresh again on every boot.
            BakeReloadEpoch.Bump();

            EditorApplication.update += Tick;
            Tick(); // boot is simply the first tick.
        }

        /// <summary>Called by SceneBakeCommands.DoStart the moment a NEW job is recorded, so a stale
        /// <see cref="_sawRunning"/> left over from a PRIOR job (in the same epoch, e.g. a lighting
        /// bake that finished and was immediately re-baked) can never be misread as having already
        /// observed the NEW job running.</summary>
        public static void NotifyJobStarted() => _sawRunning = false;

        /// <summary>Runs the DESIGN-06 §4.4 reconciliation exactly once. Public so tests can drive it
        /// directly and deterministically, without a real EditorApplication.update tick or a real
        /// bake.</summary>
        public static void Tick()
        {
            if (!BakeJobStore.HasJob) return;
            if (!string.IsNullOrEmpty(BakeJobStore.Terminal)) return; // sticky - nothing to do

            var jobEpoch = BakeJobStore.JobEpoch;
            var currentEpoch = BakeReloadEpoch.Current;

            // "Across an epoch boundary it writes nothing at all and leaves the outcome to rule 3" -
            // §4.4, verbatim. The observer defers ENTIRELY to SceneBakeCommands.ResolveStatus once a
            // reload has happened since this job started; this is the one-line fix that closes the
            // attribution hole (the tick observer, not the poll, is what would otherwise write the
            // false success).
            if (jobEpoch != currentEpoch) return;

            var (state, _) = BakeTargetSeams.Resolve(BakeJobStore.CurrentTarget, BakeJobStore.CurrentScope);

            if (state == RunningState.Running)
            {
                _sawRunning = true;
                return;
            }

            if (state == RunningState.Indeterminate) return; // a Resolve failure is never a completion signal

            // NotRunning, same epoch:
            if (_sawRunning)
            {
                BakeJobStore.MarkTerminal("complete"); // positively observed running -> not-running
                _sawRunning = false;
            }
            // else: never observed running yet - stays non-terminal; SceneBakeCommands.ResolveStatus's
            // rule 5 reports 'indeterminate', never 'complete'.
        }

        /// <summary>Test-only reset. Public for the same reason <see cref="BakeJobStore.Reset"/> is.</summary>
        public static void ResetForTests() => _sawRunning = false;
    }

    /// <summary>
    /// DESIGN-06 §6.2/§6.3 - Layers 2 and 3 of discoverability: an execution-free "can this target
    /// actually run in this AppDomain" probe per bake target, computed once at boot
    /// (<see cref="Hades.Runtime.HadesBoot.BuildHello"/>) and carried home in the <c>Hello</c>
    /// handshake so <c>hades_charon_status</c> can serve it with zero round trips and zero
    /// busy-sensitivity - the property that makes it useful when the Editor is wedged. Never
    /// constructs or invokes anything - same "safe, execution-free check that the reflection LOOKUP
    /// itself works" contract as <c>TestRunnerBridge.TypesAreResolvable</c>.
    /// </summary>
    public static class CapabilityProbe
    {
        public static System.Collections.Generic.List<Hello.Capability> Compute()
        {
            var list = new System.Collections.Generic.List<Hello.Capability>
            {
                // Built into UnityEditor - no package, always available.
                new Hello.Capability { Id = "bake.lighting", Available = true },
                new Hello.Capability { Id = "bake.occlusion", Available = true },
            };

            var navAvailable = NavMeshBridge.TypesAreResolvable();
            list.Add(new Hello.Capability
            {
                Id = "bake.navmesh",
                Available = navAvailable,
                Reason = navAvailable ? null : NavMeshBridge.UnavailableDetail(),
            });

            return list;
        }
    }
}
