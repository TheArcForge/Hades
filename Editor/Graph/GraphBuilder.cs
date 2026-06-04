using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Core;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.Graph.Pipeline;
using ArcForge.Hades.Editor.Graph.Scanning;
using UnityEditor;
using UnityEngine;

[assembly: InternalsVisibleTo("ArcForge.Hades.Tests.Editor")]

namespace ArcForge.Hades.Editor.Graph
{
    public enum BuildStatus { Idle, Rebuilding, Updating, ScanningPackages }

    public class GraphBuilder
    {
        public static event Action OnRebuildComplete;

        readonly GraphDatabase _db;
        readonly ScannerRegistry _scannerRegistry;

        // Thread-safe "busy" flag mirroring _status. A long synchronous rebuild blocks
        // the main thread, so MCPServer's main-thread queue processor is frozen and cannot
        // answer "are we rebuilding?". Background transport threads read this volatile flag
        // instead to short-circuit calls with a structured busy response (no SQLite access).
        static volatile bool _busy;
        public static bool IsBusy => _busy;

        // True only during a *long*, main-thread-blocking operation (a full rebuild or
        // package scan), NOT during the fast transient incremental Updating state.
        // The MCP busy gate keys off this: incrementals are now O(changed) (Phase 9.6
        // Workstream A) and finish in well under a frame, so gating them would return a
        // spurious "busy" and open an at-least-once retry window on non-idempotent writes.
        // Only a genuine long op (which truly blocks the queue) warrants a busy response.
        static volatile bool _longOp;
        public static bool IsInLongOperation => _longOp;

        BuildStatus _statusBacking = BuildStatus.Idle;
        BuildStatus _status
        {
            get => _statusBacking;
            set
            {
                _statusBacking = value;
                _busy = value != BuildStatus.Idle;
                _longOp = value == BuildStatus.Rebuilding || value == BuildStatus.ScanningPackages;
            }
        }

        // Session-level node map: accumulates guid:fileId → nodeId across all scanned files
        // during a rebuild. Eliminates per-edge DB lookups in WriteScanResult.
        Dictionary<string, long> _sessionNodeMap;

        // Cached for the duration of a startup/rebuild session to avoid repeated Unity API calls
        string[] _cachedAllPaths;

        // Build log — records timing/results per step, written to .arcforge/graph_build.log
        GraphBuildLog _buildLog;

        // Timeout for the Node.js scanner subprocess.
        // Project-tier: 5 minutes — Assets/ is bounded to the user's project.
        // Package-tier: 10 minutes — Library/PackageCache can be very large on first
        //   run (hundreds of packages × dozens of .cs files each). Using the same
        //   short timeout was the root cause of the "package nodes halved" regression:
        //   on timeout the old code deleted the tier upfront and only partially
        //   repopulated it. See ScanPackages for the non-destructive fix.
        const int ProjectNodeScannerTimeoutMs = 300_000;  // 5 minutes
        const int PackageNodeScannerTimeoutMs  = 600_000;  // 10 minutes — PackageCache is large

        public GraphBuilder(GraphDatabase db)
        {
            _db = db;
            _scannerRegistry = new ScannerRegistry();
        }

        public BuildStatus GetStatus() => _status;

        public void EnsureProjectNode()
        {
            var existing = _db.FindNodesByType("Project");
            if (existing.Count > 0) return;

            var projectName = Path.GetFileName(Path.GetDirectoryName(Application.dataPath));
            _db.InsertNode(new NodeRecord("Project")
            {
                Name = projectName,
                Path = Application.dataPath
            });
        }

        // -------------------------------------------------------------------
        // Full rebuild (synchronous) — kept for tests and menu items
        // -------------------------------------------------------------------

        public void RebuildAll()
        {
            _status = BuildStatus.Rebuilding;
            ScanResolver.Clear();
            var allPaths = GetScannablePaths("Assets/");

            _db.SetCurrentOperation("rebuild");

            using (var span = CharonEmitter.StartSpan("graph.build.full_rebuild", SpanKind.Internal))
            {
                span.SetAttribute("assets.total", (long)allPaths.Length);

                try
                {
                    SeedBuiltinTypes();

                    _db.RunInTransaction(() =>
                    {
                        _db.Execute("DELETE FROM pending_edges;");
                        // Deleting project nodes cascades to remove all edges involving them
                        _db.DeleteNodesByTier("project");
                        _db.Execute(@"DELETE FROM scanned_assets WHERE guid NOT IN (
                            SELECT DISTINCT guid FROM nodes WHERE guid IS NOT NULL);");

                        EnsureProjectNode();
                        _sessionNodeMap = BuildSessionMapFromExistingNodes();

                        int total = allPaths.Length;
                        int processed = 0;

                        foreach (var path in allPaths)
                        {
                            processed++;
                            if (processed % 20 == 0)
                                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                                    $"Scanning project assets ({processed}/{total})…",
                                    (float)processed / total);

                            ScanAsset(path, "project");
                        }

                        ResolvePendingEdges();
                    });

                    span.SetAttribute("nodes.count", _db.GetNodeCount());
                    span.SetAttribute("edges.count", _db.GetEdgeCount());
                }
                catch (Exception ex)
                {
                    span.SetStatus(SpanStatus.Error);
                    span.SetAttribute("error.message", ex.Message);
                    throw;
                }
                finally
                {
                    _sessionNodeMap = null;
                    EditorUtility.ClearProgressBar();
                    _db.ClearCurrentOperation();
                    _db.SetMetadata("last_full_rebuild_at",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                    _status = BuildStatus.Idle;
                    OnRebuildComplete?.Invoke();
                }
            }
        }

        // -------------------------------------------------------------------
        // Chunked project rebuild (non-blocking, processes over multiple frames)
        // -------------------------------------------------------------------

        /// <summary>
        /// Chunked project rebuild with progress bar.
        /// Preserves package-tier nodes. Blocks the main thread.
        /// </summary>
        public void RebuildAllChunked(int assetsPerBatch = 50)
        {
            if (_status == BuildStatus.Rebuilding) return;

            _status = BuildStatus.Rebuilding;
            ScanResolver.Clear();
            _db.SetCurrentOperation("rebuild");

            try
            {
                _buildLog?.BeginStep("Chunked rebuild");
                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                    "Loading project asset list from Unity…", 0f);

                var allPaths = GetScannablePaths("Assets/");
                int total = allPaths.Length;

                _db.RunInTransaction(() =>
                {
                    _db.Execute("DELETE FROM pending_edges;");
                    _db.DeleteNodesByTier("project");
                    _db.Execute(@"DELETE FROM scanned_assets WHERE guid NOT IN (
                        SELECT DISTINCT guid FROM nodes WHERE guid IS NOT NULL);");
                });

                EnsureProjectNode();
                SeedBuiltinTypes();
                _sessionNodeMap = BuildSessionMapFromExistingNodes();

                Debug.Log($"[Hades] Rebuild started: {total} scannable assets (package nodes preserved)");
                _buildLog?.Detail("Total scannable assets", total);

                for (int index = 0; index < total;)
                {
                    var batchEnd = Math.Min(index + assetsPerBatch, total);

                    _db.RunInTransaction(() =>
                    {
                        for (; index < batchEnd; index++)
                        {
                            ScanAsset(allPaths[index], "project");
                        }
                    });

                    EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                        $"Scanning project assets ({index}/{total})…",
                        0.05f + 0.85f * ((float)index / total));
                }

                var pendingCount = _db.GetPendingEdges().Count;
                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                    $"Resolving {pendingCount} cross-file type edges…", 0.95f);

                _db.RunInTransaction(() => ResolvePendingEdges());

                _buildLog?.EndStep();

                _db.SetMetadata("last_full_rebuild_at",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

                Debug.Log($"[Hades] Rebuild complete: {_db.GetNodeCount()} nodes, {_db.GetEdgeCount()} edges");
                OnRebuildComplete?.Invoke();
            }
            catch (Exception ex)
            {
                _buildLog?.Detail("ERROR", ex.Message);
                Debug.LogError($"[Hades] Rebuild failed: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _sessionNodeMap = null;
                EditorUtility.ClearProgressBar();
                _db.ClearCurrentOperation();
                _status = BuildStatus.Idle;
            }
        }

        // -------------------------------------------------------------------
        // Parallel rebuild — Node.js for .cs files, main thread for Unity-API scanners
        // -------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the graph: Node.js handles .cs files, main thread handles scenes/prefabs.
        /// Blocks the main thread with a progress bar.
        /// </summary>
        public void RebuildParallel(int assetsPerBatch = 50)
        {
            if (_status == BuildStatus.Rebuilding) return;

            _status = BuildStatus.Rebuilding;
            ScanResolver.Clear();
            _db.SetCurrentOperation("rebuild_parallel");

            try
            {
                // --- Prepare ---
                _buildLog?.BeginStep("Prepare project rebuild");
                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                    "Clearing stale project data…", 0f);

                _db.RunInTransaction(() =>
                {
                    _db.Execute("DELETE FROM pending_edges;");
                    _db.DeleteNodesByTier("project");
                    _db.Execute(@"DELETE FROM scanned_assets WHERE guid NOT IN (
                        SELECT DISTINCT guid FROM nodes WHERE guid IS NOT NULL);");
                });

                EnsureProjectNode();
                SeedBuiltinTypes();
                _buildLog?.EndStep();

                // --- Phase A: Node.js script scan ---
                _buildLog?.BeginStep("Node.js project script scan");
                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                    "Scanning project scripts via Node.js…", 0.1f);

                var assetsDir = Application.dataPath;
                var nodeResult = RunNodeScanner("full", assetsDir);

                if (nodeResult.Success)
                {
                    _buildLog?.Detail("Result", "Success");
                }
                else
                {
                    _buildLog?.Detail("Result", $"Failed: exit {nodeResult.ExitCode}");
                    if (nodeResult.ExitCode == 100)
                        _buildLog?.ReportDegraded("C# nodes missing — Node.js not found");
                    else if (nodeResult.ExitCode == 101)
                        _buildLog?.ReportDegraded("C# nodes missing — Scanner npm install failed");
                    else
                        _buildLog?.ReportDegraded($"C# nodes missing — Scanner failed (exit {nodeResult.ExitCode})");
                    Debug.LogWarning("[Hades] Script scan failed, continuing with other scanners");
                }

                // Persist the C# scan outcome so MCP tools can distinguish "no
                // references" from "C# scanning unavailable" and avoid returning a
                // confident, wrong 0 on .cs queries when the scanner failed.
                _db.SetMetadata("csharp_scan_status", nodeResult.Success ? "ok" : "degraded");

                // Cross-process WAL safety: the Node scanner wrote project-script
                // frames to the WAL from a separate process. Integrate them into the
                // main DB through this connection now — before Phase C writes or the
                // final TRUNCATE checkpoint — otherwise that TRUNCATE can discard the
                // not-yet-merged frames and drop every project script on a full rebuild.
                if (nodeResult.Success)
                    _db.CheckpointPassive();

                _buildLog?.EndStep();

                // Rebuild session map from DB (includes what Node.js wrote)
                _sessionNodeMap = BuildSessionMapFromExistingNodes();

                // --- Phase C: Main-thread assets (scenes, prefabs, etc.) ---
                // Honest default: assume the asset scan is incomplete until it finishes
                // without throwing. A thrown Phase-C scan then leaves an accurate "degraded".
                _db.SetMetadata("meta_scan_status", "degraded");
                var projectAssets = DiscoverProjectAssets();
                int totalOther = projectAssets.OtherPaths.Length;

                if (totalOther > 0)
                {
                    _buildLog?.BeginStep("Main-thread asset scan (scenes, prefabs, etc.)");

                    _db.RunInTransaction(() =>
                    {
                        for (int i = 0; i < projectAssets.OtherPaths.Length; i++)
                        {
                            if (i % 10 == 0)
                            {
                                float progress = 0.5f + 0.3f * ((float)i / totalOther);
                                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                                    $"Scanning project assets ({i}/{totalOther} — scenes, prefabs)…",
                                    progress);
                            }

                            ScanAsset(projectAssets.OtherPaths[i], "project");
                        }
                    });

                    _buildLog?.Detail("Assets scanned", totalOther);
                    _buildLog?.EndStep();
                }

                // Project settings: a scanner the registry-driven Assets/-only scan never reaches.
                // Run in its own transaction so a failure here cannot roll back the main asset scan.
                _buildLog?.BeginStep("Project settings scan");
                _db.RunInTransaction(() =>
                {
                    ScanProjectSettings();
                    ScanAddressables();
                });
                _buildLog?.EndStep();

                _db.SetMetadata("meta_scan_status", "ok");

                // --- Phase D: Edge resolution ---
                _buildLog?.BeginStep("Edge resolution");

                var pendingCount = _db.GetPendingEdges().Count;
                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                    $"Resolving {pendingCount} cross-file type edges…", 0.9f);

                _db.RunInTransaction(() => ResolvePendingEdges());
                _buildLog?.Detail("Pending edges input", pendingCount);
                _buildLog?.EndStep();

                _db.SetMetadata("last_full_rebuild_at",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

                if (_buildLog != null && _buildLog.IsDegraded)
                    Debug.LogWarning($"[Hades] Rebuild complete (degraded): {_db.GetNodeCount()} nodes, {_db.GetEdgeCount()} edges — {string.Join("; ", _buildLog.Degradations)}");
                else
                    Debug.Log($"[Hades] Rebuild complete: {_db.GetNodeCount()} nodes, {_db.GetEdgeCount()} edges");
                OnRebuildComplete?.Invoke();
            }
            catch (Exception ex)
            {
                _buildLog?.Detail("ERROR", ex.Message);
                Debug.LogError($"[Hades] Rebuild failed: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _sessionNodeMap = null;

                // Clear the operation marker (a small write), then force the WAL
                // checkpoint while the progress bar is still visible. A full rebuild
                // leaves a huge write-ahead log; if we don't checkpoint it now,
                // SQLite defers it to the next write — which then blocks the editor
                // for minutes with no progress bar (the field report's "mystery
                // freeze"). Doing it explicitly here keeps the cost observable.
                _db.ClearCurrentOperation();
                EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                    "Finalizing (checkpointing database)…", 0.99f);
                _db.Checkpoint();

                EditorUtility.ClearProgressBar();
                _status = BuildStatus.Idle;
            }
        }

        // -------------------------------------------------------------------
        // Package scanning — scan once, cache until versions change
        // -------------------------------------------------------------------

        /// <summary>
        /// Scans package .cs files and stores them as tier="package" nodes.
        /// Only rescans if package versions have changed since last scan.
        /// </summary>
        public void ScanPackagesIfNeeded()
        {
            if (!IsPackageScanNeeded()) return;

            ScanPackages();
        }

        /// <summary>
        /// Scans package .cs files via Node.js scanner. Blocks the main thread with a progress bar.
        /// Optionally chains a follow-up action when complete.
        ///
        /// Non-destructive on failure: existing package-tier nodes are PRESERVED when the scanner
        /// fails or times out. The upfront DeleteNodesByTier("package") has been removed — it was
        /// the root cause of the "package nodes halved" regression (nodes deleted, scanner timed
        /// out on large PackageCache, only partial repopulation). The scanner does per-GUID
        /// delete+reinsert for each file it processes, so in-place updates are always safe.
        /// Stale removal (nodes for packages no longer on disk) is deferred to AFTER a confirmed
        /// successful scan, never before.
        /// </summary>
        public void ScanPackages(Action onComplete = null)
        {
            if (_status != BuildStatus.Idle && _status != BuildStatus.ScanningPackages) return;

            _status = BuildStatus.ScanningPackages;
            _db.SetCurrentOperation("package_scan");

            // Honest default: assume degraded until confirmed success. If an unhandled
            // exception escapes the try block, the finally still writes this value.
            _db.SetMetadata("package_scan_status", "degraded");

            try
            {
                _buildLog?.BeginStep("Node.js package script scan");
                EditorUtility.DisplayProgressBar("Hades: Scanning Packages",
                    "Scanning package scripts via Node.js…", 0.1f);

                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var cacheDir = Path.Combine(projectRoot, "Library", "PackageCache");
                var packagesDir = Path.Combine(projectRoot, "Packages");
                var scanDirs = new[] { cacheDir, packagesDir }.Where(Directory.Exists).ToArray();
                var dirs = string.Join(",", scanDirs);

                if (string.IsNullOrEmpty(dirs))
                {
                    Debug.Log("[Hades] No package directories found, skipping scan");
                    _buildLog?.Detail("Result", "No package directories");
                    // No package dirs = nothing to scan; treat as OK so we don't report degraded
                    // for a project that simply has no PackageCache/Packages directories.
                    _db.SetMetadata("package_scan_status", "ok");
                    _buildLog?.EndStep();
                    return;
                }

                _sessionNodeMap = _sessionNodeMap ?? BuildSessionMapFromExistingNodes();

                // Mark this packages-lock state as "attempted" BEFORE running the scanner.
                // If the scan fails or times out, IsPackageScanNeeded() must NOT re-trigger an
                // identical scan on the next lock touch / domain reload — otherwise a project
                // whose package scan can't complete spins in a re-scan loop (freezing the editor).
                // A genuine package change (new lock hash) clears this gate and re-attempts.
                var attemptHash = ComputePackageLockHash();
                if (attemptHash != null)
                    _db.SetMetadata("package_scan_attempted_hash", attemptHash);

                // DO NOT DeleteNodesByTier("package") here. Run the scanner first.
                // On failure/timeout: leave existing package nodes intact — stale data is
                // better than no data, and MCP tools will see package_scan_status=degraded.
                var result = RunNodeScanner("full", dirs, "--tier package",
                    PackageNodeScannerTimeoutMs);

                if (result.Success)
                {
                    // Checkpoint: integrate scanner's WAL writes before any C# reads/writes.
                    _db.CheckpointPassive();

                    // NOTE: stale-package-node reconciliation (RemoveStalePackageNodes) is
                    // DISABLED. It walked the entire PackageCache on the main thread
                    // (Directory.GetFiles over all .cs + reading every .meta), which froze the
                    // editor on large projects. Uninstalled-package nodes now linger until the
                    // next full rebuild — a benign staleness. Re-introduce reconciliation only
                    // OFF the main thread (e.g. from the scanner's own scanned-GUID set).

                    var packageHash = ComputePackageLockHash();
                    if (packageHash != null)
                        _db.SetMetadata("packages_lock_hash", packageHash);

                    _db.SetMetadata("last_package_scan_at",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                    _db.SetMetadata("package_scan_status", "ok");

                    _sessionNodeMap = BuildSessionMapFromExistingNodes();

                    var typeCount = _db.GetNodeCount("ScriptType", "package");
                    Debug.Log($"[Hades] Package scan complete: {typeCount} package types indexed");
                    _buildLog?.Detail("Types indexed", typeCount);
                    _buildLog?.Detail("Result", "Success");
                }
                else
                {
                    // Scanner failed or timed out. Existing package nodes are preserved.
                    // package_scan_status remains "degraded" (set above).
                    if (result.ExitCode == 100)
                        _buildLog?.ReportDegraded("Package C# nodes missing — Node.js not found");
                    else if (result.ExitCode == 101)
                        _buildLog?.ReportDegraded("Package C# nodes missing — Scanner npm install failed");
                    else
                        _buildLog?.ReportDegraded($"Package C# nodes missing — Scanner failed (exit {result.ExitCode})");
                    Debug.LogWarning("[Hades] Package scan failed; existing package nodes preserved (degraded mode)");
                    _buildLog?.Detail("Result", $"Failed: exit {result.ExitCode}");
                }

                _buildLog?.EndStep();
            }
            catch (Exception ex)
            {
                // package_scan_status = "degraded" was already written before the try block.
                _buildLog?.Detail("ERROR", $"{ex.Message}");
                Debug.LogError($"[Hades] Package scan failed: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // Checkpoint the WAL while the bar is still up (see RebuildParallel).
                _db.ClearCurrentOperation();
                EditorUtility.DisplayProgressBar("Hades: Scanning Packages",
                    "Finalizing (checkpointing database)…", 0.99f);
                _db.Checkpoint();

                EditorUtility.ClearProgressBar();
                _status = BuildStatus.Idle;
            }

            onComplete?.Invoke();
        }

        /// <summary>
        /// After a confirmed successful package scan, removes package-tier nodes whose backing
        /// .cs file no longer exists on disk (e.g. a package was uninstalled).
        ///
        /// Safety: we enumerate the exact same directories that were just scanned and collect
        /// every .cs GUID that has a live .meta file — these are ALL live package GUIDs. Any
        /// package node whose GUID is not in this set has no backing file and is safe to delete.
        /// This runs ONLY after a successful scan, never on failure or timeout.
        ///
        /// If the filesystem walk fails, we log a warning and skip cleanup rather than risk
        /// deleting anything — correctness is preserved, stale nodes stay until the next scan.
        /// </summary>
        void RemoveStalePackageNodes(string[] packageDirs)
        {
            try
            {
                // Collect every live .cs GUID from the package directories.
                var liveGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in packageDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var csFile in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                    {
                        var metaPath = csFile + ".meta";
                        if (!File.Exists(metaPath)) continue;
                        var guid = ExtractGuidFromMeta(metaPath);
                        if (guid != null) liveGuids.Add(guid);
                    }
                }

                if (liveGuids.Count == 0) return; // nothing to do (or dirs empty)

                // Get all GUIDs currently stored as package-tier nodes.
                var storedGuids = _db.GetDistinctGuidsForTier("package");
                var stale = new List<string>();
                foreach (var guid in storedGuids)
                {
                    if (!liveGuids.Contains(guid))
                        stale.Add(guid);
                }

                if (stale.Count == 0) return;

                _db.RunInTransaction(() =>
                {
                    foreach (var guid in stale)
                    {
                        _db.DeleteNodesByGuid(guid);
                        _db.DeletePendingEdgesBySourceAsset(guid);
                    }
                });

                Debug.Log($"[Hades] Package scan: removed {stale.Count} stale node GUIDs (packages no longer on disk)");
                _buildLog?.Detail("Stale package GUIDs removed", stale.Count);
            }
            catch (Exception ex)
            {
                // Non-fatal: stale nodes stay until the next scan. Do not delete anything.
                Debug.LogWarning($"[Hades] Could not reconcile stale package nodes (skipped): {ex.Message}");
                _buildLog?.Detail("Stale removal skipped (error)", ex.Message);
            }
        }

        /// <summary>
        /// Extracts the GUID from a Unity .meta file.
        /// Reads up to the first 4 KB — the guid: line is always near the top.
        /// Returns null if the file cannot be read or has no guid line.
        /// </summary>
        internal static string ExtractGuidFromMeta(string metaPath)
        {
            try
            {
                // Read just the first 4 KB — guid: is always in the first few lines.
                using (var reader = new StreamReader(metaPath))
                {
                    var buf = new char[4096];
                    int read = reader.Read(buf, 0, buf.Length);
                    var content = new string(buf, 0, read);

                    // Line-anchored "guid: <32 hex>" — mirrors the Node scanner's
                    // /^guid:\s*([0-9a-f]{32})/m so the two never disagree on which node
                    // is "live" (a mismatch could falsely flag a live package node as stale).
                    var match = System.Text.RegularExpressions.Regex.Match(
                        content, @"^guid:\s*([0-9a-fA-F]{32})",
                        System.Text.RegularExpressions.RegexOptions.Multiline);
                    if (!match.Success) return null;
                    return match.Groups[1].Value.ToLowerInvariant();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks if packages need rescanning by comparing packages-lock.json hash.
        /// </summary>
        // Unity asset GUIDs are 32 hex chars. Used to skip AssetDatabase lookups for
        // pending-edge targets that are type names (code/supertype refs), not GUIDs.
        static bool LooksLikeGuid(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 32) return false;
            foreach (var c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        bool IsPackageScanNeeded()
        {
            var currentHash = ComputePackageLockHash();

            // Don't re-attempt a scan we already tried for this exact packages-lock state
            // (success OR failure). Prevents an infinite re-scan loop when the scanner can't
            // complete — Unity re-touches packages-lock.json during package resolution, which
            // would otherwise re-trigger the same failing scan and freeze the editor. A genuine
            // package change (new lock hash) clears this gate and re-attempts.
            if (currentHash != null &&
                _db.GetMetadata("package_scan_attempted_hash") == currentHash)
                return false;

            // If no package nodes exist at all, we need a scan
            if (_db.GetNodeCount("ScriptType", "package") == 0) return true;

            if (currentHash == null) return false; // No lock file, can't determine staleness

            var storedHash = _db.GetMetadata("packages_lock_hash");
            return storedHash != currentHash;
        }

        string ComputePackageLockHash()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var lockFile = Path.Combine(projectRoot, "Packages", "packages-lock.json");
            if (!File.Exists(lockFile)) return null;
            return ComputeContentHash(lockFile);
        }

        // -------------------------------------------------------------------
        // Incremental update
        // -------------------------------------------------------------------

        public void UpdateAssets(string[] guids)
        {
            if (guids == null || guids.Length == 0) return;

            _status = BuildStatus.Updating;
            _db.SetCurrentOperation("update", guids);

            using (var span = CharonEmitter.StartSpan("graph.build.incremental", SpanKind.Internal))
            {
                span.SetAttribute("assets.count", (long)guids.Length);

                try
                {
                    var csGuids = new List<string>();
                    var otherGuids = new List<string>();

                    foreach (var guid in guids)
                    {
                        var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(assetPath))
                        {
                            _db.DeleteNodesByGuid(guid);
                            _db.DeletePendingEdgesBySourceAsset(guid);
                            continue;
                        }

                        if (!File.Exists(assetPath) && !Directory.Exists(assetPath))
                        {
                            _db.DeleteNodesByGuid(guid);
                            _db.DeletePendingEdgesBySourceAsset(guid);
                            continue;
                        }

                        if (assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            csGuids.Add(guid);
                        else
                            otherGuids.Add(guid);
                    }

                    if (csGuids.Count > 0)
                    {
                        var assetsDir = Application.dataPath;
                        var guidList = string.Join(",", csGuids);
                        RunNodeScanner("incremental", assetsDir, $"--guids \"{guidList}\"");
                    }

                    // Incrementals stay O(changed): no full session-map rebuild
                    // (WriteScanResult falls back to targeted FindNodeByGuid lookups
                    // when _sessionNodeMap is null) and pending-edge resolution is
                    // scoped to the changed assets instead of the whole table.
                    if (otherGuids.Count > 0)
                    {
                        _db.RunInTransaction(() =>
                        {
                            foreach (var guid in otherGuids)
                            {
                                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                                if (string.IsNullOrEmpty(assetPath)) continue;

                                var currentHash = ComputeContentHash(assetPath);
                                var storedHash = _db.GetScannedAssetHash(guid);

                                if (storedHash == currentHash) continue;

                                _db.DeleteNodesByGuid(guid);
                                _db.DeletePendingEdgesBySourceAsset(guid);

                                var tier = assetPath.StartsWith("Packages/") ? "package" : "project";
                                ScanAsset(assetPath, tier);
                            }

                            ResolvePendingEdges(guids);
                        });
                    }
                    else
                    {
                        _db.RunInTransaction(() => ResolvePendingEdges(guids));
                    }
                }
                catch (Exception ex)
                {
                    span.SetStatus(SpanStatus.Error);
                    span.SetAttribute("error.message", ex.Message);
                    throw;
                }
                finally
                {
                    _sessionNodeMap = null;
                    _db.ClearCurrentOperation();
                    _db.SetMetadata("last_incremental_at",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                    _status = BuildStatus.Idle;
                    OnRebuildComplete?.Invoke();
                }
            }
        }

        public void HandleDeletedAssets(string[] deletedPaths)
        {
            foreach (var path in deletedPaths)
            {
                _db.Execute("DELETE FROM nodes WHERE path = ? AND tier = 'project';", path);
            }
        }

        public void HandleMovedAssets(string[] movedFromPaths, string[] movedToPaths)
        {
            for (int i = 0; i < movedFromPaths.Length && i < movedToPaths.Length; i++)
            {
                var guid = AssetDatabase.AssetPathToGUID(movedToPaths[i]);
                var node = _db.FindNodeByGuid(guid);
                if (node != null)
                {
                    _db.UpdateNodePath(node.Id, movedToPaths[i]);
                }
            }
        }

        // -------------------------------------------------------------------
        // Path collection
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns scannable asset paths under the given prefix that have a registered scanner.
        /// </summary>
        string[] GetAllAssetPathsCached()
        {
            if (_cachedAllPaths == null)
                _cachedAllPaths = AssetDatabase.GetAllAssetPaths();
            return _cachedAllPaths;
        }

        string[] GetScannablePaths(string pathPrefix)
        {
            var allPaths = GetAllAssetPathsCached();
            var scannable = new List<string>();

            foreach (var path in allPaths)
            {
                if (!path.StartsWith(pathPrefix))
                    continue;

                if (_scannerRegistry.GetScannerForPath(path) != null)
                    scannable.Add(path);
            }

            return scannable.ToArray();
        }

        // -------------------------------------------------------------------
        // Project asset discovery (pure filesystem, no AssetDatabase)
        // -------------------------------------------------------------------

        struct ProjectDiscoveryResult
        {
            public string[] ScriptPaths; // Asset-relative .cs paths (Assets/Scripts/Foo.cs)
            public string[] OtherPaths;  // Asset-relative paths for other scannable extensions
        }

        /// <summary>
        /// Discovers all scannable files under Assets/ using pure filesystem operations.
        /// No Unity API calls — safe to call during startup without triggering asset refresh.
        /// Returns asset-relative paths partitioned into scripts (.cs) and other scannable types.
        /// </summary>
        ProjectDiscoveryResult DiscoverProjectAssets()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var assetsDir = Path.Combine(projectRoot, "Assets");

            if (!Directory.Exists(assetsDir))
            {
                return new ProjectDiscoveryResult
                {
                    ScriptPaths = Array.Empty<string>(),
                    OtherPaths = Array.Empty<string>()
                };
            }

            // Collect the set of scannable extensions from all registered scanners
            var scannableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scanner in _scannerRegistry.GetAll())
            {
                foreach (var ext in scanner.SupportedExtensions)
                    scannableExtensions.Add(ext.ToLowerInvariant());
            }

            var scriptPaths = new List<string>();
            var otherPaths = new List<string>();

            // Prefix to strip: projectRoot + separator, so we get "Assets/..." relative paths
            var prefixLength = projectRoot.Length + 1;

            var allFiles = Directory.GetFiles(assetsDir, "*.*", SearchOption.AllDirectories);

            foreach (var fullPath in allFiles)
            {
                var ext = Path.GetExtension(fullPath);
                if (string.IsNullOrEmpty(ext)) continue;
                if (!scannableExtensions.Contains(ext.ToLowerInvariant())) continue;

                // Convert to asset-relative path with forward slashes
                var assetPath = fullPath.Substring(prefixLength).Replace('\\', '/');

                if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    scriptPaths.Add(assetPath);
                else
                    otherPaths.Add(assetPath);
            }

            return new ProjectDiscoveryResult
            {
                ScriptPaths = scriptPaths.ToArray(),
                OtherPaths = otherPaths.ToArray()
            };
        }

        // -------------------------------------------------------------------
        // Scanning core
        // -------------------------------------------------------------------

        // Project settings live outside Assets/ (and aren't in AssetDatabase.GetAllAssetPaths()),
        // and ProjectSettingsScanner's .asset registration collides with ScriptableObjectScanner
        // in the extension map — so the registry-driven Phase C scan never reaches them. Invoke
        // the scanner directly on the known ProjectSettings files. These are not AssetDatabase
        // assets, so route results straight through WriteScanResult rather than ScanAsset (which
        // keys on AssetPathToGUID + RecordScannedAsset).
        void ScanProjectSettings()
        {
            var scanner = new ProjectSettingsScanner();
            string[] settingsPaths =
            {
                "ProjectSettings/EditorBuildSettings.asset",
                "ProjectSettings/GraphicsSettings.asset",
                "ProjectSettings/DynamicsManager.asset",
                "ProjectSettings/InputManager.asset",
            };

            foreach (var path in settingsPaths)
            {
                try
                {
                    WriteScanResult(scanner.Scan(path), "", "project");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Hades] Settings scan failed for {path}: {ex.Message}");
                }
            }
        }

        // The AddressablesScanner declares no extensions, so the registry never selects it.
        // Locate the settings asset directly and invoke it. Sets addressables_scan_status so
        // get_project_summary can report Addressables completeness honestly.
        void ScanAddressables()
        {
            var guids = AssetDatabase.FindAssets("t:AddressableAssetSettings");
            if (guids == null || guids.Length == 0)
            {
                _db.SetMetadata("addressables_scan_status", "not_installed");
                return;
            }

            try
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var scanner = new AddressablesScanner();
                var scanResult = scanner.Scan(path);

                WriteScanResult(scanResult, AssetDatabase.AssetPathToGUID(path), "project");

                bool packageAbsent = scanResult.Warnings.Exists(w =>
                    w.Message != null && w.Message.Contains("not installed"));
                _db.SetMetadata("addressables_scan_status", packageAbsent ? "not_installed" : "ok");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Addressables scan failed: {ex.Message}");
                _db.SetMetadata("addressables_scan_status", "degraded");
            }
        }

        void ScanAsset(string assetPath, string tier)
        {
            var scanner = _scannerRegistry.GetScannerForPath(assetPath);
            if (scanner == null) return;

            using (var span = CharonEmitter.StartSpan($"graph.scan.{scanner.GetType().Name}", SpanKind.Internal))
            {
                span.SetAttribute("asset.path", assetPath);
                span.SetAttribute("scanner.type", scanner.GetType().Name);
                span.SetAttribute("tier", tier);

                try
                {
                    var scanResult = scanner.Scan(assetPath);
                    var guid = AssetDatabase.AssetPathToGUID(assetPath);

                    WriteScanResult(scanResult, guid, tier);

                    var hash = ComputeContentHash(assetPath);
                    _db.RecordScannedAsset(guid, hash, scanner.Version);

                    span.SetAttribute("nodes.produced", (long)scanResult.Nodes.Count);
                    span.SetAttribute("edges.produced", (long)scanResult.Edges.Count);

                    foreach (var warning in scanResult.Warnings)
                    {
                        if (warning.Severity >= WarningSeverity.Warning)
                        {
                            span.AddEvent("scan.warning", new Dictionary<string, string>
                            {
                                { "message", warning.Message },
                                { "asset_path", warning.AssetPath }
                            });
                            Debug.LogWarning($"[Hades] {warning.Message} ({warning.AssetPath})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    span.SetStatus(SpanStatus.Error);
                    span.SetAttribute("error.message", ex.Message);
                    Debug.LogError($"[Hades] Scanner error on {assetPath}: {ex.Message}");
                }
            }
        }

        // -------------------------------------------------------------------
        // Write scan results with session-level node map
        // -------------------------------------------------------------------

        void WriteScanResult(ScanResult scanResult, string assetGuid, string tier)
        {
            // Local map for nodes created in THIS scan result
            var localNodeMap = new Dictionary<string, long>();

            foreach (var node in scanResult.Nodes)
            {
                var id = _db.InsertNode(node, tier);
                var key = $"{node.Guid ?? ""}:{node.FileId ?? 0}";
                localNodeMap[key] = id;

                // Also register in the session-wide map for cross-file resolution
                if (_sessionNodeMap != null)
                    _sessionNodeMap[key] = id;
            }

            foreach (var edge in scanResult.Edges)
            {
                var sourceKey = $"{edge.SourceGuid ?? ""}:{edge.SourceFileId}";
                var targetKey = $"{edge.TargetGuid ?? ""}:{edge.TargetFileId}";

                long sourceId, targetId;

                // Resolve source: local map → session map → (incremental only) targeted DB lookup.
                // During a FULL rebuild the session map holds ALL existing nodes and is kept
                // current, so a miss means a DB lookup would return null anyway — skip it.
                if (!localNodeMap.TryGetValue(sourceKey, out sourceId)
                    && (_sessionNodeMap == null || !_sessionNodeMap.TryGetValue(sourceKey, out sourceId)))
                {
                    if (_sessionNodeMap != null)
                        continue; // full rebuild: source not present (DB would be null too) — skip edge

                    var sourceNode = _db.FindNodeByGuid(edge.SourceGuid, edge.SourceFileId);
                    if (sourceNode == null) sourceNode = _db.FindNodeByGuid(edge.SourceGuid);
                    if (sourceNode == null) continue;
                    sourceId = sourceNode.Id;
                }

                // Resolve target. The per-edge FindNodeBy* lookups below were the rebuild hot loop
                // (millions of SELECT * pinning the main thread for tens of minutes at 700K nodes).
                // During a FULL rebuild we defer any map miss to the batched ResolvePendingEdges
                // pass instead of doing a per-edge query; incremental updates (no session map, few
                // edges) keep the cheap targeted DB lookup.
                if (edge.TargetGuid != null && edge.TargetGuid.StartsWith("__pending__"))
                {
                    // Name-based edge — resolves against ScriptType nodes by name.
                    var targetTypeName = edge.TargetGuid.Substring("__pending__".Length);
                    string targetNamespace = null;
                    if (edge.Properties != null && edge.Properties.TryGetValue("target_type_name", out var tn))
                        targetTypeName = tn.ToString();

                    if (_sessionNodeMap != null)
                    {
                        _db.InsertPendingEdge(sourceId, edge.Type, targetTypeName, targetNamespace, assetGuid);
                        continue;
                    }

                    var resolved = _db.FindNodeByNameAndType(targetTypeName, "ScriptType");
                    if (resolved != null)
                    {
                        targetId = resolved.Id;
                    }
                    else
                    {
                        _db.InsertPendingEdge(sourceId, edge.Type, targetTypeName, targetNamespace, assetGuid);
                        continue;
                    }
                }
                else if (!localNodeMap.TryGetValue(targetKey, out targetId)
                         && (_sessionNodeMap == null || !_sessionNodeMap.TryGetValue(targetKey, out targetId)))
                {
                    if (_sessionNodeMap != null)
                    {
                        // Full rebuild: target not scanned yet → defer to batched resolution.
                        _db.InsertPendingEdge(sourceId, edge.Type, edge.TargetGuid ?? "", null, assetGuid);
                        continue;
                    }

                    var targetNode = _db.FindNodeByGuid(edge.TargetGuid, edge.TargetFileId);
                    if (targetNode == null) targetNode = _db.FindNodeByGuid(edge.TargetGuid);
                    if (targetNode == null)
                    {
                        _db.InsertPendingEdge(sourceId, edge.Type, edge.TargetGuid ?? "", null, assetGuid);
                        continue;
                    }
                    targetId = targetNode.Id;
                }

                _db.InsertEdge(sourceId, targetId, edge.Type, edge.PropertiesJson);
            }
        }

        // -------------------------------------------------------------------
        // Edge resolution
        // -------------------------------------------------------------------

        /// <summary>
        /// Resolves pending edges by matching target GUIDs/names against existing nodes.
        /// Called after scanning completes.
        /// </summary>
        /// <param name="scopeGuids">
        /// When non-null, only pending edges originating from these source asset GUIDs
        /// are considered — keeping incremental updates O(changed) instead of scanning
        /// the entire pending_edges table. A full rebuild passes null (resolve all).
        /// Pre-existing pending edges from other assets that a newly-scanned node would
        /// now satisfy are intentionally left for the next rebuild (they persist, not lost).
        /// </param>
        void ResolvePendingEdges(IReadOnlyCollection<string> scopeGuids = null)
        {
            var pending = scopeGuids != null
                ? _db.GetPendingEdgesBySourceAssets(scopeGuids)
                : _db.GetPendingEdges();
            if (pending.Count == 0) return;

            var coveredExtensions = _scannerRegistry.GetCoveredExtensions();

            int resolved = 0;
            int permanent = 0;
            int transient = 0;
            int external = 0;
            var toDelete = new HashSet<long>();

            // Bulk-load the resolution lookups ONCE (one indexed scan each) instead of two
            // SQLite round-trips per pending edge. On large graphs (700K+ nodes) the per-edge
            // lookups were O(P) main-thread queries that pinned the editor in sqlite3_step for
            // tens of minutes; resolving against in-memory maps is O(P) with no per-edge I/O.
            var guidToNodeId = _db.LoadGuidToNodeIdMap();
            var scriptTypeByName = _db.LoadScriptTypeByNameMap();

            foreach (var pe in pending)
            {
                if (string.IsNullOrEmpty(pe.TargetTypeName)) continue;

                long targetId = 0;
                string targetKind = null;
                bool found = false;

                // 1) GUID match first (asset references resolve here) — mirrors FindNodeByGuid.
                if (guidToNodeId.TryGetValue(pe.TargetTypeName, out var gId))
                {
                    targetId = gId;
                    found = true;
                }

                // 2) Else, for code/supertype edges, match a ScriptType by name —
                //    mirrors FindNodeByNameAndType(name, "ScriptType").
                if (!found
                    && (pe.EdgeType == "inherits_from" || pe.EdgeType == "implements"
                        || pe.EdgeType == "extends_or_implements" || pe.EdgeType == "code_references")
                    && scriptTypeByName.TryGetValue(pe.TargetTypeName, out var st))
                {
                    targetId = st.Id;
                    targetKind = st.Kind;
                    found = true;
                }

                if (found)
                {
                    string propertiesJson = null;
                    if (pe.EdgeType == "code_references" && !string.IsNullOrEmpty(pe.TargetNamespace))
                        propertiesJson = $"{{\"reference_kind\":\"{pe.TargetNamespace}\"}}";

                    // Reclassify the neutral supertype edge now that we know the target's kind:
                    // interface target → 'implements', otherwise → 'inherits_from'. (Base-list
                    // syntax doesn't encode class-vs-interface, so we couldn't tell at scan time.)
                    string resolvedEdgeType = pe.EdgeType == "extends_or_implements"
                        ? (targetKind == "interface" ? "implements" : "inherits_from")
                        : pe.EdgeType;

                    _db.InsertEdge(pe.SourceNodeId, targetId, resolvedEdgeType, propertiesJson);
                    toDelete.Add(pe.Id);
                    resolved++;
                }
            }

            foreach (var id in toDelete)
            {
                _db.DeletePendingEdge(id);
            }

            // Classify remaining unresolved edges
            var remaining = pending.Count - resolved;
            if (remaining > 0)
            {
                foreach (var pe in pending)
                {
                    if (toDelete.Contains(pe.Id)) continue;

                    // Try to resolve the GUID to an asset path to check its extension.
                    // Only asset references carry a GUID target; code/supertype targets are type
                    // names (GUIDToAssetPath would always miss). Skipping the main-thread
                    // AssetDatabase call for non-GUID targets avoids millions of calls at scale.
                    var assetPath = LooksLikeGuid(pe.TargetTypeName)
                        ? UnityEditor.AssetDatabase.GUIDToAssetPath(pe.TargetTypeName)
                        : null;
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var ext = Path.GetExtension(assetPath)?.ToLowerInvariant();
                        if (ext != null && !coveredExtensions.Contains(ext))
                            permanent++;
                        else
                            transient++;
                    }
                    else if (IsKnownExternalTarget(pe))
                    {
                        // Target is a BCL/Unity/framework type or an unstripped generic — no
                        // user node will ever satisfy it. Count separately so it does not
                        // depress the "still pending" (will-resolve-next-rebuild) signal.
                        external++;
                    }
                    else
                    {
                        // GUID doesn't resolve to any asset — likely a user type for
                        // inherits_from/implements not yet scanned this pass.
                        transient++;
                    }
                }
            }

            // Build informative log message
            if (resolved > 0 || remaining > 0)
            {
                var parts = new List<string>();
                parts.Add($"{resolved} resolved");
                if (permanent > 0)
                    parts.Add($"{permanent} unresolvable (refs to textures, meshes, audio, etc. — asset types not indexed by Hades)");
                if (external > 0)
                    parts.Add($"{external} external (BCL/Unity/framework types — not user code)");
                if (transient > 0)
                    parts.Add($"{transient} still pending (will resolve on next rebuild)");

                Debug.Log($"[Hades] Pending edges: {string.Join(", ", parts)}");

                _buildLog?.Detail("Edges resolved", resolved);
                if (permanent > 0)
                    _buildLog?.Detail("Edges unresolvable (unscanned types)", permanent);
                if (external > 0)
                    _buildLog?.Detail("Edges external (BCL/framework)", external);
                if (transient > 0)
                    _buildLog?.Detail("Edges still pending", transient);
            }

            // On a full pass (scope == null) the classification above is global, so persist
            // the never-resolvable tallies. get_project_summary reads these to keep its
            // coverage metric honest — framework/asset-type references that can never resolve
            // must not be counted as "missing user-code links". Incrementals leave them as-is;
            // they reconverge on the next full rebuild.
            if (scopeGuids == null)
            {
                _db.SetMetadata("pending_edges_external", external.ToString());
                _db.SetMetadata("pending_edges_unresolvable", permanent.ToString());
            }
        }

        // -------------------------------------------------------------------
        // Builtin Unity type seeding via reflection
        // -------------------------------------------------------------------

        void SeedBuiltinTypes()
        {
            var currentVersion = UnityEngine.Application.unityVersion;
            var cachedVersion = _db.GetMetadata("builtin_unity_version");

            if (cachedVersion == currentVersion)
            {
                Debug.Log($"[Hades] Builtin types already seeded for Unity {currentVersion}");
                return;
            }

            Debug.Log($"[Hades] Seeding builtin types for Unity {currentVersion}...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            _db.DeleteNodesByTier("builtin");

            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies()
                .Where(a =>
                {
                    var name = a.GetName().Name;
                    return name.StartsWith("UnityEngine") || name.StartsWith("UnityEditor");
                })
                .ToArray();

            int typeCount = 0;
            int edgeCount = 0;

            _db.RunInTransaction(() =>
            {
                foreach (var assembly in assemblies)
                {
                    var assemblyName = assembly.GetName().Name;
                    System.Type[] types;
                    try { types = assembly.GetExportedTypes(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        if (type.IsGenericTypeDefinition) continue;
                        if (type.IsNested) continue;

                        // Determine kind from reflection metadata so resolution can tell
                        // interfaces apart from classes (used by extends_or_implements reclassification).
                        string typeKind = type.IsInterface ? "interface"
                            : type.IsEnum ? "enum"
                            : type.IsValueType ? "struct"
                            : "class";

                        var node = new NodeRecord("ScriptType")
                        {
                            Name = type.Name,
                            Properties = new Dictionary<string, object>
                            {
                                ["source"] = "builtin",
                                ["namespace"] = type.Namespace ?? "",
                                ["assembly"] = assemblyName,
                                ["kind"] = typeKind
                            }
                        };

                        var nodeId = _db.InsertNode(node, "builtin");
                        typeCount++;

                        if (type.BaseType != null && type.BaseType != typeof(object))
                        {
                            var baseTypeName = StripGenericArity(type.BaseType.Name);
                            _db.InsertPendingEdge(nodeId, "inherits_from", baseTypeName, type.BaseType.Namespace, null);
                            edgeCount++;
                        }

                        var directInterfaces = type.GetInterfaces();
                        if (type.BaseType != null)
                        {
                            var baseInterfaces = type.BaseType.GetInterfaces();
                            directInterfaces = directInterfaces.Except(baseInterfaces).ToArray();
                        }
                        foreach (var iface in directInterfaces)
                        {
                            _db.InsertPendingEdge(nodeId, "implements", StripGenericArity(iface.Name), iface.Namespace, null);
                            edgeCount++;
                        }
                    }
                }

                _db.SetMetadata("builtin_unity_version", currentVersion);
            });

            sw.Stop();
            Debug.Log($"[Hades] Seeded {typeCount} builtin types, {edgeCount} edges in {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// Strips the CLR generic-arity suffix (e.g. "IList`1" → "IList") so builtin-type
        /// pending edges match the unadorned type names scanners record. Without this,
        /// generic base/interface edges never resolve.
        /// </summary>
        static string StripGenericArity(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var tick = name.IndexOf('`');
            return tick >= 0 ? name.Substring(0, tick) : name;
        }

        static readonly string[] ExternalNamespaceRoots =
        {
            "System", "Microsoft", "Mono", "UnityEngine", "UnityEditor",
            "Unity", "TMPro", "JetBrains", "NUnit", "Newtonsoft"
        };

        /// <summary>
        /// Heuristic: does this unresolved pending edge target a BCL/Unity/framework type
        /// (or an unstripped generic) that no user-scanned node will ever satisfy? Such
        /// edges are tallied as "external" rather than "still pending" so coverage metrics
        /// reflect genuinely missing user-code links, not permanent framework references.
        /// </summary>
        internal static bool IsKnownExternalTarget(Models.PendingEdge pe)
        {
            // Unstripped generic-arity names (e.g. "IList`1") can never match a scanned or
            // seeded node, so they are effectively external for coverage purposes.
            if (!string.IsNullOrEmpty(pe.TargetTypeName) && pe.TargetTypeName.IndexOf('`') >= 0)
                return true;

            // For code_references the TargetNamespace field carries the reference_kind, not a
            // namespace — so only inheritance/interface edges get namespace classification.
            // extends_or_implements is the pre-resolution neutral form of inherits_from/implements
            // and also carries a namespace hint, so include it here.
            if (pe.EdgeType != "inherits_from" && pe.EdgeType != "implements"
                && pe.EdgeType != "extends_or_implements")
                return false;

            var ns = pe.TargetNamespace;
            if (string.IsNullOrEmpty(ns)) return false;
            foreach (var root in ExternalNamespaceRoots)
            {
                if (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // -------------------------------------------------------------------
        // Session map helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Pre-populates the session node map from all existing nodes in the DB.
        /// This allows cross-file edge resolution without per-edge DB queries.
        /// </summary>
        Dictionary<string, long> BuildSessionMapFromExistingNodes()
        {
            return _db.BuildNodeGuidMap();
        }

        // -------------------------------------------------------------------
        // Startup sync
        // -------------------------------------------------------------------

        public void CheckStartupSync()
        {
            // Determine trigger reason
            bool firstBoot = _db.GetNodeCount() == 0;
            bool packagesChanged = !firstBoot && IsPackageScanNeeded();
            string trigger = firstBoot ? "first_boot"
                           : packagesChanged ? "packages_changed"
                           : "incremental";

            _buildLog = new GraphBuildLog(trigger);

            try
            {
                if (firstBoot)
                {
                    // Package scan uses pure filesystem (no AssetDatabase needed).
                    // AssetDatabase.GetAllAssetPaths is deferred to RebuildParallel
                    // where it's called with a progress bar already showing.
                    ScanPackages(onComplete: () => RebuildParallel());
                }
                else if (packagesChanged)
                {
                    ScanPackages(onComplete: () => CheckStaleProjectAssets());
                }
                else
                {
                    CheckStaleProjectAssets();
                }
            }
            finally
            {
                _buildLog?.Flush(_db.GetNodeCount(), _db.GetEdgeCount());
                _buildLog = null;
                _cachedAllPaths = null;
            }
        }

        void CheckStaleProjectAssets()
        {
            _buildLog?.BeginStep("Check stale project assets");

            EditorUtility.DisplayProgressBar("Hades: Checking Project",
                "Loading project asset list from Unity…", 0f);

            var allPaths = GetAllAssetPathsCached();
            var staleGuids = new List<string>();
            int checked_ = 0;
            int projectAssets = 0;

            foreach (var path in allPaths)
            {
                if (!path.StartsWith("Assets/")) continue;
                projectAssets++;

                if (projectAssets % 100 == 0)
                {
                    EditorUtility.DisplayProgressBar("Hades: Checking Project",
                        $"Checking project assets for changes ({projectAssets} checked, {staleGuids.Count} stale)…",
                        0.5f);
                }

                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;

                var storedHash = _db.GetScannedAssetHash(guid);
                if (storedHash == null)
                {
                    staleGuids.Add(guid);
                    continue;
                }

                var scanner = _scannerRegistry.GetScannerForPath(path);
                if (scanner != null)
                {
                    var storedVersion = _db.GetScannedAssetScannerVersion(guid);
                    if (storedVersion.HasValue && storedVersion.Value < scanner.Version)
                    {
                        staleGuids.Add(guid);
                        continue;
                    }
                }

                var currentHash = ComputeContentHash(path);
                if (currentHash != storedHash)
                    staleGuids.Add(guid);
            }

            _buildLog?.Detail("Project assets checked", projectAssets);
            _buildLog?.Detail("Stale assets found", staleGuids.Count);
            _buildLog?.EndStep();

            EditorUtility.ClearProgressBar();

            if (staleGuids.Count > 0)
            {
                Debug.Log($"[Hades] Startup sync: {staleGuids.Count} assets need re-scanning");

                _buildLog?.BeginStep("Incremental update of stale assets");
                UpdateAssets(staleGuids.ToArray());
                _buildLog?.Detail("Assets updated", staleGuids.Count);
                _buildLog?.EndStep();
            }
        }

        // -------------------------------------------------------------------
        // Utilities
        // -------------------------------------------------------------------

        static string ComputeContentHash(string filePath)
        {
            if (!File.Exists(filePath)) return "";
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static bool IsNodeModulesValid(string scannerDir)
        {
            var sqliteMarker = Path.Combine(scannerDir, "node_modules", "better-sqlite3", "package.json");
            var treeSitterMarker = Path.Combine(scannerDir, "node_modules", "tree-sitter", "package.json");
            return File.Exists(sqliteMarker) && File.Exists(treeSitterMarker);
        }

        RunResult RunNodeScanner(string mode, string dirs, string extraArgs = "",
            int timeoutMs = ProjectNodeScannerTimeoutMs)
        {
            var nodePath = ProcessResolver.FindExecutable("node");
            if (nodePath == null)
            {
                Debug.LogWarning("[Hades] Node.js not found — script scanning disabled. Install Node.js for full graph indexing.");
                _buildLog?.Detail("Result", "Node.js not found (exit 100)");
                return new RunResult { ExitCode = 100, Error = "Node.js not found" };
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var dbPath = Path.Combine(projectRoot, ".arcforge", "graph.db");
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GraphBuilder).Assembly);
            var scannerDir = Path.Combine(packageInfo.resolvedPath, "Scanner~");

            if (!IsNodeModulesValid(scannerDir))
            {
                EditorUtility.DisplayProgressBar("Hades: Installing Scanner",
                    "Running npm install for script scanner…", 0f);

                var npmResult = ProcessResolver.Run("npm", "install", scannerDir, 120000, ProcessResolver.NativeBuildEnv);
                if (!npmResult.Success)
                {
                    Debug.LogWarning($"[Hades] npm install failed (attempt 1/2): {npmResult.Error}");
                    _buildLog?.Detail("npm install attempt 1", $"Failed: {npmResult.Error}");

                    npmResult = ProcessResolver.Run("npm", "install", scannerDir, 300000, ProcessResolver.NativeBuildEnv);
                    if (!npmResult.Success)
                    {
                        EditorUtility.ClearProgressBar();
                        var errorMsg = $"npm install failed after 2 attempts: {npmResult.Error}";
                        Debug.LogError($"[Hades] {errorMsg}");
                        _buildLog?.Detail("npm install attempt 2", $"Failed: {npmResult.Error}");
                        return new RunResult { ExitCode = 101, Error = errorMsg };
                    }
                }

                EditorUtility.ClearProgressBar();
            }

            var args = $"\"{Path.Combine(scannerDir, "index.js")}\" --db \"{dbPath}\" --mode {mode} --dirs \"{dirs}\" --project-root \"{projectRoot}\" {extraArgs}";

            _buildLog?.Detail("Node.js scanner", $"mode={mode} dirs={dirs} timeout={timeoutMs}ms");

            var result = ProcessResolver.Run("node", args, projectRoot, timeoutMs);

            if (result.ExitCode == 2)
            {
                Debug.LogWarning("[Hades] Scanner reported database contention, retrying…");
                System.Threading.Thread.Sleep(1000);
                result = ProcessResolver.Run("node", args, projectRoot, timeoutMs);
            }

            if (!result.Success)
            {
                Debug.LogError($"[Hades] Node.js scanner failed (exit {result.ExitCode}): {result.Error}");
                _buildLog?.Detail("Scanner error", $"exit {result.ExitCode}: {result.Error}");
            }

            return result;
        }
    }
}
