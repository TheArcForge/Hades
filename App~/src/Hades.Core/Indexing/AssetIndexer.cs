using System.Diagnostics;
using Hades.Core.Graph;
using Hades.Core.Unity;

namespace Hades.Core.Indexing;

/// <summary>
/// Walks a Unity project's assets — scenes, prefabs, materials, ScriptableObjects, animator
/// controllers — into the graph, mirroring <see cref="ScriptIndexer"/>. Both share
/// <see cref="ProjectWalker"/>, so "what counts as project source" is answered once: local
/// "file:" packages outside the project are included, and directories Unity ignores are pruned.
///
/// <see cref="IndexFiles"/> and <see cref="IndexProject"/> each also call straight into
/// <see cref="BinaryAssetIndexer"/> and merge its <see cref="IndexResult"/> into their own —
/// textures, models, audio, fonts, shaders, and animation clips are binary and carry no structure
/// this type's own YAML reader could extract, so they get meta-only nodes there instead of a
/// parse here. Delegating from this type's own entry points (rather than adding a third call at
/// every caller) means the two callers that matter — a full reindex and an incremental sync —
/// pick up binary assets automatically, with no call-site changes anywhere else.
/// </summary>
public static class AssetIndexer
{
    /// <summary>The asset kinds Unity serialises as YAML documents — the shapes this type's own
    /// reader can actually parse into an object graph. Textures, models, audio, fonts, shaders,
    /// and animation clips are binary/imported instead; see <see cref="BinaryAssetIndexer"/>,
    /// which <see cref="IndexFiles"/> and <see cref="IndexProject"/> both also call, for how those
    /// become meta-only nodes with no content parse.</summary>
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnityYamlParseException)
            {
                // I1: an unparseable file (same as an unreadable one) must not abort the batch —
                // it is named here and the loop moves on to the next file.
                warnings.Add($"{relativePath}: {ex.Message}");
            }
        }

        // Same batch, filtered independently by BinaryAssetIndexer's own extension set — see
        // this type's own class doc comment for why delegating here, rather than a third call at
        // every caller, is what makes binary assets flow through the existing incremental path.
        var binary = BinaryAssetIndexer.IndexFiles(projectRoot, database, relativePaths);

        return new IndexResult
        {
            FilesScanned = filesScanned + binary.FilesScanned,
            TypesFound = objectsFound + binary.TypesFound,
            Duration = stopwatch.Elapsed,
            Warnings = [.. warnings, .. binary.Warnings],
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
            var failedDirectories = new List<string>();

            foreach (var file in ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*", failedDirectories))
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnityYamlParseException)
                {
                    // I1: same as IndexFiles above — one unparseable file is named here, never
                    // allowed to abort the rest of a full project walk.
                    warnings.Add($"{relativePath}: {ex.Message}");
                }
            }

            // I10: a directory this walk could not even read is not evidence anything under it
            // was deleted — reserved from the sweep below exactly like an unresolvable package's
            // prefix already is (same parameter, same reasoning, see ScriptIndexer's identical
            // handling), and named in a warning rather than silently wiping whatever was
            // previously recorded for it.
            var reserved = unreachablePackagePrefixes;
            if (failedDirectories.Count > 0)
            {
                var unreadablePrefixes = failedDirectories.Select(dir => ProjectWalker.ToRecordedPath(root, dir)).ToList();
                reserved = [.. unreachablePackagePrefixes, .. unreadablePrefixes];
                foreach (var prefix in unreadablePrefixes)
                    warnings.Add($"{prefix}: directory could not be read this rebuild; previously recorded state preserved.");
            }

            // Same contract as ScriptIndexer: a file deleted since the last index was never
            // visited, so delete-then-insert alone would leave its nodes and edges behind.
            database.SweepStaleNodes(root.PathPrefix, visited, reserved, Extensions);
        }

        // A full, independent walk of the same project — BinaryAssetIndexer resolves its own
        // scan roots and sweeps only the extensions it owns (see its own IndexProject), so this
        // cannot double-count or step on the YAML loop above.
        var binary = BinaryAssetIndexer.IndexProject(projectRoot, database);

        return new IndexResult
        {
            FilesScanned = filesScanned + binary.FilesScanned,
            TypesFound = objectsFound + binary.TypesFound,
            Duration = stopwatch.Elapsed,
            Warnings = [.. warnings, .. binary.Warnings],
        };
    }

    static int IndexAsset(string absolutePath, string relativePath, GraphDatabase database)
    {
        var content = File.ReadAllText(absolutePath);

        // Force Text does not mean every asset is text — Unity writes LightingData.asset and
        // friends as binary regardless. I3: this path must not just skip quietly when the file
        // WAS previously a parseable asset — its old nodes have to go too, or a file rewritten
        // into something Hades can no longer understand keeps answering confidently from stale
        // content forever. DeleteNodesAndEdgesForPath (F22: never the file-state-clearing
        // DeleteNodesForPath — this file still exists, it is simply unparseable) is a no-op for a
        // path with nothing recorded, so this costs nothing extra for the ordinary "never was
        // Unity YAML" case.
        if (!UnityYamlPreprocessor.LooksLikeUnityYaml(content))
        {
            database.DeleteNodesAndEdgesForPath(relativePath);
            return 0;
        }

        IReadOnlyList<UnityObject> objects;
        try
        {
            objects = UnityYamlReader.Read(content, relativePath);
        }
        catch (UnityYamlParseException)
        {
            // I1: the caller (IndexFiles/IndexProject) turns this into a per-file warning and
            // moves on to the next file. I3: same "gone, not stale" treatment as above — a file
            // Hades cannot parse must not keep answering from whatever it parsed last time.
            database.DeleteNodesAndEdgesForPath(relativePath);
            throw;
        }

        if (objects.Count == 0)
        {
            // Genuinely nothing recognizable in an otherwise YAML-shaped file — same I3 reasoning
            // as both branches above.
            database.DeleteNodesAndEdgesForPath(relativePath);
            return 0;
        }

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
        // removed from an asset must disappear rather than linger. DeleteNodesAndEdgesForPath
        // drops this path's edges in the same transaction — F22: never the file-state-clearing
        // DeleteNodesForPath, which is reserved for a path a sweep has confirmed gone from disk,
        // not one simply being re-indexed here (see that method's own doc comment).
        database.DeleteNodesAndEdgesForPath(relativePath);
        database.UpsertNodes(nodes);
        database.UpsertEdges(edges);

        return objects.Count;
    }
}
