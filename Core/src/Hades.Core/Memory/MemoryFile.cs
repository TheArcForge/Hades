using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Hades.Core.Memory;

/// <summary>
/// One AUTHORED memory document, parsed from Markdown with an optional YAML frontmatter block -
/// the format every file under memory/ uses (see the real Hades-Unity-Client corpus). Parsing
/// never rewrites what it reads: <see cref="RawText"/> is always the exact original string, and
/// <see cref="Body"/> is always a substring of it, sliced by position - never reconstructed from
/// a reformatted frontmatter block. That is what makes <see cref="MemoryStore.Write"/> followed by
/// <see cref="MemoryStore.Read"/> lossless, which is the one non-negotiable property of authored
/// storage: nothing here may lose a byte a human typed, no matter how the file is malformed.
/// </summary>
public sealed record MemoryFile
{
    /// <summary>Plain basename, e.g. "conventions.md" - never a path. Carried here purely so a
    /// message built from one <see cref="MemoryFile"/> (like <see cref="FrontmatterError"/>)
    /// stays meaningful once it is pulled out of context, e.g. into an import report listing
    /// many files at once.</summary>
    public required string Name { get; init; }

    /// <summary>The complete original text, byte-for-byte (as a string). This is the only field
    /// a caller needs to reproduce the file exactly; <see cref="Body"/> and
    /// <see cref="Frontmatter"/> are read-only convenience views over it.</summary>
    public required string RawText { get; init; }

    /// <summary>
    /// True when the file opens with a CLOSED <c>---</c> ... <c>---</c> block, regardless of
    /// whether that block's contents went on to parse as a flat YAML mapping. False for a plain
    /// markdown file with no frontmatter at all, and also false when an opening <c>---</c> is
    /// never closed (see <see cref="FrontmatterError"/>) - in that case nothing was actually
    /// delimited, so there is no frontmatter block to speak of, only a whole file that is body.
    /// </summary>
    public required bool HasFrontmatter { get; init; }

    /// <summary>The frontmatter's parsed key/value pairs, scalar values only, taken verbatim
    /// (never coerced to a date, number, or bool - a caller reading "last_reviewed" wants back
    /// exactly "2026-05-12", not a reformatted <see cref="DateTime"/>). Empty when there is no
    /// frontmatter or it did not parse - never null, so callers never null-check the common
    /// case.</summary>
    public required IReadOnlyDictionary<string, string> Frontmatter { get; init; }

    /// <summary>
    /// Set when a frontmatter block was found but could not be read - either the opening
    /// <c>---</c> was never closed, or its contents did not parse as a flat scalar mapping.
    /// Includes <see cref="Name"/> in the message itself. Null whenever frontmatter parsed
    /// cleanly or was simply absent. A broken header must never make the file's prose
    /// unreachable, so <see cref="Body"/> is always populated regardless of this being set.
    /// </summary>
    public string? FrontmatterError { get; init; }

    /// <summary>Everything after the frontmatter block, or the entire file when there is none, or
    /// when an opening <c>---</c> was never closed. Always readable.</summary>
    public required string Body { get; init; }

    static readonly IReadOnlyDictionary<string, string> EmptyFrontmatter =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Stateless and safe to reuse across calls - YamlDotNet's deserializer holds no per-call state.
    static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    /// <summary>
    /// Parses <paramref name="rawText"/> - pure, no I/O, so both <see cref="MemoryStore.Read"/>
    /// and a unit test can call it directly.
    /// </summary>
    public static MemoryFile Parse(string name, string rawText)
    {
        if (!StartsWithDelimiterLine(rawText, out var afterFirstLine))
        {
            return new MemoryFile
            {
                Name = name,
                RawText = rawText,
                HasFrontmatter = false,
                Frontmatter = EmptyFrontmatter,
                FrontmatterError = null,
                Body = rawText,
            };
        }

        var closing = FindClosingDelimiter(rawText, afterFirstLine);
        if (closing is null)
        {
            return new MemoryFile
            {
                Name = name,
                RawText = rawText,
                HasFrontmatter = false,
                Frontmatter = EmptyFrontmatter,
                FrontmatterError = $"'{name}' has an unterminated frontmatter block (an opening "
                    + "'---' with no closing '---') - treating the whole file as body.",
                Body = rawText,
            };
        }

        var (closingLineStart, bodyStart) = closing.Value;
        var frontmatterText = rawText[afterFirstLine..closingLineStart];
        var body = rawText[bodyStart..];

        var (fields, error) = ParseFrontmatterYaml(frontmatterText, name);

        return new MemoryFile
        {
            Name = name,
            RawText = rawText,
            HasFrontmatter = true,
            Frontmatter = fields,
            FrontmatterError = error,
            Body = body,
        };
    }

    /// <summary>
    /// True when <paramref name="text"/>'s first line, delimiter and all, is exactly "---" (a
    /// trailing '\r' is stripped before the comparison, so both LF and CRLF files are
    /// recognised). <paramref name="afterFirstLine"/> is the index right after that line's own
    /// terminator - <c>text.Length</c> when the file is nothing but that one line.
    /// </summary>
    static bool StartsWithDelimiterLine(string text, out int afterFirstLine)
    {
        var newlineIndex = text.IndexOf('\n');
        var firstLineEnd = newlineIndex < 0 ? text.Length : newlineIndex;
        var firstLine = StripTrailingCr(text[..firstLineEnd]);

        afterFirstLine = newlineIndex < 0 ? text.Length : newlineIndex + 1;
        return firstLine == "---";
    }

    /// <summary>
    /// Scans line by line from <paramref name="start"/> for the first line that is exactly
    /// "---", stopping there rather than continuing - a body containing its own "---" horizontal
    /// rule further down must never be mistaken for part of the search. Returns the closing
    /// line's own start index (where the frontmatter slice ends) and the index right after its
    /// terminator (where the body begins), or null when no closing line exists before EOF.
    /// </summary>
    static (int ClosingLineStart, int BodyStart)? FindClosingDelimiter(string text, int start)
    {
        var pos = start;
        while (pos <= text.Length)
        {
            var newlineIndex = text.IndexOf('\n', pos);
            var lineEnd = newlineIndex < 0 ? text.Length : newlineIndex;
            var line = StripTrailingCr(text[pos..lineEnd]);

            if (line == "---")
            {
                var bodyStart = newlineIndex < 0 ? text.Length : newlineIndex + 1;
                return (pos, bodyStart);
            }

            if (newlineIndex < 0) return null;
            pos = newlineIndex + 1;
        }

        return null;
    }

    static string StripTrailingCr(string line) => line.EndsWith('\r') ? line[..^1] : line;

    /// <summary>
    /// Reads the frontmatter block's raw text as a flat scalar mapping. Deserializes into
    /// <c>string?</c> values specifically (not <c>object</c>) so a value comes back as the exact
    /// scalar text YAML saw - not coerced into a <see cref="DateTime"/>, number, or bool the way
    /// deserializing to <c>object</c> would. An empty block (nothing between the delimiters) is
    /// valid and yields no fields, not an error; a value with nothing after its colon (the real
    /// Hades-Unity-Client corpus has this - an empty "target_file:") is a valid null scalar and
    /// becomes "", also not an error. Only a block that fails to parse as YAML at all, or that
    /// parses to something other than a flat mapping (a list, a nested mapping, a bare scalar),
    /// is reported as malformed.
    /// </summary>
    static (IReadOnlyDictionary<string, string> Fields, string? Error) ParseFrontmatterYaml(string yamlText, string name)
    {
        try
        {
            var raw = Deserializer.Deserialize<Dictionary<string, string?>>(yamlText);
            if (raw is null) return (EmptyFrontmatter, null);

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in raw) fields[key] = value ?? "";
            return (fields, null);
        }
        catch (YamlException ex)
        {
            return (EmptyFrontmatter, $"'{name}' has malformed frontmatter: {ex.Message}");
        }
    }
}
