using System.Text.RegularExpressions;

namespace Hades.Core.Unity;

/// <summary>
/// Unity's asset format is YAML 1.1 with one non-conformance: it emits
/// <c>%TAG !u! tag:unity3d.com,2011:</c> once at the top of the file, but YAML directives apply
/// only to the document that follows them and reset at each <c>---</c>. Every document after the
/// first therefore uses an undeclared tag handle, and a strict parser stops there.
///
/// Rewriting each document header to a plain anchor makes the whole file standard YAML —
/// measured at 603 of 612 files in a real project, the remaining 9 being genuinely binary.
/// The class id is not lost: <see cref="UnityYamlReader"/> captures it from this same match.
/// </summary>
public static partial class UnityYamlPreprocessor
{
    // Every character here is load-bearing, and each was established against the real corpus:
    //   -?   fileIDs are signed; "&-1766573354249734030" occurs, and missing it drops ~7% of prefabs
    //   ( stripped)?   variant objects carry a trailing marker
    //   \r?$ 112 corpus files use CRLF; without this the " stripped" form never matches and
    //        16 files fail to parse — silently, because the tag is simply left in place
    [GeneratedRegex(@"^--- !u!(?<classId>\d+) (?<anchor>&-?\d+)( stripped)?\r?$", RegexOptions.Multiline)]
    public static partial Regex DocumentHeaderPattern();

    public static string MakeStandardYaml(string content) =>
        DocumentHeaderPattern().Replace(content, "--- ${anchor}");

    /// <summary>
    /// True when this looks like text Unity YAML worth parsing. Force Text mode does NOT mean
    /// every asset is text: Unity writes some assets (LightingData.asset among them) as binary
    /// regardless, 9 of them in the measured corpus. A NUL byte is the reliable tell.
    /// </summary>
    public static bool LooksLikeUnityYaml(string content) =>
        content.StartsWith("%YAML", StringComparison.Ordinal) && !content.Contains('\0');
}
