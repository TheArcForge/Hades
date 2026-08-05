using Hades.Core;
using Hades.Core.Storage;
using Hades.Server.Mcp;
using ModelContextProtocol;

namespace Hades.Server.Tests;

/// <summary>
/// Unit-level coverage of <see cref="ToolSupport.ResolveProject"/> directly against
/// <see cref="ProjectService"/> — no HTTP, no MCP envelope. The HTTP-level behaviour (same
/// logic, driven through a real tool call) stays covered by ProjectHandleTests and ToolCallTests;
/// this fixture exists so the resolution rules themselves have one place they are pinned,
/// regardless of which tool calls into them.
/// </summary>
public class ToolSupportTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<string> _projectRoots = [];

    ProjectService NewService() => new(new AppPaths(_appRoot));

    string MakeProject(string guid, string typeName)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _projectRoots.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {guid}\n");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        File.WriteAllText(Path.Combine(root, "Assets", $"{typeName}.cs"), $"public class {typeName} {{ }}");
        return root;
    }

    [Fact]
    public void ResolveProject_ReturnsTheExplicitHandle()
    {
        var service = NewService();
        service.AdoptAndIndex(MakeProject("aaaabbbbccccddddeeeeffff00001111", "Alpha"));
        service.AdoptAndIndex(MakeProject("bbbbccccddddeeeeffff000011112222", "Beta"));

        Assert.Equal("bbbbccccddddeeeeffff000011112222",
            ToolSupport.ResolveProject(service, "bbbbccccddddeeeeffff000011112222"));
    }

    [Fact]
    public void ResolveProject_FallsBackToTheSoleKnownProjectWhenHandleIsOmitted()
    {
        var service = NewService();
        service.AdoptAndIndex(MakeProject("aaaabbbbccccddddeeeeffff00001111", "Alpha"));

        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", ToolSupport.ResolveProject(service, null));
    }

    [Fact]
    public void ResolveProject_WithSeveralProjectsAndNoHandle_NamesTheAvailableHandles()
    {
        var service = NewService();
        service.AdoptAndIndex(MakeProject("aaaabbbbccccddddeeeeffff00001111", "Alpha"));
        service.AdoptAndIndex(MakeProject("bbbbccccddddeeeeffff000011112222", "Beta"));

        var ex = Assert.Throws<McpException>(() => ToolSupport.ResolveProject(service, null));

        Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aaaabbbbccccddddeeeeffff00001111", ex.Message);
        Assert.Contains("bbbbccccddddeeeeffff000011112222", ex.Message);
    }

    [Fact]
    public void ResolveProject_WithAnUnknownHandle_ListsTheValidOnesRatherThanJustNotFound()
    {
        var service = NewService();
        service.AdoptAndIndex(MakeProject("aaaabbbbccccddddeeeeffff00001111", "Alpha"));

        var ex = Assert.Throws<McpException>(() => ToolSupport.ResolveProject(service, "not-a-real-handle"));

        Assert.DoesNotContain("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aaaabbbbccccddddeeeeffff00001111", ex.Message);
    }

    public void Dispose()
    {
        foreach (var dir in _projectRoots.Append(_appRoot))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
