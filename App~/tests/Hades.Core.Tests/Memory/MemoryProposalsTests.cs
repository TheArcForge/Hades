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
