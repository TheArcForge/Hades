using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.Tests
{
    public class MigratedToolDiscoveryTests
    {
        MCPDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _dispatcher = new MCPDispatcher();
        }

        static readonly string[] SceneToolNames =
        {
            "scene_get_hierarchy", "scene_create_gameobject", "scene_create_primitive",
            "scene_delete_gameobject", "scene_reparent_gameobject", "scene_rename_gameobject",
            "scene_setup"
        };

        static readonly string[] SceneManagementToolNames =
        {
            "scene_save", "scene_create", "scene_open",
            "scene_duplicate", "scene_list_build", "scene_set_build"
        };

        static readonly string[] InspectorToolNames =
        {
            "inspector_select", "inspector_inspect"
        };

        static readonly string[] ComponentToolNames =
        {
            "component_add", "component_find", "component_remove", "component_get_all",
            "component_get_property", "component_set_property", "component_set_properties",
            "component_list_properties"
        };

        static readonly string[] PrefabToolNames =
        {
            "prefab_create", "prefab_instantiate", "prefab_apply_overrides",
            "prefab_get_contents", "prefab_edit_property", "prefab_open_editing",
            "prefab_save_editing", "prefab_create_variant"
        };

        static readonly string[] MaterialToolNames =
        {
            "material_create", "material_set_property", "material_get_properties",
            "material_assign", "material_duplicate", "material_swap_shader"
        };

        static readonly string[] TagLayerToolNames =
        {
            "tag_create", "tag_delete", "tag_list", "layer_create", "layer_list"
        };

        static readonly string[] AnimationToolNames =
        {
            "animation_assign_controller", "animation_assign_clip",
            "animation_get_controller", "animation_create_controller",
            "animation_edit_controller"
        };

        static readonly string[] ReferenceToolNames =
        {
            "reference_set", "reference_get", "reference_find_unset"
        };

        static readonly string[] EventToolNames =
        {
            "event_add_listener", "event_remove_listener",
            "event_list_listeners", "event_find_all"
        };

        static readonly string[] AssetToolNames =
        {
            "asset_get_info", "asset_find", "asset_move", "asset_import"
        };

        static readonly string[] AssetImportToolNames =
        {
            "asset_get_import_settings", "asset_set_import_settings",
            "asset_set_clip_import_settings"
        };

        static readonly string[] DomainReloadToolNames =
        {
            "BeginScriptEditing", "EndScriptEditing", "project_recompile_scripts"
        };

        static readonly string[] ProjectToolNames =
        {
            "project_run_tests", "project_get_console_log",
            "project_get_settings", "project_refresh_assets"
        };

        static readonly string[][] AllToolGroups =
        {
            SceneToolNames, SceneManagementToolNames, InspectorToolNames,
            ComponentToolNames, PrefabToolNames, MaterialToolNames,
            TagLayerToolNames, AnimationToolNames, ReferenceToolNames,
            EventToolNames, AssetToolNames, AssetImportToolNames,
            DomainReloadToolNames, ProjectToolNames
        };

        [Test]
        public void AllMigratedTools_AreDiscoverable()
        {
            var tools = _dispatcher.GetTools();
            var toolNames = tools.Select(t => t.Name).ToHashSet();

            var missing = AllToolGroups
                .SelectMany(g => g)
                .Where(name => !toolNames.Contains(name))
                .ToList();

            Assert.IsEmpty(missing,
                $"Missing tools: {string.Join(", ", missing)}");
        }

        [Test]
        public void MigratedToolCount_Is68()
        {
            var expected = AllToolGroups.SelectMany(g => g).Count();
            Assert.AreEqual(68, expected, "Tool name arrays should total 68");
        }

        [TestCase("scene_get_hierarchy")]
        [TestCase("component_add")]
        [TestCase("prefab_create")]
        [TestCase("material_create")]
        [TestCase("animation_create_controller")]
        [TestCase("event_add_listener")]
        [TestCase("asset_get_info")]
        [TestCase("BeginScriptEditing")]
        [TestCase("project_run_tests")]
        [TestCase("inspector_select")]
        [TestCase("tag_create")]
        [TestCase("reference_set")]
        [TestCase("scene_save")]
        [TestCase("asset_get_import_settings")]
        public void RepresentativeTool_IsDiscoverable(string toolName)
        {
            var tools = _dispatcher.GetTools();
            Assert.IsTrue(tools.Any(t => t.Name == toolName),
                $"{toolName} tool should be discovered by MCPDispatcher");
        }
    }
}
