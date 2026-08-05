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
