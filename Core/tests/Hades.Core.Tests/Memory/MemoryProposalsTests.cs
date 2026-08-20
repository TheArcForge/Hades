using System.Threading;
using Hades.Core.Memory;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Memory;

/// <summary>
/// Plan 11 Task 6's own additions to <see cref="MemoryProposals"/> - List/Read/SetStatus/Delete,
/// needed so the control API's proposal queue (spec #3 §3.4: Accept/Dismiss/Defer) has something
/// to list and act on at all. Same fixture conventions as MemoryStoreTests.cs.
///
/// <b>Defect fix:</b> <see cref="MemoryProposals.Write"/> used to return
/// <see cref="MemoryProposal.FileName"/> WITH a "proposals/" prefix - a different shape than
/// <see cref="MemoryProposalInfo.FileName"/> (what List/Read/SetStatus/Delete actually accept and
/// validate as a basename), so feeding Write's own result straight back into any of them was
/// rejected as an unsafe name. Every test below that round-trips a written proposal now asserts
/// the two agree by construction - no more `["proposals/".Length..]` stripping.
/// </summary>
public sealed class MemoryProposalsTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string ProductGuid = "aaaabbbbccccddddeeeeffff00002222";
    static readonly DateTimeOffset CreatedAt = new(2026, 8, 1, 15, 30, 0, TimeSpan.Zero);

    MemoryProposals NewProposals() => new(new AppPaths(_appRoot));

    // ---------------------------------------------------------------- List

    [Fact]
    public void List_NoProposalsDirectoryYet_ReturnsAnEmptyList_NotAnException()
    {
        Assert.Empty(NewProposals().List(ProductGuid));
    }

    [Fact]
    public void List_AfterWrite_ReturnsThePlainBasename_AndParsedFrontmatterFields()
    {
        var proposals = NewProposals();
        var written = proposals.Write(ProductGuid, "patterns.md", "Use object pooling for bullets.", "Seen 3 times in the graph.", CreatedAt);

        var listed = Assert.Single(proposals.List(ProductGuid));

        // FileName is a plain basename on BOTH sides now - Write's own result agrees by
        // construction with List/Read/SetStatus/Delete's own basename-validated name param, so a
        // caller can pass Write's result straight back into any of them with no stripping.
        Assert.Equal(written.FileName, listed.FileName);
        Assert.Equal("patterns.md", listed.TargetFile);
        Assert.Equal(CreatedAt, listed.CreatedAt);
        Assert.Equal("Seen 3 times in the graph.", listed.Rationale);
        Assert.Equal("pending", listed.Status);
        Assert.Equal("Use object pooling for bullets.", listed.Content);
        Assert.Null(listed.FrontmatterError);
    }

    [Fact]
    public void List_MultipleProposals_NewestFirst()
    {
        var proposals = NewProposals();
        proposals.Write(ProductGuid, "patterns.md", "first", "r1", CreatedAt);
        proposals.Write(ProductGuid, "patterns.md", "second", "r2", CreatedAt.AddMinutes(1));

        var listed = proposals.List(ProductGuid);

        Assert.Equal(2, listed.Count);
        Assert.Equal("second", listed[0].Content);
        Assert.Equal("first", listed[1].Content);
    }

    // ---------------------------------------------------------------- List: review ordering
    // (the control API's proposal queue - spec #3 §3.4, replacing `/hades:show-proposals` as the
    // primary surface - must put the handful of proposals a person would actually act on ahead of
    // dozens of analyzer-generated statistical rows, without the shell doing any of the sorting -
    // see MemoryEndpoint's own class doc comment and spec #3 §1 "Swift renders, .NET decides.")

    [Fact]
    public void List_InferredStatus_SortsAfterEverythingElse_EvenWhenItsFileNameWouldSortFirstUnderTheOldRule()
    {
        var proposals = NewProposals();
        var authored = proposals.Write(ProductGuid, "patterns.md", "authored body", "r1", CreatedAt);
        var analyzerLike = proposals.Write(ProductGuid, "patterns.md", "analyzer body", "r2", CreatedAt.AddMinutes(1));
        proposals.SetStatus(ProductGuid, analyzerLike.FileName, "inferred");

        // Sanity check on the fixture itself: analyzerLike's name (later timestamp) sorts AFTER
        // authored's under a plain ordinal comparison, so the old "newest name first" rule would
        // have put it FIRST - proving the assertion below exercises the status override, not a
        // coincidence of these two filenames.
        Assert.True(string.CompareOrdinal(analyzerLike.FileName, authored.FileName) > 0);

        var listed = proposals.List(ProductGuid);

        Assert.Equal([authored.FileName, analyzerLike.FileName], listed.Select(p => p.FileName));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("accepted")]
    [InlineData("deferred")]
    [InlineData("flagged")] // an unrecognised future value - status is not a closed enum, see MemoryProposalInfo.Status's own doc comment
    public void List_AnyNonInferredStatus_SortsBeforeInferred(string status)
    {
        var proposals = NewProposals();
        var inferred = proposals.Write(ProductGuid, "patterns.md", "inferred body", "r1", CreatedAt);
        proposals.SetStatus(ProductGuid, inferred.FileName, "inferred");
        var other = proposals.Write(ProductGuid, "patterns.md", "other body", "r2", CreatedAt.AddSeconds(-1));
        proposals.SetStatus(ProductGuid, other.FileName, status);

        var listed = proposals.List(ProductGuid);

        Assert.Equal([other.FileName, inferred.FileName], listed.Select(p => p.FileName));
    }

    [Fact]
    public void List_EqualStatusRows_StayContiguous_EvenWhenFileNamesWouldOtherwiseInterleaveThem()
    {
        var proposals = NewProposals();
        // Timestamped so plain newest-name-first would interleave the two statuses: pendingNew,
        // inferredMiddle, pendingOld - proving the sort keeps every "pending" row together rather
        // than letting an "inferred" row split them, which would fragment a shell's own grouping
        // of consecutive equal-status rows into two separate, non-adjacent runs of the same label.
        var pendingNew = proposals.Write(ProductGuid, "patterns.md", "pending new", "r", new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));
        var inferredMiddle = proposals.Write(ProductGuid, "patterns.md", "inferred middle", "r", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));
        proposals.SetStatus(ProductGuid, inferredMiddle.FileName, "inferred");
        var pendingOld = proposals.Write(ProductGuid, "patterns.md", "pending old", "r", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        var listed = proposals.List(ProductGuid);

        Assert.Equal([pendingNew.FileName, pendingOld.FileName, inferredMiddle.FileName], listed.Select(p => p.FileName));
    }

    [Fact]
    public void List_BlankStatus_SortsBeforeInferred_AndIsNeverLost_AndNeverThrows()
    {
        // A hand-written file, not Write(): Write() always sets "pending", and SetStatus rejects a
        // blank value outright (ArgumentException.ThrowIfNullOrWhiteSpace) - so a genuinely blank
        // "status:" frontmatter field can only come from a pre-existing file on disk, the same way
        // a real analyzer bug would produce one (see MemoryFile.Parse's own doc comment: a value
        // with nothing after its colon is a valid null scalar, not an error). The filename below
        // echoes a real historical example (see RealProjectMemoryImportSmokeTest's own comment on
        // proposals/20260614-174745-.md) purely for readability - this test never touches the real
        // corpus, everything here is written into this test's own temp app root.
        var proposalsDir = Path.Combine(new AppPaths(_appRoot).MemoryDir(ProductGuid), "proposals");
        Directory.CreateDirectory(proposalsDir);
        const string blankStatusFile = "20260614-174745-.md";
        File.WriteAllText(Path.Combine(proposalsDir, blankStatusFile), "---\ntarget_file: \nrationale: \nstatus:\n---\n");

        var proposals = NewProposals();
        var inferred = proposals.Write(ProductGuid, "patterns.md", "inferred body", "r", CreatedAt);
        proposals.SetStatus(ProductGuid, inferred.FileName, "inferred");

        var listed = proposals.List(ProductGuid);

        Assert.Equal(2, listed.Count); // never lost
        Assert.Equal([blankStatusFile, inferred.FileName], listed.Select(p => p.FileName));
        Assert.Equal("", listed[0].Status);
    }

    // ---------------------------------------------------------------- P1: concurrent allocation

    [Fact]
    public void Write_ConcurrentProposesForTheSameTargetInTheSameSecond_EachLandsAsADistinctFileWithItsOwnContent()
    {
        // The live-reproduced defect: a filename-allocation TOCTOU (File.Exists check-then-return,
        // landed with File.Move(overwrite:true)) let concurrent callers targeting the same second
        // be handed the SAME fileName, silently destroying all but the last writer's content while
        // telling every caller success. A barrier forces genuine overlap rather than relying on
        // thread-scheduling luck to hit the race.
        const int concurrency = 16;
        var proposals = NewProposals();
        using var barrier = new Barrier(concurrency);
        var results = new MemoryProposal[concurrency];

        Parallel.For(0, concurrency, i =>
        {
            barrier.SignalAndWait();
            results[i] = proposals.Write(ProductGuid, "patterns.md", $"content-{i}", "r", CreatedAt);
        });

        // Every caller was handed a DISTINCT name - never a shared one where the last writer's
        // move silently won and the rest were told success for content that was never landed.
        Assert.Equal(concurrency, results.Select(r => r.FileName).Distinct().Count());

        // N distinct files actually exist on disk...
        var proposalsDir = Path.Combine(new AppPaths(_appRoot).MemoryDir(ProductGuid), "proposals");
        Assert.Equal(concurrency, Directory.EnumerateFiles(proposalsDir, "*.md").Count());

        // ...each holding EXACTLY the content its own caller wrote - no proposal silently
        // destroyed by another's overwrite.
        for (var i = 0; i < concurrency; i++)
        {
            var read = proposals.Read(ProductGuid, results[i].FileName);
            Assert.NotNull(read);
            Assert.Equal($"content-{i}", read!.Content);
        }
    }

    // ---------------------------------------------------------------- P2: target_file extension normalization

    [Fact]
    public void Write_ExtensionLessTargetFile_RecordsTheNormalizedDotMdTargetFile()
    {
        var proposals = NewProposals();

        var written = proposals.Write(ProductGuid, "conventions", "body", "why", CreatedAt);

        var read = proposals.Read(ProductGuid, written.FileName);
        Assert.Equal("conventions.md", read!.TargetFile);
    }

    [Fact]
    public void Write_TargetFileAlreadyEndingInUppercaseMD_IsNotDoubleAppended()
    {
        var proposals = NewProposals();

        var written = proposals.Write(ProductGuid, "conventions.MD", "body", "why", CreatedAt);

        var read = proposals.Read(ProductGuid, written.FileName);
        Assert.Equal("conventions.MD", read!.TargetFile);
    }

    [Fact]
    public void List_ProposalFileWithUppercaseMDExtension_IsStillListed()
    {
        // Simulates a proposal imported byte-for-byte (MemoryStore.ImportFromArcforge) with
        // whatever case its source had - Write() itself always generates a lowercase ".md", so
        // this shape can only arise from disk, not from this class's own writer.
        var proposalsDir = Path.Combine(new AppPaths(_appRoot).MemoryDir(ProductGuid), "proposals");
        Directory.CreateDirectory(proposalsDir);
        const string upperExtensionFile = "20260801-160000-caps.MD";
        File.WriteAllText(Path.Combine(proposalsDir, upperExtensionFile),
            "---\ntarget_file: patterns.md\nstatus: pending\n---\nBody.\n");

        var listed = NewProposals().List(ProductGuid);

        var hit = Assert.Single(listed);
        Assert.Equal(upperExtensionFile, hit.FileName);
    }

    // ---------------------------------------------------------------- P4: concurrent SetStatus

    [Fact]
    public void SetStatus_ConcurrentCallsOnTheSameProposal_NeverCorruptsItAndAlwaysLeavesAValidCompleteWrite()
    {
        const int concurrency = 16;
        var proposals = NewProposals();
        var written = proposals.Write(ProductGuid, "patterns.md", "body", "original rationale", CreatedAt);
        using var barrier = new Barrier(concurrency);
        var outcomes = new bool[concurrency];

        Parallel.For(0, concurrency, i =>
        {
            barrier.SignalAndWait();
            outcomes[i] = proposals.SetStatus(ProductGuid, written.FileName, i % 2 == 0 ? "accepted" : "deferred");
        });

        // Every concurrent call found the file and updated it - none silently no-op'd.
        Assert.All(outcomes, Assert.True);

        var read = proposals.Read(ProductGuid, written.FileName);
        Assert.NotNull(read);
        // Never corrupted: frontmatter still parses, and every field OTHER than status - the only
        // one any caller here ever changes - survived every concurrent write byte-for-byte.
        Assert.Null(read!.FrontmatterError);
        Assert.Equal("patterns.md", read.TargetFile);
        Assert.Equal("original rationale", read.Rationale);
        Assert.Equal(CreatedAt, read.CreatedAt);
        Assert.Equal("body", read.Content);
        // The guard's own semantics: the read-modify-write is serialized, so the final status is
        // deterministically whichever call entered the lock last - always ONE fully-formed
        // attempted value, never a torn or invented third value.
        Assert.Contains(read.Status, new[] { "accepted", "deferred" });
    }

    // ---------------------------------------------------------------- Read

    [Fact]
    public void Read_UnknownFileName_ReturnsNull_NeverThrows()
    {
        Assert.Null(NewProposals().Read(ProductGuid, "not-a-real-proposal.md"));
    }

    [Fact]
    public void Read_KnownFileName_ReturnsTheSameDataAsList()
    {
        var proposals = NewProposals();
        var written = proposals.Write(ProductGuid, "conventions.md", "Body text.", "Why", CreatedAt);

        var read = proposals.Read(ProductGuid, written.FileName);

        Assert.NotNull(read);
        Assert.Equal("conventions.md", read!.TargetFile);
        Assert.Equal("Body text.", read.Content);
    }

    // ---------------------------------------------------------------- SetStatus

    [Fact]
    public void SetStatus_UnknownFileName_ReturnsFalse()
    {
        Assert.False(NewProposals().SetStatus(ProductGuid, "nope.md", "accepted"));
    }

    [Fact]
    public void SetStatus_UpdatesStatus_PreservesEveryOtherFieldAndTheBodyByteForByte()
    {
        var proposals = NewProposals();
        var written = proposals.Write(ProductGuid, "patterns.md", "The proposed body, untouched.", "My rationale", CreatedAt);

        var ok = proposals.SetStatus(ProductGuid, written.FileName, "accepted");

        Assert.True(ok);
        var read = proposals.Read(ProductGuid, written.FileName);
        Assert.NotNull(read);
        Assert.Equal("accepted", read!.Status);
        Assert.Equal("patterns.md", read.TargetFile);
        Assert.Equal("My rationale", read.Rationale);
        Assert.Equal(CreatedAt, read.CreatedAt);
        Assert.Equal("The proposed body, untouched.", read.Content);
    }

    // ---------------------------------------------------------------- Delete

    [Fact]
    public void Delete_UnknownFileName_ReturnsFalse()
    {
        Assert.False(NewProposals().Delete(ProductGuid, "nope.md"));
    }

    [Fact]
    public void Delete_KnownFileName_RemovesIt_SubsequentReadIsNull()
    {
        var proposals = NewProposals();
        var written = proposals.Write(ProductGuid, "patterns.md", "body", "r", CreatedAt);

        var ok = proposals.Delete(ProductGuid, written.FileName);

        Assert.True(ok);
        Assert.Null(proposals.Read(ProductGuid, written.FileName));
        Assert.Empty(proposals.List(ProductGuid));
    }

    // ---------------------------------------------------------------- basename validation
    // (mirrors MemoryStoreTests.Write_RejectsAnyNameThatCouldEscapeTheMemoryDirectory exactly -
    // same theory data, same discipline, now for memory/proposals/ instead of memory/)

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir/file")]
    [InlineData(null)]
    public void Read_RejectsAnyNameThatCouldEscapeTheProposalsDirectory(string? name)
    {
        Assert.Throws<ArgumentException>(() => NewProposals().Read(ProductGuid, name!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir/file")]
    [InlineData(null)]
    public void SetStatus_RejectsAnyNameThatCouldEscapeTheProposalsDirectory(string? name)
    {
        Assert.Throws<ArgumentException>(() => NewProposals().SetStatus(ProductGuid, name!, "accepted"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir/file")]
    [InlineData(null)]
    public void Delete_RejectsAnyNameThatCouldEscapeTheProposalsDirectory(string? name)
    {
        Assert.Throws<ArgumentException>(() => NewProposals().Delete(ProductGuid, name!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
