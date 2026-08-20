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

    // ---------------------------------------------------------------- P2: extension normalization
    // (real v1.2 .arcforge imports carry target_file values with no ".md" - see
    // MemoryProposalsTests's own coverage of the propose side. Every document this store reads or
    // writes is a markdown file by definition, so an extension-less name and its ".md" counterpart
    // must name the SAME document, never two different ones - otherwise a write lands in a file no
    // *.md listing surface (GetMemorySummary, ValidateMemory, MemoryIndex) ever shows again.)

    [Fact]
    public void Write_ExtensionLessName_LandsInADotMdFileOnDisk()
    {
        var store = NewStore();

        store.Write(ProductGuid, "conventions", "# Conventions\n");

        var memoryDir = new AppPaths(_appRoot).MemoryDir(ProductGuid);
        Assert.True(File.Exists(Path.Combine(memoryDir, "conventions.md")));
        Assert.False(File.Exists(Path.Combine(memoryDir, "conventions")));
    }

    [Fact]
    public void Read_ExtensionLessName_FindsTheSameDotMdFileAWriteWithTheExtensionWouldHaveFound()
    {
        var store = NewStore();
        store.Write(ProductGuid, "conventions.md", "# Conventions\n");

        var readWithoutExtension = store.Read(ProductGuid, "conventions");

        Assert.NotNull(readWithoutExtension);
        Assert.Equal("# Conventions\n", readWithoutExtension!.Body);
    }

    [Fact]
    public void WriteThenRead_ExtensionLessName_RoundTripsThroughTheNormalizedDotMdFile()
    {
        // The scenario AcceptProposal depends on: a first accept creates the document (Write), a
        // later accept looks up "existing" content to append to (Read) - both must resolve to the
        // SAME file even when the caller never types ".md", or the second accept would think no
        // document exists yet and silently replace instead of append.
        var store = NewStore();

        store.Write(ProductGuid, "conventions", "first");
        var afterFirst = store.Read(ProductGuid, "conventions");
        Assert.Equal("first", afterFirst?.Body);

        store.Write(ProductGuid, "conventions", afterFirst!.RawText + "\nsecond");
        var afterSecond = store.Read(ProductGuid, "conventions.md");

        Assert.Equal("first\nsecond", afterSecond?.Body);
    }

    [Fact]
    public void Write_NameAlreadyEndingInUppercaseMD_IsNotDoubleAppended()
    {
        var store = NewStore();

        store.Write(ProductGuid, "conventions.MD", "content");

        var memoryDir = new AppPaths(_appRoot).MemoryDir(ProductGuid);
        Assert.True(File.Exists(Path.Combine(memoryDir, "conventions.MD")));
        Assert.False(File.Exists(Path.Combine(memoryDir, "conventions.MD.md")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir/file")]
    public void Write_ExtensionLessUnsafeName_StillRejectedBeforeNormalizationCouldMaskIt(string name)
    {
        // Regression guard for the fix itself: normalizing MUST happen after validation, never
        // before - appending ".md" to ".." before checking it would neuter the traversal check
        // (".." would become "...md", a harmless-looking single path segment).
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Write(ProductGuid, name, "content"));
    }

    // ---------------------------------------------------------------- P2: cross-platform *.md enumeration
    // (Directory.EnumerateFiles(dir, "*.md") is case-sensitive on Linux - a document or import
    // source named e.g. "CAPS.MD" would silently vanish from every listing surface there alone.)

    [Fact]
    public void Import_UppercaseMdExtensionSourceFile_IsStillRecognisedAndImported()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var arcforgeMemoryDir = Path.Combine(projectRoot, ".arcforge", "memory");
        Directory.CreateDirectory(arcforgeMemoryDir);
        File.WriteAllText(Path.Combine(arcforgeMemoryDir, "CAPS.MD"), "# Caps\n");

        try
        {
            var store = NewStore();
            var result = store.ImportFromArcforge(ProductGuid, projectRoot);

            Assert.Contains("CAPS.MD", result.Imported);
            Assert.Empty(result.Skipped);
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Import_OneUnreadableSourceFile_IsReportedSkippedAndEveryOtherFileStillImports()
    {
        // Unix permissions are the mechanism under test; there is no Windows equivalent of an
        // unreadable-but-present mode-000 file to exercise this path with.
        if (OperatingSystem.IsWindows()) return;

        var projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var arcforgeMemoryDir = Path.Combine(projectRoot, ".arcforge", "memory");
        Directory.CreateDirectory(arcforgeMemoryDir);
        File.WriteAllText(Path.Combine(arcforgeMemoryDir, "aaa-first.md"), "# First\n");
        var unreadable = Path.Combine(arcforgeMemoryDir, "bbb-unreadable.md");
        File.WriteAllText(unreadable, "# Locked\n");
        File.WriteAllText(Path.Combine(arcforgeMemoryDir, "ccc-last.md"), "# Last\n");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);

        try
        {
            var store = NewStore();
            var result = store.ImportFromArcforge(ProductGuid, projectRoot);

            Assert.Contains("aaa-first.md", result.Imported);
            Assert.Contains("ccc-last.md", result.Imported);
            Assert.DoesNotContain("bbb-unreadable.md", result.Imported);
            var skip = Assert.Single(result.Skipped);
            Assert.Equal("bbb-unreadable.md", skip.Source);
            Assert.False(string.IsNullOrWhiteSpace(skip.Reason));
        }
        finally
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
