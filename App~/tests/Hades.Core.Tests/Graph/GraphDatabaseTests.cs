using Hades.Core.Graph;
using Microsoft.Data.Sqlite;

namespace Hades.Core.Tests.Graph;

public class GraphDatabaseTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    GraphDatabase Open()
    {
        Directory.CreateDirectory(_dir);
        return GraphDatabase.Open(Path.Combine(_dir, "graph.db"));
    }

    static GraphNode Node(string name, string kind = "Class", string path = "Assets/A.cs") => new()
    {
        Kind = kind,
        Name = name,
        Path = path,
    };

    [Fact]
    public void Open_CreatesSchema()
    {
        using var db = Open();

        Assert.Equal(GraphSchema.Version, db.SchemaVersion);
    }

    [Fact]
    public void PathForGuid_DuplicatedGuid_AlwaysResolvesToTheLexicographicallyFirstPath()
    {
        // A copy-pasted .meta gives two files one GUID - degraded input, but the answer must be
        // the same one every call, not whichever row SQLite happens to visit first.
        using var db = Open();
        db.UpsertNodes([
            Node("Zeta", kind: "Prefab", path: "Assets/Zeta.prefab") with { Guid = "feedfacefeedfacefeedfacefeedface" },
            Node("Alpha", kind: "Prefab", path: "Assets/Alpha.prefab") with { Guid = "feedfacefeedfacefeedfacefeedface" },
        ]);

        Assert.Equal("Assets/Alpha.prefab", db.PathForGuid("feedfacefeedfacefeedfacefeedface"));
    }

    [Fact]
    public void UpsertNodes_ThenSearchByName_FindsIt()
    {
        using var db = Open();
        db.UpsertNodes([Node("PlayerController")]);

        var results = db.SearchByName("player");

        Assert.Single(results);
        Assert.Equal("PlayerController", results[0].Name);
    }

    [Fact]
    public void SearchByName_IsCaseInsensitiveSubstring()
    {
        using var db = Open();
        db.UpsertNodes([Node("PlayerController"), Node("EnemyController"), Node("Inventory")]);

        Assert.Equal(2, db.SearchByName("CONTROLLER").Count);
    }

    [Fact]
    public void SearchByName_FiltersByKind()
    {
        using var db = Open();
        db.UpsertNodes([Node("PlayerController"), Node("IController", kind: "Interface")]);

        var results = db.SearchByName("controller", kind: "Interface");

        Assert.Single(results);
        Assert.Equal("IController", results[0].Name);
    }

    [Fact]
    public void SearchByName_RespectsLimit()
    {
        using var db = Open();
        db.UpsertNodes(Enumerable.Range(0, 10).Select(i => Node($"Thing{i}")).ToList());

        Assert.Equal(3, db.SearchByName("Thing", limit: 3).Count);
    }

    [Fact]
    public void UpsertNodes_IsIdempotentOnPathAndName()
    {
        using var db = Open();
        db.UpsertNodes([Node("PlayerController")]);
        db.UpsertNodes([Node("PlayerController")]);

        Assert.Single(db.SearchByName("PlayerController"));
    }

    [Fact]
    public void UpsertNodes_TreatsDifferentNamespacesAsDistinctNodes()
    {
        // namespace was not part of node identity, so "namespace A { class Config }" and
        // "namespace B { class Config }" — genuinely different types — collided onto one row.
        using var db = Open();
        db.UpsertNodes([
            new GraphNode { Kind = "Class", Name = "Config", Path = "Assets/A.cs", Namespace = "A" },
            new GraphNode { Kind = "Class", Name = "Config", Path = "Assets/A.cs", Namespace = "B" },
        ]);

        Assert.Equal(2, db.SearchByName("Config").Count);
    }

    [Fact]
    public void Open_MigratesFromVersion1_DiscardingOldData()
    {
        // The graph is derived data: bumping GraphSchema.Version means "drop and recreate", not
        // "alter in place" — this exercises that self-healing path from a v1 database.
        // Deliberately version-agnostic: asserting a literal current version made this fail on
        // every legitimate schema bump, which tests nothing about migration.
        Directory.CreateDirectory(_dir);
        var dbPath = Path.Combine(_dir, "graph.db");
        using (var raw = new SqliteConnection($"Data Source={dbPath}"))
        {
            raw.Open();
            Exec(raw, """
                CREATE TABLE nodes (
                    id INTEGER PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL,
                    name_lower TEXT NOT NULL, path TEXT NOT NULL, namespace TEXT,
                    line INTEGER NOT NULL DEFAULT 0, UNIQUE (path, kind, name)
                );
                """);
            Exec(raw, "INSERT INTO nodes (kind, name, name_lower, path, namespace, line) " +
                      "VALUES ('Class', 'Old', 'old', 'Assets/Old.cs', NULL, 1);");
            Exec(raw, "PRAGMA user_version = 1;");
        }

        using var reopened = GraphDatabase.Open(dbPath);

        Assert.True(GraphSchema.Version > 1, "this test seeds a v1 database, so current must be newer");
        Assert.Equal(GraphSchema.Version, reopened.SchemaVersion);
        Assert.Equal(0, reopened.TotalNodes());

        // The rebuilt database must be fully usable, not merely stamped with the new version.
        reopened.UpsertNodes([new GraphNode { Kind = "Class", Name = "Fresh", Path = "Assets/New.cs" }]);
        Assert.Single(reopened.SearchByName("Fresh"));
    }

    static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void DeleteNodesForPath_RemovesOnlyThatFilesNodes()
    {
        using var db = Open();
        db.UpsertNodes([Node("A", path: "Assets/A.cs"), Node("B", path: "Assets/B.cs")]);

        db.DeleteNodesForPath("Assets/A.cs");

        Assert.Empty(db.SearchByName("A"));
        Assert.Single(db.SearchByName("B"));
    }

    [Fact]
    public void SweepStaleNodes_RemovesOnlyNodesUnderPrefixThatAreNotKept()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("Kept", path: "Assets/Kept.cs"),
            Node("Gone", path: "Assets/Gone.cs"),
            Node("Other", path: "Packages/com.example/Other.cs"),
        ]);

        db.SweepStaleNodes("Assets", new HashSet<string> { "Assets/Kept.cs" }, reservedSubPrefixes: []);

        Assert.Single(db.SearchByName("Kept"));
        Assert.Empty(db.SearchByName("Gone"));
        Assert.Single(db.SearchByName("Other"));   // different prefix — untouched by this sweep
    }

    [Fact]
    public void SweepStaleNodes_LeavesNodesUnderAReservedSubPrefixAlone()
    {
        // Prefixes nest — "Packages/com.example" is textually a child of "Packages". Sweeping
        // the generic "Packages" root must not catch nodes that belong to a more specific,
        // separately-swept root, even though they lexically fall under its own prefix too.
        using var db = Open();
        db.UpsertNodes([Node("Owned", path: "Packages/com.example/Owned.cs")]);

        db.SweepStaleNodes("Packages", keepPaths: new HashSet<string>(),
            reservedSubPrefixes: ["Packages/com.example"]);

        Assert.Single(db.SearchByName("Owned"));
    }

    [Fact]
    public void CountByKind_SummarisesTheGraph()
    {
        using var db = Open();
        db.UpsertNodes([Node("A"), Node("B", path: "Assets/B.cs"), Node("IC", kind: "Interface", path: "Assets/C.cs")]);

        var counts = db.CountByKind();

        Assert.Equal(2, counts["Class"]);
        Assert.Equal(1, counts["Interface"]);
    }

    [Fact]
    public void Open_RecoversFromBrickedDatabase_MissingTableWithCurrentVersionStamp()
    {
        var dbPath = Path.Combine(_dir, "graph.db");
        using (var db = Open())
        {
            db.UpsertNodes([Node("Survivor")]);
        }

        // Simulate a process killed between the old code's DROP TABLE and its PRAGMA
        // user_version write: the version stamp already matches current, but the table
        // is gone. Without the fix, GraphSchema.Apply sees a matching version and returns
        // early forever, so every later call fails with "no such table: nodes".
        using (var raw = new SqliteConnection($"Data Source={dbPath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "DROP TABLE nodes;";
            command.ExecuteNonQuery();
        }

        using var reopened = GraphDatabase.Open(dbPath);

        Assert.Equal(GraphSchema.Version, reopened.SchemaVersion);
        Assert.Equal(0, reopened.TotalNodes());
    }

    [Fact]
    public void Open_WhenSchemaIsAlreadyCurrent_DoesNotBlockOnAConcurrentWriter()
    {
        // GraphSchema.Apply must not take BEGIN IMMEDIATE before it has established that a
        // migration is actually needed: doing so serialises every Open() against any writer
        // holding the lock, even in the overwhelmingly common case where nothing here needs
        // to change. Reproduced: a single held-open writer transaction made the old
        // always-lock-first code block for the writer's entire busy timeout.
        var dbPath = Path.Combine(_dir, "graph.db");
        using (var db = Open())
        {
            // Just to create the schema at the current version.
        }

        using var writer = new SqliteConnection($"Data Source={dbPath}");
        writer.Open();
        using var writerTransaction = writer.BeginTransaction(deferred: false);
        using (var command = writer.CreateCommand())
        {
            command.Transaction = writerTransaction;
            command.CommandText = """
                INSERT INTO nodes (kind, name, name_lower, path, namespace, line)
                VALUES ('Class', 'Holder', 'holder', 'Assets/Holder.cs', NULL, 0);
                """;
            command.ExecuteNonQuery();
        }
        // Deliberately never committed: the write lock stays held for the rest of this test.

        var started = DateTime.UtcNow;
        using var reopened = GraphDatabase.Open(dbPath);
        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"Open() took {elapsed.TotalMilliseconds}ms while a writer held the lock — " +
            "the no-migration-needed path must never attempt to take one.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Open_ConcurrentOpensOnFreshFile_DoNotThrowOrLoseRows()
    {
        // Raw Threads gated by a Barrier, not Task.Run: the thread pool's gradual ramp-up
        // staggers when queued work actually starts, which shrinks the race window this test
        // exists to hit. A Barrier forces all N threads to call Open() at effectively the
        // same instant. The race is inherently timing-dependent — the reviewer measured 4/40
        // on their machine — so this repeats several independent attempts and fails on the
        // first bad one, rather than trusting a single roll of the dice to land on the
        // ~1-in-10 case. Attempt count is calibrated empirically (see GraphDatabaseTests
        // ablation notes) to stay fast while still reliably catching the unfixed race.
        const int concurrency = 16;
        const int attempts = 5;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var attemptDir = Path.Combine(_dir, attempt.ToString());
            Directory.CreateDirectory(attemptDir);
            var dbPath = Path.Combine(attemptDir, "graph.db");

            var exceptions = new Exception?[concurrency];
            var barrier = new Barrier(concurrency);
            var threads = new Thread[concurrency];

            for (var i = 0; i < concurrency; i++)
            {
                var index = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        using var db = GraphDatabase.Open(dbPath);
                        db.UpsertNodes([Node($"Thing{index}", path: $"Assets/{index}.cs")]);
                    }
                    catch (Exception e)
                    {
                        exceptions[index] = e;
                    }
                });
            }

            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();

            Assert.All(exceptions, e => Assert.True(e is null, $"attempt {attempt}: {e}"));

            using var verify = GraphDatabase.Open(dbPath);
            Assert.True(concurrency == verify.TotalNodes(),
                $"attempt {attempt}: expected {concurrency} rows, found {verify.TotalNodes()}");
        }
    }

    [Fact]
    public void SearchByName_TreatsUnderscoreAsLiteralNotWildcard()
    {
        // Underscore is a LIKE single-character wildcard. Unity/C# identifiers use it
        // routinely (m_Health, _connection), so an unescaped search would over-match.
        using var db = Open();
        db.UpsertNodes([Node("m_Health"), Node("mXHealth")]);

        var results = db.SearchByName("m_Health");

        Assert.Single(results);
        Assert.Equal("m_Health", results[0].Name);
    }

    [Fact]
    public void SearchByName_TreatsPercentAsLiteralNotWildcard()
    {
        using var db = Open();
        db.UpsertNodes([Node("100%Complete"), Node("1000Complete"), Node("HalfDone")]);

        var results = db.SearchByName("100%");

        Assert.Single(results);
        Assert.Equal("100%Complete", results[0].Name);
    }

    [Fact]
    public void SearchByName_ThrowsOnNullPattern()
    {
        using var db = Open();

        Assert.Throws<ArgumentNullException>(() => db.SearchByName(null!));
    }

    [Fact]
    public void SearchByName_NegativeLimit_DoesNotReturnEverything()
    {
        // SQLite treats a negative LIMIT as "unlimited", which defeats the token-budget
        // cap SearchByName's own doc comment says it exists to enforce.
        using var db = Open();
        db.UpsertNodes(Enumerable.Range(0, 10).Select(i => Node($"Thing{i}")).ToList());

        var results = db.SearchByName("Thing", limit: -1);

        Assert.NotEqual(10, results.Count);
        Assert.Single(results);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
