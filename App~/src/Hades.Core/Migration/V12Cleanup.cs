using System.Text;
using System.Text.Json;
using Hades.Core.Mcp;

namespace Hades.Core.Migration;

/// <summary>Outcome of <see cref="V12Cleanup.CleanClaudeMd"/>.</summary>
public sealed record ClaudeMdCleanupResult
{
    public required bool Removed { get; init; }

    /// <summary>Always populated - explains the outcome whether or not anything was removed, so a
    /// caller can surface it to the user directly.</summary>
    public required string Message { get; init; }

    /// <summary>True only when <see cref="Removed"/> is true AND the file still contains
    /// non-whitespace content outside the block that was removed - e.g. the real
    /// Hades-Unity-Client shape, where 60 unmarked lines from an older template revision sit
    /// ahead of the marked block. "Cleanup succeeded" and "the file looks right afterwards" are
    /// different claims: this flag is how that difference gets reported instead of silently
    /// disappearing. It says nothing about WHOSE content remains (see
    /// <see cref="V12Detector"/>'s own remarks on why that can't be determined) - only that some
    /// exists and was correctly left alone.</summary>
    public bool RemainingContentOutsideBlock { get; init; }
}

/// <summary>Outcome of <see cref="V12Cleanup.CleanManifest"/>.</summary>
public sealed record ManifestCleanupResult
{
    public required bool Removed { get; init; }
    public required string Message { get; init; }

    /// <summary>How many occurrences of the package id were found - in the reference project this
    /// is 2 (a "testables" array element and a "dependencies" entry), not 1. Populated even when
    /// <see cref="Removed"/> is false (no go-ahead), so a caller can show an accurate count before
    /// asking for confirmation.</summary>
    public required int OccurrencesFound { get; init; }

    /// <summary>Always populated, regardless of outcome: leaving both v1.2's package entry and
    /// the app installed means both will try to bind the same MCP port and conflict.</summary>
    public required string PortConflictWarning { get; init; }
}

/// <summary>Outcome of <see cref="V12Cleanup.CleanMcpConfig"/>.</summary>
public sealed record McpConfigCleanupResult
{
    public required bool Removed { get; init; }
    public required string Message { get; init; }

    /// <summary>How many "hades" entries were found under <c>mcpServers</c>, at the TOP LEVEL of
    /// the file - always 0 or 1 in practice (JSON object keys are unique), but typed as a count for
    /// exact symmetry with <see cref="ClaudeDesktopConfigCleanupResult.OccurrencesFound"/>, which
    /// the same underlying <see cref="FindJsonSpans"/> scan already powers. Populated on
    /// every path, including a missing file, malformed JSON, and a not-yet-confirmed (proceed:
    /// false) call.</summary>
    public required int OccurrencesFound { get; init; }
}

/// <summary>Outcome of <see cref="V12Cleanup.CleanClaudeDesktopConfig"/>.</summary>
public sealed record ClaudeDesktopConfigCleanupResult
{
    public required bool Removed { get; init; }
    public required string Message { get; init; }

    /// <summary>Always populated, regardless of outcome: this file is global and per-user, not
    /// per-project - the confirmation must say so plainly rather than implying project scope.</summary>
    public required string ScopeWarning { get; init; }

    /// <summary>How many "hades" entries were found under <c>mcpServers</c> - always 0 or 1 in
    /// practice (JSON object keys are unique), but typed as a count for exact symmetry with
    /// <see cref="ManifestCleanupResult.OccurrencesFound"/>, which the same underlying
    /// <see cref="FindJsonSpans"/> scan already powers. Populated on every path, including a
    /// missing file, malformed JSON, and a not-yet-confirmed (<c>proceed: false</c>) call - unlike
    /// every other target here, this file has no companion <see cref="V12Detector"/> scan (it is global, not
    /// per-project - see this class's own remarks), so this field is a caller's ONLY way to learn
    /// whether there is anything here worth offering to clean up at all.</summary>
    public required int OccurrencesFound { get; init; }
}

/// <summary>Outcome of <see cref="V12Cleanup.CleanHadesHub"/>.</summary>
public sealed record HadesHubCleanupResult
{
    public required bool Removed { get; init; }
    public required string Message { get; init; }

    /// <summary>Whether <c>~/.arcforge/hades-hub/</c> existed at all - always populated, including
    /// when <see cref="Removed"/> is false (no go-ahead, or there was nothing there to begin with).
    /// A directory-existence check, not a count: unlike
    /// <see cref="ManifestCleanupResult.OccurrencesFound"/> there is no JSON, and nothing inside
    /// this directory for a caller to count - only whether the directory itself is there. Exists
    /// for the same reason <see cref="ClaudeDesktopConfigCleanupResult.OccurrencesFound"/> does:
    /// this target has no companion <see cref="V12Detector"/> scan (it is global, not per-project),
    /// so this field is a caller's ONLY way to learn whether there is anything here worth offering
    /// to clean up at all.</summary>
    public required bool Found { get; init; }
}

/// <summary>
/// The v1.2 config-cleanup steps spec #4 §5 lists: the marked <c>CLAUDE.md</c> block, the
/// <c>Packages/manifest.json</c> package entry, the generated project-level <c>.mcp.json</c>, and
/// the <c>hades</c> entry in the global <c>claude_desktop_config.json</c> (all under §5, "optional")
/// - plus one target §5's own "Clean config" row never names but §1 retires all the same:
/// <c>~/.arcforge/hades-hub/</c>, the retired v1.2 stdio launcher and its hub state. §1's "what is
/// not an install unit any more" list names <c>~/.arcforge/hades-hub/launcher.js</c> explicitly -
/// see <see cref="CleanHadesHub"/>'s own doc comment for why cleanup here follows the launcher's
/// whole directory, not just that one file. Unlike <see cref="V12Detector"/> (read-only) and
/// <see cref="V12Importer"/> (additive - copies into app storage, never touches the source), every
/// method here can delete or rewrite a file (or directory) in place.
///
/// <para><b>Five independent methods, not one.</b> Spec #10: "Migration is always offered, never
/// performed silently." There is deliberately no <c>CleanupAll</c> - each step is its own call, its
/// own <paramref name="proceed"><c>proceed</c></paramref> parameter with no default, and its own
/// result. Calling one never performs another; refusing one never blocks the rest from
/// running.</para>
///
/// <para><b>CLAUDE.md reuses <see cref="V12Detector"/>'s classification rather than re-deriving
/// it.</b> <see cref="CleanClaudeMd"/> takes the already-computed <see cref="ClaudeMdState"/> as a
/// parameter. Re-implementing marker detection here - even slightly differently - would risk the
/// exact failure mode <see cref="V12Importer"/>'s own remarks warn about for memory import: two
/// classifiers with different rules, one of them silently wrong. <see cref="ClaudeMdShape.Unmarked"/>
/// is never deleted by this class, under any value of <paramref name="proceed"/> - that is not a
/// confirmation gate, it is a hard rule (spec #5: "ask, never delete").</para>
///
/// <para><b>Three of the other four targets get their own JSON scans</b> - manifest.json, the
/// generated .mcp.json, and claude_desktop_config.json, for exactly where the package id / "hades"
/// key sits in the JSON (byte offsets <see cref="V12Detector"/> never computes). .mcp.json is not
/// "an install unit any more" per spec #4 §1 and v1.2 always wrote it wholesale, but a project-level
/// MCP config file is exactly as easy for a user (or another tool) to hand-add a second server to
/// afterwards as claude_desktop_config.json is - see <see cref="CleanMcpConfig"/>'s own doc comment
/// for the wholesale-delete defect this replaced. Only <c>~/.arcforge/hades-hub/</c> stays
/// existence-only: it is a DIRECTORY, not a JSON file, wholesale-generated by the retired v1.2 Node
/// hub with nothing inside it ever user-authored (see <see cref="CleanHadesHub"/>'s own doc
/// comment).</para>
///
/// <para><b>JSON edits are byte-level surgery, never parse-and-reserialize.</b> Round-tripping
/// through <see cref="JsonSerializer"/> (or <c>Newtonsoft.Json</c>, which is how v1.2's own
/// <c>MCPClientConfig.UpdateClaudeDesktopConfig</c> wrote this exact file) reformats whatever it
/// touches - different indentation conventions, possible key reordering, a rewritten trailing
/// newline. For a file the user did not ask to have reformatted, that is a dirty diff nobody asked
/// for. Every write here instead locates the exact byte span of the property or array element being
/// removed with <see cref="Utf8JsonReader"/> and splices it out of the original bytes, so every byte
/// that is not part of the removed entry - including its own file's overall formatting - survives
/// untouched.</para>
/// </summary>
public static class V12Cleanup
{
    /// <summary>The key both the v1.2-generated <c>.mcp.json</c> and <c>claude_desktop_config.json</c>
    /// use under their <c>mcpServers</c> map - confirmed against the reference project's real,
    /// live copies of both files.</summary>
    public const string McpServerKey = "hades";

    /// <summary>Where Claude Desktop keeps its config on this machine. A pure path computation -
    /// never reads or creates anything. Mirrors v1.2's own
    /// <c>MCPClientConfig.GetDesktopConfigPath</c> (macOS branch).</summary>
    public static string ClaudeDesktopConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "Claude", "claude_desktop_config.json");

    // ======================================================================
    // 1. CLAUDE.md
    // ======================================================================

    /// <summary>
    /// Removes the <c>&lt;!-- HADES:START --&gt;</c> / <c>&lt;!-- HADES:END --&gt;</c> block from
    /// <paramref name="projectRoot"/>'s <c>CLAUDE.md</c>, and only that block - every byte outside
    /// <paramref name="claudeMdState"/>'s <see cref="ClaudeMdMarkedBlock"/> survives exactly,
    /// including any unmarked content that sits alongside it (see this file's own remarks on the
    /// real hybrid shape).
    /// </summary>
    /// <param name="claudeMdState">The classification a prior <see cref="V12Detector.Detect"/> call
    /// already computed for this project - reused, not re-derived. Passing a state that does not
    /// actually describe the current file's marker positions (stale, or from a different file) is
    /// safe: it is re-validated against the file's live content below and refused if it no longer
    /// matches, never blindly trusted.</param>
    /// <param name="proceed">Must be explicitly true to delete anything. There is no default - a
    /// default-on destructive flag is exactly the trap this parameter exists to avoid. Only
    /// meaningful when <paramref name="claudeMdState"/>'s <see cref="ClaudeMdState.Shape"/> is
    /// <see cref="ClaudeMdShape.Marked"/>; <see cref="ClaudeMdShape.Unmarked"/> is refused
    /// unconditionally, regardless of this value.</param>
    public static ClaudeMdCleanupResult CleanClaudeMd(string projectRoot, ClaudeMdState claudeMdState, bool proceed)
    {
        switch (claudeMdState.Shape)
        {
            case ClaudeMdShape.Absent:
                return new ClaudeMdCleanupResult { Removed = false, Message = "No CLAUDE.md file; nothing to clean." };

            case ClaudeMdShape.Unmarked:
                // Not a confirmation gate - proceed is not even consulted here. See this class's
                // own remarks: Unmarked covers both "Hades wrote this wholesale" and "the user
                // wrote this," reliably indistinguishable, so neither is ever auto-removed.
                return new ClaudeMdCleanupResult
                {
                    Removed = false,
                    Message = "CLAUDE.md has no HADES:START/END marker pair. Whether Hades wrote this file " +
                        "wholesale or the user wrote it by hand cannot be reliably told apart, so cleanup " +
                        "never deletes it - ask the user instead.",
                };

            case ClaudeMdShape.Marked:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(claudeMdState), claudeMdState.Shape, "Unknown ClaudeMdShape.");
        }

        var block = claudeMdState.MarkedBlock
            ?? throw new ArgumentException("claudeMdState.Shape is Marked but MarkedBlock is null.", nameof(claudeMdState));

        var path = Path.Combine(projectRoot, "CLAUDE.md");
        if (!File.Exists(path))
        {
            return new ClaudeMdCleanupResult
            {
                Removed = false,
                Message = "CLAUDE.md no longer exists on disk (it did when classified); nothing to remove.",
            };
        }

        var rawBytes = File.ReadAllBytes(path);

        // Checked BEFORE the UTF-8 decode below, not after: every read/write in this class is
        // byte-level UTF-8 (see this class's own "JSON edits are byte-level surgery" remarks and
        // HasUtf16Bom's own doc comment), so genuinely UTF-16 content would otherwise decode into
        // silent mojibake, fail the marker re-match below, and refuse with a MISLEADING "changed
        // since it was classified" message that never names the real cause - a dead end V12Detector
        // itself does not hit (File.ReadAllText auto-detects and correctly decodes a UTF-16 BOM), so
        // this exact file can reach "Marked" from detection and then dead-end here without this
        // check.
        if (HasUtf16Bom(rawBytes))
        {
            return new ClaudeMdCleanupResult
            {
                Removed = false,
                Message = "CLAUDE.md is UTF-16 encoded; cleanup only supports UTF-8 (or plain ASCII) text files. Convert it to UTF-8 and try again.",
            };
        }

        var (bomStrippedBody, hadBom) = StripUtf8Bom(rawBytes);
        var content = Encoding.UTF8.GetString(bomStrippedBody);

        if (block.Start < 0 || block.End > content.Length || block.Start >= block.End)
        {
            return new ClaudeMdCleanupResult
            {
                Removed = false,
                Message = "The supplied marked-block offsets do not fit the current file; refusing to guess. Re-run detection and try again.",
            };
        }

        // Defense against a stale or mismatched state: re-confirm the offsets still bound actual
        // marker text before trusting them for deletion. If the file changed since Detect ran (or
        // this state came from a different file entirely), this is where that gets caught rather
        // than silently deleting the wrong bytes.
        if (!MatchesAt(content, block.Start, V12Detector.StartMarker) || !MatchesAt(content, block.End - V12Detector.EndMarker.Length, V12Detector.EndMarker))
        {
            return new ClaudeMdCleanupResult
            {
                Removed = false,
                Message = "CLAUDE.md changed since it was classified and no longer matches the expected marker block; refusing to modify it.",
            };
        }

        // Defense against nested/ambiguous markers: V12Detector.ReadClaudeMd pairs the FIRST
        // start with the FIRST end, which for overlapping markers still yields Shape.Marked - a
        // syntactically valid but semantically ambiguous pair. Refuse rather than guess whenever
        // more than one marker of either kind exists anywhere in the file.
        if (CountOccurrences(content, V12Detector.StartMarker) != 1 || CountOccurrences(content, V12Detector.EndMarker) != 1)
        {
            return new ClaudeMdCleanupResult
            {
                Removed = false,
                Message = "CLAUDE.md contains more than one HADES:START or HADES:END marker; refusing to guess which pair is the real block.",
            };
        }

        // Computed BEFORE the proceed check, deliberately: this is a pure read of content already
        // in hand (no write happens here), so it costs nothing to make available on a dry run too.
        // Before this fix, RemainingContentOutsideBlock was only ever computed in the Removed=true
        // branch below - meaning the one fact a caller needs BEFORE agreeing (stale unmarked
        // content, like the ~60 lines ahead of the marked block in the reference project's own
        // CLAUDE.md, will survive) was unavailable until after the file had already been changed.
        var remainder = content.Remove(block.Start, block.End - block.Start);
        var remainingOutside = !string.IsNullOrWhiteSpace(remainder);

        if (!proceed)
        {
            return new ClaudeMdCleanupResult
            {
                Removed = false,
                Message = remainingOutside
                    ? "Found a well-formed HADES:START/END block, with other content outside it that will remain untouched; not removed yet (no go-ahead)."
                    : "Found a well-formed HADES:START/END block; not removed (no go-ahead).",
                RemainingContentOutsideBlock = remainingOutside,
            };
        }

        if (IsReadOnly(path))
        {
            return new ClaudeMdCleanupResult
            {
                Removed = false,
                Message = "CLAUDE.md is read-only; refusing to modify it. Remove the read-only flag and try again.",
                RemainingContentOutsideBlock = remainingOutside,
            };
        }

        AtomicWriteUtf8Text(path, remainder, hadBom);

        return new ClaudeMdCleanupResult
        {
            Removed = true,
            Message = remainingOutside
                ? "Removed the HADES:START/END block. Other content outside the block remains in the file, untouched."
                : "Removed the HADES:START/END block. Every other byte in the file is untouched.",
            RemainingContentOutsideBlock = remainingOutside,
        };
    }

    static bool MatchesAt(string content, int start, string marker) =>
        start >= 0 && start + marker.Length <= content.Length
        && string.CompareOrdinal(content, start, marker, 0, marker.Length) == 0;

    static int CountOccurrences(string content, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }
        return count;
    }

    // ======================================================================
    // 2. Packages/manifest.json
    // ======================================================================

    /// <summary>
    /// Removes every occurrence of <see cref="V12Detector.PackageId"/> from
    /// <paramref name="projectRoot"/>'s <c>Packages/manifest.json</c> - both as a
    /// <c>dependencies</c> entry and as a <c>testables</c> array element, the two places the
    /// reference project's real manifest carries it. Every other byte in the file - key order,
    /// indentation, every other entry - survives exactly; nothing is reformatted.
    /// </summary>
    /// <param name="proceed">Must be explicitly true to write anything. No default.</param>
    public static ManifestCleanupResult CleanManifest(string projectRoot, bool proceed)
    {
        var portWarning = $"If v1.2's package entry stays in Packages/manifest.json while the app is also " +
            $"running, both will try to bind port {McpDefaults.Port} and conflict.";

        var path = Path.Combine(projectRoot, "Packages", "manifest.json");
        if (!File.Exists(path))
        {
            return new ManifestCleanupResult { Removed = false, Message = "No Packages/manifest.json.", OccurrencesFound = 0, PortConflictWarning = portWarning };
        }

        var raw = File.ReadAllBytes(path);
        var (body, hadBom) = StripUtf8Bom(raw);

        List<(int Start, int End)> spans;
        try
        {
            spans = FindJsonSpans(body,
            [
                new JsonRemovalTarget(JsonTargetKind.Property, "dependencies", V12Detector.PackageId),
                new JsonRemovalTarget(JsonTargetKind.ArrayElement, "testables", V12Detector.PackageId),
            ]);
        }
        catch (JsonException)
        {
            return new ManifestCleanupResult { Removed = false, Message = "manifest.json is not valid JSON; refusing to modify it.", OccurrencesFound = 0, PortConflictWarning = portWarning };
        }

        if (spans.Count == 0)
        {
            return new ManifestCleanupResult { Removed = false, Message = "No com.arcforge.hades entry found in manifest.json.", OccurrencesFound = 0, PortConflictWarning = portWarning };
        }

        if (!proceed)
        {
            return new ManifestCleanupResult
            {
                Removed = false,
                Message = $"Found {spans.Count} occurrence(s) of com.arcforge.hades in manifest.json; not removed (no go-ahead).",
                OccurrencesFound = spans.Count,
                PortConflictWarning = portWarning,
            };
        }

        if (IsReadOnly(path))
        {
            return new ManifestCleanupResult
            {
                Removed = false,
                Message = "manifest.json is read-only; refusing to modify it. Remove the read-only flag and try again.",
                OccurrencesFound = spans.Count,
                PortConflictWarning = portWarning,
            };
        }

        var newBody = RemoveJsonEntries(body, spans);
        AtomicWriteBytes(path, hadBom ? Prepend(Utf8Bom, newBody) : newBody);

        return new ManifestCleanupResult
        {
            Removed = true,
            Message = $"Removed {spans.Count} occurrence(s) of com.arcforge.hades from manifest.json.",
            OccurrencesFound = spans.Count,
            PortConflictWarning = portWarning,
        };
    }

    // ======================================================================
    // 3. .mcp.json - the generated project-level config
    // ======================================================================

    /// <summary>
    /// Removes the <c>"hades"</c> entry under <c>mcpServers</c> in <paramref name="projectRoot"/>'s
    /// generated <c>.mcp.json</c> - surgical byte splice, identical in shape to
    /// <see cref="CleanClaudeDesktopConfig"/>'s own treatment of the exact same <c>mcpServers</c>/
    /// <c>"hades"</c> structure in a different file. Every other server entry, and every other key
    /// in the file, survives byte-for-byte.
    ///
    /// <para><b>Used to delete the whole file wholesale instead.</b> The reasoning at the time -
    /// v1.2 always wrote this file wholesale, so there is no "whose content is this" question to
    /// resolve - assumed the file could never carry anything but Hades' own entry. That assumption
    /// does not survive contact with a real project: a project-level <c>.mcp.json</c> is exactly as
    /// easy for a user (or another tool) to hand-add a second server to, after v1.2 wrote it, as
    /// <c>claude_desktop_config.json</c> is - and a wholesale <see cref="File.Delete(string)"/>
    /// destroyed every one of those other entries along with Hades' own, exactly the risk this
    /// class's own class doc comment already warns <see cref="CleanClaudeDesktopConfig"/> against
    /// ("a user may have several MCP servers configured, and clobbering them would be far worse
    /// than the mess being cleaned up").</para>
    ///
    /// <para><b>Never deletes the file, even when "hades" is the only server.</b> Mirrors
    /// <see cref="CleanClaudeDesktopConfig"/>'s own documented behaviour exactly (see that method's
    /// own "hades is the only server" test) rather than inventing a second rule for "the file is now
    /// pointless": an empty <c>mcpServers: {}</c> is still valid JSON a user - or another tool - may
    /// still add to later, and guessing at "nothing else meaningful remains" is exactly the kind of
    /// silent judgment call this whole class avoids elsewhere.</para>
    /// </summary>
    /// <param name="proceed">Must be explicitly true to write anything. No default.</param>
    public static McpConfigCleanupResult CleanMcpConfig(string projectRoot, bool proceed)
    {
        var path = Path.Combine(projectRoot, ".mcp.json");
        if (!File.Exists(path))
        {
            return new McpConfigCleanupResult { Removed = false, Message = "No .mcp.json file.", OccurrencesFound = 0 };
        }

        var raw = File.ReadAllBytes(path);
        var (body, hadBom) = StripUtf8Bom(raw);

        List<(int Start, int End)> spans;
        try
        {
            spans = FindJsonSpans(body, [new JsonRemovalTarget(JsonTargetKind.Property, "mcpServers", McpServerKey)]);
        }
        catch (JsonException)
        {
            return new McpConfigCleanupResult { Removed = false, Message = ".mcp.json is not valid JSON; refusing to modify it.", OccurrencesFound = 0 };
        }

        if (spans.Count == 0)
        {
            return new McpConfigCleanupResult { Removed = false, Message = "No 'hades' entry found under mcpServers in .mcp.json.", OccurrencesFound = 0 };
        }

        if (!proceed)
        {
            return new McpConfigCleanupResult
            {
                Removed = false,
                Message = $"Found {spans.Count} occurrence(s) of the 'hades' entry in .mcp.json; not removed (no go-ahead).",
                OccurrencesFound = spans.Count,
            };
        }

        if (IsReadOnly(path))
        {
            return new McpConfigCleanupResult
            {
                Removed = false,
                Message = ".mcp.json is read-only; refusing to modify it. Remove the read-only flag and try again.",
                OccurrencesFound = spans.Count,
            };
        }

        var newBody = RemoveJsonEntries(body, spans);
        AtomicWriteBytes(path, hadBom ? Prepend(Utf8Bom, newBody) : newBody);

        return new McpConfigCleanupResult
        {
            Removed = true,
            Message = "Removed the 'hades' entry from .mcp.json. Every other server entry is untouched.",
            OccurrencesFound = spans.Count,
        };
    }

    // ======================================================================
    // 4. claude_desktop_config.json - global, and easy to get wrong
    // ======================================================================

    /// <summary>
    /// Removes the <c>"hades"</c> entry under <c>mcpServers</c> in the <c>claude_desktop_config.json</c>
    /// at <paramref name="configPath"/>. Every other server entry, and every other key in the file,
    /// survives byte-for-byte - a user may have several MCP servers configured, and clobbering them
    /// would be far worse than the mess being cleaned up.
    ///
    /// <para>Takes an explicit path rather than a project root: this file is global and per-user,
    /// not per-project, so it does not fit <see cref="V12Detector.Detect"/>'s
    /// <c>Detect(projectRoot)</c> shape. Production callers pass <see cref="ClaudeDesktopConfigPath"/>;
    /// tests pass a scratch copy. This method never computes the real path itself, so it is
    /// structurally impossible for a test to reach the real file by accident.</para>
    /// </summary>
    /// <param name="proceed">Must be explicitly true to write anything. No default.</param>
    public static ClaudeDesktopConfigCleanupResult CleanClaudeDesktopConfig(string configPath, bool proceed)
    {
        const string scopeWarning = "This changes claude_desktop_config.json globally for Claude Desktop on " +
            "this machine, not just this project - any other MCP server entries are left untouched.";

        if (!File.Exists(configPath))
        {
            return new ClaudeDesktopConfigCleanupResult { Removed = false, Message = "No claude_desktop_config.json at the given path.", ScopeWarning = scopeWarning, OccurrencesFound = 0 };
        }

        var raw = File.ReadAllBytes(configPath);
        var (body, hadBom) = StripUtf8Bom(raw);

        List<(int Start, int End)> spans;
        try
        {
            spans = FindJsonSpans(body, [new JsonRemovalTarget(JsonTargetKind.Property, "mcpServers", McpServerKey)]);
        }
        catch (JsonException)
        {
            return new ClaudeDesktopConfigCleanupResult { Removed = false, Message = "claude_desktop_config.json is not valid JSON; refusing to modify it.", ScopeWarning = scopeWarning, OccurrencesFound = 0 };
        }

        if (spans.Count == 0)
        {
            return new ClaudeDesktopConfigCleanupResult { Removed = false, Message = "No 'hades' entry found under mcpServers in claude_desktop_config.json.", ScopeWarning = scopeWarning, OccurrencesFound = 0 };
        }

        if (!proceed)
        {
            return new ClaudeDesktopConfigCleanupResult { Removed = false, Message = "Found the 'hades' entry; not removed (no go-ahead).", ScopeWarning = scopeWarning, OccurrencesFound = spans.Count };
        }

        if (IsReadOnly(configPath))
        {
            return new ClaudeDesktopConfigCleanupResult
            {
                Removed = false,
                Message = "claude_desktop_config.json is read-only; refusing to modify it. Remove the read-only flag and try again.",
                ScopeWarning = scopeWarning,
                OccurrencesFound = spans.Count,
            };
        }

        var newBody = RemoveJsonEntries(body, spans);
        AtomicWriteBytes(configPath, hadBom ? Prepend(Utf8Bom, newBody) : newBody);

        return new ClaudeDesktopConfigCleanupResult
        {
            Removed = true,
            Message = "Removed the 'hades' entry from claude_desktop_config.json. Every other server entry is untouched.",
            ScopeWarning = scopeWarning,
            OccurrencesFound = spans.Count,
        };
    }

    // ======================================================================
    // 5. ~/.arcforge/hades-hub/ - the retired v1.2 Node launcher, removed wholesale
    // ======================================================================

    /// <summary>Where the v1.2 stdio launcher (<c>launcher.js</c>) and its hub state
    /// (<c>hub.json</c>, <c>hub-path.json</c>, and whatever else the hub itself writes at runtime -
    /// e.g. a <c>pending/</c> directory, confirmed present on the reference machine) live: directly
    /// under the user's home directory, one level below <c>~/.arcforge/</c>. A pure path
    /// computation - never reads or creates anything. <c>~/.arcforge/</c> ITSELF is not this
    /// property's concern, and is never touched by <see cref="CleanHadesHub"/> below - it holds
    /// other things unrelated to the v1.2 hub (confirmed on the reference machine:
    /// <c>mcp-bridge.js</c>, a <c>servers/</c> directory) that this class has no business
    /// deleting.</summary>
    public static string HadesHubDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".arcforge", "hades-hub");

    /// <summary>
    /// Deletes <paramref name="hadesHubDirectory"/> wholesale, recursively. Spec #4 §1's "what is
    /// not an install unit any more" list names <c>~/.arcforge/hades-hub/launcher.js</c> explicitly
    /// - the v1.2 stdio launcher Claude Code's old <c>.mcp.json</c> pointed <c>command</c>/<c>args</c>
    /// at (see this file's own test fixtures for a live example of that shape) - but the launcher
    /// script is never alone there: <c>hub.json</c> (the running hub's port/pid/startedAt) and
    /// <c>hub-path.json</c> (where its compiled entry point lives) sit alongside it, and the hub
    /// itself writes further runtime state into the same directory (a <c>pending/</c> directory,
    /// confirmed present on the reference machine, that neither spec #4 §1 nor this method's own
    /// name mentions). Every one of those is wholesale-generated by the retired v1.2 Node
    /// hub/launcher system - none of it is ever user-authored - so, exactly like
    /// <see cref="CleanMcpConfig"/>'s own ".mcp.json goes whole or not at all" reasoning, there is no
    /// "whose content is this" question to resolve file by file: the whole DIRECTORY goes, or none
    /// of it does. Enumerating a fixed set of filenames instead would silently leave behind whatever
    /// the hub wrote that this method does not happen to name - already proven necessary by
    /// <c>pending/</c> alone.
    ///
    /// <para><b>Never the parent.</b> Only <paramref name="hadesHubDirectory"/> itself is removed -
    /// never <c>Path.GetDirectoryName(hadesHubDirectory)</c> (<c>~/.arcforge/</c> in production),
    /// which is shared with things this class has no business touching (see
    /// <see cref="HadesHubDirectory"/>'s own doc comment). Nor is this ever confusable with a
    /// PROJECT's own <c>.arcforge/memory/</c> - that directory sits under a project root and holds
    /// authored, irreplaceable content (<see cref="V12Importer"/>'s whole reason for existing);
    /// this one sits under the user's HOME directory and holds nothing but retired Node-hub scratch
    /// state. <paramref name="hadesHubDirectory"/> is the only path this method ever touches - there
    /// is no project-root parameter here for a project's memory path to be confused with,
    /// structurally, the same guarantee <see cref="CleanClaudeDesktopConfig"/>'s own
    /// <c>configPath</c> parameter gives ("structurally impossible for a test to reach the real file
    /// by accident" - see that method's own doc comment).</para>
    ///
    /// <para>Takes an explicit directory rather than deriving one, for the identical reason
    /// <see cref="CleanClaudeDesktopConfig"/> takes an explicit <c>configPath</c>: this is global
    /// and per-user, not per-project, so it does not fit <see cref="V12Detector.Detect"/>'s
    /// <c>Detect(projectRoot)</c> shape. Production callers pass <see cref="HadesHubDirectory"/>;
    /// tests pass a scratch directory.</para>
    /// </summary>
    /// <param name="proceed">Must be explicitly true to delete anything. No default.</param>
    public static HadesHubCleanupResult CleanHadesHub(string hadesHubDirectory, bool proceed)
    {
        const string what = "~/.arcforge/hades-hub/ - the retired v1.2 Node launcher and its hub state " +
            "(launcher.js, hub.json, hub-path.json, and anything else left there)";

        if (!Directory.Exists(hadesHubDirectory))
        {
            return new HadesHubCleanupResult
            {
                Removed = false,
                Found = false,
                Message = "No ~/.arcforge/hades-hub/ directory found; nothing to remove.",
            };
        }

        if (!proceed)
        {
            return new HadesHubCleanupResult
            {
                Removed = false,
                Found = true,
                Message = $"Found {what}; not removed (no go-ahead). The whole directory would be " +
                    "removed, but ~/.arcforge/ itself and everything else under it would be untouched.",
            };
        }

        try
        {
            Directory.Delete(hadesHubDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file (another process has it open) or a permissions problem can leave this
            // partway through - possibly with some, but not all, of the directory already gone.
            // Reported as a normal result, never a bare unhandled exception: Found stays true
            // (Directory.Delete throwing means the top-level directory itself was NOT removed, so
            // it necessarily still exists - no extra Directory.Exists re-check needed), which is
            // exactly what keeps a later dry-run honest about there still being something here.
            return new HadesHubCleanupResult
            {
                Removed = false,
                Found = true,
                Message = $"Could not fully remove {what}: {ex.Message} The directory may be "
                    + "partially removed; check what is still using it and try again.",
            };
        }

        return new HadesHubCleanupResult
        {
            Removed = true,
            Found = true,
            Message = $"Removed {what}. ~/.arcforge/ itself and everything else under it is untouched.",
        };
    }

    // ======================================================================
    // Shared JSON surgery: find exactly what to remove, then splice bytes -
    // never parse-and-reserialize (see this class's own remarks on why).
    // ======================================================================

    enum JsonTargetKind { Property, ArrayElement }

    readonly record struct JsonRemovalTarget(JsonTargetKind Kind, string Container, string Name);

    /// <summary>
    /// Single pass over <paramref name="json"/> with <see cref="Utf8JsonReader"/>, tracking which
    /// named container (object or array) each token sits directly inside, AND how deep that
    /// container itself sits relative to the document root. For each <see cref="JsonRemovalTarget"/>
    /// that matches - a property with the given name directly inside a TOP-LEVEL object reached via
    /// the given container property name, or a string array element with the given value directly
    /// inside such a TOP-LEVEL array - records the exact byte span of that property (name through
    /// value, whatever the value's shape) or array element. No surrounding whitespace or comma is
    /// included: that is <see cref="ComputeDeletionRange"/>'s job.
    ///
    /// <para><b>"Top-level" is load-bearing, not incidental.</b> Every real target here
    /// (<c>mcpServers</c>, <c>dependencies</c>, <c>testables</c>) names a container that is meant to
    /// be a direct child of the JSON root - but matching on the container's NAME alone, with no
    /// notion of depth, cannot tell that apart from a container with the SAME name sitting anywhere
    /// else in the document. A real, live shape this bit: a user's <c>claude_desktop_config.json</c>
    /// held a full backup copy of an older config - itself containing its own nested
    /// <c>mcpServers.hades</c> - inside an unrelated top-level key; the depth-blind version of this
    /// scan matched the NESTED "hades" as readily as the real, top-level one and spliced out
    /// whichever the reader reached, including entries buried inside the user's own backup blob.
    /// <paramref name="targets"/>' own <see cref="JsonRemovalTarget.Container"/> is always intended
    /// as a TOP-LEVEL container name for exactly this reason, so every match here additionally
    /// requires the enclosing container's depth to be <see cref="TopLevelContainerDepth"/> - the
    /// container itself must be the value of a property that lives directly in the document's root
    /// object, never a same-named container nested deeper.</para>
    /// </summary>
    static List<(int Start, int End)> FindJsonSpans(byte[] json, IReadOnlyList<JsonRemovalTarget> targets)
    {
        var found = new List<(int Start, int End)>();
        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);

        // (Name, Depth) per open container. The pre-seeded sentinel sits one level "outside" the
        // document root, so the root object's own StartObject frame below lands at depth 0, and any
        // container that is a direct child of the root (mcpServers, dependencies, testables) lands
        // at depth 1 - see TopLevelContainerDepth and this method's own doc comment.
        var containerStack = new Stack<(string? Name, int Depth)>();
        containerStack.Push((null, -1));
        string? pendingName = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                {
                    var name = reader.GetString()!;
                    var (container, depth) = containerStack.Peek();

                    JsonRemovalTarget? matched = null;
                    foreach (var t in targets)
                    {
                        if (t.Kind == JsonTargetKind.Property && depth == TopLevelContainerDepth
                            && t.Container == container && t.Name == name) { matched = t; break; }
                    }

                    if (matched is not null)
                    {
                        var start = (int)reader.TokenStartIndex;
                        reader.Read(); // advance onto the value
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                        found.Add((start, (int)reader.BytesConsumed));
                        // Deliberately do not push a container frame for this value - it has been
                        // fully consumed (Skip, if used, already passed its matching End token),
                        // so the main loop never sees its children.
                    }
                    else
                    {
                        pendingName = name;
                    }
                    break;
                }

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    containerStack.Push((pendingName, containerStack.Peek().Depth + 1));
                    pendingName = null;
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    containerStack.Pop();
                    break;

                case JsonTokenType.String:
                {
                    var (container, depth) = containerStack.Peek();
                    var value = reader.GetString();
                    foreach (var t in targets)
                    {
                        if (t.Kind == JsonTargetKind.ArrayElement && depth == TopLevelContainerDepth
                            && t.Container == container && t.Name == value)
                        {
                            found.Add(((int)reader.TokenStartIndex, (int)reader.BytesConsumed));
                            break;
                        }
                    }
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>The depth (see <see cref="FindJsonSpans"/>'s own doc comment for exactly how depth
    /// is counted) a <see cref="JsonRemovalTarget.Container"/> must sit at to match: 1, meaning the
    /// container is the value of a property declared directly on the document's root object -
    /// "top-level" for every real target this class scans for today.</summary>
    const int TopLevelContainerDepth = 1;

    /// <summary>Applies <see cref="ComputeDeletionRange"/> to every span (all computed against the
    /// original, untouched bytes), coalesces any that overlap (see <see cref="CoalesceOverlapping"/>
    /// for why two ADJACENT matching entries need this), and splices the result out from rightmost
    /// to leftmost, so removing one never invalidates the offsets of another still to be
    /// applied.</summary>
    static byte[] RemoveJsonEntries(byte[] json, List<(int Start, int End)> spans)
    {
        var deletions = CoalesceOverlapping(spans
            .Select(s => ComputeDeletionRange(json, s.Start, s.End))
            .OrderBy(d => d.Start)
            .ToList());

        var result = json;
        foreach (var (start, end) in deletions.OrderByDescending(d => d.Start))
        {
            var next = new byte[result.Length - (end - start)];
            Array.Copy(result, 0, next, 0, start);
            Array.Copy(result, end, next, start, result.Length - end);
            result = next;
        }
        return result;
    }

    /// <summary>
    /// Merges overlapping (or merely touching) deletion ranges into one. Two matching entries
    /// sitting next to each other in the same array each independently extend to reach the ONE
    /// comma between them - the first's own <see cref="ComputeDeletionRange"/> range extends
    /// forward over it (removing its own trailing comma), the second's extends backward over the
    /// SAME comma (removing its own leading comma) - so their ranges overlap on that shared byte.
    /// Splicing both independently (each still computed against, and applied to, offsets that
    /// assume the OTHER splice never happened) double-counts the overlap and eats past wherever
    /// the two ranges disagree - observed eating the JSON's own closing bracket. Coalescing first
    /// makes the two-entries-in-a-row case, and any longer run of adjacent matches, splice out as
    /// ONE contiguous range instead - correct by construction, since a single range can never
    /// disagree with itself. A no-op whenever ranges do not actually touch, which is every existing
    /// non-adjacent case - see this method's own callers' tests for proof those stay byte-identical.
    /// <paramref name="ranges"/> must already be sorted by <c>Start</c>.
    /// </summary>
    static List<(int Start, int End)> CoalesceOverlapping(List<(int Start, int End)> ranges)
    {
        var merged = new List<(int Start, int End)>();

        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else
                merged.Add(range);
        }

        return merged;
    }

    /// <summary>
    /// Extends an item's own token span (<paramref name="itemStart"/>..<paramref name="itemEnd"/>,
    /// with no surrounding whitespace/comma) out to the full range that must be deleted to remove
    /// it cleanly from a comma-separated, typically one-per-line JSON object or array while leaving
    /// every other byte exactly as it was:
    ///
    /// <list type="bullet">
    /// <item>If a comma follows (this is not the last item): delete the item itself, its own
    /// leading indentation if it starts its own line, its trailing comma, and the single line
    /// terminator right after that comma.</item>
    /// <item>Else if a comma precedes (this is the last of several items): delete that preceding
    /// comma through the end of the item - this also removes the item's own leading
    /// indentation/newline, which sits between the comma and the item.</item>
    /// <item>Else (this is the sole item in its container): delete the item itself, its own
    /// leading indentation if it starts its own line, and the single line terminator right after
    /// it.</item>
    /// </list>
    ///
    /// For compact, non-pretty-printed JSON (no items on their own line, no surrounding
    /// whitespace), every branch degrades to deleting exactly the item plus exactly one adjacent
    /// comma - never more.
    /// </summary>
    static (int Start, int End) ComputeDeletionRange(byte[] json, int itemStart, int itemEnd)
    {
        var lineStart = itemStart;
        while (lineStart > 0 && IsHorizontalWhitespace(json[lineStart - 1])) lineStart--;
        var itemOwnsItsLine = lineStart == 0 || json[lineStart - 1] == (byte)'\n';

        var afterWs = itemEnd;
        while (afterWs < json.Length && IsAsciiWhitespace(json[afterWs])) afterWs++;
        var hasCommaAfter = afterWs < json.Length && json[afterWs] == (byte)',';

        var beforeWs = itemStart;
        while (beforeWs > 0 && IsAsciiWhitespace(json[beforeWs - 1])) beforeWs--;
        var hasCommaBefore = beforeWs > 0 && json[beforeWs - 1] == (byte)',';

        if (hasCommaAfter)
        {
            var start = itemOwnsItsLine ? lineStart : itemStart;
            var end = SkipOneLineTerminator(json, afterWs + 1);
            return (start, end);
        }

        if (hasCommaBefore)
        {
            return (beforeWs - 1, itemEnd);
        }

        // Sole item in its container - no comma on either side.
        {
            var start = itemOwnsItsLine ? lineStart : itemStart;
            var end = SkipOneLineTerminator(json, itemEnd);
            return (start, end);
        }
    }

    static int SkipOneLineTerminator(byte[] json, int index)
    {
        if (index < json.Length && json[index] == (byte)'\r') index++;
        if (index < json.Length && json[index] == (byte)'\n') index++;
        return index;
    }

    static bool IsHorizontalWhitespace(byte b) => b is (byte)' ' or (byte)'\t';
    static bool IsAsciiWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    // ======================================================================
    // Byte-faithful I/O: preserve a leading UTF-8 BOM either way, and never
    // leave a half-written file behind.
    // ======================================================================

    static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    static (byte[] Body, bool HadBom) StripUtf8Bom(byte[] raw) =>
        raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
            ? (raw[3..], true)
            : (raw, false);

    /// <summary>True when <paramref name="raw"/> begins with a UTF-16 (LE or BE) byte-order mark.
    /// Every text read/write in this class - <see cref="CleanClaudeMd"/>'s own read,
    /// <see cref="AtomicWriteUtf8Text"/> - decodes and encodes as UTF-8 unconditionally (see this
    /// class's own "JSON edits are byte-level surgery" remarks for why byte-level, never
    /// parse-and-reserialize); handed genuinely UTF-16 bytes, that silently produces MOJIBAKE rather
    /// than throwing - a NUL byte between every ASCII character reads as UTF-8 fine on its own, so
    /// the marker search inside <see cref="CleanClaudeMd"/> simply finds nothing and refuses with a
    /// message ("changed since it was classified") that does not name the real cause. Checked before
    /// any such decode is attempted, so cleanup can name the actual problem instead of dead-ending on
    /// a misleading refusal.</summary>
    static bool HasUtf16Bom(byte[] raw) =>
        raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF));

    /// <summary>True when <paramref name="path"/> exists and is marked read-only for its owner -
    /// checked by every write path in this class (<see cref="CleanClaudeMd"/>,
    /// <see cref="CleanManifest"/>, <see cref="CleanMcpConfig"/>, <see cref="CleanClaudeDesktopConfig"/>)
    /// immediately before it would otherwise write, so a read-only target is refused cleanly rather
    /// than silently re-permissioned. The risk being guarded against: <see cref="AtomicWriteBytes"/>
    /// writes a NEW temp file (ordinary, non-read-only permissions) and then RENAMES it over
    /// <paramref name="path"/> - and POSIX <c>rename()</c> only checks the enclosing DIRECTORY's own
    /// write permission, never the target file's own mode bits. Without this check, a read-only
    /// config file would end up silently swapped for a normally-permissioned replacement: the write
    /// "succeeds," but the read-only protection the user (or another tool) put on that file is gone,
    /// with nothing here having asked or said so. Cross-platform via <see cref="FileAttributes.ReadOnly"/>
    /// rather than a POSIX-only permission-bit check.</summary>
    static bool IsReadOnly(string path) =>
        File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

    static byte[] Prepend(byte[] prefix, byte[] body)
    {
        var result = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, result, prefix.Length, body.Length);
        return result;
    }

    static void AtomicWriteUtf8Text(string path, string content, bool withBom)
    {
        var body = Encoding.UTF8.GetBytes(content);
        AtomicWriteBytes(path, withBom ? Prepend(Utf8Bom, body) : body);
    }

    /// <summary>Writes via a temp file plus an atomic rename, so a crash or power loss mid-write
    /// can never leave a truncated, half-written config file behind - matching the discipline
    /// v1.2's own <c>MCPClientConfig.AtomicWrite</c> already held for these exact files.
    ///
    /// <para>The temp file is always cleaned up, even when <see cref="File.Move(string, string, bool)"/>
    /// itself fails - every caller here already checks <see cref="IsReadOnly"/> first and refuses
    /// before ever reaching this method, but a write can still fail for other reasons (a directory
    /// permission change, a full disk, ...), and without the <c>finally</c> below a failed move left
    /// a stray <c>*.hades-cleanup-tmp</c> file sitting next to the real one - written once, never
    /// cleaned up by anything, and never reported to whoever called cleanup.</para>
    /// </summary>
    static void AtomicWriteBytes(string path, byte[] content)
    {
        var tmpPath = path + ".hades-cleanup-tmp";
        File.WriteAllBytes(tmpPath, content);
        try
        {
            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }
}
