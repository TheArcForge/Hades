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

        BuildStatus _statusBacking = BuildStatus.Idle;
        BuildStatus _status
        {
            get => _statusBacking;
            set { _statusBacking = value; _busy = value != BuildStatus.Idle; }
        }

        // Session-level node map: accumulates guid:fileId → nodeId across all scanned files
        // during a rebuild. Eliminates per-edge DB lookups in WriteScanResult.
        Dictionary<string, long> _sessionNodeMap;

        // Cached for the duration of a startup/rebuild session to avoid repeated Unity API calls
        string[] _cachedAllPaths;

        // Build log — records timing/results per step, written to .arcforge/graph_build.log
        GraphBuildLog _buildLog;

        const int NodeScannerTimeoutMs = 300000; // 5 minutes

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
                _buildLog?.EndStep();

                // Rebuild session map from DB (includes what Node.js wrote)
                _sessionNodeMap = BuildSessionMapFromExistingNodes();

                // --- Phase C: Main-thread assets (scenes, prefabs, etc.) ---
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
        /// </summary>
        public void ScanPackages(Action onComplete = null)
        {
            if (_status != BuildStatus.Idle && _status != BuildStatus.ScanningPackages) return;

            _status = BuildStatus.ScanningPackages;
            _db.SetCurrentOperation("package_scan");

            try
            {
                _buildLog?.BeginStep("Node.js package script scan");
                EditorUtility.DisplayProgressBar("Hades: Scanning Packages",
                    "Scanning package scripts via Node.js…", 0.1f);

                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var cacheDir = Path.Combine(projectRoot, "Library", "PackageCache");
                var packagesDir = Path.Combine(projectRoot, "Packages");
                var dirs = string.Join(",", new[] { cacheDir, packagesDir }.Where(Directory.Exists));

                if (string.IsNullOrEmpty(dirs))
                {
                    Debug.Log("[Hades] No package directories found, skipping scan");
                    _buildLog?.Detail("Result", "No package directories");
                    _buildLog?.EndStep();
                    return;
                }

                _sessionNodeMap = _sessionNodeMap ?? BuildSessionMapFromExistingNodes();
                _db.RunInTransaction(() => _db.DeleteNodesByTier("package"));

                var result = RunNodeScanner("full", dirs, "--tier package");

                if (result.Success)
                {
                    var packageHash = ComputePackageLockHash();
                    if (packageHash != null)
                        _db.SetMetadata("packages_lock_hash", packageHash);

                    _db.SetMetadata("last_package_scan_at",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

                    _sessionNodeMap = BuildSessionMapFromExistingNodes();

                    var typeCount = _db.GetNodeCount("ScriptType", "package");
                    Debug.Log($"[Hades] Package scan complete: {typeCount} package types indexed");
                    _buildLog?.Detail("Types indexed", typeCount);
                }
                else
                {
                    if (result.ExitCode == 100)
                        _buildLog?.ReportDegraded("Package C# nodes missing — Node.js not found");
                    else if (result.ExitCode == 101)
                        _buildLog?.ReportDegraded("Package C# nodes missing — Scanner npm install failed");
                    else
                        _buildLog?.ReportDegraded($"Package C# nodes missing — Scanner failed (exit {result.ExitCode})");
                    Debug.LogWarning("[Hades] Package scan skipped (Node.js scanner unavailable or failed)");
                    _buildLog?.Detail("Result", $"Failed: exit {result.ExitCode}");
                }

                _buildLog?.EndStep();
            }
            catch (Exception ex)
            {
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
        /// Checks if packages need rescanning by comparing packages-lock.json hash.
        /// </summary>
        bool IsPackageScanNeeded()
        {
            // If no package nodes exist at all, we need a scan
            var packageNodeCount = _db.GetNodeCount("ScriptType", "package");
            if (packageNodeCount == 0) return true;

            // Check if packages-lock.json has changed
            var currentHash = ComputePackageLockHash();
            if (currentHash == null) return false; // No lock file, can't determine

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

                    if (otherGuids.Count > 0)
                    {
                        _sessionNodeMap = BuildSessionMapFromExistingNodes();

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

                            ResolvePendingEdges();
                        });
                    }
                    else
                    {
                        _sessionNodeMap = BuildSessionMapFromExistingNodes();
                        _db.RunInTransaction(() => ResolvePendingEdges());
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

                // Resolve source: local map → session map → DB
                if (!localNodeMap.TryGetValue(sourceKey, out sourceId))
                {
                    if (_sessionNodeMap == null || !_sessionNodeMap.TryGetValue(sourceKey, out sourceId))
                    {
                        var sourceNode = _db.FindNodeByGuid(edge.SourceGuid, edge.SourceFileId);
                        if (sourceNode == null) sourceNode = _db.FindNodeByGuid(edge.SourceGuid);
                        if (sourceNode == null) continue;
                        sourceId = sourceNode.Id;
                    }
                }

                // Resolve target: check for name-based pending marker → local map → session map → DB → pending
                if (edge.TargetGuid != null && edge.TargetGuid.StartsWith("__pending__"))
                {
                    // Name-based edge — goes to pending resolution
                    var targetTypeName = edge.TargetGuid.Substring("__pending__".Length);
                    string targetNamespace = null;
                    if (edge.Properties != null && edge.Properties.TryGetValue("target_type_name", out var tn))
                        targetTypeName = tn.ToString();

                    // Try immediate resolution against existing nodes
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
                else if (!localNodeMap.TryGetValue(targetKey, out targetId))
                {
                    if (_sessionNodeMap != null && _sessionNodeMap.TryGetValue(targetKey, out targetId))
                    {
                        // Found in session map
                    }
                    else
                    {
                        var targetNode = _db.FindNodeByGuid(edge.TargetGuid, edge.TargetFileId);
                        if (targetNode == null) targetNode = _db.FindNodeByGuid(edge.TargetGuid);
                        if (targetNode == null)
                        {
                            // Target doesn't exist yet — store as pending edge for later resolution
                            _db.InsertPendingEdge(sourceId, edge.Type,
                                edge.TargetGuid ?? "", null, assetGuid);
                            continue;
                        }
                        targetId = targetNode.Id;
                    }
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
        void ResolvePendingEdges()
        {
            var pending = _db.GetPendingEdges();
            if (pending.Count == 0) return;

            var coveredExtensions = _scannerRegistry.GetCoveredExtensions();

            int resolved = 0;
            int permanent = 0;
            int transient = 0;
            var toDelete = new HashSet<long>();

            foreach (var pe in pending)
            {
                NodeRecord targetNode = null;
                if (!string.IsNullOrEmpty(pe.TargetTypeName))
                {
                    targetNode = _db.FindNodeByGuid(pe.TargetTypeName);
                }

                if (targetNode == null && !string.IsNullOrEmpty(pe.TargetTypeName)
                    && (pe.EdgeType == "inherits_from" || pe.EdgeType == "implements" || pe.EdgeType == "code_references"))
                {
                    targetNode = _db.FindNodeByNameAndType(pe.TargetTypeName, "ScriptType");
                }

                if (targetNode != null)
                {
                    string propertiesJson = null;
                    if (pe.EdgeType == "code_references" && !string.IsNullOrEmpty(pe.TargetNamespace))
                    {
                        propertiesJson = $"{{\"reference_kind\":\"{pe.TargetNamespace}\"}}";
                    }
                    _db.InsertEdge(pe.SourceNodeId, targetNode.Id, pe.EdgeType, propertiesJson);
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

                    // Try to resolve the GUID to an asset path to check its extension
                    var assetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(pe.TargetTypeName);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var ext = Path.GetExtension(assetPath)?.ToLowerInvariant();
                        if (ext != null && !coveredExtensions.Contains(ext))
                            permanent++;
                        else
                            transient++;
                    }
                    else
                    {
                        // GUID doesn't resolve to any asset — likely a type name for inherits_from/implements
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
                if (transient > 0)
                    parts.Add($"{transient} still pending (will resolve on next rebuild)");

                Debug.Log($"[Hades] Pending edges: {string.Join(", ", parts)}");

                _buildLog?.Detail("Edges resolved", resolved);
                if (permanent > 0)
                    _buildLog?.Detail("Edges unresolvable (unscanned types)", permanent);
                if (transient > 0)
                    _buildLog?.Detail("Edges still pending", transient);
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

                        var node = new NodeRecord("ScriptType")
                        {
                            Name = type.Name,
                            Properties = new Dictionary<string, object>
                            {
                                ["source"] = "builtin",
                                ["namespace"] = type.Namespace ?? "",
                                ["assembly"] = assemblyName
                            }
                        };

                        var nodeId = _db.InsertNode(node, "builtin");
                        typeCount++;

                        if (type.BaseType != null && type.BaseType != typeof(object))
                        {
                            var baseTypeName = type.BaseType.Name;
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
                            _db.InsertPendingEdge(nodeId, "implements", iface.Name, iface.Namespace, null);
                            edgeCount++;
                        }
                    }
                }

                _db.SetMetadata("builtin_unity_version", currentVersion);
            });

            sw.Stop();
            Debug.Log($"[Hades] Seeded {typeCount} builtin types, {edgeCount} edges in {sw.ElapsedMilliseconds}ms");
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

        RunResult RunNodeScanner(string mode, string dirs, string extraArgs = "")
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

            _buildLog?.Detail("Node.js scanner", $"mode={mode} dirs={dirs}");

            var result = ProcessResolver.Run("node", args, projectRoot, NodeScannerTimeoutMs);

            if (result.ExitCode == 2)
            {
                Debug.LogWarning("[Hades] Scanner reported database contention, retrying…");
                System.Threading.Thread.Sleep(1000);
                result = ProcessResolver.Run("node", args, projectRoot, NodeScannerTimeoutMs);
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
