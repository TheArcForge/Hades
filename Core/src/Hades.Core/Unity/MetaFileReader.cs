using System.Text.RegularExpressions;

namespace Hades.Core.Unity;

/// <summary>
/// Reads an asset's GUID from its sibling <c>.meta</c>. The GUID is the asset's identity across
/// the entire project — every external reference in every scene and prefab resolves through it.
/// </summary>
public static partial class MetaFileReader
{
    // The guid sits in the first few lines; .meta files for large importers can be long.
    const int HeadBytes = 4 * 1024;

    [GeneratedRegex(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline)]
    private static partial Regex GuidPattern();

    public static string? TryReadGuid(string assetPath)
    {
        var metaPath = assetPath + ".meta";
        if (!File.Exists(metaPath)) return null;

        try
        {
            using var stream = File.OpenRead(metaPath);
            var buffer = new byte[Math.Min(HeadBytes, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            var match = GuidPattern().Match(head);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
