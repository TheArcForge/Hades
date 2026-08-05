using System.Diagnostics;
using Hades.Core.Graph;
using Hades.Core.Unity;

namespace Hades.Core.Indexing;

/// <summary>
/// Walks a Unity project's assets — scenes, prefabs, materials, ScriptableObjects, animator
/// controllers — into the graph, mirroring <see cref="ScriptIndexer"/>. Both share
/// <see cref="ProjectWalker"/>, so "what counts as project source" is answered once: local
/// "file:" packages outside the project are included, and directories Unity ignores are pruned.
/// </summary>
public static class AssetIndexer
{
    /// <summary>The asset kinds Unity serialises as YAML documents. Textures, models and audio
    /// are binary and carry their structure in .meta importer settings, which is a later plan.</summary>
    static readonly string[] Extensions = [".unity", ".prefab", ".asset", ".mat", ".controller"];

    /// <summary>
    /// Indexes ONLY the named files. As with <see cref="ScriptIndexer.IndexFiles"/>, this must not
    /// sweep — a sweep scoped to a partial batch deletes everything outside it.
    /// </summary>
    public static IndexResult IndexFiles(string projectRoot, GraphDatabase database,
        IReadOnlyList<string> relativePaths)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var roots = ProjectWalker.ResolveScanRoots(projectRoot, warnings);
        var filesScanned = 0;
        var objectsFound = 0;

        foreach (var relativePath in relativePaths)
        {
            if (!Extensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase)) continue;
            if (Observation.ProjectSweeper.ToAbsolute(roots, relativePath) is not { } absolute) continue;
            if (!File.Exists(absolute)) continue;

            filesScanned++;

            try
            {
                objectsFound += IndexAsset(absolute, relativePath, database);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relativePath}: {ex.Message}");
            }
        }

        return new IndexResult
        {
            FilesScanned = filesScanned,
            TypesFound = objectsFound,
            Duration = stopwatch.Elapsed,
            Warnings = warnings,
        };
    }

    public static IndexResult IndexProject(string projectRoot, GraphDatabase database)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var filesScanned = 0;
        var objectsFound = 0;

        var unreachablePackagePrefixes = ProjectWalker.UnreachablePackagePrefixes(projectRoot);

        foreach (var root in ProjectWalker.ResolveScanRoots(projectRoot, warnings))
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*"))
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                filesScanned++;
                var relativePath = ProjectWalker.ToRecordedPath(root, file);

                // Recorded even when the read below fails: a file that exists but could not be
                // read was not deleted, so its previous nodes must survive the sweep.
                visited.Add(relativePath);

                try
                {
                    objectsFound += IndexAsset(file, relativePath, database);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{relativePath}: {ex.Message}");
                }
            }

            // Same contract as ScriptIndexer: a file deleted since the last index was never
            // visited, so delete-then-insert alone would leave its nodes and edges behind.
            database.SweepStaleNodes(root.PathPrefix, visited, unreachablePackagePrefixes, Extensions);
        }

        return new IndexResult
        {
            FilesScanned = filesScanned,
            TypesFound = objectsFound,
            Duration = stopwatch.Elapsed,
            Warnings = warnings,
        };
    }

    static int IndexAsset(string absolutePath, string relativePath, GraphDatabase database)
    {
        var content = File.ReadAllText(absolutePath);

        // Force Text does not mean every asset is text — Unity writes LightingData.asset and
        // friends as binary regardless. Skipping quietly is correct; these carry no graph value.
        if (!UnityYamlPreprocessor.LooksLikeUnityYaml(content)) return 0;

        var objects = UnityYamlReader.Read(content, relativePath);
        if (objects.Count == 0) return 0;

        var assetGuid = MetaFileReader.TryReadGuid(absolutePath);

        var nodes = new List<GraphNode>(objects.Count);
        var edges = new List<GraphEdge>();

        foreach (var obj in objects)
        {
            nodes.Add(new GraphNode
            {
                Kind = obj.TypeName,
                // Components have no m_Name — they take their identity from the GameObject they
                // hang off. Naming them after their type keeps them searchable, and FileId
                // disambiguates the many instances.
                Name = obj.Name ?? obj.TypeName,
                Path = relativePath,
                Guid = assetGuid,
                FileId = obj.FileId,
            });

            foreach (var reference in obj.References)
            {
                edges.Add(new GraphEdge
                {
                    FromPath = relativePath,
                    FromFileId = obj.FileId,
                    // A local reference resolves within this same asset, so it inherits the
                    // asset's own GUID rather than being left null and unresolvable.
                    ToGuid = reference.Guid ?? assetGuid,
                    ToFileId = reference.FileId,
                    Kind = "references",
                    PropertyPath = reference.PropertyPath,
                });
            }

            // A stripped object stands in for one owned by a nested prefab. Without this link a
            // scene's hierarchy has holes exactly where prefab instances are. 527 corpus-wide.
            if (obj.IsStripped && obj.CorrespondingSourceObject is { } source)
            {
                edges.Add(new GraphEdge
                {
                    FromPath = relativePath,
                    FromFileId = obj.FileId,
                    ToGuid = source.Guid ?? assetGuid,
                    ToFileId = source.FileId,
                    Kind = "corresponds_to",
                    PropertyPath = "m_CorrespondingSourceObject",
                });
            }

            // Prefab instancing is what makes "which scenes use this prefab" answerable, and it
            // is overwhelmingly a scene phenomenon: 386 instances across 25 of 49 scenes against
            // 17 across 114 prefabs.
            if (PrefabInstanceReader.TryRead(obj) is { } instance)
            {
                edges.Add(new GraphEdge
                {
                    FromPath = relativePath,
                    FromFileId = instance.FileId,
                    ToGuid = instance.SourcePrefab.Guid ?? assetGuid,
                    ToFileId = instance.SourcePrefab.FileId,
                    Kind = "instance_of",
                    PropertyPath = "m_SourcePrefab",
                });

                // Only overrides that rewire a reference. The other 43,784 corpus-wide set
                // scalars and belong to a configuration feature, not a reference graph — storing
                // them would inflate the graph roughly 55x for nothing it can answer.
                foreach (var over in instance.ReferenceOverrides)
                {
                    edges.Add(new GraphEdge
                    {
                        FromPath = relativePath,
                        FromFileId = instance.FileId,
                        ToGuid = over.ObjectReference!.Guid ?? assetGuid,
                        ToFileId = over.ObjectReference.FileId,
                        Kind = "references",
                        PropertyPath = $"m_Modifications[{over.PropertyPath}]",
                    });
                }
            }
        }

        // Delete-then-insert per file, exactly as ScriptIndexer does: a component or reference
        // removed from an asset must disappear rather than linger. DeleteNodesForPath drops this
        // path's edges in the same transaction.
        database.DeleteNodesForPath(relativePath);
        database.UpsertNodes(nodes);
        database.UpsertEdges(edges);

        return objects.Count;
    }
}
