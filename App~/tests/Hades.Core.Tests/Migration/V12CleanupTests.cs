using Hades.Core.Migration;

namespace Hades.Core.Tests.Migration;

public sealed class V12CleanupTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _claudeDesktopScratchDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

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

    string WriteClaudeDesktopConfig(string content)
    {
        Directory.CreateDirectory(_claudeDesktopScratchDir);
        var path = Path.Combine(_claudeDesktopScratchDir, "claude_desktop_config.json");
        File.WriteAllText(path, content);
        return path;
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
    // .mcp.json: the generated project-level config - removed wholesale
    // ==================================================================

    [Fact]
    public void CleanMcpConfig_NoFile_ReportsNothingToDo()
    {
        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.False(result.Removed);
        Assert.False(File.Exists(McpJsonPath));
    }

    [Fact]
    public void CleanMcpConfig_Present_DeletesTheWholeFile()
    {
        WriteMcpJson();

        var result = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);

        Assert.True(result.Removed);
        Assert.False(File.Exists(McpJsonPath));
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
    // Cross-cutting: the standard every step is held to
    // ==================================================================

    [Fact]
    public void NoGoAhead_AcrossAllFourTargets_NothingOnDiskChangesAtAll()
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

        var claudeMdBefore = Snapshot(ClaudeMdPath);
        var manifestBefore = Snapshot(ManifestPath);
        var mcpJsonBefore = Snapshot(McpJsonPath);
        var desktopConfigBefore = Snapshot(desktopConfigPath);

        var claudeMdState = DetectClaudeMd();
        var claudeMdResult = V12Cleanup.CleanClaudeMd(_projectRoot, claudeMdState, proceed: false);
        var manifestResult = V12Cleanup.CleanManifest(_projectRoot, proceed: false);
        var mcpConfigResult = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: false);
        var desktopConfigResult = V12Cleanup.CleanClaudeDesktopConfig(desktopConfigPath, proceed: false);

        Assert.False(claudeMdResult.Removed);
        Assert.False(manifestResult.Removed);
        Assert.False(mcpConfigResult.Removed);
        Assert.False(desktopConfigResult.Removed);

        AssertUnchanged(ClaudeMdPath, claudeMdBefore);
        AssertUnchanged(ManifestPath, manifestBefore);
        AssertUnchanged(McpJsonPath, mcpJsonBefore);
        AssertUnchanged(desktopConfigPath, desktopConfigBefore);
    }

    [Fact]
    public void EachStep_IsIndependent_OneRefusingDoesNotBlockOthersSucceeding()
    {
        // CLAUDE.md is deliberately hand-written (Unmarked -> always refused), while the other
        // three targets are all cleanly removable. Spec #10: every destructive step is
        // individually optional; one step's outcome must never depend on another's.
        WriteClaudeMd("# Team conventions\n\nHand-written, nothing to do with Hades.\n");
        WriteManifest("""{ "dependencies": { "com.arcforge.hades": "1.2.3" } }""");
        WriteMcpJson();
        var desktopConfigPath = WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");

        var claudeMdResult = V12Cleanup.CleanClaudeMd(_projectRoot, DetectClaudeMd(), proceed: true);
        var manifestResult = V12Cleanup.CleanManifest(_projectRoot, proceed: true);
        var mcpConfigResult = V12Cleanup.CleanMcpConfig(_projectRoot, proceed: true);
        var desktopConfigResult = V12Cleanup.CleanClaudeDesktopConfig(desktopConfigPath, proceed: true);

        Assert.False(claudeMdResult.Removed);
        Assert.True(File.Exists(ClaudeMdPath));

        Assert.True(manifestResult.Removed);
        Assert.True(mcpConfigResult.Removed);
        Assert.True(desktopConfigResult.Removed);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _projectRoot, _claudeDesktopScratchDir })
        {
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
