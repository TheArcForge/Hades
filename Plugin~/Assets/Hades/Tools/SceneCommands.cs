// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 (single-tick, no reload lease - see the "52 Editor tools" plan's operation-class
    /// table) scene and hierarchy mutations: create/delete/reparent/rename a GameObject, create a
    /// primitive, and the batch scene_setup. Every method here runs to completion inside one
    /// <see cref="CommandTable.Dispatch"/> call on the main thread, which is what makes it safe
    /// with no <see cref="ReloadGate"/> involvement at all - none of these methods ever reference
    /// the <c>gate</c> parameter CommandTable's <see cref="CommandTable.Handler"/> delegate shape
    /// requires; it is accepted only so every handler has the same signature. Registered into
    /// <see cref="CommandTable"/>'s dispatch table under the "scene.*" method names, mirroring the
    /// existing "assets.refresh" / "lease.*" naming (namespace.snake_case_action) rather than the
    /// app-facing MCP tool names (scene_create_gameobject, etc, in Hades.Server.Mcp.EditorSceneTools)
    /// - see that class's own doc comment for why the two names deliberately differ.
    ///
    /// Every mutation registers Undo (Undo.RegisterCreatedObjectUndo / Undo.DestroyObjectImmediate /
    /// Undo.SetTransformParent / Undo.RecordObject), because a tool that mutates a user's scene
    /// without an undo entry is not acceptable - see SceneCommandsTests, which asserts a single
    /// Undo.PerformUndo reverts every one of these.
    /// </summary>
    public static class SceneCommands
    {
        // ---------------------------------------------------------------- scene.create_gameobject

        internal static JsonValue CreateGameObject(ReloadGate gate, JsonValue @params)
        {
            var name = JsonParams.RequireString(@params, "name", "scene.create_gameobject");
            var parentPath = JsonParams.OptionalString(@params, "parent");

            var parentTransform = ResolveOptionalParent(parentPath);

            var go = new GameObject(name);
            if (parentTransform != null) go.transform.SetParent(parentTransform);

            Undo.RegisterCreatedObjectUndo(go, "Hades Create " + name);

            return BuildGameObjectResult(go);
        }

        // ---------------------------------------------------------------- scene.create_primitive

        static readonly string[] ValidPrimitiveTypes = Enum.GetNames(typeof(PrimitiveType));

        internal static JsonValue CreatePrimitive(ReloadGate gate, JsonValue @params)
        {
            var typeName = JsonParams.RequireString(@params, "type", "scene.create_primitive");
            if (!Enum.TryParse<PrimitiveType>(typeName, true, out var primitiveType))
            {
                throw new ArgumentException(
                    "Invalid primitive type: '" + typeName + "'. Valid types: " + string.Join(", ", ValidPrimitiveTypes) + ".");
            }

            var name = JsonParams.OptionalString(@params, "name");
            var parentTransform = ResolveOptionalParent(JsonParams.OptionalString(@params, "parent"));
            var position = JsonParams.OptionalVector3(@params, "position");
            var rotation = JsonParams.OptionalVector3(@params, "rotation");
            var scale = JsonParams.OptionalVector3(@params, "scale");

            var go = GameObject.CreatePrimitive(primitiveType);
            if (!string.IsNullOrEmpty(name)) go.name = name;
            if (parentTransform != null) go.transform.SetParent(parentTransform);
            if (position != null) go.transform.localPosition = new Vector3(position[0], position[1], position[2]);
            if (rotation != null) go.transform.localEulerAngles = new Vector3(rotation[0], rotation[1], rotation[2]);
            if (scale != null) go.transform.localScale = new Vector3(scale[0], scale[1], scale[2]);

            Undo.RegisterCreatedObjectUndo(go, "Hades Create Primitive " + typeName);

            var result = BuildGameObjectResult(go);
            result.SetProperty("type", JsonValue.String(typeName));
            return result;
        }

        // ---------------------------------------------------------------- scene.delete_gameobject

        internal static JsonValue DeleteGameObject(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "scene.delete_gameobject");
            var go = GameObjectPaths.FindByPath(path) ?? throw GameObjectPaths.NotFoundError(path);

            var fileId = GameObjectPaths.FileId(go);
            var name = go.name;

            Undo.DestroyObjectImmediate(go);

            return JsonValue.NewObject()
                .SetProperty("deletedPath", JsonValue.String(path))
                .SetProperty("deletedName", JsonValue.String(name))
                .SetProperty("fileId", JsonValue.Integer(fileId));
        }

        // ---------------------------------------------------------------- scene.reparent_gameobject

        internal static JsonValue ReparentGameObject(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "scene.reparent_gameobject");
            var go = GameObjectPaths.FindByPath(path) ?? throw GameObjectPaths.NotFoundError(path);

            var newParentTransform = ResolveOptionalParent(JsonParams.OptionalString(@params, "newParent"));

            Undo.SetTransformParent(go.transform, newParentTransform, "Hades Reparent " + go.name);

            return BuildGameObjectResult(go);
        }

        // ---------------------------------------------------------------- scene.rename_gameobject

        internal static JsonValue RenameGameObject(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "scene.rename_gameobject");
            var go = GameObjectPaths.FindByPath(path) ?? throw GameObjectPaths.NotFoundError(path);
            var newName = JsonParams.RequireString(@params, "newName", "scene.rename_gameobject");

            Undo.RecordObject(go, "Hades Rename " + go.name);
            go.name = newName;

            return BuildGameObjectResult(go);
        }

        // ---------------------------------------------------------------- scene.setup

        /// <summary>Batch creation: a JSON array of GameObject definitions (name, parent, tag,
        /// layer, position/rotation/scale, components with properties, and recursive children).
        /// One Undo group for the whole call - undoing restores the scene to exactly how it was
        /// before this call, regardless of how many GameObjects/components it touched. A per-entry
        /// failure (bad parent, unknown component type, bad property) is recorded in the returned
        /// 'errors' array and processing continues with the rest of the batch, rather than the
        /// whole call failing opaquely partway through.</summary>
        internal static JsonValue SceneSetup(ReloadGate gate, JsonValue @params)
        {
            var defs = JsonParams.OptionalValue(@params, "gameObjects");
            if (defs == null || defs.Kind != JsonValueKind.Array)
                throw new ArgumentException("scene.setup requires a 'gameObjects' array parameter.");

            var results = JsonValue.NewArray();
            var errors = JsonValue.NewArray();

            Undo.IncrementCurrentGroup();

            foreach (var def in defs.Items)
                ProcessGameObjectDef(def, null, results, errors);

            var goCount = results.Items.Count;
            var errCount = errors.Items.Count;
            Undo.SetCurrentGroupName("Hades Scene Setup: " + goCount + " GameObjects");

            return JsonValue.NewObject()
                .SetProperty("results", results)
                .SetProperty("errors", errors)
                .SetProperty("summary", JsonValue.String(goCount + " GameObjects created, " + errCount + " error(s)"));
        }

        static void ProcessGameObjectDef(JsonValue def, Transform parentTransform, JsonValue results, JsonValue errors)
        {
            if (def == null || def.Kind != JsonValueKind.Object)
            {
                errors.Add(ErrorEntry("(unnamed)", "create", null, null, "each entry in 'gameObjects' must be an object"));
                return;
            }

            var name = JsonParams.OptionalString(def, "name");
            if (string.IsNullOrEmpty(name))
            {
                errors.Add(ErrorEntry("(unnamed)", "create", null, null, "name is required"));
                return;
            }

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Hades Create " + name);

            var parentPath = JsonParams.OptionalString(def, "parent");
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObjectPaths.FindByPath(parentPath);
                if (parentGo != null) go.transform.SetParent(parentGo.transform);
                else errors.Add(ErrorEntry(name, "parent", null, null, "Parent not found: '" + parentPath + "'."));
            }
            else if (parentTransform != null)
            {
                go.transform.SetParent(parentTransform);
            }

            var position = JsonParams.OptionalVector3(def, "position");
            if (position != null) go.transform.localPosition = new Vector3(position[0], position[1], position[2]);
            var rotation = JsonParams.OptionalVector3(def, "rotation");
            if (rotation != null) go.transform.localEulerAngles = new Vector3(rotation[0], rotation[1], rotation[2]);
            var scale = JsonParams.OptionalVector3(def, "scale");
            if (scale != null) go.transform.localScale = new Vector3(scale[0], scale[1], scale[2]);

            var tag = JsonParams.OptionalString(def, "tag");
            if (!string.IsNullOrEmpty(tag))
            {
                try { go.tag = tag; }
                catch (Exception ex) { errors.Add(ErrorEntry(name, "tag", null, null, ex.Message)); }
            }

            var layer = JsonParams.OptionalString(def, "layer");
            if (!string.IsNullOrEmpty(layer))
            {
                var layerIndex = LayerMask.NameToLayer(layer);
                if (layerIndex >= 0) go.layer = layerIndex;
                else errors.Add(ErrorEntry(name, "layer", null, null, "Layer not found: '" + layer + "'."));
            }

            var addedComponents = JsonValue.NewArray();
            var components = JsonParams.OptionalValue(def, "components");
            if (components != null && components.Kind == JsonValueKind.Array)
            {
                foreach (var compDef in components.Items)
                    ProcessComponentDef(compDef, go, name, addedComponents, errors);
            }

            results.Add(JsonValue.NewObject()
                .SetProperty("name", JsonValue.String(name))
                .SetProperty("fileId", JsonValue.Integer(GameObjectPaths.FileId(go)))
                .SetProperty("path", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("parent", go.transform.parent != null ? JsonValue.String(go.transform.parent.name) : JsonValue.Null)
                .SetProperty("components", addedComponents));

            var children = JsonParams.OptionalValue(def, "children");
            if (children != null && children.Kind == JsonValueKind.Array)
                foreach (var child in children.Items)
                    ProcessGameObjectDef(child, go.transform, results, errors);
        }

        static void ProcessComponentDef(JsonValue compDef, GameObject go, string goName, JsonValue addedComponents, JsonValue errors)
        {
            var typeName = compDef != null ? JsonParams.OptionalString(compDef, "type") : null;
            if (string.IsNullOrEmpty(typeName))
            {
                errors.Add(ErrorEntry(goName, "add_component", null, null, "Component 'type' is required"));
                return;
            }

            var type = ComponentTypes.Find(typeName);
            if (type == null)
            {
                errors.Add(ErrorEntry(goName, "add_component", typeName, null, "Component type not found: '" + typeName + "'"));
                return;
            }

            Undo.AddComponent(go, type);
            addedComponents.Add(JsonValue.String(type.Name));

            var properties = JsonParams.OptionalValue(compDef, "properties");
            if (properties == null || properties.Kind != JsonValueKind.Object || properties.Members.Count == 0) return;

            var component = go.GetComponent(type);
            var so = new SerializedObject(component);
            foreach (var member in properties.Members)
            {
                var resolved = SerializedPropertyJson.ResolvePropertyName(so, member.Key, out var resolveErr);
                if (resolved == null)
                {
                    errors.Add(ErrorEntry(goName, "set_property", typeName, member.Key, resolveErr));
                    continue;
                }

                try { SerializedPropertyJson.Set(so.FindProperty(resolved), member.Value); }
                catch (Exception ex) { errors.Add(ErrorEntry(goName, "set_property", typeName, member.Key, ex.Message)); }
            }
            so.ApplyModifiedProperties();
        }

        static JsonValue ErrorEntry(string gameObject, string operation, string component, string property, string error)
        {
            var entry = JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(gameObject))
                .SetProperty("operation", JsonValue.String(operation));
            if (component != null) entry.SetProperty("component", JsonValue.String(component));
            if (property != null) entry.SetProperty("property", JsonValue.String(property));
            entry.SetProperty("error", JsonValue.String(error));
            return entry;
        }

        // ---------------------------------------------------------------- shared helpers

        static Transform ResolveOptionalParent(string parentPath)
        {
            if (string.IsNullOrEmpty(parentPath)) return null;
            var parentGo = GameObjectPaths.FindByPath(parentPath) ?? throw GameObjectPaths.NotFoundError(parentPath);
            return parentGo.transform;
        }

        static JsonValue BuildGameObjectResult(GameObject go) =>
            JsonValue.NewObject()
                .SetProperty("name", JsonValue.String(go.name))
                .SetProperty("fileId", JsonValue.Integer(GameObjectPaths.FileId(go)))
                .SetProperty("path", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("parent", go.transform.parent != null ? JsonValue.String(go.transform.parent.name) : JsonValue.Null);
    }

    /// <summary>GameObject-by-hierarchy-path resolution, shared by SceneCommands and
    /// ComponentCommands (which resolves gameObjectPath/targetPath the identical way) - port of
    /// the old package's GameObjectResolver + ComponentTools.GetPath, unchanged in behaviour.</summary>
    internal static class GameObjectPaths
    {
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var segments = path.Split('/');
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();

            GameObject current = null;
            foreach (var root in roots)
            {
                if (root.name == segments[0]) { current = root; break; }
            }
            if (current == null) return null;

            for (var i = 1; i < segments.Length; i++)
            {
                var child = current.transform.Find(segments[i]);
                if (child == null) return null;
                current = child.gameObject;
            }
            return current;
        }

        public static string GetPath(GameObject go)
        {
            var path = go.name;
            var current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        public static string[] RootNames() =>
            SceneManager.GetActiveScene().GetRootGameObjects().Select(r => r.name).ToArray();

        /// <summary>The Unity local file identifier <paramref name="obj"/> would serialize with if
        /// the scene/prefab were saved right now - the same identifier the app's on-disk YAML
        /// graph reports as "fileId" elsewhere (see GraphTools/InspectionTools), computed here
        /// without needing a save first so a just-created object can be verified against the graph
        /// immediately after this call returns.
        ///
        /// Deliberately the OBSOLETE overload, not its replacement
        /// (GetLocalIdentifierInFileForPersistentObject): measured against a real Editor, the
        /// replacement throws ArgumentException("Input object must be persistent") for exactly the
        /// case this method exists to handle - a GameObject created in a brand-new, never-saved
        /// scene. The deprecated instanceID-based overload has no such restriction and is what
        /// every "predict the fileID before saving" community tool has relied on for years; the
        /// compiler warning is accepted rather than a correctness regression.</summary>
#pragma warning disable CS0618 // GetLocalIdentifierInFile is obsolete - see doc comment above for why it is still the correct call here.
        public static long FileId(UnityEngine.Object obj) => Unsupported.GetLocalIdentifierInFile(obj.GetInstanceID());
#pragma warning restore CS0618

        /// <summary>Actionable by construction: names the exact path that failed to resolve, the
        /// root objects that DO exist right now, and the tool that lists the full tree.</summary>
        public static ArgumentException NotFoundError(string path) =>
            new ArgumentException(
                "GameObject not found: '" + path + "'. Root objects in the active scene: "
                + string.Join(", ", RootNames()) + ". Call scene_get_hierarchy to see the full tree.");

        static ArgumentException ComponentNotFoundError(GameObject go, string typeName)
        {
            var existing = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
            return new ArgumentException(
                "Component '" + typeName + "' not found on '" + GetPath(go) + "'. Existing components: "
                + string.Join(", ", existing) + ". Use component_get_all to confirm.");
        }

        /// <summary>The one safe way to fetch a required component anywhere in this plugin.
        /// Deliberately NOT <c>go.GetComponent(type) ?? throw ...</c>: GetComponent's "not found"
        /// result crosses the native interop boundary and comes back as a UnityEngine.Object "fake
        /// null" - non-null at the raw C# reference level, "null" only through the type's
        /// OVERLOADED == operator (see the Unity manual's "Custom == operator" note). The ??, ?.,
        /// and "is null" forms all use the raw reference check and so never catch it, letting a
        /// fake-null Component silently flow into Undo.RecordObject/DestroyObjectImmediate, which
        /// then throw their own confusing MissingComponentException instead of this file's
        /// actionable error. Measured directly: ComponentCommandsTests caught exactly this for
        /// component.remove before this helper existed. An explicit "== null" (or, as here,
        /// "component == null") is the only form that calls the overloaded operator and works
        /// correctly both ways.
        ///
        /// Originally private to ComponentCommands; promoted here (unchanged in behaviour) once
        /// MaterialCommands/AnimationCommands needed the identical safe lookup for Renderer/
        /// Animator - sharing this instead of a second hand-rolled null check is the whole point
        /// the fake-null doc comment exists to enforce.</summary>
        public static Component RequireComponent(GameObject go, Type type, string typeName)
        {
            var component = go.GetComponent(type);
            if (component == null) throw ComponentNotFoundError(go, typeName);
            return component;
        }
    }

    /// <summary>JsonValue parameter extraction shared by every class-1 command handler - throwing
    /// <see cref="ArgumentException"/> (not a null reference) is what makes a missing/malformed
    /// parameter surface as an actionable JSON-RPC error rather than an opaque failure (see
    /// HadesClient.DescribeFailure, which puts the thrown exception's Message - nothing more -
    /// onto the wire).</summary>
    internal static class JsonParams
    {
        public static string RequireString(JsonValue @params, string key, string context)
        {
            if (@params != null && @params.Kind == JsonValueKind.Object
                && @params.TryGetProperty(key, out var value) && value != null
                && value.Kind == JsonValueKind.String && !string.IsNullOrEmpty(value.AsString()))
            {
                return value.AsString();
            }

            throw new ArgumentException("'" + context + "' requires a non-empty string '" + key + "' parameter.");
        }

        public static int RequireInt(JsonValue @params, string key, string context)
        {
            if (@params != null && @params.Kind == JsonValueKind.Object
                && @params.TryGetProperty(key, out var value) && value != null
                && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float))
            {
                return (int)value.AsDouble();
            }

            throw new ArgumentException("'" + context + "' requires an integer '" + key + "' parameter.");
        }

        public static string OptionalString(JsonValue @params, string key)
        {
            if (@params != null && @params.Kind == JsonValueKind.Object
                && @params.TryGetProperty(key, out var value) && value != null && value.Kind == JsonValueKind.String)
            {
                return value.AsString();
            }
            return null;
        }

        public static JsonValue OptionalValue(JsonValue @params, string key)
        {
            if (@params != null && @params.Kind == JsonValueKind.Object && @params.TryGetProperty(key, out var value))
                return value;
            return null;
        }

        /// <summary><paramref name="defaultValue"/> when the key is absent or null. Throws if
        /// present but not a number - same "explicit over silent" reasoning as
        /// <see cref="OptionalVector3"/>.</summary>
        public static int OptionalInt(JsonValue @params, string key, int defaultValue)
        {
            var value = OptionalValue(@params, key);
            if (value == null || value.Kind == JsonValueKind.Null) return defaultValue;
            if (value.Kind != JsonValueKind.Integer && value.Kind != JsonValueKind.Float)
                throw new ArgumentException("'" + key + "' must be an integer.");
            return (int)value.AsDouble();
        }

        /// <summary><paramref name="defaultValue"/> when the key is absent, null, or not a JSON
        /// boolean - lenient (not throwing) because every current caller treats this as a genuinely
        /// optional flag with a sensible default, not a required, type-checked parameter.</summary>
        public static bool OptionalBool(JsonValue @params, string key, bool defaultValue)
        {
            var value = OptionalValue(@params, key);
            return value != null && value.Kind == JsonValueKind.Boolean ? value.AsBoolean() : defaultValue;
        }

        /// <summary><paramref name="defaultValue"/> when the key is absent or not a number.</summary>
        public static double OptionalDouble(JsonValue @params, string key, double defaultValue)
        {
            var value = OptionalValue(@params, key);
            return value != null && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float)
                ? value.AsDouble()
                : defaultValue;
        }

        /// <summary>A 3-element JSON array [x, y, z], or null when the key is absent. Throws if
        /// present but malformed - a silently-ignored bad array is worse than an explicit error.</summary>
        public static float[] OptionalVector3(JsonValue @params, string key)
        {
            var value = OptionalValue(@params, key);
            if (value == null || value.Kind == JsonValueKind.Null) return null;
            if (value.Kind != JsonValueKind.Array || value.Items.Count != 3)
                throw new ArgumentException("'" + key + "' must be a 3-element array [x, y, z].");

            return value.Items.Select(v => (float)v.AsDouble()).ToArray();
        }
    }
}
