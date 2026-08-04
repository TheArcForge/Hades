using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
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

        const string Launcher = ".arcforge/hades-hub/launcher.js";

        static JObject Merge(string existing, string launcherArg = Launcher) =>
            JObject.Parse(MCPClientConfig.MergeHadesServer(existing, launcherArg));

        static string HadesArg(JObject root) =>
            (string)root["mcpServers"]["hades"]["args"][0];

        // The bug this guards: WriteProjectMcpJson built a fresh single-entry object and wrote it
        // over .mcp.json on every server start, deleting every other MCP server the team had
        // declared there. Silently — and .mcp.json is gitignored, so git could not show them what
        // had gone.
        [Test]
        public void ExistingSiblingServers_ArePreserved()
        {
            var root = Merge(@"{
              ""mcpServers"": {
                ""postgres"": { ""command"": ""npx"", ""args"": [""-y"", ""server-postgres""] }
              }
            }");

            var postgres = root["mcpServers"]["postgres"];
            Assert.IsNotNull(postgres, "sibling server was dropped");
            Assert.AreEqual("npx", (string)postgres["command"]);
            Assert.AreEqual(Launcher, HadesArg(root));
        }

        [Test]
        public void ExistingHadesEntry_IsUpdatedInPlace()
        {
            var root = Merge(@"{
              ""mcpServers"": {
                ""hades"": { ""command"": ""node"", ""args"": [""/old/absolute/launcher.js""] }
              }
            }");

            Assert.AreEqual(Launcher, HadesArg(root));
            Assert.AreEqual(1, ((JObject)root["mcpServers"]).Count);
        }

        [Test]
        public void UnrelatedTopLevelKeys_ArePreserved()
        {
            var root = Merge(@"{ ""$schema"": ""./schema.json"", ""mcpServers"": {} }");

            Assert.AreEqual("./schema.json", (string)root["$schema"]);
            Assert.AreEqual(Launcher, HadesArg(root));
        }

        [Test]
        public void MissingMcpServersKey_IsCreated()
        {
            var root = Merge(@"{ ""someOtherTool"": true }");

            Assert.AreEqual(Launcher, HadesArg(root));
            Assert.IsTrue((bool)root["someOtherTool"]);
        }

        // "mcpServers": null reads back as a JValue of type Null, not C# null, so a blind
        // (JObject) cast throws instead of falling through to the create branch.
        [Test]
        public void McpServersExplicitNull_IsReplacedWithObject()
        {
            var root = Merge(@"{ ""mcpServers"": null }");

            Assert.AreEqual(Launcher, HadesArg(root));
        }

        [Test]
        public void McpServersWrongType_IsReplacedWithObject()
        {
            var root = Merge(@"{ ""mcpServers"": [""nonsense""] }");

            Assert.AreEqual(Launcher, HadesArg(root));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   \n\t ")]
        public void NoUsableExistingFile_WritesFreshConfig(string existing)
        {
            var root = Merge(existing);

            Assert.AreEqual("node", (string)root["mcpServers"]["hades"]["command"]);
            Assert.AreEqual(Launcher, HadesArg(root));
        }

        // Hades must still self-heal past a corrupt file: bailing out would leave the MCP server
        // unreachable with only a console warning. Nothing recoverable is lost, since a file that
        // does not parse has no readable sibling entries.
        [Test]
        public void CorruptJson_IsReplacedWithFreshConfig()
        {
            LogAssert.Expect(LogType.Warning, new Regex("not valid JSON"));

            var root = Merge(@"{ ""mcpServers"": { ""hades"": ");

            Assert.AreEqual(Launcher, HadesArg(root));
        }

        [Test]
        public void AbsoluteLauncherArg_IsWrittenVerbatim()
        {
            const string absolute = "/Users/tester/.arcforge/hades-hub/launcher.js";

            Assert.AreEqual(absolute, HadesArg(Merge("{}", absolute)));
        }
    }
}
