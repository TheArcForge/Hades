using System.Text.Json;

namespace Hades.Core.Tests;

/// <summary>
/// Builds <c>Packages/manifest.json</c> content for tests that need a local <c>file:</c> package.
///
/// <para><b>Why this exists rather than string interpolation at each call site.</b> Nine fixtures
/// across five files used to write the path straight into the JSON:</para>
///
/// <code>$"{{\"dependencies\":{{\"com.example.pkg\":\"file:{external}\"}}}}"</code>
///
/// <para>That is correct on macOS and produces INVALID JSON on Windows, where the path is
/// <c>C:\Users\...\Temp\xyz</c> and <c>\U</c>, <c>\T</c> and friends are not legal escape sequences.
/// The manifest then fails to parse, no package resolves, and the test fails with an empty
/// collection — which reads like a broken indexer rather than a broken fixture. It is not: escaping
/// the path makes the same tests pass unchanged, so the resolver was always fine.</para>
///
/// <para>Escaping is delegated to <see cref="JsonSerializer"/> rather than hand-rolled with a
/// <c>Replace("\\", "\\\\")</c>, which is the obvious fix and only handles the one character that
/// happened to break here.</para>
/// </summary>
public static class ManifestJson
{
    /// <summary>
    /// One dependency entry — <c>"name":"file:escaped/path"</c> — for callers that assemble the
    /// surrounding manifest themselves.
    /// </summary>
    public static string LocalPackageEntry(string packageName, string absolutePath) =>
        $"{JsonSerializer.Serialize(packageName)}:{JsonSerializer.Serialize("file:" + absolutePath)}";

    /// <summary>A complete manifest whose only dependency is one local <c>file:</c> package.</summary>
    public static string WithLocalPackage(string packageName, string absolutePath) =>
        $"{{\"dependencies\":{{{LocalPackageEntry(packageName, absolutePath)}}}}}";
}
