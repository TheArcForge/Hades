using Hades.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Hades.Core.Tests.Memory;

public class MemoryIndexTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    MemoryIndex Open()
    {
        Directory.CreateDirectory(_dir);
        return MemoryIndex.Open(Path.Combine(_dir, "memory-index.db"));
    }

    // ---------------------------------------------------------------- basic indexing + search

    [Fact]
    public void IndexDocument_ThenSearch_FindsItByAWordInTheBody()
    {
        using var index = Open();
        index.IndexDocument("conventions.md", "We use Universal Render Pipeline for everything.");

        var hits = index.Search("Universal");

        var hit = Assert.Single(hits);
        Assert.Equal("conventions.md", hit.Name);
    }

    [Fact]
    public void Search_WithNoMatchesReturnsEmpty()
    {
        using var index = Open();
        index.IndexDocument("conventions.md", "We use Universal Render Pipeline for everything.");

        Assert.Empty(index.Search("nonexistentword"));
    }

    [Fact]
    public void Search_TreatsUnderscoreAsPartOfAnIdentifierNotASeparator()
    {
        // This codebase's own serialized field names are full of underscores (m_Health, m_Script,
        // ...) - the same class of identifier GraphDatabase.SearchByName's LIKE-escaping fix cares
        // about. FTS5's default tokenizer treats '_' as a separator, which would silently index
        // "m_Health" as two unrelated tokens ("m", "Health") - searching the whole identifier must
        // still find it as the one term a human actually typed.
        using var index = Open();
        index.IndexDocument("conventions.md", "Field naming convention: always prefix with m_Health style.");

        Assert.Single(index.Search("m_Health"));
    }

    // ---------------------------------------------------------------- ranking

    [Fact]
    public void Search_RanksADocumentMatchingTwiceAboveOneMatchingOnce()
    {
        using var index = Open();
        // Equal token count (6 each) so BM25's document-length normalisation cannot be what
        // decides the order - isolates term frequency as the only variable, which is the property
        // under test. Assert the order, not just membership: ranking is the entire point.
        index.IndexDocument("once.md", "widget alpha bravo charlie delta echo");
        index.IndexDocument("twice.md", "widget widget alpha bravo charlie delta");

        var hits = index.Search("widget");

        Assert.Equal(2, hits.Count);
        Assert.Equal("twice.md", hits[0].Name);
        Assert.Equal("once.md", hits[1].Name);
        Assert.True(hits[0].Score > hits[1].Score,
            $"expected twice.md ({hits[0].Score}) to outrank once.md ({hits[1].Score})");
    }

    // ---------------------------------------------------------------- editing / deleting

    [Fact]
    public void IndexDocument_CalledAgainForTheSameNameUpdatesItsContent()
    {
        using var index = Open();
        index.IndexDocument("patterns.md", "Original text about grapefruit.");
        Assert.Single(index.Search("grapefruit"));

        index.IndexDocument("patterns.md", "Edited text about pineapple.");

        Assert.Empty(index.Search("grapefruit"));
        Assert.Single(index.Search("pineapple"));
    }

    [Fact]
    public void RemoveDocument_TakesItOutOfResults()
    {
        using var index = Open();
        index.IndexDocument("pitfalls.md", "Never disable the safety interlock.");
        Assert.Single(index.Search("interlock"));

        index.RemoveDocument("pitfalls.md");

        Assert.Empty(index.Search("interlock"));
    }

    [Fact]
    public void RemoveDocument_OfANameNeverIndexedIsANoOpNotAnError()
    {
        using var index = Open();

        var exception = Record.Exception(() => index.RemoveDocument("never-existed.md"));

        Assert.Null(exception);
    }

    // ---------------------------------------------------------------- derived / rebuildable

    [Fact]
    public void DeletingTheDatabaseFileAndResyncingFromDiskReproducesIdenticalResults()
    {
        var memoryDir = Path.Combine(_dir, "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, "conventions.md"),
            "---\nlast_reviewed: 2026-05-12\n---\nUse URP for rendering.\n");
        File.WriteAllText(Path.Combine(memoryDir, "decisions.md"),
            "# Decisions\n\nURP chosen for tiered rendering.\n");

        var dbPath = Path.Combine(_dir, "memory-index.db");

        List<MemorySearchHit> before;
        using (var index = MemoryIndex.Open(dbPath))
        {
            index.SyncFromDirectory(memoryDir);
            before = index.Search("URP").ToList();
        }

        File.Delete(dbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);

        // See SummaryToolTests.Ping_StillWorksWhenTheProjectDatabaseIsBroken for why this is
        // required: Microsoft.Data.Sqlite pools native connections per connection string, so
        // without clearing the pool a fresh Open() below could silently keep talking to the
        // now-deleted file through a stale pooled handle.
        SqliteConnection.ClearAllPools();

        List<MemorySearchHit> after;
        using (var index = MemoryIndex.Open(dbPath))
        {
            index.SyncFromDirectory(memoryDir);
            after = index.Search("URP").ToList();
        }

        Assert.Equal(2, before.Count);
        Assert.Equal(before.Select(h => h.Name), after.Select(h => h.Name));
        Assert.Equal(before.Select(h => h.Score), after.Select(h => h.Score));
    }

    [Fact]
    public void SyncFromDirectory_OnlyIndexesTopLevelMdFilesNotProposals()
    {
        var memoryDir = Path.Combine(_dir, "memory");
        Directory.CreateDirectory(Path.Combine(memoryDir, "proposals"));
        File.WriteAllText(Path.Combine(memoryDir, "conventions.md"), "Top level mentions kumquat.");
        File.WriteAllText(Path.Combine(memoryDir, "proposals", "idea.md"), "Proposal mentions kumquat too.");

        using var index = Open();
        index.SyncFromDirectory(memoryDir);

        var hits = index.Search("kumquat");

        var hit = Assert.Single(hits);
        Assert.Equal("conventions.md", hit.Name);
    }

    [Fact]
    public void SyncFromDirectory_ReflectsFilesDeletedFromDiskSinceLastSync()
    {
        var memoryDir = Path.Combine(_dir, "memory");
        Directory.CreateDirectory(memoryDir);
        var path = Path.Combine(memoryDir, "glossary.md");
        File.WriteAllText(path, "Defines the term zeppelin.");

        using var index = Open();
        index.SyncFromDirectory(memoryDir);
        Assert.Single(index.Search("zeppelin"));

        File.Delete(path);
        index.SyncFromDirectory(memoryDir);

        Assert.Empty(index.Search("zeppelin"));
    }

    [Fact]
    public void SyncFromDirectory_EditingAFileOnDiskThenResyncingUpdatesItsContent()
    {
        var memoryDir = Path.Combine(_dir, "memory");
        Directory.CreateDirectory(memoryDir);
        var path = Path.Combine(memoryDir, "intent.md");
        File.WriteAllText(path, "Currently focused on mangoes.");

        using var index = Open();
        index.SyncFromDirectory(memoryDir);
        Assert.Single(index.Search("mangoes"));

        File.WriteAllText(path, "Currently focused on lychees.");
        index.SyncFromDirectory(memoryDir);

        Assert.Empty(index.Search("mangoes"));
        Assert.Single(index.Search("lychees"));
    }

    [Fact]
    public void SyncFromDirectory_UppercaseMDExtension_IsStillIndexed()
    {
        // Directory.EnumerateFiles(dir, "*.md")'s search-pattern casing follows the underlying
        // filesystem, case-SENSITIVE on Linux - a document like "CAPS.MD" would silently never be
        // indexed there alone, so Search would never find it either.
        var memoryDir = Path.Combine(_dir, "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, "CAPS.MD"), "Mentions a durian.");

        using var index = Open();
        index.SyncFromDirectory(memoryDir);

        var hit = Assert.Single(index.Search("durian"));
        Assert.Equal("CAPS.MD", hit.Name);
    }

    [Fact]
    public void SyncFromDirectory_OnANonExistentDirectoryLeavesTheIndexEmptyWithoutErroring()
    {
        using var index = Open();

        var exception = Record.Exception(() => index.SyncFromDirectory(Path.Combine(_dir, "does-not-exist")));

        Assert.Null(exception);
        Assert.Empty(index.Search("anything"));
    }

    // ---------------------------------------------------------------- empty index

    [Fact]
    public void Search_OnAFreshEmptyIndexReturnsNoResultsRatherThanErroring()
    {
        using var index = Open();

        Assert.Empty(index.Search("anything"));
    }

    // ---------------------------------------------------------------- FTS5 metacharacter neutralisation

    [Theory]
    [InlineData("\"")]
    [InlineData("NEAR(")]
    [InlineData("*")]
    [InlineData("OR")]
    public void Search_FtsSpecialSyntaxDoesNotThrow(string query)
    {
        using var index = Open();
        index.IndexDocument("conventions.md", "Some ordinary prose about rendering.");

        var exception = Record.Exception(() => index.Search(query));

        Assert.Null(exception);
    }

    [Fact]
    public void Search_OrIsNotInterpretedAsABooleanOperator()
    {
        // Unsanitised, "cat OR dog" is a real FTS5 boolean-OR query matching either word - it would
        // hit cat-only.md AND dog-only.md AND has-or.md (3 results). Neutralised, every token
        // (including the literal word "or") becomes its own required phrase, so only the one
        // document containing all three literal tokens - cat, or, dog - matches.
        using var index = Open();
        index.IndexDocument("has-or.md", "We chose cat or dog for the mascot.");
        index.IndexDocument("cat-only.md", "The cat sat on the mat.");
        index.IndexDocument("dog-only.md", "The dog barked loudly.");

        var hits = index.Search("cat OR dog");

        var hit = Assert.Single(hits);
        Assert.Equal("has-or.md", hit.Name);
    }

    [Fact]
    public void Search_TrailingStarIsNotInterpretedAsAPrefixWildcard()
    {
        // Unsanitised, "cat*" is a real FTS5 prefix query matching both "cat" and "cats".
        // Neutralised, it becomes a literal phrase for the single token "cat" only.
        using var index = Open();
        index.IndexDocument("cat.md", "A cat is a small animal.");
        index.IndexDocument("cats.md", "Cats are small animals.");

        var hits = index.Search("cat*");

        var hit = Assert.Single(hits);
        Assert.Equal("cat.md", hit.Name);
    }

    [Fact]
    public void Search_NearFunctionSyntaxIsNotInterpretedAsAProximityOperator()
    {
        // Unsanitised, "NEAR(cat dog, 2)" is a real FTS5 proximity query, with its own strict
        // argument grammar. A caller who is not thinking about FTS5 syntax at all - just pasting
        // text shaped like this - must not get a different query than they typed, and must not
        // get a syntax-error exception either.
        using var index = Open();
        index.IndexDocument("unrelated.md", "Cats and dogs are common pets.");

        var exception = Record.Exception(() => index.Search("NEAR(cat dog, 2)"));

        Assert.Null(exception);
    }

    [Fact]
    public void Search_QuoteCharacterAloneReturnsNoResultsRatherThanErroring()
    {
        using var index = Open();
        index.IndexDocument("conventions.md", "Ordinary prose, no quotes involved.");

        Assert.Empty(index.Search("\""));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
