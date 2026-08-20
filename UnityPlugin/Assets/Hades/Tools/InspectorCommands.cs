// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 (single-tick, no reload lease) Editor-selection mutation: inspector.select. Also
    /// carries its sibling read, inspector.inspect (class 4, live-state read - see the "52 Editor
    /// tools" plan's operation-class table, Task 5): a full dump of one GameObject's LIVE,
    /// in-memory state - unlike inspector.select, it never references <c>gate</c> either, for the
    /// identical "nothing here a domain reload could interrupt" reason, so the two sit together
    /// despite belonging to different operation classes.
    ///
    /// inspector.select has no explicit Undo call: unlike a scene/component field,
    /// <see cref="Selection.activeGameObject"/> is not a serialized property on a
    /// UnityEngine.Object <see cref="Undo.RecordObject"/> could snapshot - it is Editor-only UI
    /// state. Unity tracks selection changes on its OWN undo stack automatically whenever the
    /// Selection API is used (the Editor's "Edit > Undo Selection Change" always exists),
    /// independent of anything this file does - verified against a real Editor rather than
    /// assumed, since this plugin has nothing to add that would change that behaviour either way.
    ///
    /// inspector.inspect is addressed by hierarchy path exactly like inspector.select, independent
    /// of whatever <see cref="Selection.activeGameObject"/> currently holds - "select" (Editor UI
    /// highlight) and "inspect" (a JSON dump) are orthogonal, exactly as in the old package both
    /// port from. It is the live counterpart to Hades.Server's component_get_all (Plan 5,
    /// read-through): that tool re-parses the scene/prefab file as last SAVED to disk; this one
    /// never touches disk at all, so unsaved Editor edits - and only this tool - can see them.
    /// </summary>
    public static class InspectorCommands
    {
        // --------------------------------------------------------------------------- inspector.select

        internal static JsonValue SelectGameObject(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "inspector.select");
            var go = GameObjectPaths.FindByPath(path) ?? throw GameObjectPaths.NotFoundError(path);

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);

            return JsonValue.NewObject()
                .SetProperty("selected", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("fileId", JsonValue.Integer(GameObjectPaths.FileId(go)));
        }

        // --------------------------------------------------------------------------- inspector.inspect

        internal static JsonValue InspectGameObject(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "inspector.inspect");
            var go = GameObjectPaths.FindByPath(path) ?? throw GameObjectPaths.NotFoundError(path);
            var transform = go.transform;

            var children = JsonValue.NewArray();
            for (var i = 0; i < transform.childCount; i++)
                children.Add(JsonValue.String(transform.GetChild(i).name));

            var components = JsonValue.NewArray();
            foreach (var component in go.GetComponents<Component>().Where(c => c != null))
                components.Add(DumpComponent(component));

            return JsonValue.NewObject()
                .SetProperty("name", JsonValue.String(go.name))
                .SetProperty("path", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("fileId", JsonValue.Integer(GameObjectPaths.FileId(go)))
                .SetProperty("active", JsonValue.Bool(go.activeSelf))
                .SetProperty("layer", JsonValue.String(LayerMask.LayerToName(go.layer)))
                .SetProperty("tag", JsonValue.String(go.tag))
                .SetProperty("isStatic", JsonValue.Bool(go.isStatic))
                .SetProperty("position", Vec3(transform.position))
                .SetProperty("rotation", Vec3(transform.eulerAngles))
                .SetProperty("scale", Vec3(transform.localScale))
                .SetProperty("childCount", JsonValue.Integer(transform.childCount))
                .SetProperty("children", children)
                .SetProperty("components", components);
        }

        /// <summary>Every serialized property on <paramref name="component"/>, converted through
        /// the SAME <see cref="SerializedPropertyJson.Get"/> ComponentCommands' own mutations use -
        /// a structured JsonValue per field (a real object for a Vector3/Color/..., not a
        /// stringified one), so a caller gets exactly the shape component_set_property would
        /// accept back, not a human-readable-only dump.</summary>
        static JsonValue DumpComponent(Component component)
        {
            var so = new SerializedObject(component);
            var properties = JsonValue.NewArray();

            var iterator = so.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    properties.Add(JsonValue.NewObject()
                        .SetProperty("name", JsonValue.String(iterator.name))
                        .SetProperty("displayName", JsonValue.String(iterator.displayName))
                        .SetProperty("type", JsonValue.String(iterator.propertyType.ToString()))
                        .SetProperty("value", SerializedPropertyJson.Get(iterator)));
                } while (iterator.NextVisible(false));
            }

            return JsonValue.NewObject()
                .SetProperty("fileId", JsonValue.Integer(GameObjectPaths.FileId(component)))
                .SetProperty("type", JsonValue.String(component.GetType().Name))
                .SetProperty("fullType", JsonValue.String(component.GetType().FullName))
                .SetProperty("enabled", JsonValue.Bool(IsComponentEnabled(component)))
                .SetProperty("properties", properties);
        }

        static bool IsComponentEnabled(Component component)
        {
            if (component is Behaviour behaviour) return behaviour.enabled;
            if (component is Renderer renderer) return renderer.enabled;
            if (component is Collider collider) return collider.enabled;
            return true;
        }

        static JsonValue Vec3(Vector3 v) =>
            JsonValue.NewObject()
                .SetProperty("x", JsonValue.Float(v.x))
                .SetProperty("y", JsonValue.Float(v.y))
                .SetProperty("z", JsonValue.Float(v.z));
    }
}
