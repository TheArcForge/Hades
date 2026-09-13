using Microsoft.Data.Sqlite;

namespace Hades.Core.Graph;

/// <summary>
/// The graph is derived data: it can always be deleted and rebuilt from the project.
/// Migration is therefore "if the version differs, drop and recreate" — no incremental
/// migration machinery is warranted for a store with no authored content in it.
///
/// <see cref="Apply"/> uses double-checked locking. The overwhelmingly common case is
/// "no migration needed" — every <see cref="Graph.GraphDatabase.Open"/> call reaches this,
/// not just the rare schema bump — so the first check runs with no transaction at all and
/// takes no lock. Taking <c>BEGIN IMMEDIATE</c> unconditionally, before even reading the
/// version, was measured to starve readers for 30+ seconds under continuous write load from
/// another connection, because SQLite's busy handler does not queue fairly against a writer
/// that keeps renewing its lock. Only when the unlocked check says migration looks necessary
/// does this take the write lock — and then it re-checks <em>inside</em> the transaction
/// before touching anything, because another connection may have already migrated while this
/// one waited for the lock. That inner re-check is what keeps the whole thing atomic; skipping
/// it would let two connections both "decide" to migrate and race, which is the bug this
/// design replaced. <c>PRAGMA user_version</c> is a transactional header write, so on the
/// locked path it commits or rolls back atomically with the DDL — a process killed
/// mid-migration cannot leave behind a database whose version stamp claims "current" while the
/// table is missing.
/// </summary>
public static class GraphSchema
{
    // v2: namespace joined (path, kind, name) as node identity, so two same-named types in
    // different namespaces no longer collide onto one row.
    // v3: nodes gained guid/file_id, and an edges table arrived, so Unity asset references are
    // first-class.
    // v4: a file_state table, so indexing can be incremental — without a record of what each file
    // looked like when indexed, every edit costs a full reindex. The graph is derived data, so a bump alone is the migration — see
    // NeedsMigration / Apply's drop-and-recreate below.
    // v5 (DESIGN-07): five nodes columns (asset_subtype/width/height/max_size/flags) for binary
    // asset introspection, populated only for Unity.ImportedAssetKind rows; two file_state
    // columns (meta_mtime_utc, meta_size) so a .meta-only edit is finally visible to incremental
    // indexing (I4) without disturbing mtime_utc's other job as get_recently_changed's sort key;
    // and the ".anim" extension moves from ImportedAssetKind (binary, meta-only) to
    // AssetIndexer (it is plain Unity YAML, already readable by UnityYamlReader — see
    // ImportedAssetKind's own updated doc comment). One bump carries all three, per DESIGN-07 §13.3.
    public const int Version = 5;

    public static void Apply(SqliteConnection connection)
    {
        // Fast path: no lock. Checking costs two cheap autocommit reads; skipping it would
        // cost every caller a write-lock acquisition for the common case where nothing here
        // needs to change.
        if (!NeedsMigration(connection, transaction: null)) return;

        // Slow path: migration looked necessary from the unlocked read. Take the write lock,
        // then re-check before acting — see the class doc comment for why the re-check is
        // required, not optional.
        using var transaction = connection.BeginTransaction(deferred: false);

        if (!NeedsMigration(connection, transaction))
        {
            transaction.Commit();
            return;
        }

        Execute(connection, transaction, "DROP TABLE IF EXISTS nodes;");
        Execute(connection, transaction, "DROP TABLE IF EXISTS edges;");
        Execute(connection, transaction, "DROP TABLE IF EXISTS file_state;");
        Execute(connection, transaction, """
            CREATE TABLE nodes (
                id            INTEGER PRIMARY KEY,
                kind          TEXT NOT NULL,
                name          TEXT NOT NULL,
                name_lower    TEXT NOT NULL,
                path          TEXT NOT NULL,
                namespace     TEXT,
                line          INTEGER NOT NULL DEFAULT 0,
                guid          TEXT,
                file_id       INTEGER NOT NULL DEFAULT 0,
                asset_subtype TEXT,
                asset_width   INTEGER,
                asset_height  INTEGER,
                asset_max_size INTEGER,
                asset_flags   INTEGER
            );
            """);
        // An expression index, not a table-level UNIQUE(...,namespace): SQLite treats every NULL
        // as distinct from every other NULL in a unique index, and namespace is NULL for every
        // global-namespace type, so a plain 4-column UNIQUE would silently stop deduplicating the
        // global namespace. COALESCE folds NULL to '' so identity still collapses correctly there
        // — the ON CONFLICT target in GraphDatabase.UpsertNodes must name this same expression.
        Execute(connection, transaction,
            "CREATE UNIQUE INDEX idx_nodes_identity ON nodes (path, kind, name, COALESCE(namespace, ''), file_id);");
        Execute(connection, transaction, "CREATE INDEX idx_nodes_name_lower ON nodes (name_lower);");
        Execute(connection, transaction, "CREATE INDEX idx_nodes_kind ON nodes (kind);");
        Execute(connection, transaction, "CREATE INDEX idx_nodes_path ON nodes (path);");
        Execute(connection, transaction, "CREATE INDEX idx_nodes_guid ON nodes (guid);");
        // DESIGN-07 §7.2 — backs graph_query's minWidth/minHeight texture-dimension filter. `kind`
        // leads so a texture-scoped scan never touches every other kind's rows.
        Execute(connection, transaction,
            "CREATE INDEX idx_nodes_asset_dims ON nodes (kind, asset_width, asset_height);");

        Execute(connection, transaction, """
            CREATE TABLE edges (
                id            INTEGER PRIMARY KEY,
                from_path     TEXT NOT NULL,
                from_file_id  INTEGER NOT NULL,
                to_guid       TEXT,
                to_file_id    INTEGER NOT NULL,
                kind          TEXT NOT NULL,
                property_path TEXT NOT NULL
            );
            """);
        // COALESCE, not a plain column list: SQLite treats every NULL as distinct in a unique
        // index, so UNIQUE(..., to_guid, ...) would silently stop deduplicating every LOCAL
        // reference — the same trap hit on nodes.namespace at v2.
        Execute(connection, transaction, """
            CREATE UNIQUE INDEX idx_edges_identity
                ON edges (from_path, from_file_id, COALESCE(to_guid, ''), to_file_id, property_path);
            """);
        Execute(connection, transaction, "CREATE INDEX idx_edges_from ON edges (from_path, from_file_id);");
        Execute(connection, transaction, "CREATE INDEX idx_edges_to ON edges (to_guid);");

        // size alongside mtime deliberately: mtime has one-second granularity on some
        // filesystems, so two edits within the same second can be indistinguishable. A size
        // change catches most of those. It is a cheap hedge, not a guarantee — content hashing
        // is the guarantee, and it is not worth 8,000 file reads per sweep.
        // meta_mtime_utc/meta_size (DESIGN-07 §7.3, the I4 fix): the sibling .meta's own stat,
        // tracked SEPARATELY from mtime_utc/size above — mtime_utc is ALSO get_recently_changed's
        // sort key, and folding a .meta timestamp into it would move an asset's reported "last
        // changed" time independent of its own content ever changing (the exact defect the first,
        // reverted attempt at this fix produced — see IncrementalIndexTests' historical note).
        // NULL for a path outside the scope ProjectSweeper stats a sibling .meta for, and for one
        // with no .meta yet.
        Execute(connection, transaction, """
            CREATE TABLE file_state (
                path           TEXT PRIMARY KEY,
                mtime_utc      INTEGER NOT NULL,
                size           INTEGER NOT NULL,
                meta_mtime_utc INTEGER,
                meta_size      INTEGER
            );
            """);
        Execute(connection, transaction, $"PRAGMA user_version = {Version};");

        transaction.Commit();
    }

    /// <summary>
    /// True when the version stamp differs from <see cref="Version"/>, or when it already
    /// matches but the <c>nodes</c> table is missing — the signature of a process killed
    /// between the old DROP and the version write. Either condition re-runs the migration,
    /// so an already-bricked database self-heals the next time it is opened.
    /// <paramref name="transaction"/> is null on the unlocked fast-path check.
    /// </summary>
    static bool NeedsMigration(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(versionCommand.ExecuteScalar()) != Version) return true;
        }

        using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('nodes','edges','file_state');";
        return Convert.ToInt32(tableCommand.ExecuteScalar()) < 3;
    }

    public static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
