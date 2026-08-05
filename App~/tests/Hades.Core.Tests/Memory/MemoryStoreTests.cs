using Hades.Core.Memory;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Memory;

public class MemoryStoreTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string ProductGuid = "aaaabbbbccccddddeeeeffff00001111";

    MemoryStore NewStore() => new(new AppPaths(_appRoot));

    [Fact]
    public void Read_ReturnsNullWhenDocumentDoesNotExist()
    {
        Assert.Null(NewStore().Read(ProductGuid, "conventions.md"));
    }

    [Fact]
    public void WriteThenRead_RoundTripsAwkwardBodyByteForByte()
    {
        // Every awkward real-world characteristic authored markdown can have, in one file:
        // trailing whitespace, CRLF line endings, a tab, unicode, and a body that itself
        // contains a "---" line (a markdown horizontal rule) that must NOT be mistaken for a
        // second frontmatter block.
        var content = "---\r\n"
            + "last_reviewed: 2026-05-12\r\n"
            + "validation_status: ok\r\n"
            + "---\r\n"
            + "# Conventions   \r\n"
            + "\r\n"
            + "Trailing whitespace on this line.   \n"
            + "A\ttabbed\tline.\n"
            + "Unicode: café, 日本語, 😀\n"
            + "\n"
            + "---\n"
            + "\n"
            + "The line above is a horizontal rule inside the body, not a frontmatter delimiter.\n";

        var store = NewStore();
        store.Write(ProductGuid, "conventions.md", content);
        var file = store.Read(ProductGuid, "conventions.md");

        Assert.NotNull(file);
        Assert.Equal(content, file!.RawText);
        Assert.True(file.HasFrontmatter);
        Assert.Null(file.FrontmatterError);
        Assert.Equal("2026-05-12", file.Frontmatter["last_reviewed"]);
        Assert.Equal("ok", file.Frontmatter["validation_status"]);

        var expectedBody = "# Conventions   \r\n"
            + "\r\n"
            + "Trailing whitespace on this line.   \n"
            + "A\ttabbed\tline.\n"
            + "Unicode: café, 日本語, 😀\n"
            + "\n"
            + "---\n"
            + "\n"
            + "The line above is a horizontal rule inside the body, not a frontmatter delimiter.\n";
        Assert.Equal(expectedBody, file.Body);
    }

    [Fact]
    public void WriteThenRead_FileWithNoFrontmatterIsValid()
    {
        const string content = "# Just plain markdown\n\nA human wrote this by hand, no frontmatter at all.\n";

        var store = NewStore();
        store.Write(ProductGuid, "notes.md", content);
        var file = store.Read(ProductGuid, "notes.md");

        Assert.NotNull(file);
        Assert.False(file!.HasFrontmatter);
        Assert.Null(file.FrontmatterError);
        Assert.Empty(file.Frontmatter);
        Assert.Equal(content, file.Body);
        Assert.Equal(content, file.RawText);
    }

    [Fact]
    public void Read_UnterminatedFrontmatterIsReportedButBodyStillReturned()
    {
        const string content = "---\nlast_reviewed: 2026-05-12\n# No closing delimiter below\nSome prose.\n";

        var store = NewStore();
        store.Write(ProductGuid, "broken.md", content);
        var file = store.Read(ProductGuid, "broken.md");

        Assert.NotNull(file);
        Assert.NotNull(file!.FrontmatterError);
        Assert.Contains("broken.md", file.FrontmatterError);
        Assert.Empty(file.Frontmatter);
        // Nothing a human typed may become unreachable: the whole file, including the stray
        // opening delimiter, must still be readable as the body.
        Assert.Equal(content, file.Body);
        Assert.Equal(content, file.RawText);
    }

    [Fact]
    public void Read_InvalidYamlFrontmatterIsReportedButBodyStillReturned()
    {
        // A closed block whose contents are not a flat key/value mapping - here, a YAML syntax
        // error (an unterminated quoted scalar).
        const string content = "---\nname: \"unterminated\nstatus: ok\n---\n# Body\n\nStill here.\n";

        var store = NewStore();
        store.Write(ProductGuid, "bad-yaml.md", content);
        var file = store.Read(ProductGuid, "bad-yaml.md");

        Assert.NotNull(file);
        Assert.True(file!.HasFrontmatter);
        Assert.NotNull(file.FrontmatterError);
        Assert.Contains("bad-yaml.md", file.FrontmatterError);
        Assert.Empty(file.Frontmatter);
        Assert.Equal("# Body\n\nStill here.\n", file.Body);
    }

    [Fact]
    public void Read_EmptyFrontmatterBlockIsValidWithNoFields()
    {
        const string content = "---\n---\n# Body\n";

        var store = NewStore();
        store.Write(ProductGuid, "empty-front.md", content);
        var file = store.Read(ProductGuid, "empty-front.md");

        Assert.NotNull(file);
        Assert.True(file!.HasFrontmatter);
        Assert.Null(file.FrontmatterError);
        Assert.Empty(file.Frontmatter);
        Assert.Equal("# Body\n", file.Body);
    }

    [Fact]
    public void Read_EmptyScalarFrontmatterValueParsesAsEmptyStringNotAnError()
    {
        // The real Hades-Unity-Client corpus has proposal files with an empty "target_file:"
        // field (see proposals/20260614-174745-.md). An empty scalar is valid YAML - it must
        // not be reported as malformed frontmatter.
        const string content = "---\ntarget_file: \nstatus: pending\n---\nBody text.\n";

        var store = NewStore();
        store.Write(ProductGuid, "empty-value.md", content);
        var file = store.Read(ProductGuid, "empty-value.md");

        Assert.NotNull(file);
        Assert.Null(file!.FrontmatterError);
        Assert.Equal("", file.Frontmatter["target_file"]);
        Assert.Equal("pending", file.Frontmatter["status"]);
    }

    [Fact]
    public void KnownDocuments_ListsTheSixRecognisedNames()
    {
        Assert.Equal(
            new[] { "conventions.md", "decisions.md", "glossary.md", "intent.md", "patterns.md", "pitfalls.md" },
            MemoryStore.KnownDocuments);
    }

    [Fact]
    public void WriteThenRead_AllowsAnUnknownDocumentName()
    {
        // A fixed enum of document names would make memory less useful than a text editor - a
        // human must be able to create any new file they want.
        var store = NewStore();
        store.Write(ProductGuid, "my-custom-notes.md", "# Whatever I want\n");

        var file = store.Read(ProductGuid, "my-custom-notes.md");

        Assert.NotNull(file);
        Assert.Equal("# Whatever I want\n", file!.Body);
    }

    [Fact]
    public void Write_CreatesTheMemoryDirectoryOnFirstUse()
    {
        var paths = new AppPaths(_appRoot);
        var store = new MemoryStore(paths);

        store.Write(ProductGuid, "conventions.md", "# Conventions\n");

        Assert.True(File.Exists(Path.Combine(paths.MemoryDir(ProductGuid), "conventions.md")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir/file")]
    [InlineData(null)]
    public void Write_RejectsAnyNameThatCouldEscapeTheMemoryDirectory(string? name)
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Write(ProductGuid, name!, "content"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir/file")]
    [InlineData(null)]
    public void Read_RejectsAnyNameThatCouldEscapeTheMemoryDirectory(string? name)
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Read(ProductGuid, name!));
    }

    [Fact]
    public void Write_RejectsPathEscapeEvenWhenATraversalWouldLandInsideAnotherProjectsMemoryDir()
    {
        // The single-path-segment check must fire before the containment check even considers
        // where "../<other-guid>/memory/x.md" would resolve to.
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Write(ProductGuid, "../other-guid/memory/x.md", "content"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
