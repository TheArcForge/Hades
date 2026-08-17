using System.Text.Json;
using Hades.Core.Migration;

namespace Hades.Core.Tests.Migration;

public sealed class V12CleanupTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _claudeDesktopScratchDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _hadesHubScratchDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public V12CleanupTests() => Directory.CreateDirectory(_projectRoot);

    // ---- fixture helpers ----

    static readonly string Start = V12Detector.StartMarker;
    static readonly string End = V12Detector.EndMarker;

    string ClaudeMdPath => Path.Combine(_projectRoot, "CLAUDE.md");
    void WriteClaudeMd(string content) => File.WriteAllText(ClaudeMdPath, content);
    string ReadClaudeMd() => File.ReadAllText(ClaudeMdPath);
    ClaudeMdState DetectClaudeMd() => V12Detector.Detect(_projectRoot).ClaudeMd;

    string ManifestDir => Path.Combine(_projectRoot, "Packages");
    string ManifestPath => Path.Combine(ManifestDir, "manifest.json");
    void WriteManifest(string content)
    {
        Directory.CreateDirectory(ManifestDir);
        File.WriteAllText(ManifestPath, content);
    }
    string ReadManifest() => File.ReadAllText(ManifestPath);

    string McpJsonPath => Path.Combine(_projectRoot, ".mcp.json");
    void WriteMcpJson(string content = """
        {
          "mcpServers": {
            "hades": {
              "command": "node",
              "args": [
                "/Users/mike/.arcforge/hades-hub/launcher.js"
              ]
            }
          }
        }
        """) => File.WriteAllText(McpJsonPath, content);
    string ReadMcpJson() => File.ReadAllText(McpJsonPath);

    string WriteClaudeDesktopConfig(string content)
    {
        Directory.CreateDirectory(_claudeDesktopScratchDir);
        var path = Path.Combine(_claudeDesktopScratchDir, "claude_desktop_config.json");
        File.WriteAllText(path, content);
        return path;
    }

    /// Mirrors the real, live shape confirmed on the reference machine: launcher.js, hub.json, and
    /// hub-path.json sitting directly inside the hades-hub directory - see
    /// <see cref="V12Cleanup.CleanHadesHub"/>'s own doc comment for where these three names come
    /// from.
    void WriteHadesHubFixture()
    {
        Directory.CreateDirectory(_hadesHubScratchDir);
        File.WriteAllText(Path.Combine(_hadesHubScratchDir, "launcher.js"), "// retired v1.2 stdio launcher\n");
        File.WriteAllText(Path.Combine(_hadesHubScratchDir, "hub.json"), """{"port":64638,"pid":15503,"startedAt":1786166593681}""");
        File.WriteAllText(Path.Combine(_hadesHubScratchDir, "hub-path.json"), """{"hubEntry":"/Users/mike/Projects/Hades/Bridge~/hub/dist/index.js"}""");
    }

    static (byte[] Content, DateTime WriteTimeUtc) Snapshot(string path) =>
        (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

    // Deliberately not Assert.Equal(before, Snapshot(path)): ValueTuple's default equality
    // compares the byte[] element by reference, not content, so two separately-read arrays with
    // identical bytes would always compare unequal. Compare content and mtime separately instead.
    static void AssertUnchanged(string path, (byte[] Content, DateTime WriteTimeUtc) before)
    {
        var after = Snapshot(path);
        Assert.True(before.Content.AsSpan().SequenceEqual(after.Content), $"'{path}' content changed");
        Assert.Equal(before.WriteTimeUtc, after.WriteTimeUtc);
    }

    /// Directory-level counterpart to <see cref="Snapshot"/>/<see cref="AssertUnchanged"/> - every
    /// file's relative path and content, recursively, for proving a wholesale directory delete
    /// either did or (on a dry run) did NOT touch a single byte anywhere underneath it.
    static Dictionary<string, byte[]> SnapshotDirectory(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes);

    static void AssertDirectoryUnchanged(string dir, Dictionary<string, byte[]> before)
    {
        var after = SnapshotDirectory(dir);
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, bytes) in before)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(after[path]), $"'{path}' changed");
        }
    }

    // ==================================================================
    // CLAUDE.md: the three shapes
    // ==================================================================

    [Fact]
    public void CleanClaudeMd_Absent_ReportsNothingToDoAndDoesNotThrow()
    {
        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.False(result.Removed);
        Assert.False(File.Exists(ClaudeMdPath));
    }

    [Fact]
    public void CleanClaudeMd_UnmarkedHadesAuthoredWholesale_RefusesAndLeavesFileUntouched()
    {
        WriteClaudeMd(
            "# Hades — Agent Guidelines\n\n" +
            "This is a Unity project with Hades installed. You have 89 MCP tools.\n");
        var before = Snapshot(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public void CleanClaudeMd_UnmarkedHandWritten_RefusesAndLeavesFileUntouched()
    {
        // Same outcome as the wholesale-Hades-authored case above, deliberately - V12Detector
        // cannot reliably tell these apart (see its own remarks), so cleanup must not either.
        WriteClaudeMd(
            "# Team conventions\n\n" +
            "We use tabs, not spaces. PRs need two approvals.\n");
        var before = Snapshot(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public void CleanClaudeMd_Marked_RemovesOnlyTheBlock_EveryByteOutsideSurvives()
    {
        const string before = "# My notes\n\n";
        const string block = "guidance from hades\nmore guidance\n";
        const string after = "\n# My own section after\n";
        WriteClaudeMd(before + Start + "\n" + block + End + after);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(before + after, ReadClaudeMd());
    }

    // ---- positional variants ----

    [Fact]
    public void CleanClaudeMd_MarkedBlockAtStartOfFile_LeavesOnlyTheAfterContent()
    {
        WriteClaudeMd(Start + "\nblock content\n" + End + "\nafter content\n");

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.True(result.Removed);
        Assert.Equal("\nafter content\n", ReadClaudeMd());
    }

    [Fact]
    public void CleanClaudeMd_MarkedBlockAtEndOfFile_LeavesOnlyTheBeforeContent()
    {
        WriteClaudeMd("before content\n" + Start + "\nblock content\n" + End);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.True(result.Removed);
        Assert.Equal("before content\n", ReadClaudeMd());
    }

    // ---- the real-world hybrid shape: unmarked prefix, then a marked block ----

    [Fact]
    public void CleanClaudeMd_UnmarkedPrefixThenMarkedBlock_RemovesBlockButReportsRemainingContent()
    {
        // Mirrors the shape task 2 found in the reference project's own CLAUDE.md: a chunk of
        // unmarked Hades-authored content from an older template revision, followed by a marked
        // block containing a newer revision of the same material. Not a literal copy of
        // Hades-Unity-Client's file - just the same structural shape.
        const string unmarkedPrefix =
            "# Hades — Agent Guidelines\n\n" +
            "This is a Unity project with Hades installed. Old template revision, no markers.\n\n" +
            "## Core principle\n\n" +
            "Query the graph before grepping.\n";
        const string markedBlock =
            "# Hades — Agent Guidelines\n\n" +
            "This is a Unity project with Hades installed. Newer template revision, with markers.\n";
        WriteClaudeMd(unmarkedPrefix + Start + "\n" + markedBlock + End + "\n");

        var state = DetectClaudeMd();
        Assert.Equal(ClaudeMdShape.Marked, state.Shape); // sanity: a lone well-formed pair exists

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, state, proceed: true);

        Assert.True(result.Removed);
        Assert.True(result.RemainingContentOutsideBlock);
        // "cleanup succeeded" and "the file looks right afterwards" are different claims - the
        // stale unmarked prefix must survive byte-for-byte, not be silently swept away.
        Assert.Equal(unmarkedPrefix + "\n", ReadClaudeMd());
    }

    // ---- RemainingContentOutsideBlock: true only when there is real content left ----

    [Fact]
    public void CleanClaudeMd_BlockIsEntireFile_RemainingContentIsFalse()
    {
        WriteClaudeMd(Start + "\nblock\n" + End);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.True(result.Removed);
        Assert.False(result.RemainingContentOutsideBlock);
        Assert.Equal("", ReadClaudeMd());
    }

    [Fact]
    public void CleanClaudeMd_OnlyWhitespaceRemainsOutsideBlock_RemainingContentIsFalse()
    {
        WriteClaudeMd(Start + "\nblock\n" + End + "\n");

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.True(result.Removed);
        Assert.False(result.RemainingContentOutsideBlock);
        Assert.Equal("\n", ReadClaudeMd());
    }

    // ---- malformed markers: never delete, always refuse and report ----

    [Theory]
    [InlineData("some text <!-- HADES:START -->\nblock with no end\n")]
    [InlineData("<!-- HADES:END -->\nend before start\n<!-- HADES:START -->\n")]
    public void CleanClaudeMd_MalformedMarkerPair_RefusesAndLeavesFileUntouched(string content)
    {
        WriteClaudeMd(content);
        var before = Snapshot(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public void CleanClaudeMd_NestedMarkers_DetectorReportsUnmarked_RefusesViaTheOrdinaryUnmarkedPath()
    {
        // Two START occurrences and two END occurrences, overlapping: which pair is "the" block
        // is genuinely ambiguous. V12Detector.ReadClaudeMd now agrees with this class's own
        // multiplicity guard below and reports Unmarked rather than guessing - see
        // V12DetectorTests.Detect_ClaudeMd_NestedOrDuplicateMarkers_FallsBackToUnmarkedRatherThanGuessing.
        // Cleanup therefore refuses via the ordinary Unmarked branch here; the test right after
        // this one proves the internal recount still independently catches the same shape if it
        // were ever handed a stale Marked state instead (defense in depth stays, per this class's
        // own doc comment, regardless of the detector now being honest).
        var content = "Notes\n\n" + Start + "\nInner A\n" + Start + "\nInner B\n" + End + "\nInner C\n" + End + "\nFinal\n";
        WriteClaudeMd(content);

        var state = DetectClaudeMd();
        Assert.Equal(ClaudeMdShape.Unmarked, state.Shape); // the fix under test: no longer a false Marked

        var before = Snapshot(ClaudeMdPath);
        var result = V12Cleanup.CleanClaudeMd(_projectRoot, state, proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public void CleanClaudeMd_StaleMarkedStateOverNestedMarkers_InternalGuardStillRefuses()
    {
        // Defense in depth: even if a caller hands CleanClaudeMd a Shape.Marked state for content
        // that actually has multiple START/END markers - computed before the file changed, say,
        // or some future detector regressing on this exact case - the multiplicity recount inside
        // the Marked branch below must independently catch it and refuse, never trusting the
        // passed-in Shape blindly. This is the guard the task that found this class's own
        // multiplicity check insisted must stay regardless of whether V12Detector is honest.
        var content = "Notes\n\n" + Start + "\nInner A\n" + Start + "\nInner B\n" + End + "\nInner C\n" + End + "\nFinal\n";
        WriteClaudeMd(content);

        // A Shape.Marked state pointing at the first START/END pair - exactly what the detector
        // used to hand back for this content (see the test above) before it was fixed to agree
        // with this guard.
        var firstStart = content.IndexOf(Start, StringComparison.Ordinal);
        var firstEnd = content.IndexOf(End, StringComparison.Ordinal);
        var staleState = new ClaudeMdState
        {
            Shape = ClaudeMdShape.Marked,
            MarkedBlock = new ClaudeMdMarkedBlock { Start = firstStart, End = firstEnd + End.Length },
        };
        var before = Snapshot(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, staleState, proceed: true);

        Assert.False(result.Removed);
        Assert.Contains("more than one", result.Message, StringComparison.OrdinalIgnoreCase);
        AssertUnchanged(ClaudeMdPath, before);
    }

    // ---- go-ahead gate ----

    [Fact]
    public void CleanClaudeMd_MarkedButNoGoAhead_RefusesAndLeavesFileUntouched()
    {
        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");
        var before = Snapshot(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: false);

        Assert.False(result.Removed);
        AssertUnchanged(ClaudeMdPath, before);
    }

    // ---- the pre-action gap: a caller building a confirmation prompt needs to know whether
    // content will remain BEFORE agreeing, not just learn it from the result after acting ----

    [Fact]
    public void CleanClaudeMd_UnmarkedPrefixThenMarkedBlock_NoGoAhead_ReportsRemainingContentTrue()
    {
        // Same hybrid shape as CleanClaudeMd_UnmarkedPrefixThenMarkedBlock_RemovesBlockButReportsRemainingContent
        // above, but with proceed:false. RemainingContentOutsideBlock is a pure fact about the
        // file's current content and the already-detected block offsets - it does not depend on
        // whether a write is about to happen - so a dry run must report it exactly as accurately
        // as an actual removal does. Before this fix, the no-go-ahead path never computed it at
        // all and always reported false, regardless of the file's real shape.
        const string unmarkedPrefix =
            "# Hades — Agent Guidelines\n\n" +
            "This is a Unity project with Hades installed. Old template revision, no markers.\n";
        const string markedBlock =
            "This is a Unity project with Hades installed. Newer template revision, with markers.\n";
        WriteClaudeMd(unmarkedPrefix + Start + "\n" + markedBlock + End + "\n");
        var before = Snapshot(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: false);

        Assert.False(result.Removed);
        Assert.True(result.RemainingContentOutsideBlock);
        Assert.Contains("outside", result.Message, StringComparison.OrdinalIgnoreCase);
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public void CleanClaudeMd_BlockIsEntireFile_NoGoAhead_RemainingContentIsFalse()
    {
        // The negative case, proven with the same rigor: a dry run must not OVER-report either -
        // when the block genuinely is the whole file, RemainingContentOutsideBlock stays false
        // before an action just as it does after one.
        WriteClaudeMd(Start + "\nblock\n" + End);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: false);

        Assert.False(result.Removed);
        Assert.False(result.RemainingContentOutsideBlock);
    }

    // ---- defensive: a stale/mismatched state must never cause deleting the wrong bytes ----

    [Fact]
    public void CleanClaudeMd_StateOffsetsDoNotMatchCurrentFileContent_RefusesRatherThanGuessing()
    {
        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");
        // A state that claims Marked but whose offsets do not actually point at marker text -
        // simulating the file having changed on disk since Detect ran, or a caller passing a
        // state computed for a different file entirely.
        var staleState = new ClaudeMdState
        {
            Shape = ClaudeMdShape.Marked,
            MarkedBlock = new ClaudeMdMarkedBlock { Start = 0, End = 6 }, // "before" - not marker text
        };
        var before = Snapshot(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, staleState, proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public void CleanClaudeMd_FileDeletedAfterDetection_ReportsGracefullyWithoutThrowing()
    {
        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");
        var state = DetectClaudeMd();
        File.Delete(ClaudeMdPath);

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, state, proceed: true);

        Assert.False(result.Removed);
    }

    // ---- M7: a UTF-16 CLAUDE.md used to dead-end with a misleading message ----

    [Fact]
    public void CleanClaudeMd_Utf16EncodedFile_RefusesWithAClearMessageNamingTheEncoding()
    {
        // V12Detector.ReadClaudeMd uses File.ReadAllText, which auto-detects and correctly decodes
        // a UTF-16 BOM - so detection is NOT the bug here, and can legitimately report Shape.Marked
        // for a genuinely UTF-16 file (asserted below). This class's own read/write is byte-level
        // UTF-8 only (see this class's own "JSON edits are byte-level surgery" remarks), which -
        // before this fix - silently decoded the UTF-16 bytes as mojibake, failed the marker
        // re-match, and refused with "changed since it was classified": true of no byte actually on
        // disk, and useless to a caller trying to figure out what to do next.
        var content = "before\n" + Start + "\nblock\n" + End + "\nafter\n";
        var preamble = System.Text.Encoding.Unicode.GetPreamble(); // UTF-16 LE BOM: FF FE
        var encoded = System.Text.Encoding.Unicode.GetBytes(content);
        File.WriteAllBytes(ClaudeMdPath, [.. preamble, .. encoded]);
        var before = Snapshot(ClaudeMdPath);

        var state = DetectClaudeMd();
        Assert.Equal(ClaudeMdShape.Marked, state.Shape); // detection itself is unaffected

        var result = V12Cleanup.CleanClaudeMd(_projectRoot, state, proceed: true);

        Assert.False(result.Removed);
        Assert.Contains("UTF-16", result.Message, StringComparison.OrdinalIgnoreCase);
        AssertUnchanged(ClaudeMdPath, before);
    }

    // ---- M9: a read-only CLAUDE.md must be refused cleanly, never silently re-permissioned ----

    [Fact]
    public void CleanClaudeMd_ReadOnlyFile_RefusesAndLeavesFileAndPermissionsUntouched()
    {
        if (OperatingSystem.IsWindows())
        {
            // Same POSIX-only scope as every other permission test in this file - this suite's CI
            // runs ubuntu-latest and macos-latest only (see .github/workflows/ci.yml).
            return;
        }

        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");
        var before = Snapshot(ClaudeMdPath);
        File.SetAttributes(ClaudeMdPath, FileAttributes.ReadOnly);
        var readOnlyMode = File.GetUnixFileMode(ClaudeMdPath);

        try
        {
            var result = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);

            Assert.False(result.Removed);
            Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
            AssertUnchanged(ClaudeMdPath, before);
            Assert.Equal(readOnlyMode, File.GetUnixFileMode(ClaudeMdPath));
        }
        finally
        {
            File.SetAttributes(ClaudeMdPath, FileAttributes.Normal);
        }
    }

    // ==================================================================
    // Packages/manifest.json: remove the entry, byte-identical otherwise
    // ==================================================================

    [Fact]
    public void CleanManifest_NoManifestFile_ReportsNothingToDoWithoutThrowing()
    {
        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
    }

    [Fact]
    public void CleanManifest_NoHadesEntry_ReportsNothingToDoAndLeavesFileUntouched()
    {
        WriteManifest("""
            {
              "dependencies": {
                "com.unity.collab-proxy": "2.10.2",
                "com.unity.ide.rider": "3.0.38"
              }
            }
            """);
        var before = Snapshot(ManifestPath);

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
        AssertUnchanged(ManifestPath, before);
    }

    [Fact]
    public void CleanManifest_MalformedJson_RefusesAndLeavesFileUntouched()
    {
        WriteManifest("{ not valid json ");
        var before = Snapshot(ManifestPath);

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(ManifestPath, before);
    }

    [Fact]
    public void CleanManifest_NoGoAhead_ReportsCountButLeavesFileUntouched()
    {
        WriteManifest("""
            {
              "dependencies": {
                "com.arcforge.hades": "file:/Users/mike/Projects/Hades",
                "com.unity.collab-proxy": "2.10.2"
              }
            }
            """);
        var before = Snapshot(ManifestPath);

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: false);

        Assert.False(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
        AssertUnchanged(ManifestPath, before);
    }

    [Fact]
    public void CleanManifest_AlwaysWarnsAboutPortConflict()
    {
        WriteManifest("""{ "dependencies": { "com.arcforge.hades": "1.2.3" } }""");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.False(string.IsNullOrWhiteSpace(result.PortConflictWarning));
        Assert.Contains("port", result.PortConflictWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanManifest_FileFormDependency_NotLastEntry_RemovesOnlyThatLine()
    {
        WriteManifest(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.arcforge.hades\": \"file:/Users/mike/Projects/Hades\",\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\",\n" +
            "    \"com.unity.ide.rider\": \"3.0.38\"\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
        Assert.Equal(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\",\n" +
            "    \"com.unity.ide.rider\": \"3.0.38\"\n" +
            "  }\n" +
            "}\n",
            ReadManifest());
    }

    [Fact]
    public void CleanManifest_RegistryVersionForm_RemovesEntryTheSameWay()
    {
        WriteManifest(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\",\n" +
            "    \"com.arcforge.hades\": \"1.2.3\"\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\"\n" +
            "  }\n" +
            "}\n",
            ReadManifest());
    }

    [Fact]
    public void CleanManifest_SoleDependency_CollapsesToEmptyDependenciesObject()
    {
        WriteManifest(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.arcforge.hades\": \"file:/Users/mike/Projects/Hades\"\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(
            "{\n" +
            "  \"dependencies\": {\n" +
            "  }\n" +
            "}\n",
            ReadManifest());
    }

    [Fact]
    public void CleanManifest_LastDependencyWithPredecessors_RemovesPrecedingCommaNotTrailingContent()
    {
        WriteManifest(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\",\n" +
            "    \"com.arcforge.hades\": \"file:/Users/mike/Projects/Hades\"\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\"\n" +
            "  }\n" +
            "}\n",
            ReadManifest());
    }

    [Fact]
    public void CleanManifest_TwoOccurrenceShape_RemovesBothTestablesAndDependenciesEntries()
    {
        // Matches the reference project's real manifest.json exactly: com.arcforge.hades appears
        // both as a "testables" array element (line 3 in the real file) and as a "dependencies"
        // entry. Read before assuming a single occurrence.
        WriteManifest(
            "{\n" +
            "  \"testables\": [\n" +
            "    \"com.arcforge.hades\"\n" +
            "  ],\n" +
            "  \"dependencies\": {\n" +
            "    \"com.arcforge.hades\": \"file:/Users/mike/Projects/Hades\",\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\",\n" +
            "    \"com.unity.ide.rider\": \"3.0.38\"\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(2, result.OccurrencesFound);
        Assert.Equal(
            "{\n" +
            "  \"testables\": [\n" +
            "  ],\n" +
            "  \"dependencies\": {\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\",\n" +
            "    \"com.unity.ide.rider\": \"3.0.38\"\n" +
            "  }\n" +
            "}\n",
            ReadManifest());
    }

    [Fact]
    public void CleanManifest_OnlyInTestables_NotInDependencies_ReportsOneOccurrence()
    {
        WriteManifest(
            "{\n" +
            "  \"testables\": [\n" +
            "    \"com.arcforge.hades\"\n" +
            "  ]\n" +
            "}\n");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
        Assert.Equal(
            "{\n" +
            "  \"testables\": [\n" +
            "  ]\n" +
            "}\n",
            ReadManifest());
    }

    // ==================================================================
    // Adjacent duplicate array entries: RemoveJsonEntries/ComputeDeletionRange defect. Two
    // matching "testables" elements sitting next to each other each independently compute a
    // deletion range that reaches for the SAME shared comma between them (the first extends
    // forward over it, the second extends backward over it) - overlapping ranges the splice loop
    // did not coalesce, so the second splice's own offsets - still measured against the ORIGINAL
    // bytes, per RemoveJsonEntries' own doc comment - land past where the first splice already
    // cut, eating into whatever followed (here, the closing bracket). Hand-traced on the minimal
    // compact case in the bug report: {"t":["X","X"]} -> {"t":[} before the fix.
    // ==================================================================

    [Fact]
    public void CleanManifest_AdjacentDuplicateTestablesEntries_Compact_RemovesBothAndStaysValidJson()
    {
        WriteManifest("""{"testables":["com.arcforge.hades","com.arcforge.hades"]}""");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(2, result.OccurrencesFound);

        var written = ReadManifest();
        using var doc = JsonDocument.Parse(written); // throws on invalid JSON - the crux of the bug
        Assert.Equal(0, doc.RootElement.GetProperty("testables").GetArrayLength());
    }

    [Fact]
    public void CleanManifest_AdjacentDuplicateTestablesEntries_PrettyPrinted_RemovesBothAndStaysValidJson()
    {
        WriteManifest(
            "{\n" +
            "  \"testables\": [\n" +
            "    \"com.arcforge.hades\",\n" +
            "    \"com.arcforge.hades\"\n" +
            "  ]\n" +
            "}\n");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(2, result.OccurrencesFound);

        var written = ReadManifest();
        using var doc = JsonDocument.Parse(written); // throws on invalid JSON - the crux of the bug
        Assert.Equal(0, doc.RootElement.GetProperty("testables").GetArrayLength());

        // Exact content, not just validity: the two overlapping ranges here happen to still
        // produce PARSEABLE JSON even uncoalesced ("testables": [ ] on one line, item2's range
        // eating item1's own trailing comma+newline while item1's range separately eats item2's
        // leading indentation+item - two wrongs that happen to cancel out on THIS shape) - so only
        // pinning the correct, coalesced-single-range output actually distinguishes fixed from
        // buggy here. See the compact-JSON tests above for the shape where the same overlap is
        // NOT parseable at all.
        Assert.Equal(
            "{\n" +
            "  \"testables\": [\n" +
            "\n" +
            "  ]\n" +
            "}\n",
            written);
    }

    [Fact]
    public void CleanManifest_ThreeAdjacentDuplicateTestablesEntries_RemovesAllAndStaysValidJson()
    {
        // Not just a two-way overlap: three-in-a-row means the middle entry's own computed range
        // overlaps BOTH of its neighbours', so the fix must coalesce a whole run, not just pairs.
        WriteManifest("""{"testables":["com.arcforge.hades","com.arcforge.hades","com.arcforge.hades"]}""");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(3, result.OccurrencesFound);

        var written = ReadManifest();
        using var doc = JsonDocument.Parse(written);
        Assert.Equal(0, doc.RootElement.GetProperty("testables").GetArrayLength());
    }

    [Fact]
    public void CleanManifest_AdjacentDuplicatesAmongOtherEntries_RemovesOnlyTheDuplicatesLeavesNeighboursIntact()
    {
        // The overlap is specifically between the two ADJACENT hades entries; a non-adjacent
        // neighbour on either side must survive untouched, proving the coalesce is scoped to the
        // genuinely-overlapping pair and does not over-reach into unrelated entries.
        WriteManifest("""{"testables":["com.example.before","com.arcforge.hades","com.arcforge.hades","com.example.after"]}""");

        var result = V12Cleanup.CleanManifest(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(2, result.OccurrencesFound);

        var written = ReadManifest();
        using var doc = JsonDocument.Parse(written);
        var remaining = doc.RootElement.GetProperty("testables").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Contains("com.example.before", remaining);
        Assert.Contains("com.example.after", remaining);
        Assert.DoesNotContain("com.arcforge.hades", remaining);
    }

    // ==================================================================
    // .mcp.json: the generated project-level config - the "hades" entry under mcpServers is
    // spliced out surgically, exactly like CleanClaudeDesktopConfig's own mcpServers/hades scan
    // below. This class used to delete the whole file wholesale (File.Delete) on the assumption
    // that v1.2 always wrote it and nothing else could be in it - false in practice, and exactly
    // the defect these tests prove fixed: a hand-added second server, or a nested decoy sharing the
    // "mcpServers"/"hades" names, must both survive untouched.
    // ==================================================================

    [Fact]
    public void CleanMcpConfig_NoFile_ReportsNothingToDo()
    {
        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
        Assert.False(File.Exists(McpJsonPath));
    }

    [Fact]
    public void CleanMcpConfig_MalformedJson_RefusesAndLeavesFileUntouched()
    {
        WriteMcpJson("{ not valid json ");
        var before = Snapshot(McpJsonPath);

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
        AssertUnchanged(McpJsonPath, before);
    }

    [Fact]
    public void CleanMcpConfig_NoHadesEntry_ReportsNothingToDoAndLeavesFileUntouched()
    {
        WriteMcpJson("""{ "mcpServers": { "other-server": { "command": "npx" } } }""");
        var before = Snapshot(McpJsonPath);

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
        AssertUnchanged(McpJsonPath, before);
    }

    // ---- M1: used to File.Delete the whole file - proven fixed by these three ----

    [Fact]
    public void CleanMcpConfig_HadesPlusOtherServer_OnlyHadesIsRemoved_OtherServerByteIdentical()
    {
        // The headline fix: a project-level .mcp.json is exactly as easy for a user (or another
        // tool) to hand-add a second server to, after v1.2 wrote it, as claude_desktop_config.json
        // is - a wholesale delete destroyed that other server along with Hades' own entry.
        WriteMcpJson(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"hades\": {\n" +
            "      \"command\": \"node\",\n" +
            "      \"args\": [\n" +
            "        \"/Users/mike/.arcforge/hades-hub/launcher.js\"\n" +
            "      ]\n" +
            "    },\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\",\n" +
            "      \"args\": [\n" +
            "        \"-y\",\n" +
            "        \"some-other-mcp-server\"\n" +
            "      ]\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
        Assert.True(File.Exists(McpJsonPath)); // never deleted - see this class's own doc comment
        Assert.Equal(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\",\n" +
            "      \"args\": [\n" +
            "        \"-y\",\n" +
            "        \"some-other-mcp-server\"\n" +
            "      ]\n" +
            "    }\n" +
            "  }\n" +
            "}\n",
            ReadMcpJson());
    }

    [Fact]
    public void CleanMcpConfig_HadesIsOnlyServer_LeavesEmptyMcpServersObject_NeverDeletesTheFile()
    {
        // Mirrors CleanClaudeDesktopConfig_HadesIsOnlyServer_LeavesEmptyMcpServersObject exactly -
        // per this class's own doc comment, "the file is now pointless" is never guessed at. An
        // explicit fixture (not the default WriteMcpJson() one), so the expected output below is
        // exact rather than depending on the default fixture's own raw-string-literal whitespace.
        WriteMcpJson(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"hades\": {\n" +
            "      \"command\": \"node\"\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
        Assert.True(File.Exists(McpJsonPath));
        Assert.Equal(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "  }\n" +
            "}\n",
            ReadMcpJson());
    }

    [Fact]
    public void CleanMcpConfig_NoGoAhead_ReportsOccurrencesFoundButLeavesFileUntouched()
    {
        // Dry-run parity: this class's own documented convention (see ManifestCleanupResult and
        // ClaudeDesktopConfigCleanupResult's own OccurrencesFound) is that a dry run reports exactly
        // what the real run would do, never less.
        WriteMcpJson();
        var before = Snapshot(McpJsonPath);

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: false);

        Assert.False(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
        Assert.True(File.Exists(McpJsonPath));
        AssertUnchanged(McpJsonPath, before);
    }

    // ---- M8: the span scanner used to match "hades" inside ANY container literally named
    // "mcpServers", regardless of how deeply nested - not just the top-level one ----

    [Fact]
    public void CleanMcpConfig_NestedMcpServersDecoyElsewhereInFile_OnlyTopLevelHadesRemoved_DecoyUntouched()
    {
        // Live-shaped repro: a backup blob elsewhere in the file happens to carry its own nested
        // mcpServers/hades pair. Depth-blind matching does not distinguish this from the real,
        // top-level entry - it spliced out whichever the reader reached first, which could be the
        // decoy instead of (or as well as) the real one.
        WriteMcpJson(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"hades\": {\n" +
            "      \"command\": \"node\"\n" +
            "    },\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"backupOfOldConfig\": {\n" +
            "    \"mcpServers\": {\n" +
            "      \"hades\": {\n" +
            "        \"command\": \"node\",\n" +
            "        \"args\": [\n" +
            "          \"decoy - must survive\"\n" +
            "        ]\n" +
            "      }\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(1, result.OccurrencesFound); // only the top-level one counted
        var written = ReadMcpJson();
        Assert.Equal(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"backupOfOldConfig\": {\n" +
            "    \"mcpServers\": {\n" +
            "      \"hades\": {\n" +
            "        \"command\": \"node\",\n" +
            "        \"args\": [\n" +
            "          \"decoy - must survive\"\n" +
            "        ]\n" +
            "      }\n" +
            "    }\n" +
            "  }\n" +
            "}\n",
            written);
    }

    [Fact]
    public void CleanMcpConfig_OnlyNestedDecoyExists_NoTopLevelHades_ReportsNothingAndLeavesDecoyUntouched()
    {
        // The sharper case: no top-level "hades" at all, only a nested decoy. Before the fix, the
        // depth-blind scanner still found and removed the nested one; the fix must report zero
        // occurrences and leave the whole file byte-identical.
        WriteMcpJson(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"backupOfOldConfig\": {\n" +
            "    \"mcpServers\": {\n" +
            "      \"hades\": {\n" +
            "        \"command\": \"node\"\n" +
            "      }\n" +
            "    }\n" +
            "  }\n" +
            "}\n");
        var before = Snapshot(McpJsonPath);

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
        AssertUnchanged(McpJsonPath, before);
    }

    [Fact]
    public void CleanMcpConfig_NoGoAhead_LeavesFileUntouched()
    {
        WriteMcpJson();
        var before = Snapshot(McpJsonPath);

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: false);

        Assert.False(result.Removed);
        Assert.True(File.Exists(McpJsonPath));
        AssertUnchanged(McpJsonPath, before);
    }

    // ---- M9: a read-only target must be refused cleanly, never silently re-permissioned ----

    [Fact]
    public void CleanMcpConfig_ReadOnlyFile_RefusesAndLeavesFileAndPermissionsUntouched()
    {
        if (OperatingSystem.IsWindows())
        {
            // FileAttributes.ReadOnly is honoured for writes on Windows too, but this suite's CI
            // runs ubuntu-latest and macos-latest only (see .github/workflows/ci.yml) - same POSIX
            // scope as every other permission test in this file.
            return;
        }

        WriteMcpJson();
        var before = Snapshot(McpJsonPath);
        File.SetAttributes(McpJsonPath, FileAttributes.ReadOnly);
        // Captured AFTER marking read-only, deliberately: the claim under test is "the refused
        // cleanup attempt leaves permissions exactly as they were AT THE MOMENT OF THE ATTEMPT",
        // not "restores whatever they were before this test ever touched the file".
        var readOnlyMode = File.GetUnixFileMode(McpJsonPath);

        try
        {
            var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

            Assert.False(result.Removed);
            Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
            AssertUnchanged(McpJsonPath, before);
            Assert.Equal(readOnlyMode, File.GetUnixFileMode(McpJsonPath)); // never re-permissioned
        }
        finally
        {
            File.SetAttributes(McpJsonPath, FileAttributes.Normal);
        }
    }

    // ==================================================================
    // claude_desktop_config.json: global, and easy to get wrong
    // ==================================================================

    [Fact]
    public void ClaudeDesktopConfigPath_PointsAtTheStandardMacOSLocation()
    {
        // Pure string computation - never touches disk, never risks the real file.
        var path = V12Cleanup.ClaudeDesktopConfigPath;

        Assert.EndsWith(Path.Combine("Library", "Application Support", "Claude", "claude_desktop_config.json"), path);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_NoFileAtPath_ReportsNothingToDoAndNeverCreatesOne()
    {
        var path = Path.Combine(_claudeDesktopScratchDir, "claude_desktop_config.json");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.False(result.Removed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CleanClaudeDesktopConfig_MalformedJson_RefusesAndLeavesFileUntouched()
    {
        var path = WriteClaudeDesktopConfig("{ not valid json ");
        var before = Snapshot(path);

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(path, before);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_AlwaysStatesItIsGlobalNotProjectScoped()
    {
        var path = WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.False(string.IsNullOrWhiteSpace(result.ScopeWarning));
        Assert.Contains("Claude Desktop", result.ScopeWarning, StringComparison.Ordinal);
        Assert.Contains("global", result.ScopeWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_NoHadesKey_ReportsNothingToDoAndLeavesFileUntouched()
    {
        var path = WriteClaudeDesktopConfig("""
            {
              "mcpServers": {
                "other-server": {
                  "command": "npx"
                }
              }
            }
            """);
        var before = Snapshot(path);

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.False(result.Removed);
        AssertUnchanged(path, before);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_NoGoAhead_LeavesFileUntouched()
    {
        var path = WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");
        var before = Snapshot(path);

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: false);

        Assert.False(result.Removed);
        AssertUnchanged(path, before);
    }

    // ---- OccurrencesFound: this file has no companion detector (it is global, not per-project -
    // see this class's own doc comment), so a dry run's OccurrencesFound is the ONLY way a caller
    // can tell "there is a hades entry to offer cleaning up" apart from "there is nothing here",
    // mirroring ManifestCleanupResult.OccurrencesFound exactly ----

    [Fact]
    public void CleanClaudeDesktopConfig_NoGoAhead_ReportsOccurrencesFoundWithoutRemoving()
    {
        var path = WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: false);

        Assert.False(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_NoHadesKey_OccurrencesFoundIsZero()
    {
        var path = WriteClaudeDesktopConfig("""
            {
              "mcpServers": {
                "other-server": {
                  "command": "npx"
                }
              }
            }
            """);

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_NoFileAtPath_OccurrencesFoundIsZero()
    {
        var path = Path.Combine(_claudeDesktopScratchDir, "claude_desktop_config.json");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.Equal(0, result.OccurrencesFound);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_MalformedJson_OccurrencesFoundIsZero()
    {
        var path = WriteClaudeDesktopConfig("{ not valid json ");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.False(result.Removed);
        Assert.Equal(0, result.OccurrencesFound);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_Removed_OccurrencesFoundReflectsWhatWasRemoved()
    {
        var path = WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(1, result.OccurrencesFound);
    }

    [Fact]
    public void CleanClaudeDesktopConfig_HadesPlusOtherServersAndUnrelatedKeys_OnlyHadesIsRemoved()
    {
        // Mirrors the real, live shape on this machine: a "hades" entry alongside another MCP
        // server AND unrelated top-level application preferences. Removing the Hades entry must
        // leave every other server entry - and everything else in the file - byte-identical.
        var path = WriteClaudeDesktopConfig(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"hades\": {\n" +
            "      \"command\": \"node\",\n" +
            "      \"args\": [\n" +
            "        \"/Users/mike/.arcforge/hades-hub/launcher.js\"\n" +
            "      ]\n" +
            "    },\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\",\n" +
            "      \"args\": [\n" +
            "        \"-y\",\n" +
            "        \"some-other-mcp-server\"\n" +
            "      ]\n" +
            "    }\n" +
            "  },\n" +
            "  \"coworkUserFilesPath\": \"/Users/mike/Claude\",\n" +
            "  \"preferences\": {\n" +
            "    \"keepAwakeEnabled\": true\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\",\n" +
            "      \"args\": [\n" +
            "        \"-y\",\n" +
            "        \"some-other-mcp-server\"\n" +
            "      ]\n" +
            "    }\n" +
            "  },\n" +
            "  \"coworkUserFilesPath\": \"/Users/mike/Claude\",\n" +
            "  \"preferences\": {\n" +
            "    \"keepAwakeEnabled\": true\n" +
            "  }\n" +
            "}\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void CleanClaudeDesktopConfig_HadesIsOnlyServer_LeavesEmptyMcpServersObject()
    {
        var path = WriteClaudeDesktopConfig(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"hades\": {\n" +
            "      \"command\": \"node\"\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        var result = V12Cleanup.CleanClaudeDesktopConfig(path, proceed: true);

        Assert.True(result.Removed);
        Assert.Equal(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "  }\n" +
            "}\n",
            File.ReadAllText(path));
    }

    // ==================================================================
    // ~/.arcforge/hades-hub/: the retired v1.2 Node launcher + its hub state - global, and
    // removed wholesale (spec #4 §1 lists ~/.arcforge/hades-hub/launcher.js among what v2 retires)
    // ==================================================================

    [Fact]
    public void HadesHubDirectory_PointsAtTheStandardLocationUnderTheHomeDirectory()
    {
        // Pure string computation - never touches disk, never risks the real directory.
        var path = V12Cleanup.HadesHubDirectory;

        Assert.EndsWith(Path.Combine(".arcforge", "hades-hub"), path);
    }

    [Fact]
    public void CleanHadesHub_NoDirectoryAtPath_ReportsNothingToDoAndNeverCreatesOne()
    {
        var result = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: true);

        Assert.False(result.Removed);
        Assert.False(result.Found);
        Assert.False(Directory.Exists(_hadesHubScratchDir));
        Assert.Contains("hades-hub", result.Message, StringComparison.Ordinal);
        Assert.Contains("nothing to remove", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanHadesHub_Present_DeletesTheWholeDirectoryRecursively()
    {
        WriteHadesHubFixture();

        var result = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: true);

        Assert.True(result.Removed);
        Assert.True(result.Found);
        Assert.False(Directory.Exists(_hadesHubScratchDir));
        Assert.Contains("hades-hub", result.Message, StringComparison.Ordinal);
        Assert.Contains("Removed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanHadesHub_NestedSubdirectoriesAndOtherFiles_AllRemovedRecursively()
    {
        // Mirrors the real, live shape confirmed on the reference machine: hades-hub can hold more
        // than the three well-known files - e.g. a "pending" subdirectory the hub writes to at
        // runtime. Wholesale removal must not depend on enumerating every possible file it might
        // contain.
        WriteHadesHubFixture();
        Directory.CreateDirectory(Path.Combine(_hadesHubScratchDir, "pending"));
        File.WriteAllText(Path.Combine(_hadesHubScratchDir, "pending", "some-project.json"), "{}");

        var result = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: true);

        Assert.True(result.Removed);
        Assert.False(Directory.Exists(_hadesHubScratchDir));
    }

    // ---- Directory.Delete can throw (a locked file, possibly after partially deleting the rest) -
    // that must come back as a normal, honest result, never a bare unhandled exception ----

    [Fact]
    public void CleanHadesHub_ADirectoryEntryCannotBeDeleted_ReturnsAFailureResultInsteadOfThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            // File.SetUnixFileMode below is POSIX-only. This suite's CI runs ubuntu-latest and
            // macos-latest only (see .github/workflows/ci.yml) - never Windows - so reproducing
            // "Directory.Delete fails partway through" via denied write permission on a directory
            // (the portable way; an open file handle alone does not block deletion on macOS/Linux
            // the way it does on Windows) is this test's job everywhere it actually runs.
            return;
        }

        WriteHadesHubFixture();
        var lockedDir = Path.Combine(_hadesHubScratchDir, "pending");
        Directory.CreateDirectory(lockedDir);
        File.WriteAllText(Path.Combine(lockedDir, "stuck.json"), "{}");

        // POSIX requires WRITE permission on a directory to delete an entry inside it (unlike
        // Windows, an open file handle alone does not block deletion on macOS/Linux) - denying
        // write on "pending" makes the recursive delete fail partway through without depending on
        // any Windows-only "file in use" semantics.
        var originalMode = File.GetUnixFileMode(lockedDir);
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var result = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: true);

            Assert.False(result.Removed);
            Assert.True(result.Found); // the directory could not be fully removed, so it is still there
            Assert.False(string.IsNullOrWhiteSpace(result.Message));

            // The next dry-run stays honest: Found still reflects reality rather than a cached
            // "it's gone now" belief from the failed attempt.
            var again = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: false);
            Assert.True(again.Found);
        }
        finally
        {
            // Restore permissions so this test's own teardown (Dispose deletes _hadesHubScratchDir)
            // does not itself fail for the identical reason this test just proved CleanHadesHub
            // must handle.
            File.SetUnixFileMode(lockedDir, originalMode);
        }
    }

    // ---- Found: this target has no companion V12Detector scan (it is global, not per-project -
    // same reasoning as CleanClaudeDesktopConfig's own OccurrencesFound), so Found is a caller's
    // ONLY way to learn whether there is anything here worth offering to clean up at all, and
    // must be accurate on a dry run, before any go-ahead ----

    [Fact]
    public void CleanHadesHub_NoGoAhead_LeavesDirectoryAndEveryFileUntouched()
    {
        WriteHadesHubFixture();
        var before = SnapshotDirectory(_hadesHubScratchDir);

        var result = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: false);

        Assert.False(result.Removed);
        Assert.True(result.Found);
        Assert.True(Directory.Exists(_hadesHubScratchDir));
        Assert.Contains("hades-hub", result.Message, StringComparison.Ordinal);
        Assert.Contains("no go-ahead", result.Message, StringComparison.OrdinalIgnoreCase);
        AssertDirectoryUnchanged(_hadesHubScratchDir, before);
    }

    // ---- scope: the whole directory goes, but never its parent or anything else under it ----

    [Fact]
    public void CleanHadesHub_NeverTouchesTheParentArcforgeDirectoryOrItsOtherContents()
    {
        // ~/.arcforge/ holds more than hades-hub on the reference machine (mcp-bridge.js,
        // servers/) - removing hades-hub must never reach a level up.
        var parent = Path.GetDirectoryName(_hadesHubScratchDir)!;
        Directory.CreateDirectory(parent);
        var siblingFile = Path.Combine(parent, "mcp-bridge.js");
        File.WriteAllText(siblingFile, "// unrelated, must survive");
        WriteHadesHubFixture();

        var result = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: true);

        Assert.True(result.Removed);
        Assert.True(Directory.Exists(parent));
        Assert.Equal("// unrelated, must survive", File.ReadAllText(siblingFile));
    }

    [Fact]
    public void CleanHadesHub_NeverTouchesAProjectsArcforgeMemoryDirectory()
    {
        // ~/.arcforge/hades-hub/ (home directory, global, retired v1.2 Node hub state) and
        // <projectRoot>/.arcforge/memory/ (per-project, authored, irreplaceable - see
        // V12Importer's own remarks) are two entirely different directories that happen to share
        // the ".arcforge" name. CleanHadesHub takes an explicit directory argument and no
        // project-root parameter at all, so there is no code path through which it could ever
        // reach a project's memory - proven here, not just asserted by inspection.
        var memoryFile = Path.Combine(_projectRoot, ".arcforge", "memory", "conventions.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memoryFile)!);
        File.WriteAllText(memoryFile, "# Authored conventions - never delete\n");
        WriteHadesHubFixture();

        var result = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: true);

        Assert.True(result.Removed);
        Assert.True(File.Exists(memoryFile));
        Assert.Equal("# Authored conventions - never delete\n", File.ReadAllText(memoryFile));
    }

    // ==================================================================
    // M5: AtomicWriteBytes must never leave a stray *.hades-cleanup-tmp file behind when the
    // final rename fails - it wrote the temp file once and nothing else ever cleaned it up.
    // ==================================================================

    [Fact]
    public void CleanClaudeMd_MoveFailsAfterTempFileWritten_TempFileIsCleanedUpNotLeftStray()
    {
        if (!OperatingSystem.IsMacOS())
        {
            // chflags (BSD file flags) is macOS-specific - this suite's CI also runs ubuntu-latest
            // (see .github/workflows/ci.yml), which has no equivalent invocable the same way. A
            // directory-write-permission trick (chmod) was considered instead, but denying a
            // directory's own write bit blocks deleting the SAME temp file just as much as it
            // blocks renaming onto the target - proving nothing distinct about THIS fix, since the
            // temp file could never be removed by anything under that setup either. chflags uchg on
            // ONLY the target file selectively fails the rename while leaving an unrelated file's
            // own delete unaffected - confirmed empirically, not merely assumed - which is what
            // isolates "did the fix clean up after itself" from "was cleanup even possible here".
            return;
        }

        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");
        var targetBefore = Snapshot(ClaudeMdPath);
        var tmpPath = ClaudeMdPath + ".hades-cleanup-tmp";

        // macOS's user-immutable flag - a BSD chflags mechanism entirely separate from the POSIX
        // owner-write permission bit .NET's own FileAttributes.ReadOnly reflects (see IsReadOnly's
        // own doc comment) - so this class's read-only pre-check does NOT intercept this case:
        // AtomicWriteBytes' own File.Move is what actually fails here, exactly the scenario this
        // fix targets, rather than the earlier, differently-refused M9 case.
        RunChflags("uchg", ClaudeMdPath);
        try
        {
            var ex = Record.Exception(() => V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true));

            Assert.NotNull(ex); // the move genuinely failed here - not silently swallowed
            Assert.False(File.Exists(tmpPath), "the temp file must not survive a failed rename");
            AssertUnchanged(ClaudeMdPath, targetBefore); // the failed move touched nothing at the target either
        }
        finally
        {
            RunChflags("nouchg", ClaudeMdPath);
        }
    }

    static void RunChflags(string flag, string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/chflags", [flag, path])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
    }

    // ==================================================================
    // Cross-cutting: the standard every step is held to
    // ==================================================================

    [Fact]
    public void NoGoAhead_AcrossAllFiveTargets_NothingOnDiskChangesAtAll()
    {
        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");
        WriteManifest(
            "{\n" +
            "  \"testables\": [\n" +
            "    \"com.arcforge.hades\"\n" +
            "  ],\n" +
            "  \"dependencies\": {\n" +
            "    \"com.arcforge.hades\": \"file:/Users/mike/Projects/Hades\",\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\"\n" +
            "  }\n" +
            "}\n");
        WriteMcpJson();
        var desktopConfigPath = WriteClaudeDesktopConfig(
            """{ "mcpServers": { "hades": { "command": "node" }, "other": { "command": "x" } } }""");
        WriteHadesHubFixture();

        var claudeMdBefore = Snapshot(ClaudeMdPath);
        var manifestBefore = Snapshot(ManifestPath);
        var mcpJsonBefore = Snapshot(McpJsonPath);
        var desktopConfigBefore = Snapshot(desktopConfigPath);
        var hadesHubBefore = SnapshotDirectory(_hadesHubScratchDir);

        var claudeMdState = DetectClaudeMd();
        var claudeMdResult = V12Cleanup.CleanClaudeMd(_projectRoot, claudeMdState, proceed: false);
        var manifestResult = V12Cleanup.CleanManifest(_projectRoot, proceed: false);
        var mcpConfigResult = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: false);
        var desktopConfigResult = V12Cleanup.CleanClaudeDesktopConfig(desktopConfigPath, proceed: false);
        var hadesHubResult = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: false);

        Assert.False(claudeMdResult.Removed);
        Assert.False(manifestResult.Removed);
        Assert.False(mcpConfigResult.Removed);
        Assert.False(desktopConfigResult.Removed);
        Assert.False(hadesHubResult.Removed);

        AssertUnchanged(ClaudeMdPath, claudeMdBefore);
        AssertUnchanged(ManifestPath, manifestBefore);
        AssertUnchanged(McpJsonPath, mcpJsonBefore);
        AssertUnchanged(desktopConfigPath, desktopConfigBefore);
        AssertDirectoryUnchanged(_hadesHubScratchDir, hadesHubBefore);
    }

    [Fact]
    public void EachStep_IsIndependent_OneRefusingDoesNotBlockOthersSucceeding()
    {
        // CLAUDE.md is deliberately hand-written (Unmarked -> always refused), while the other
        // four targets are all cleanly removable. Spec #10: every destructive step is
        // individually optional; one step's outcome must never depend on another's.
        WriteClaudeMd("# Team conventions\n\nHand-written, nothing to do with Hades.\n");
        WriteManifest("""{ "dependencies": { "com.arcforge.hades": "1.2.3" } }""");
        WriteMcpJson();
        var desktopConfigPath = WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");
        WriteHadesHubFixture();

        var claudeMdResult = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);
        var manifestResult = V12Cleanup.CleanManifest(_projectRoot, proceed: true);
        var mcpConfigResult = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);
        var desktopConfigResult = V12Cleanup.CleanClaudeDesktopConfig(desktopConfigPath, proceed: true);
        var hadesHubResult = V12Cleanup.CleanHadesHub(_hadesHubScratchDir, proceed: true);

        Assert.False(claudeMdResult.Removed);
        Assert.True(File.Exists(ClaudeMdPath));

        Assert.True(manifestResult.Removed);
        Assert.True(mcpConfigResult.Removed);
        Assert.True(desktopConfigResult.Removed);
        Assert.True(hadesHubResult.Removed);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _projectRoot, _claudeDesktopScratchDir, _hadesHubScratchDir })
        {
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
