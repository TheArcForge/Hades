using System.Text.Json;
using System.Text.RegularExpressions;
using Hades.Core.Editors;

namespace Hades.Core.Tests.Editors;

public sealed class PluginInstallerTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public PluginInstallerTests()
    {
        // A believable Unity project: ProjectSettings, a package manifest, Claude Code config, an
        // existing script with its .meta - everything Install() must leave completely alone.
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");

        Directory.CreateDirectory(Path.Combine(_projectRoot, "Packages"));
        File.WriteAllText(Path.Combine(_projectRoot, "Packages", "manifest.json"), "{ \"dependencies\": {} }");

        File.WriteAllText(Path.Combine(_projectRoot, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(_projectRoot, "CLAUDE.md"), "# Project notes\n");

        Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets", "Scripts"));
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Scripts", "Player.cs"), "public class Player {}");
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Scripts", "Player.cs.meta"),
            "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n");
    }

    static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
                f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes)
            : [];

    string PluginRoot => Path.Combine(_projectRoot, "Assets", "Hades");

    [Fact]
    public void Install_RejectsNullOrBlankProjectRoot()
    {
        Assert.Throws<ArgumentException>(() => PluginInstaller.Install(""));
        Assert.Throws<ArgumentException>(() => PluginInstaller.Install("   "));
    }

    [Fact]
    public void Install_WritesTheAsmdefAndEveryExpectedFile()
    {
        PluginInstaller.Install(_projectRoot);

        Assert.True(File.Exists(Path.Combine(PluginRoot, "Hades.asmdef")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Contract", "Hello.cs")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Contract", "JsonRpc.cs")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Contract", "MiniJson.cs")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Contract", "EditorConnectionInfo.cs")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Runtime", "HadesBoot.cs")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Runtime", "MainThreadPump.cs")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Transport", "HadesClient.cs")));
        Assert.True(File.Exists(Path.Combine(PluginRoot, "Transport", "HadesConnectionFile.cs")));

        // Non-empty and plausibly real source, not placeholder content.
        Assert.Contains("class Hello", File.ReadAllText(Path.Combine(PluginRoot, "Contract", "Hello.cs")));
        Assert.Contains("HadesClient", File.ReadAllText(Path.Combine(PluginRoot, "Transport", "HadesClient.cs")));
    }

    [Fact]
    public void Install_WritesNoMetaFilesAndNoGuids()
    {
        // Standing project rule, asserted explicitly (not just "we never wrote code that does
        // this"): Unity mints .meta files and their guids on next Editor refresh. An installer
        // that pre-empts that would collide with Unity's own assignment.
        PluginInstaller.Install(_projectRoot);

        var files = Directory.EnumerateFiles(PluginRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));

        var guidLine = new Regex(@"^\s*guid:\s*[0-9a-f]{32}\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (var file in files)
            Assert.False(guidLine.IsMatch(File.ReadAllText(file)), $"'{file}' contains a guid: line - Unity must mint these, not the installer");
    }

    [Fact]
    public void Install_TouchesNothingElseInTheProject()
    {
        var before = Snapshot(_projectRoot);
        Assert.NotEmpty(before); // sanity: the fixture really did seed files

        PluginInstaller.Install(_projectRoot);

        var after = Snapshot(_projectRoot);

        // Full tree diff, not a spot check: every pre-existing path must survive byte-for-byte...
        foreach (var (path, contentBefore) in before)
        {
            Assert.True(after.TryGetValue(path, out var contentAfter), $"'{path}' disappeared after install");
            Assert.True(contentBefore.AsSpan().SequenceEqual(contentAfter), $"'{path}' changed after install");
        }

        // ...and nothing new may appear anywhere outside Assets/Hades/.
        var unexpectedNewPaths = after.Keys.Except(before.Keys)
            .Where(p => !p.StartsWith("Assets/Hades/", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(unexpectedNewPaths);
    }

    [Fact]
    public void Reinstall_IsIdempotent()
    {
        PluginInstaller.Install(_projectRoot);
        var first = Snapshot(PluginRoot);

        PluginInstaller.Install(_projectRoot);
        var second = Snapshot(PluginRoot);

        Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in first.Keys)
            Assert.True(first[key].AsSpan().SequenceEqual(second[key]), $"'{key}' differs after a second, unchanged install");
    }

    [Fact]
    public void Reinstall_UpdatesStaleContentInPlace()
    {
        Directory.CreateDirectory(PluginRoot);
        File.WriteAllText(Path.Combine(PluginRoot, "Hades.asmdef"), "{ \"stale\": true }");

        PluginInstaller.Install(_projectRoot);

        var content = File.ReadAllText(Path.Combine(PluginRoot, "Hades.asmdef"));
        Assert.DoesNotContain("stale", content);
        Assert.Contains("\"name\": \"Hades\"", content);
    }

    [Fact]
    public void Install_ZeroDependencyGuard_NoDllAndAsmdefReferencesNothingOutsideUnity()
    {
        // Permanent regression guard, in the spirit of Bridge~/tests/launcher/bundle.test.ts's
        // self-contained-bundle invariant: a drop-in Unity plugin that must never break a
        // stranger's project cannot import a dependency tree.
        PluginInstaller.Install(_projectRoot);

        var files = Directory.EnumerateFiles(PluginRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.DoesNotContain(files, f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginRoot, "Hades.asmdef")));
        Assert.Equal(0, doc.RootElement.GetProperty("references").GetArrayLength());
    }

    [Fact]
    public void Install_MatchesTheRealPluginSourceTreeExactly()
    {
        // Local sanity check rather than a skip package - see RealProjectIndexSmokeTest for the
        // same convention. Cross-checks against the CANONICAL sources on disk: Runtime/Transport/
        // the asmdef come from UnityPlugin/Assets/Hades (the folder this app installs), but Contract/
        // comes from Core/src/Hades.Contract/Wire - the sources this app itself compiled against,
        // NOT the separately-maintained UnityPlugin/Assets/Hades/Contract/ copy used for Unity-side
        // development. That is the whole point of embedding: the app ships the contract it
        // actually compiled, so the two sides cannot silently drift.
        const string PluginSourceDir = "/Users/mike/Projects/Hades/UnityPlugin/Assets/Hades";
        const string ContractSourceDir = "/Users/mike/Projects/Hades/Core/src/Hades.Contract/Wire";
        if (!Directory.Exists(PluginSourceDir) || !Directory.Exists(ContractSourceDir)) return;

        PluginInstaller.Install(_projectRoot);

        var expected = new Dictionary<string, byte[]>
        {
            ["Hades.asmdef"] = File.ReadAllBytes(Path.Combine(PluginSourceDir, "Hades.asmdef")),
        };
        foreach (var folder in new[] { "Runtime", "Transport", "Tools" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(PluginSourceDir, folder)))
            expected[$"{folder}/{Path.GetFileName(file)}"] = File.ReadAllBytes(file);
        foreach (var file in Directory.EnumerateFiles(ContractSourceDir))
            expected[$"Contract/{Path.GetFileName(file)}"] = File.ReadAllBytes(file);

        var actual = Snapshot(PluginRoot);

        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in expected.Keys)
            Assert.True(expected[key].AsSpan().SequenceEqual(actual[key]), $"'{key}' differs from the real plugin source it should be a byte-exact copy of");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
