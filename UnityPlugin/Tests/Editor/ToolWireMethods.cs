// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-01 §15.1.1: the test that would have caught the allow-list defect an earlier draft
    /// shipped - a refusal message naming a live MCP tool (<see cref="LiveMcpToolNames"/> already
    /// proves the NAME exists) whose own wire method(s) the play-mode guard itself refused, so the
    /// recovery it advertised was itself unfollowable. Naming a live tool is necessary but not
    /// sufficient; this maps a tool name to the wire method(s) IT sends, so a test can also confirm
    /// at least one of them is actually reachable in the state the message is thrown from.
    ///
    /// Hand-maintained for the identical reason <see cref="LiveMcpToolNames.Names"/> is: the plugin
    /// cannot see Core/src/Hades.Server/Mcp at test time (no InternalsVisibleTo, and this assembly
    /// runs inside whatever project the plugin is deployed into - see that class's own doc comment).
    /// Only needs entries for tools actually named in a plugin-facing play-mode message - not all 33.
    /// </summary>
    static class ToolWireMethods
    {
        public static readonly Dictionary<string, string[]> ByTool = new Dictionary<string, string[]>
        {
            ["script_editing_session"] = new[] { "project.begin_script_editing", "project.end_script_editing" },
            ["play_mode"] = new[] { "play.enter", "play.exit", "play.set_paused", "play.step", "play.status" },
        };
    }

    [TestFixture]
    public sealed class ToolWireMethodsTests
    {
        [Test]
        public void ByTool_EveryKeyIsALiveToolName()
        {
            // Self-check on this file's own data, same spirit as LiveMcpToolNamesTests - a
            // hand-maintained map naming a tool LiveMcpToolNames.Names does not recognise would be
            // silently useless the moment DESIGN-01's own test 11 tries to use it.
            foreach (var tool in ToolWireMethods.ByTool.Keys)
                CollectionAssert.Contains(LiveMcpToolNames.Names, tool, "'" + tool + "' is not a live MCP tool name - see LiveMcpToolNames.Names.");
        }

        [Test]
        public void ByTool_NoToolHasZeroWireMethods()
        {
            Assert.IsTrue(ToolWireMethods.ByTool.Values.All(methods => methods.Length > 0));
        }
    }
}
