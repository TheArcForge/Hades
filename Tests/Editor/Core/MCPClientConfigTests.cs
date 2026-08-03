using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class MCPClientConfigTests
    {
        const string Project = "/Work/MyGame";

        static string HubLauncher(params string[] parts) => Path.Combine(parts);

        // The bug this guards: .mcp.json shipped one developer's absolute home path
        // (/Users/someone/Projects/Game/.arcforge/hades-hub/launcher.js) even though the launcher
        // sits inside the project.
        [Test]
        public void LauncherInsideProject_IsProjectRelative()
        {
            var launcher = HubLauncher(Project, ".arcforge", "hades-hub", "launcher.js");

            Assert.AreEqual(".arcforge/hades-hub/launcher.js",
                MCPClientConfig.McpLauncherArg(launcher, Project));
        }

        [Test]
        public void LauncherOutsideProject_StaysAbsolute()
        {
            var launcher = HubLauncher("/Users/tester", ".arcforge", "hades-hub", "launcher.js");

            Assert.AreEqual(ToSlashes(launcher), MCPClientConfig.McpLauncherArg(launcher, Project));
        }

        [Test]
        public void SiblingDirectoryWithSharedPrefix_StaysAbsolute()
        {
            var launcher = HubLauncher("/Work/MyGameTools", "hub", "launcher.js");

            Assert.AreEqual(ToSlashes(launcher), MCPClientConfig.McpLauncherArg(launcher, Project));
        }

        [Test]
        public void ProjectRootWithTrailingSeparator_StillRelativizes()
        {
            var launcher = HubLauncher(Project, ".arcforge", "hades-hub", "launcher.js");

            Assert.AreEqual(".arcforge/hades-hub/launcher.js",
                MCPClientConfig.McpLauncherArg(launcher, Project + Path.DirectorySeparatorChar));
        }

        [Test]
        public void EmptyProjectRoot_ReturnsLauncherPathUnchanged()
        {
            var launcher = HubLauncher(Project, ".arcforge", "hades-hub", "launcher.js");

            Assert.AreEqual(ToSlashes(launcher), MCPClientConfig.McpLauncherArg(launcher, ""));
        }

        [Test]
        public void NullLauncherPath_IsNull()
        {
            Assert.IsNull(MCPClientConfig.McpLauncherArg(null, Project));
        }

        // A directory whose name merely begins with ".." is inside the project — it must not be
        // mistaken for a "../" escape and forced back to an absolute path.
        [Test]
        public void DirectoryNameStartingWithDots_IsProjectRelative()
        {
            var launcher = HubLauncher(Project, "..weird", "launcher.js");

            Assert.AreEqual("..weird/launcher.js", MCPClientConfig.McpLauncherArg(launcher, Project));
        }

        static string ToSlashes(string path) => path.Replace("\\", "/");
    }
}
