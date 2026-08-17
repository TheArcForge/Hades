using Hades.Core.Observation;
using Microsoft.Data.Sqlite;

namespace Hades.Core.Graph;

/// <summary>SQLite-backed knowledge graph for one Unity project.</summary>
public sealed class GraphDatabase : IDisposable
{
    readonly SqliteConnection _connection;

    GraphDatabase(SqliteConnection connection) => _connection = connection;

    public static GraphDatabase Open(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        try
        {
            connection.Open();

            // journal_mode returns the RESULTING mode as a row, and the conversion to WAL can be
            // refused — it needs a brief exclusive lock, so another connection holding the file
            // makes it silently no-op and leave the database in rollback-journal mode. Running
            // this via ExecuteNonQuery discards that answer, so every caller downstream would
            // assume WAL's concurrency semantics on a connection that never got them. Read the
            // result instead; a refused conversion is a real condition and must be visible.
            using (var journalMode = connection.CreateCommand())
            {
                journalMode.CommandText = "PRAGMA journal_mode = WAL;";
                var resulting = journalMode.ExecuteScalar() as string;

                if (!string.Equals(resulting, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Could not put '{databasePath}' into WAL mode (it reports '{resulting}'). "
                        + "Another process may hold the database, or it may be on a filesystem "
                        + "that does not support WAL, such as a network share.");
                }
            }

            using (var synchronous = connection.CreateCommand())
            {
                synchronous.CommandText = "PRAGMA synchronous = NORMAL;";
                synchronous.ExecuteNonQuery();
            }

            GraphSchema.Apply(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new GraphDatabase(connection);
    }

    public int SchemaVersion => GraphSchema.ReadVersion(_connection);

    public void UpsertNodes(IReadOnlyList<GraphNode> nodes)
    {
        if (nodes.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO nodes (kind, name, name_lower, path, namespace, line, guid, file_id)
            VALUES ($kind, $name, $nameLower, $path, $namespace, $line, $guid, $fileId)
            ON CONFLICT (path, kind, name, COALESCE(namespace, ''), file_id) DO UPDATE SET
                namespace = excluded.namespace,
                line      = excluded.line,
                guid      = excluded.guid,
                file_id   = excluded.file_id;
            """;

        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var nameLower = command.Parameters.Add("$nameLower", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var ns = command.Parameters.Add("$namespace", SqliteType.Text);
        var line = command.Parameters.Add("$line", SqliteType.Integer);
        var guid = command.Parameters.Add("$guid", SqliteType.Text);
        var fileId = command.Parameters.Add("$fileId", SqliteType.Integer);

        foreach (var node in nodes)
        {
            kind.Value = node.Kind;
            name.Value = node.Name;
            nameLower.Value = node.Name.ToLowerInvariant();
            path.Value = node.Path;
            ns.Value = (object?)node.Namespace ?? DBNull.Value;
            line.Value = node.Line;
            guid.Value = (object?)node.Guid ?? DBNull.Value;
            fileId.Value = node.FileId;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Removes a file's nodes AND its outgoing edges, in one transaction. Edges must go with the
    /// nodes: a reference deleted from an asset has to disappear from the graph, and leaving
    /// edges behind would accumulate stale relationships exactly as orphaned nodes did before
    /// the sweep existed.
    /// </summary>
    public void DeleteNodesForPath(string path)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var sql in new[]
        {
            "DELETE FROM nodes WHERE path = $path;",
            "DELETE FROM edges WHERE from_path = $path;",
            // State must die with the nodes. Left behind, a file deleted and later re-added would
            // still match its old record and never be reindexed.
            "DELETE FROM file_state WHERE path = $path;",
        })
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void UpsertEdges(IReadOnlyList<GraphEdge> edges)
    {
        if (edges.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO edges (from_path, from_file_id, to_guid, to_file_id, kind, property_path)
            VALUES ($fromPath, $fromFileId, $toGuid, $toFileId, $kind, $propertyPath)
            ON CONFLICT (from_path, from_file_id, COALESCE(to_guid, ''), to_file_id, property_path)
                DO UPDATE SET kind = excluded.kind;
            """;

        var fromPath = command.Parameters.Add("$fromPath", SqliteType.Text);
        var fromFileId = command.Parameters.Add("$fromFileId", SqliteType.Integer);
        var toGuid = command.Parameters.Add("$toGuid", SqliteType.Text);
        var toFileId = command.Parameters.Add("$toFileId", SqliteType.Integer);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var propertyPath = command.Parameters.Add("$propertyPath", SqliteType.Text);

        foreach (var edge in edges)
        {
            fromPath.Value = edge.FromPath;
            fromFileId.Value = edge.FromFileId;
            toGuid.Value = (object?)edge.ToGuid ?? DBNull.Value;
            toFileId.Value = edge.ToFileId;
            kind.Value = edge.Kind;
            propertyPath.Value = edge.PropertyPath;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<GraphEdge> EdgesFrom(string fromPath, long fromFileId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT from_path, from_file_id, to_guid, to_file_id, kind, property_path
            FROM edges WHERE from_path = $fromPath AND from_file_id = $fromFileId;
            """;
        command.Parameters.AddWithValue("$fromPath", fromPath);
        command.Parameters.AddWithValue("$fromFileId", fromFileId);
        return ReadEdges(command);
    }

    /// <summary>
    /// Every outgoing edge from ANY node at this path — everything the FILE depends on, not just
    /// one object within it. A prefab or scene has many nodes (a GameObject, its components),
    /// each with its own file_id, but <c>edges.from_path</c> is already a direct column on every
    /// row (see <see cref="UpsertEdges"/>), so this is one indexed query (idx_edges_from) rather
    /// than reading the file's node file_ids first and calling <see cref="EdgesFrom"/> once per
    /// node — the latter would be one query per component for a prefab with dozens of them.
    /// </summary>
    public IReadOnlyList<GraphEdge> EdgesFromPath(string fromPath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT from_path, from_file_id, to_guid, to_file_id, kind, property_path
            FROM edges WHERE from_path = $fromPath;
            """;
        command.Parameters.AddWithValue("$fromPath", fromPath);
        return ReadEdges(command);
    }

    /// <summary>Depth beyond which <see cref="TraceDependencies"/> refuses to walk, regardless of
    /// what a caller asks for. The visited set already guarantees termination; this instead
    /// bounds how much a single pathological (wide, deep) graph can return in one call — the same
    /// token-budget reasoning as <see cref="MaxSearchLimit"/>.</summary>
    const int MaxTraceDepth = 10;

    /// <summary>
    /// Walks `references` and `instance_of` edges outward. Iterative with an explicit visited
    /// set: Unity projects contain reference cycles as a matter of course (a prefab referencing a
    /// manager that references the prefab), so a recursive walk without one does not terminate.
    ///
    /// F19: `instance_of` — a PrefabInstance's `m_SourcePrefab`, naming the prefab a nested
    /// instance or variant is sourced from — counts as a dependency here just like an ordinary
    /// `references` edge: a prefab nesting another prefab is exactly a "what does this asset
    /// depend on" relationship, and find_references_to (<see cref="ReferencesTo"/> /
    /// <see cref="ReferencingFiles"/>) already reported it in reverse, since neither of those
    /// filter by kind — only the forward walk did. `corresponds_to` — a stripped placeholder
    /// object's link to the real object it stands in for inside that same nested prefab — stays
    /// excluded: it exists to resolve individual objects within an instance, not to name a
    /// file-level dependency, and every PrefabInstance that writes a corresponds_to edge also
    /// writes an instance_of edge to the same target file (see AssetIndexer's PrefabInstance
    /// handling), so walking it too would only add duplicate hits, never a reachable one this
    /// walk would otherwise miss.
    ///
    /// F6-honesty: a `references` or `instance_of` edge whose target GUID resolves to no node —
    /// most often now because the target lives outside every scanned root (a registry package
    /// resolved into Library/PackageCache, never walked regardless of extension) rather than
    /// because its kind goes unindexed, now that textures, models, audio, fonts, shaders, and
    /// animation clips are indexed too (see <see cref="Unity.ImportedAssetKind"/>) — is no longer
    /// silently dropped. It cannot become a <see cref="DependencyHit"/> (there is no path to walk
    /// further from), but it is real — a row in the `edges` table, exactly as resolvable as any
    /// other — so it is reported via <see cref="DependencyTrace.Dangling"/> instead of vanishing.
    /// See <see cref="DanglingDependency"/>'s own class doc comment.
    /// </summary>
    public DependencyTrace TraceDependencies(string rootPath, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        maxDepth = Math.Clamp(maxDepth, 1, MaxTraceDepth);

        var visited = new HashSet<string>(StringComparer.Ordinal) { rootPath };
        var hits = new List<DependencyHit>();
        var dangling = new List<DanglingDependency>();
        var frontier = new List<string> { rootPath };

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();

            foreach (var path in frontier)
            foreach (var (guid, propertyPath) in DirectReferencesOf(path))
            {
                if (PathForGuid(guid) is { } targetPath)
                {
                    if (!visited.Add(targetPath)) continue;
                    hits.Add(new DependencyHit { Path = targetPath, Depth = depth });
                    next.Add(targetPath);
                }
                else
                {
                    dangling.Add(new DanglingDependency
                    {
                        FromPath = path, Depth = depth, ToGuid = guid, PropertyPath = propertyPath,
                    });
                }
            }

            frontier = next;
        }

        return new DependencyTrace { Hits = hits, Dangling = dangling };
    }

    /// <summary>One hop of <see cref="TraceDependencies"/>: every distinct `references` or
    /// `instance_of` edge <paramref name="path"/> carries directly, as (target guid, a sample
    /// property path) pairs — see <see cref="TraceDependencies"/>'s own doc comment (F19) for
    /// which edge kinds count as a dependency and why `corresponds_to` does not. Distinct GUIDs
    /// are yielded once each rather than once per edge, since a file's edges routinely repeat a
    /// target many times over (e.g. every component in a prefab pointing back at its own
    /// GameObject) — the property path kept for each is simply the first one seen, same
    /// convention as <see cref="ReferencingFile.SampleVia"/>. Resolving each guid via
    /// <see cref="PathForGuid"/> — deciding whether it becomes a hop or a dangling dependency — is
    /// the caller's job, not this method's: unlike the pre-F6-honesty TargetsOf this replaces,
    /// dropping an unresolved guid here would make it unreportable.</summary>
    IEnumerable<(string Guid, string PropertyPath)> DirectReferencesOf(string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in EdgesFromPath(path))
        {
            if (edge.Kind is not ("references" or "instance_of") || edge.ToGuid is not { } guid) continue;
            if (!seen.Add(guid)) continue;

            yield return (guid, edge.PropertyPath);
        }
    }

    /// <summary>Everything referencing this asset — the backbone of "what would break if I
    /// removed X".</summary>
    public IReadOnlyList<GraphEdge> EdgesTo(string toGuid, int limit = 200)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT from_path, from_file_id, to_guid, to_file_id, kind, property_path
            FROM edges WHERE to_guid = $toGuid LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$toGuid", toGuid);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return ReadEdges(command);
    }

    /// <summary>
    /// The GUID owning a project-relative asset path, or null when the path is unknown or
    /// predates .meta GUID capture. Every reference in Unity resolves through a GUID, so this is
    /// how a human-friendly path becomes something the edge table can be queried by.
    /// </summary>
    public string? GuidForPath(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT guid FROM nodes WHERE path = $path AND guid IS NOT NULL LIMIT 1;";
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// The project-relative path owning <paramref name="guid"/>, or null when the GUID is
    /// unknown. The inverse of <see cref="GuidForPath"/>: <see cref="TraceDependencies"/> gets a
    /// target GUID from an edge and needs this to turn it back into a path so the walk can
    /// continue from it. A GUID is a Unity .meta file's identity, so it names exactly one file in
    /// a well-formed project — checked against the real Hades-Unity-Client corpus (224 distinct
    /// GUIDs, zero mapping to more than one path) rather than assumed — but LIMIT 1 keeps that an
    /// assumption this degrades under gracefully, not one it can violate. The ORDER BY makes the
    /// degraded case DETERMINISTIC: a project with a duplicated GUID (a copy-pasted .meta) always
    /// resolves to the lexicographically-first path, not whichever row SQLite happens to visit
    /// first — the same question must never give two different answers across calls.
    /// </summary>
    public string? PathForGuid(string guid)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path FROM nodes WHERE guid = $guid ORDER BY path ASC LIMIT 1;";
        command.Parameters.AddWithValue("$guid", guid);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Everything referencing the asset at <paramref name="toGuid"/>, joined back to the
    /// referencing object so callers get a name and kind rather than a bare fileID.
    /// One row per (referencing object, property) — the shape "what would break if I removed
    /// this" actually needs.
    /// </summary>
    /// <param name="excludePath">
    /// The asset's own path. Local references inherit the owning asset's GUID, so without this a
    /// prefab containing 50 objects reports 50 self-references before any genuine external user —
    /// which is the opposite of what "what references this" is asked to find.
    /// </param>
    public IReadOnlyList<ReferencingObject> ReferencesTo(string toGuid, string? excludePath, int limit = 100)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.from_path, e.from_file_id, e.kind, e.property_path,
                   COALESCE(n.name, ''), COALESCE(n.kind, '')
            FROM edges e
            LEFT JOIN nodes n ON n.path = e.from_path AND n.file_id = e.from_file_id
            WHERE e.to_guid = $toGuid AND ($excludePath IS NULL OR e.from_path <> $excludePath)
            ORDER BY e.from_path, e.from_file_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$toGuid", toGuid);
        command.Parameters.AddWithValue("$excludePath", (object?)excludePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var results = new List<ReferencingObject>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ReferencingObject
            {
                Path = reader.GetString(0),
                FileId = reader.GetInt64(1),
                EdgeKind = reader.GetString(2),
                PropertyPath = reader.GetString(3),
                Name = reader.GetString(4) is { Length: > 0 } n ? n : null,
                Kind = reader.GetString(5) is { Length: > 0 } k ? k : null,
            });
        }

        return results;
    }

    /// <summary>References from OTHER assets — see <see cref="ReferencesTo"/> on why self-
    /// references are excluded.</summary>
    public int CountReferencesTo(string toGuid, string? excludePath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM edges WHERE to_guid = $toGuid AND ($excludePath IS NULL OR from_path <> $excludePath);";
        command.Parameters.AddWithValue("$toGuid", toGuid);
        command.Parameters.AddWithValue("$excludePath", (object?)excludePath ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// The files referencing this asset, one row each, ordered by how heavily they use it.
    /// See <see cref="ReferencingFile"/> for why this grouping is the useful shape, and
    /// <see cref="ReferencesTo"/> for why the asset's own path is excluded.
    /// </summary>
    public IReadOnlyList<ReferencingFile> ReferencingFiles(string toGuid, string? excludePath, int limit = 100)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT from_path,
                   COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT kind) AS kinds,
                   MIN(property_path) AS sample_via
            FROM edges
            WHERE to_guid = $toGuid AND ($excludePath IS NULL OR from_path <> $excludePath)
            GROUP BY from_path
            ORDER BY reference_count DESC, from_path
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$toGuid", toGuid);
        command.Parameters.AddWithValue("$excludePath", (object?)excludePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var results = new List<ReferencingFile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ReferencingFile
            {
                Path = reader.GetString(0),
                References = reader.GetInt32(1),
                Relationships = reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .OrderBy(k => k, StringComparer.Ordinal).ToList(),
                SampleVia = reader.GetString(3),
            });
        }

        return results;
    }

    /// <summary>How many distinct files reference this asset — the number that tells a caller
    /// whether the grouped list below it is complete.</summary>
    public int CountReferencingFiles(string toGuid, string? excludePath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT 1 FROM edges
                WHERE to_guid = $toGuid AND ($excludePath IS NULL OR from_path <> $excludePath)
                GROUP BY from_path);
            """;
        command.Parameters.AddWithValue("$toGuid", toGuid);
        command.Parameters.AddWithValue("$excludePath", (object?)excludePath ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Prefabs — never scenes — with at least one `references` edge to <paramref name="toGuid"/>.
    /// The narrower, component-focused sibling of <see cref="ReferencingFiles"/>: a scene's own
    /// MonoBehaviours reference scripts just as commonly as a prefab's do, so the `.prefab` filter
    /// runs in SQL, before LIMIT — filtering afterward in C# would let heavy scene usage crowd
    /// genuine prefab hits out of a truncated result. Same grouped shape as
    /// <see cref="ReferencingFiles"/> (path, count, kinds, a sample property), just scoped.
    /// </summary>
    public IReadOnlyList<ReferencingFile> PrefabsReferencing(string toGuid, int limit = 100)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT from_path,
                   COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT kind) AS kinds,
                   MIN(property_path) AS sample_via
            FROM edges
            WHERE to_guid = $toGuid AND kind = 'references' AND from_path LIKE '%.prefab'
            GROUP BY from_path
            ORDER BY reference_count DESC, from_path
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$toGuid", toGuid);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var results = new List<ReferencingFile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ReferencingFile
            {
                Path = reader.GetString(0),
                References = reader.GetInt32(1),
                Relationships = reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .OrderBy(k => k, StringComparer.Ordinal).ToList(),
                SampleVia = reader.GetString(3),
            });
        }

        return results;
    }

    public void UpsertFileState(IReadOnlyList<FileState> states)
    {
        if (states.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_state (path, mtime_utc, size) VALUES ($path, $mtime, $size)
            ON CONFLICT (path) DO UPDATE SET mtime_utc = excluded.mtime_utc, size = excluded.size;
            """;

        var path = command.Parameters.Add("$path", SqliteType.Text);
        var mtime = command.Parameters.Add("$mtime", SqliteType.Integer);
        var size = command.Parameters.Add("$size", SqliteType.Integer);

        foreach (var state in states)
        {
            path.Value = state.Path;
            mtime.Value = state.MTimeUtcMs;
            size.Value = state.Size;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Recorded state for every indexed file, keyed by project-relative path.</summary>
    public IReadOnlyDictionary<string, FileState> AllFileState()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path, mtime_utc, size FROM file_state;";

        var results = new Dictionary<string, FileState>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            results[path] = new FileState
            {
                Path = path,
                MTimeUtcMs = reader.GetInt64(1),
                Size = reader.GetInt64(2),
            };
        }

        return results;
    }

    /// <summary>
    /// Recently touched files, newest first, from <c>file_state</c> — recorded at index time, so
    /// this needs no rescan. <paramref name="sinceUtcMs"/> is an inclusive lower bound in Unix
    /// milliseconds; null means no bound.
    /// </summary>
    public IReadOnlyList<FileState> RecentlyChanged(long? sinceUtcMs, int limit = 50)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT path, mtime_utc, size FROM file_state
            WHERE $since IS NULL OR mtime_utc >= $since
            ORDER BY mtime_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$since", (object?)sinceUtcMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSearchFetch));

        var results = new List<FileState>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FileState
            {
                Path = reader.GetString(0),
                MTimeUtcMs = reader.GetInt64(1),
                Size = reader.GetInt64(2),
            });
        }

        return results;
    }

    public int TotalEdges()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edges;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    static List<GraphEdge> ReadEdges(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var results = new List<GraphEdge>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new GraphEdge
            {
                FromPath = reader.GetString(0),
                FromFileId = reader.GetInt64(1),
                ToGuid = reader.IsDBNull(2) ? null : reader.GetString(2),
                ToFileId = reader.GetInt64(3),
                Kind = reader.GetString(4),
                PropertyPath = reader.GetString(5),
            });
        }

        return results;
    }

    /// <summary>Number of nodes currently recorded for exactly this path. Callers use this
    /// after an upsert to learn how many distinct nodes actually survived — which can be fewer
    /// than the number of entries submitted, if two of them shared a unique key.</summary>
    public int CountNodesForPath(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM nodes WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Deletes every node under <paramref name="pathPrefix"/> (equal to it, or nested under it
    /// as "prefix/...") whose path is not in <paramref name="keepPaths"/> and does not fall
    /// under one of <paramref name="reservedSubPrefixes"/>. This is how a file deleted or
    /// renamed since the last index disappears from the graph: a scan's delete-then-insert only
    /// touches files actually encountered, so a vanished file is otherwise never revisited.
    /// Callers scope one call to exactly one scan root's prefix, so a root that failed to
    /// resolve this run — and so contributed no paths to <paramref name="keepPaths"/> — is never
    /// swept just because this run never got to visit it.
    /// </summary>
    /// <param name="reservedSubPrefixes">
    /// Prefixes owned by OTHER, more specific roots (e.g. "Packages/com.example.tools" is
    /// reserved when sweeping the generic "Packages" root). Prefixes nest: a package-specific
    /// root's path prefix is textually a child of the generic in-project "Packages" root's
    /// prefix. Without this exclusion, sweeping "Packages" after a package fails to resolve —
    /// which skips that package's OWN sweep entirely, exactly so its nodes survive — would
    /// still catch that package's nodes via the shorter, unrelated "Packages" prefix and delete
    /// them anyway, defeating the whole point of scoping the sweep per root.
    /// </param>
    public void SweepStaleNodes(string pathPrefix, IReadOnlySet<string> keepPaths,
        IReadOnlyCollection<string> reservedSubPrefixes,
        IReadOnlyCollection<string>? ownedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(pathPrefix);
        ArgumentNullException.ThrowIfNull(keepPaths);
        ArgumentNullException.ThrowIfNull(reservedSubPrefixes);

        using var select = _connection.CreateCommand();
        select.CommandText = """
            SELECT DISTINCT path FROM nodes
            WHERE path = $prefix OR path LIKE $childPattern ESCAPE '\';
            """;
        select.Parameters.AddWithValue("$prefix", pathPrefix);
        select.Parameters.AddWithValue("$childPattern", $"{EscapeLikeWildcards(pathPrefix)}/%");

        var stale = new List<string>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (keepPaths.Contains(path)) continue;
                if (reservedSubPrefixes.Any(reserved => IsUnderPrefix(path, reserved))) continue;

                // Only sweep file types this caller is responsible for. Several indexers share
                // one graph and one set of path prefixes: without this, the asset indexer's
                // sweep deletes every script node (and vice versa), because those paths are
                // legitimately absent from its own visited set.
                if (ownedExtensions is not null
                    && !ownedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                stale.Add(path);
            }
        }

        if (stale.Count == 0) return;

        using var transaction = _connection.BeginTransaction();

        // Edges as well as nodes. A deleted asset's references must go with it — leaving them
        // behind recreates the orphaned-node bug on a different table, and stale edges are worse
        // than stale nodes because "what references X" would keep naming a file that is gone.
        using var deleteNodes = _connection.CreateCommand();
        deleteNodes.Transaction = transaction;
        deleteNodes.CommandText = "DELETE FROM nodes WHERE path = $path;";
        var nodePath = deleteNodes.Parameters.Add("$path", SqliteType.Text);

        using var deleteEdges = _connection.CreateCommand();
        deleteEdges.Transaction = transaction;
        deleteEdges.CommandText = "DELETE FROM edges WHERE from_path = $path;";
        var edgePath = deleteEdges.Parameters.Add("$path", SqliteType.Text);

        foreach (var path in stale)
        {
            nodePath.Value = path;
            deleteNodes.ExecuteNonQuery();
            edgePath.Value = path;
            deleteEdges.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    static bool IsUnderPrefix(string path, string prefix) =>
        path.Equals(prefix, StringComparison.Ordinal)
        || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    /// <summary>
    /// The largest <c>limit</c> any MCP tool built on this database documents as its own maximum
    /// (graph_query, get_recently_changed, search_by_name, find_unset_references' project scope,
    /// ...) — what a CALLER may legitimately request. Deliberately kept separate from <see
    /// cref="MaxSearchFetch"/>, the ceiling actually applied to a raw SQL LIMIT: those are two
    /// different questions, and conflating them was Plan 15 Task 1's defect 2 (see <see
    /// cref="MaxSearchFetch"/>'s own doc comment for the mechanism). This constant is not read
    /// directly by any query — every one of the six methods below clamps against <see
    /// cref="MaxSearchFetch"/> instead, so that a caller AT this documented maximum still gets an
    /// honest answer.
    /// </summary>
    const int MaxSearchLimit = 500;

    /// <summary>
    /// Upper bound on the raw number of rows a single query is willing to fetch — deliberately ONE
    /// MORE than <see cref="MaxSearchLimit"/>, not equal to it. Unbounded results are a
    /// token-budget hazard for the agent consuming them, and SQLite treats a negative LIMIT as "no
    /// limit", so the parameter alone cannot enforce a cap — that much <see cref="MaxSearchLimit"/>
    /// already covered.
    ///
    /// The reason for the extra one: every tool built on this database (graph_query,
    /// get_recently_changed, ...) detects truncation honestly by requesting <c>limit + 1</c> rows
    /// and checking whether more came back than it asked for — see e.g. QueryTools.GraphQuery's own
    /// "One more than the cap" comment. At a caller's own documented maximum (=
    /// <see cref="MaxSearchLimit"/>), that sentinel request becomes <see cref="MaxSearchLimit"/> + 1
    /// rows. Before this constant existed as its own thing (Plan 15 Task 1 —
    /// docs/backlog/graph-correctness-defects.md defect 2), a single shared constant of 500 played
    /// both roles, so the raw SQL LIMIT clamp discarded that sentinel row at exactly a caller's own
    /// maximum: clamping 501 and clamping 500 both bottomed out at 500, indistinguishable, so a
    /// caller AT the documented maximum could never tell "exactly 500 real matches" from "500
    /// truncated out of thousands" — measured on a real project at exactly this boundary (1,232
    /// true matches, <c>limit=500</c> reported <c>truncated: false</c>).
    ///
    /// This constant is the fix, and nothing more than the fix: it only widens what the DATABASE is
    /// willing to fetch on the way there. What a tool hands back to its own caller is still bounded
    /// by the ORIGINAL limit via that tool's own <c>.Take(limit)</c> afterward (e.g.
    /// QueryTools.BuildResult) — so <c>totalReturned</c> never exceeds what was requested, only
    /// <c>truncated</c> becomes trustworthy at the boundary. Applied uniformly to every method below
    /// that shared the old single-constant clamp, not only the ones with a live caller today, so a
    /// future caller adopting the same <c>limit + 1</c> convention does not silently reintroduce
    /// this defect.
    /// </summary>
    const int MaxSearchFetch = MaxSearchLimit + 1;

    /// <summary>Escapes SQLite LIKE wildcards (<c>%</c>, <c>_</c>) and the escape character itself,
    /// so a literal search for e.g. "m_Health" cannot also match "mXHealth" — underscores are
    /// routine in Unity and C# identifiers. Backslash must be escaped first, or escaping % and _
    /// afterwards would double-escape them.</summary>
    static string EscapeLikeWildcards(string pattern) =>
        pattern.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Safety cap on <see cref="DistinctPaths"/>, well above any real project's file
    /// count — this is not the caller-facing "limit" asset_find honours (that filtering happens
    /// in <see cref="ProjectService.FindAssets"/>, in C#, after the extension-derived type filter
    /// is applied, which is not a SQL column here); it only bounds a pathological project.</summary>
    const int MaxDistinctPaths = 20_000;

    /// <summary>
    /// Every distinct file path the graph knows about, optionally narrowed to those starting with
    /// <paramref name="pathPrefix"/> — the backbone of asset_find. A path, not a node: one file
    /// (a prefab, a scene) can own many nodes, one per object inside it, so this is DISTINCT
    /// rather than a plain SELECT. Ordered for deterministic paging.
    /// </summary>
    public IReadOnlyList<string> DistinctPaths(string? pathPrefix)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT path FROM nodes
            WHERE $prefix IS NULL OR path LIKE $prefixPattern ESCAPE '\'
            ORDER BY path
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$prefix", (object?)pathPrefix ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefixPattern",
            pathPrefix is null ? DBNull.Value : $"{EscapeLikeWildcards(pathPrefix)}%");
        command.Parameters.AddWithValue("$limit", MaxDistinctPaths);

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>
    /// Every distinct file path recorded in <c>file_state</c>, optionally narrowed to those
    /// starting with <paramref name="pathPrefix"/> - the file-identity backbone graph_query's
    /// <c>fileType</c> filter needs (Plan 10 Task 6) to classify Prefab/Scene/ScriptableObject,
    /// which <see cref="DistinctPaths"/> (backed by <c>nodes</c>) structurally cannot: a scene or
    /// prefab is many per-object nodes with no single summarising one, and a ScriptableObject
    /// instance shares MonoBehaviour's generic kind with an ordinary component - see QueryTools'
    /// own class doc comment for the full story. <c>file_state</c> has exactly ONE row per file
    /// (see <see cref="UpsertFileState"/>'s own ON CONFLICT), recorded at index time independent
    /// of how many - if any - graph nodes that file produced, so this needs no DISTINCT and is
    /// strictly at least as complete as reading paths back off <c>nodes</c> would be. Same
    /// LIKE-escaping and paging shape as <see cref="DistinctPaths"/> throughout.
    /// </summary>
    public IReadOnlyList<string> DistinctFileStatePaths(string? pathPrefix)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT path FROM file_state
            WHERE $prefix IS NULL OR path LIKE $prefixPattern ESCAPE '\'
            ORDER BY path
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$prefix", (object?)pathPrefix ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefixPattern",
            pathPrefix is null ? DBNull.Value : $"{EscapeLikeWildcards(pathPrefix)}%");
        command.Parameters.AddWithValue("$limit", MaxDistinctPaths);

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>Case-insensitive substring search. <paramref name="limit"/> caps the result set:
    /// unbounded results are a token-budget hazard for the agent consuming them.</summary>
    public IReadOnlyList<GraphNode> SearchByName(string pattern, string? kind = null, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT kind, name, path, namespace, line, guid, file_id
            FROM nodes
            WHERE name_lower LIKE $pattern ESCAPE '\'
              AND ($kind IS NULL OR kind = $kind)
            ORDER BY name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikeWildcards(pattern.ToLowerInvariant())}%");
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSearchFetch));

        var results = new List<GraphNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new GraphNode
            {
                Kind = reader.GetString(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                Namespace = reader.IsDBNull(3) ? null : reader.GetString(3),
                Line = reader.GetInt32(4),
                Guid = reader.IsDBNull(5) ? null : reader.GetString(5),
                FileId = reader.GetInt64(6),
            });
        }

        return results;
    }

    /// <summary>
    /// Class-kind script nodes with no incoming `references` edge to their GUID — candidates for
    /// dead code. Restricted to <c>kind = 'Class'</c>: interfaces, structs, enums and records are
    /// never the target of a Unity m_Script reference, so including them would report every one
    /// of them as "orphaned" regardless of actual usage — a bigger honesty problem than the gap
    /// below.
    ///
    /// HONEST SUPERSET, not a confirmed dead-code list. A class referenced only from C# —
    /// <c>AddComponent&lt;T&gt;()</c>, <c>new</c>, a static utility — has no edge pointing at it
    /// either, because code-level references are not tracked as edges yet: today's edge kinds are
    /// exactly <c>corresponds_to</c>, <c>instance_of</c>, <c>references</c>, all sourced from
    /// Unity YAML, none from C#. There is no way to tell the two apart from the current edge set,
    /// so this returns the superset rather than guessing — callers must treat a result as "worth
    /// checking", not "confirmed unused". See find_orphan_scripts' [Description] for the
    /// caller-facing version of this same caveat.
    /// </summary>
    public IReadOnlyList<GraphNode> OrphanScripts(int limit = 100)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT kind, name, path, namespace, line, guid, file_id
            FROM nodes n
            WHERE kind = 'Class' AND guid IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM edges e WHERE e.to_guid = n.guid AND e.kind = 'references'
              )
            ORDER BY path, name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSearchFetch));

        var results = new List<GraphNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new GraphNode
            {
                Kind = reader.GetString(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                Namespace = reader.IsDBNull(3) ? null : reader.GetString(3),
                Line = reader.GetInt32(4),
                Guid = reader.IsDBNull(5) ? null : reader.GetString(5),
                FileId = reader.GetInt64(6),
            });
        }

        return results;
    }

    /// <summary>
    /// Prefab/scene files with a `references` edge to a script whose name matches <paramref
    /// name="namePattern"/> (case-insensitive substring) — the fuzzy, cross-file-kind sibling of
    /// <see cref="PrefabsReferencing"/>, which needs an exact script GUID and only ever looks at
    /// prefabs. Answers "what uses anything named *Health*" without knowing the exact script
    /// first, across both prefabs and scenes. One row per (file, script) pair — a file with
    /// several components all pointing at the same matching script still reports once, via
    /// SELECT DISTINCT, the same truncation-honesty reasoning as everywhere else LIKE is used
    /// (see EscapeLikeWildcards).
    /// </summary>
    public IReadOnlyList<ComponentUsage> ComponentsUsingPattern(string namePattern, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(namePattern);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT e.from_path, sn.name, sn.path
            FROM edges e
            JOIN nodes sn ON sn.guid = e.to_guid AND sn.kind = 'Class'
            WHERE e.kind = 'references' AND sn.name_lower LIKE $pattern ESCAPE '\'
            ORDER BY e.from_path, sn.name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikeWildcards(namePattern.ToLowerInvariant())}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSearchFetch));

        var results = new List<ComponentUsage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ComponentUsage
            {
                ComponentPath = reader.GetString(0),
                ScriptName = reader.GetString(1),
                ScriptPath = reader.GetString(2),
            });
        }

        return results;
    }

    /// <summary>
    /// Components anywhere in the project whose resolved type name matches <paramref
    /// name="typeNamePattern"/> (case-insensitive substring) — the project-wide search behind
    /// component_find. Builtin Unity component kinds (e.g. "Rigidbody") are matched directly
    /// against a node's own <c>kind</c>; a MonoBehaviour is matched by resolving through its
    /// <c>m_Script</c> reference to the owning script's class name, the same join
    /// <see cref="ComponentsUsingPattern"/> uses. GameObject and PrefabInstance are excluded from
    /// the direct-kind branch — they are containers, not components, and a GameObject or
    /// PrefabInstance happening to be NAMED like the pattern must not leak in (the asset indexer
    /// names a component node after its own kind, since a component has no name of its own — only
    /// a GameObject or PrefabInstance node's <c>name</c> is user-chosen). The bare "MonoBehaviour"
    /// kind is excluded too, since it names
    /// nothing a caller could have meant — the script-name branch is what a custom component
    /// actually resolves through. This is what gives component_get_all / component_get_property /
    /// component_list_properties their (path, fileId) address in the first place, which is why it
    /// is graph-served rather than read-through: it compares across every file in the project, not
    /// one the caller already named.
    /// </summary>
    public IReadOnlyList<ComponentMatch> ComponentsMatching(string typeNamePattern, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(typeNamePattern);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT path, file_id, kind AS type_name
            FROM nodes
            WHERE kind NOT IN ('GameObject', 'PrefabInstance', 'MonoBehaviour')
              AND LOWER(kind) LIKE $pattern ESCAPE '\'
            UNION ALL
            SELECT e.from_path AS path, e.from_file_id AS file_id, sn.name AS type_name
            FROM edges e
            JOIN nodes sn ON sn.guid = e.to_guid AND sn.kind = 'Class'
            WHERE e.kind = 'references' AND e.property_path = 'm_Script'
              AND LOWER(sn.name) LIKE $pattern ESCAPE '\'
            ORDER BY path, file_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikeWildcards(typeNamePattern.ToLowerInvariant())}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSearchFetch));

        var results = new List<ComponentMatch>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ComponentMatch
            {
                Path = reader.GetString(0),
                FileId = reader.GetInt64(1),
                TypeName = reader.GetString(2),
            });
        }

        return results;
    }

    public IReadOnlyDictionary<string, int> CountByKind()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT kind, COUNT(*) FROM nodes GROUP BY kind ORDER BY kind;";

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    /// <summary>Per-kind node counts scoped to one file — the same breakdown as
    /// <see cref="CountByKind"/>, narrowed to a single scene or prefab. What
    /// <c>get_scene_summary</c>'s component breakdown is built from.</summary>
    public IReadOnlyDictionary<string, int> CountByKindForPath(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT kind, COUNT(*) FROM nodes WHERE path = $path GROUP BY kind ORDER BY kind;";
        command.Parameters.AddWithValue("$path", path);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    /// <summary>
    /// Top-level GameObjects in a scene or prefab: Transforms (or RectTransforms, for UI) with no
    /// <c>m_Father</c> edge. Unity's own null reference — {fileID: 0} with no guid — is dropped by
    /// the reader rather than recorded (see UnityYamlReader.ToReference / AssetIndexer), so a root
    /// object's Transform has no m_Father edge AT ALL. Absence is the root signal here, not a
    /// sentinel value to compare against.
    /// </summary>
    public int CountRootGameObjects(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM nodes n
            WHERE n.path = $path AND n.kind IN ('Transform', 'RectTransform')
              AND NOT EXISTS (
                  SELECT 1 FROM edges e
                  WHERE e.from_path = n.path AND e.from_file_id = n.file_id AND e.property_path = 'm_Father'
              );
            """;
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int TotalNodes()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM nodes;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>The fixed tail every UnityEvent persistent-call target's property_path ends with,
    /// regardless of the event field's own name - see <see cref="FindUnityEvents"/>.</summary>
    const string PersistentCallTargetSuffix = ".m_PersistentCalls.m_Calls.m_Target";

    /// <summary>
    /// UnityEvent fields anywhere in the project with at least one WIRED persistent-call listener
    /// - grouped by (file, component, event field), with a listener count, the same "grouped,
    /// most-used-first" shape as <see cref="ReferencingFiles"/>. Graph-served, unlike its sibling
    /// tools reference_get / reference_find_unset / event_list_listeners: it compares across every
    /// file in the project rather than reading one the caller already named.
    ///
    /// HONEST LIMITATION #1, same shape as <see cref="OrphanScripts"/>'s: this can only see what
    /// Unity's own reader turns into a graph edge at all, and <see cref="Unity.UnityYamlReader"/>
    /// drops a null reference before it ever becomes one (see its own ToReference). A UnityEvent
    /// with zero listeners, or whose every listener has an unset target, leaves no trace in the
    /// edge table whatsoever - not "zero matches", genuinely invisible - so this is a superset of
    /// "wired" events, never a census of every UnityEvent field in the project. event_list_listeners
    /// (read-through, one component at a time) is what sees the full picture, unset targets
    /// included, because it reads the file directly rather than the graph.
    ///
    /// HONEST LIMITATION #2: <see cref="UnityEventHit.ListenerCount"/> counts DISTINCT edges, not
    /// literal persistent-call entries. Two listeners on the same event field that target the SAME
    /// object collapse into exactly one edge, because the edges table's own uniqueness key is
    /// (from_path, from_file_id, to_guid, to_file_id, property_path) - and property_path is the
    /// event field's fixed suffix regardless of which call index produced it, so two calls to one
    /// target are indistinguishable at the edge level no matter what method each one invokes. A
    /// count here is therefore a LOWER BOUND on the true listener count, exact only when every
    /// listener on a field targets a different object - confirmed real, not a hypothetical: caught
    /// by GraphToolsTests' own event_find_all fixture undercounting 2 as 1 before this was known.
    ///
    /// Matched by property_path's fixed suffix (<see cref="PersistentCallTargetSuffix"/>), which
    /// AssetIndexer never treats specially - it is just wherever a plain `references` edge happens
    /// to land when the referenced field is a UnityEvent's m_Target, exactly like any other
    /// reference. The event field's own name is recovered by stripping that fixed suffix back off.
    /// </summary>
    public IReadOnlyList<UnityEventHit> FindUnityEvents(int limit = 100)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT from_path, from_file_id, property_path, COUNT(*) AS listener_count
            FROM edges
            WHERE kind = 'references' AND property_path LIKE $suffixPattern ESCAPE '\'
            GROUP BY from_path, from_file_id, property_path
            ORDER BY from_path, from_file_id, property_path
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$suffixPattern", $"%{EscapeLikeWildcards(PersistentCallTargetSuffix)}");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSearchFetch));

        var results = new List<UnityEventHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var propertyPath = reader.GetString(2);
            results.Add(new UnityEventHit
            {
                Path = reader.GetString(0),
                FileId = reader.GetInt64(1),
                EventField = propertyPath[..^PersistentCallTargetSuffix.Length],
                ListenerCount = reader.GetInt32(3),
            });
        }

        return results;
    }

    /// <summary>
    /// The complete, fixed set of edge kinds this graph ever writes - "references", "instance_of",
    /// and "corresponds_to", all sourced from Unity YAML (see <see cref="OrphanScripts"/>'s own doc
    /// comment for the same fact, stated from the dead-code-detection angle). Unlike node kinds -
    /// an open, DATA-dependent vocabulary that varies per project, validated by asking the graph
    /// itself what is actually there (see <see cref="ProjectService.KnownNodeKinds"/>) - edge kinds
    /// are a closed, CODE-dependent vocabulary fixed at build time. This is the one place that list
    /// is written down, so QueryTools' 'edgeKind' validation (the F7 fix's sibling for edgeKind) can
    /// check against it directly and eagerly, with no database round trip, and with no second
    /// hand-maintained copy that could silently drift from this one.
    /// </summary>
    public static readonly string[] KnownEdgeKinds = ["references", "instance_of", "corresponds_to"];

    /// <summary>
    /// The structured filter behind query_graph - kind, name pattern, path prefix, and edge
    /// relationship, every value a bound SQLite parameter, AND-combined when more than one is
    /// given. This is deliberately the ONLY entry point in the whole port that accepts
    /// caller/model-authored query input; see QueryTools' class doc comment for why that input is a
    /// fixed set of scalars rather than a query string, and <see cref="EscapeLikeWildcards"/> for
    /// why <paramref name="namePattern"/>/<paramref name="pathPrefix"/> get the identical treatment
    /// <see cref="SearchByName"/> already established - the same LIKE-metacharacter bug (an
    /// unescaped "_" over-matching) would otherwise reappear here independently.
    ///
    /// The edge-relationship filter restricts to nodes that are one endpoint of at least one
    /// <paramref name="edgeKind"/> edge, in <paramref name="edgeDirection"/>. "outgoing" (the
    /// default) matches the node's own recorded identity precisely - from_path AND from_file_id -
    /// since both come from the exact same source-object walk that produced the node itself.
    /// "incoming" deliberately matches by to_guid ALONE, never to_file_id, mirroring
    /// <see cref="ReferencesTo"/>/<see cref="EdgesTo"/>'s existing asset-level (not object-level)
    /// granularity - and not by accident: a cross-asset reference's raw fileID is one of Unity's
    /// own fixed, asset-kind-specific constants (11500000 for a script's main object, 100100000 for
    /// a prefab's root, ...), which does not in general equal how THIS graph records that target's
    /// own node file_id - a script node's is always 0 (see <see cref="GraphNode.FileId"/>'s own doc
    /// comment) regardless of Unity's 11500000. Requiring both to match, as an earlier version of
    /// this method did, silently failed to find a script ever referenced by anything - caught by
    /// QueryToolsTests' real-indexer-backed end-to-end test, not by the synthetic, hand-picked
    /// to_file_id in this file's own DB-level test.
    /// Answered as an EXISTS subquery either way, so a node with several matching edges is still
    /// reported once.
    ///
    /// <para><b>Plan 10 Task 4 - three additive parameters behind graph_query.</b> All three default
    /// to "no effect", so every existing caller (query_graph's own MCP tool keeps calling this with
    /// them omitted) is byte-for-byte unaffected. Each earns its place by being the ONE thing
    /// missing to reach one of graph_query's absorbed tools - see QueryTools' own class doc comment
    /// for the full mapping of old tool to filter combination.</para>
    /// <para><b>Plan 10 Task 6 - <paramref name="kindPattern"/>, a fourth additive parameter,</b>
    /// added during the capability audit to close a real gap the four above did not: component_find's
    /// BUILTIN-kind branch matched via <c>LOWER(kind) LIKE pattern</c> (see
    /// <see cref="ComponentsMatching"/> - e.g. <c>typeNamePattern: "Collider"</c> matching
    /// "BoxCollider", "SphereCollider", "CapsuleCollider" alike), a genuinely different query than
    /// <paramref name="kind"/>'s exact match, which cannot express it (<c>kind: "Collider"</c> matches
    /// nothing, since no node's kind is literally "Collider"). Same LIKE-escaping and
    /// case-insensitivity as <paramref name="namePattern"/>, applied to <c>n.kind</c> instead of
    /// <c>n.name</c>. Also defaults to "no effect", so query_graph and every existing graph_query
    /// caller omitting it are unaffected.</para>
    /// <para><paramref name="edgeTargetGuid"/> - restricts the SAME edge-exists check to edges whose
    /// target is this ONE asset (already resolved to a guid by the caller - see
    /// ProjectService.QueryGraph, which does that resolution from a path, the same way
    /// find_prefabs_with_component's scriptPath already did). Reaches find_prefabs_with_component's
    /// join: "nodes referencing THIS script".</para>
    /// <para><paramref name="edgeTargetNamePattern"/> - restricts to edges whose target's OWN name
    /// matches this pattern (case-insensitive substring, the identical LIKE-escaping treatment as
    /// <paramref name="namePattern"/> - see <see cref="EscapeLikeWildcards"/>), checked via a nested
    /// EXISTS against <c>nodes</c> rather than a JOIN: a guid can own several nodes (e.g. a prefab's
    /// many GameObjects all share the file's guid), and existence is all this needs, not a
    /// row-multiplying join. Reaches find_components_using_pattern's join ("nodes referencing any
    /// script whose name matches") and component_find's MonoBehaviour-resolution branch (combined
    /// with <c>kind = "MonoBehaviour"</c> on the outer filter). <b>Deliberately does not check the
    /// target's own kind</b> - see <paramref name="edgeTargetKind"/>, immediately below, for why
    /// that is a genuine correctness gap on its own and not just a documentation omission.</para>
    /// <para><paramref name="edgeTargetKind"/> - <b>Plan 10 Task 6, capability-audit correctness
    /// fix.</b> The OLD SQL this graph_query filter replaced - <see cref="ComponentsUsingPattern"/>
    /// (find_components_using_pattern) and <see cref="ComponentsMatching"/>'s MonoBehaviour branch
    /// (component_find) - both joined with <c>AND sn.kind = 'Class'</c>, so the thing a name match
    /// resolved to was GUARANTEED to be an actual script. <paramref name="edgeTargetNamePattern"/>
    /// alone dropped that guarantee: a <c>references</c> edge pointing at a same-named Material,
    /// ScriptableObject, or any other asset produces a false positive INDISTINGUISHABLE from a real
    /// hit, because a hit reports the matched SOURCE node's own kind/name, never the resolved
    /// target's (see this method's own class-level framing in QueryTools' doc comment) - there is
    /// nothing in the response a caller could filter on after the fact. This is a correctness gap,
    /// not merely the narrower truncation-safety difference <paramref name="edgeTargetGuid"/> has
    /// relative to the old per-kind-scoped <c>PrefabsReferencing</c> join (that one degrades only
    /// under a hit-the-limit race and is mitigable by raising <c>limit</c> or post-filtering by
    /// path; this one is wrong regardless of limit and has no client-side mitigation at all).
    /// <paramref name="edgeTargetKind"/> restores the guarantee by construction: joined into the
    /// SAME nested EXISTS <paramref name="edgeTargetNamePattern"/> already uses (added as a second,
    /// independent condition on <c>tn.kind</c> - both apply when both are given, either alone when
    /// only one is), so <c>edgeTargetKind: "Class"</c> reproduces the old join exactly. Exact match
    /// via <c>tn.kind = $edgeTargetKind</c>, the same bound-parameter treatment as <paramref
    /// name="kind"/> on the outer filter (no LIKE, no escaping - a node kind is a fixed token, not
    /// free text) - never hard-coded to <c>'Class'</c>, since graph_query is a GENERAL filter and a
    /// caller may legitimately want edges to, say, a Material by name. Defaults to "no effect" like
    /// every other Task 4/6 addition, so every existing caller omitting it is byte-for-byte
    /// unaffected.</para>
    /// <para><paramref name="edgeAbsent"/> - flips the check from "at least one matching edge" to
    /// "no matching edge", reaching find_orphan_scripts' join. For <paramref name="edgeDirection"/>
    /// "incoming" specifically, a node with a NULL guid is additionally excluded even though it
    /// would otherwise vacuously satisfy "no incoming edge" (a NULL guid can never equal any edge's
    /// to_guid, so the positive EXISTS check already excludes it naturally, but negating that same
    /// check would flip "can never be referenced" into a false-positive "confirmed unreferenced") -
    /// the identical <c>guid IS NOT NULL</c> reasoning <see cref="OrphanScripts"/> already documents
    /// for its own hand-written query.</para>
    /// <para>Every target filter (<paramref name="edgeTargetGuid"/>, <paramref
    /// name="edgeTargetNamePattern"/>, <paramref name="edgeTargetKind"/>) is meaningless without
    /// <paramref name="edgeKind"/> (there is no edge-exists check to narrow), and <paramref
    /// name="edgeKind"/> IS NULL already skips the whole clause - see QueryTools' own validation for
    /// why a caller supplying one WITHOUT edgeKind is refused outright rather than silently doing
    /// nothing.</para>
    /// </summary>
    public IReadOnlyList<GraphNode> QueryGraph(string? kind, string? namePattern, string? pathPrefix,
        string? edgeKind, string edgeDirection = "outgoing", int limit = 100,
        string? edgeTargetGuid = null, string? edgeTargetNamePattern = null, bool edgeAbsent = false,
        string? kindPattern = null, string? edgeTargetKind = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT n.kind, n.name, n.path, n.namespace, n.line, n.guid, n.file_id
            FROM nodes n
            WHERE ($kind IS NULL OR n.kind = $kind)
              AND ($kindPattern IS NULL OR LOWER(n.kind) LIKE $kindPattern ESCAPE '\')
              AND ($namePattern IS NULL OR n.name_lower LIKE $namePattern ESCAPE '\')
              AND ($pathPrefixPattern IS NULL OR n.path LIKE $pathPrefixPattern ESCAPE '\')
              AND ($edgeKind IS NULL OR
                CASE WHEN $edgeAbsent THEN
                  ($direction <> 'incoming' OR n.guid IS NOT NULL) AND NOT EXISTS (
                    SELECT 1 FROM edges e
                    WHERE e.kind = $edgeKind
                      AND (($direction = 'outgoing' AND e.from_path = n.path AND e.from_file_id = n.file_id)
                        OR ($direction = 'incoming' AND e.to_guid = n.guid))
                      AND ($edgeTargetGuid IS NULL OR e.to_guid = $edgeTargetGuid)
                      AND (($edgeTargetNamePattern IS NULL AND $edgeTargetKind IS NULL) OR EXISTS (
                            SELECT 1 FROM nodes tn WHERE tn.guid = e.to_guid
                              AND ($edgeTargetNamePattern IS NULL OR tn.name_lower LIKE $edgeTargetNamePattern ESCAPE '\')
                              AND ($edgeTargetKind IS NULL OR tn.kind = $edgeTargetKind)
                          ))
                  )
                ELSE EXISTS (
                    SELECT 1 FROM edges e
                    WHERE e.kind = $edgeKind
                      AND (($direction = 'outgoing' AND e.from_path = n.path AND e.from_file_id = n.file_id)
                        OR ($direction = 'incoming' AND e.to_guid = n.guid))
                      AND ($edgeTargetGuid IS NULL OR e.to_guid = $edgeTargetGuid)
                      AND (($edgeTargetNamePattern IS NULL AND $edgeTargetKind IS NULL) OR EXISTS (
                            SELECT 1 FROM nodes tn WHERE tn.guid = e.to_guid
                              AND ($edgeTargetNamePattern IS NULL OR tn.name_lower LIKE $edgeTargetNamePattern ESCAPE '\')
                              AND ($edgeTargetKind IS NULL OR tn.kind = $edgeTargetKind)
                          ))
                  )
                END)
            ORDER BY n.path, n.name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$kindPattern",
            kindPattern is null ? DBNull.Value : $"%{EscapeLikeWildcards(kindPattern.ToLowerInvariant())}%");
        command.Parameters.AddWithValue("$namePattern",
            namePattern is null ? DBNull.Value : $"%{EscapeLikeWildcards(namePattern.ToLowerInvariant())}%");
        command.Parameters.AddWithValue("$pathPrefixPattern",
            pathPrefix is null ? DBNull.Value : $"{EscapeLikeWildcards(pathPrefix)}%");
        command.Parameters.AddWithValue("$edgeKind", (object?)edgeKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$direction", edgeDirection);
        command.Parameters.AddWithValue("$edgeTargetGuid", (object?)edgeTargetGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$edgeTargetNamePattern",
            edgeTargetNamePattern is null ? DBNull.Value : $"%{EscapeLikeWildcards(edgeTargetNamePattern.ToLowerInvariant())}%");
        command.Parameters.AddWithValue("$edgeTargetKind", (object?)edgeTargetKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$edgeAbsent", edgeAbsent ? 1 : 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxSearchFetch));

        var results = new List<GraphNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new GraphNode
            {
                Kind = reader.GetString(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                Namespace = reader.IsDBNull(3) ? null : reader.GetString(3),
                Line = reader.GetInt32(4),
                Guid = reader.IsDBNull(5) ? null : reader.GetString(5),
                FileId = reader.GetInt64(6),
            });
        }

        return results;
    }

    public void Dispose() => _connection.Dispose();
}
