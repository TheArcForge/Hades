// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// Regression guard for docs/backlog/mutation-tool-defects.md, Defect 5: after the 103-tool -&gt;
    /// 32-tool MCP consolidation, several plugin-facing messages in SceneCommands.cs/PrefabCommands.cs/
    /// PrefabApplyCommands.cs kept naming tools the consolidation had already deleted
    /// (scene_get_hierarchy, component_get_all, prefab_open_editing, prefab_edit_property,
    /// prefab_save_editing - plus BeginScriptEditing/lease_release, found the same way: neither was
    /// ever a real MCP-tool-shaped name). An agent following that guidance called something that was
    /// not there and got a generic failure it could not diagnose - wrong guidance is worse than no
    /// guidance. <see cref="AssertMessageNamesOnlyLiveTools"/> is meant to be called against every
    /// message reachable through CommandTable.Dispatch (the only entry point any test in this folder
    /// uses - see PrefabCommandsTests' own doc comment) so the same class of mistake fails a test
    /// immediately instead of shipping silently again.
    ///
    /// <para><b>Why <see cref="Names"/> is a hand-maintained literal, not a live lookup.</b>
    /// Consolidation (Plan 10) deliberately keeps the plugin's wire command surface decoupled from the
    /// app's MCP tool surface - "Changing the plugin's wire commands" is explicitly OUT of that plan's
    /// scope, and tool-name composition happens entirely in the app's MCP layer
    /// (Core/src/Hades.Server/Mcp), a separate .NET process this netstandard2.1 Editor plugin never
    /// loads or references. UnityPlugin EditMode tests also run inside whatever Unity project this plugin
    /// is deployed into, which is not guaranteed to have the rest of the Hades repo (Core, docs/)
    /// sitting alongside it on disk - so, unlike Core/tests/Hades.Server.Tests/PluginRequiredFields.cs
    /// (which parses UnityPlugin source live, something only possible from that side because Core tests
    /// run from a stable full-repo checkout), there is no reliable path from here to the app's actual
    /// tool registrations at plugin-test time. So this list is a plain literal - but a SINGLE one, not
    /// scattered across eleven messages, which is what actually broke last time. Refresh it from
    /// either source below when the tool surface changes; both agreed exactly when this file was
    /// written:
    ///   - the alphabetical 32-tool list in docs/superpowers/plans/2026-08-03-tool-consolidation.md,
    ///     "Step 3: Measure";
    ///   - grep -rhoE '\[McpServerTool\(Name = "[a-zA-Z_]+"' Core/src/Hades.Server/Mcp/*.cs | sort -u
    /// </para>
    /// </summary>
    static class LiveMcpToolNames
    {
        public static readonly string[] Names =
        {
            "animation_apply", "asset_manage", "find_references_to", "find_unset_references",
            "get_memory_summary", "get_project_summary", "get_recently_changed", "get_scene_summary",
            "graph_query", "hades_charon_status", "hades_ping", "hades_rebuild_graph", "hades_regression",
            "hades_status", "inspect_asset", "inspector_inspect", "material_apply", "prefab_apply",
            "project_get_console_log", "project_get_test_results", "project_recompile_scripts",
            "project_run_tests", "project_settings", "project_settings_apply", "propose_memory_update",
            "recall_memory", "scene_apply", "scene_manage", "script_editing_session", "search_by_name",
            "trace_dependencies", "validate_memory",
        };

        /// <summary>Matches this codebase's own naming convention for every one of the 32 live tools:
        /// all-lowercase, at least one underscore (see <see cref="Names"/>). Deliberately does not try
        /// to catch PascalCase legacy names like the old 'BeginScriptEditing'/'EndScriptEditing' tools -
        /// a generic PascalCase scan would also flag ordinary .NET/Unity type names this file's own
        /// messages legitimately mention (GameObject, ArgumentException, Transform, ...). Those two
        /// are guarded by explicit StringAssert.DoesNotContain calls at their one call site instead
        /// (PrefabCommandsTests.AnyClass2Call_WhileADifferentLeaseIsHeld_ThrowsActionableError_DoesNotStealOrReleaseIt).</summary>
        static readonly Regex ToolShapedToken = new Regex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b");

        /// <summary>Every snake_case, tool-name-shaped token found in <paramref name="message"/> must
        /// be one of the 32 currently-registered MCP tools - never a name the 103-&gt;32 consolidation
        /// (or any future one) removed. A message with no such token at all trivially passes - this
        /// only constrains tokens that ARE shaped like a tool name.</summary>
        public static void AssertMessageNamesOnlyLiveTools(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            foreach (Match match in ToolShapedToken.Matches(message))
            {
                CollectionAssert.Contains(Names, match.Value,
                    "'" + match.Value + "' looks like an MCP tool name in a plugin-facing message, but it is "
                    + "not one of the 32 currently registered tools - see LiveMcpToolNames.Names' own doc "
                    + "comment for how to tell a genuine consolidation (update this list) from a regression "
                    + "(fix the message instead). Full message: " + message);
            }
        }
    }

    /// <summary>Self-check on <see cref="LiveMcpToolNames"/>'s own data, independent of any message
    /// content - a hand-maintained list is only as trustworthy as its own internal consistency. Does
    /// NOT re-verify the 32 names against Core/src/Hades.Server/Mcp (not reachable from here - see
    /// LiveMcpToolNames' own doc comment); that cross-check was done by hand when this file was
    /// written and must be redone by hand (grep command in the same doc comment) whenever the tool
    /// surface changes again.</summary>
    [TestFixture]
    public sealed class LiveMcpToolNamesTests
    {
        [Test]
        public void Names_HasExactly32Entries_MatchingPlan10Task6sMeasuredCutoverSurface()
        {
            Assert.AreEqual(32, LiveMcpToolNames.Names.Length);
        }

        [Test]
        public void Names_HasNoDuplicates()
        {
            CollectionAssert.AllItemsAreUnique(LiveMcpToolNames.Names);
        }

        [Test]
        public void Names_EveryEntryIsLowercaseWithAtLeastOneUnderscore_MatchingThisCodebasesToolNamingConvention()
        {
            // Also guards AssertMessageNamesOnlyLiveTools' own scan regex: if a live tool name ever
            // stopped matching this shape, the scan would silently stop being able to recognise it
            // in a message at all (neither confirming nor denying it), not just fail to allow-list it.
            var offenders = LiveMcpToolNames.Names.Where(n => !Regex.IsMatch(n, @"^[a-z][a-z0-9]*(?:_[a-z0-9]+)+$")).ToList();
            CollectionAssert.IsEmpty(offenders, "every live tool name must be all-lowercase with at least one underscore");
        }

        [Test]
        public void AssertMessageNamesOnlyLiveTools_ADeadToolName_FailsTheAssertion()
        {
            // The guard must actually guard - proven against a name that is deliberately NOT live
            // (one of the ones this very defect fix removed from these files' own messages).
            Assert.Throws<AssertionException>(() =>
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools("Call scene_get_hierarchy to see the full tree."));
        }

        [Test]
        public void AssertMessageNamesOnlyLiveTools_OnlyLiveNamesAndOrdinaryProse_Passes()
        {
            Assert.DoesNotThrow(() =>
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(
                    "GameObject not found: 'Ghost'. Root objects in the active scene: Root. Call inspect_asset to see the full tree."));
        }

        [Test]
        public void AssertMessageNamesOnlyLiveTools_NullOrEmptyMessage_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(null));
            Assert.DoesNotThrow(() => LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(""));
        }
    }
}
