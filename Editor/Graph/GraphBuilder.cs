using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.Graph.Scanning;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph
{
    public enum BuildStatus { Idle, Rebuilding, Updating }

    public class GraphBuilder
    {
        readonly GraphDatabase _db;
        readonly ScannerRegistry _scannerRegistry;
        BuildStatus _status = BuildStatus.Idle;

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

        public void RebuildAll()
        {
            _status = BuildStatus.Rebuilding;
            var allPaths = AssetDatabase.GetAllAssetPaths();

            _db.SetCurrentOperation("rebuild");

            try
            {
                _db.Execute("DELETE FROM edges;");
                _db.Execute("DELETE FROM nodes;");
                _db.Execute("DELETE FROM scanned_assets;");

                EnsureProjectNode();

                int total = allPaths.Length;
                int processed = 0;

                foreach (var path in allPaths)
                {
                    processed++;
                    if (processed % 100 == 0)
                        EditorUtility.DisplayProgressBar("Hades: Rebuilding Graph",
                            $"Scanning {processed}/{total}...", (float)processed / total);

                    ScanAsset(path);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _db.ClearCurrentOperation();
                _db.SetMetadata("last_full_rebuild_at",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                _status = BuildStatus.Idle;
            }
        }

        public void UpdateAssets(string[] guids)
        {
            if (guids == null || guids.Length == 0) return;

            _status = BuildStatus.Updating;
            _db.SetCurrentOperation("update", guids);

            try
            {
                _db.RunInTransaction(() =>
                {
                    foreach (var guid in guids)
                    {
                        var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(assetPath))
                        {
                            _db.DeleteNodesByGuid(guid);
                            continue;
                        }

                        if (!File.Exists(assetPath) && !Directory.Exists(assetPath))
                        {
                            _db.DeleteNodesByGuid(guid);
                            continue;
                        }

                        var currentHash = ComputeContentHash(assetPath);
                        var storedHash = _db.GetScannedAssetHash(guid);

                        if (storedHash == currentHash) continue;

                        _db.DeleteNodesByGuid(guid);
                        ScanAsset(assetPath);
                    }
                });
            }
            finally
            {
                _db.ClearCurrentOperation();
                _db.SetMetadata("last_incremental_at",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                _status = BuildStatus.Idle;
            }
        }

        public void HandleDeletedAssets(string[] deletedPaths)
        {
            foreach (var path in deletedPaths)
            {
                _db.Execute("DELETE FROM nodes WHERE path = ?;", path);
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

        void ScanAsset(string assetPath)
        {
            var scanner = _scannerRegistry.GetScannerForPath(assetPath);
            if (scanner == null) return;

            try
            {
                var scanResult = scanner.Scan(assetPath);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);

                WriteScanResult(scanResult, guid);

                var hash = ComputeContentHash(assetPath);
                _db.RecordScannedAsset(guid, hash, scanner.Version);

                foreach (var warning in scanResult.Warnings)
                {
                    if (warning.Severity >= WarningSeverity.Warning)
                        Debug.LogWarning($"[Hades] {warning.Message} ({warning.AssetPath})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Hades] Scanner error on {assetPath}: {ex.Message}");
            }
        }

        void WriteScanResult(ScanResult scanResult, string assetGuid)
        {
            var nodeIdMap = new Dictionary<string, long>();

            foreach (var node in scanResult.Nodes)
            {
                var id = _db.InsertNode(node);
                var key = $"{node.Guid ?? ""}:{node.FileId ?? 0}";
                nodeIdMap[key] = id;
            }

            foreach (var edge in scanResult.Edges)
            {
                var sourceKey = $"{edge.SourceGuid ?? ""}:{edge.SourceFileId}";
                var targetKey = $"{edge.TargetGuid ?? ""}:{edge.TargetFileId}";

                long sourceId, targetId;

                if (!nodeIdMap.TryGetValue(sourceKey, out sourceId))
                {
                    var sourceNode = _db.FindNodeByGuid(edge.SourceGuid, edge.SourceFileId);
                    if (sourceNode == null) sourceNode = _db.FindNodeByGuid(edge.SourceGuid);
                    if (sourceNode == null) continue;
                    sourceId = sourceNode.Id;
                }

                if (!nodeIdMap.TryGetValue(targetKey, out targetId))
                {
                    var targetNode = _db.FindNodeByGuid(edge.TargetGuid, edge.TargetFileId);
                    if (targetNode == null) targetNode = _db.FindNodeByGuid(edge.TargetGuid);
                    if (targetNode == null) continue;
                    targetId = targetNode.Id;
                }

                _db.InsertEdge(sourceId, targetId, edge.Type, edge.PropertiesJson);
            }
        }

        public void CheckStartupSync()
        {
            if (_db.GetNodeCount() == 0)
            {
                RebuildAll();
                return;
            }

            var allPaths = AssetDatabase.GetAllAssetPaths();
            var staleGuids = new List<string>();

            foreach (var path in allPaths)
            {
                if (!path.StartsWith("Assets/")) continue;
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

            if (staleGuids.Count > 0)
            {
                Debug.Log($"[Hades] Startup sync: {staleGuids.Count} assets need re-scanning");
                UpdateAssets(staleGuids.ToArray());
            }
        }

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
    }
}
