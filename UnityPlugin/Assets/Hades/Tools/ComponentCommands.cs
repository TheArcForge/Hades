// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 component and wiring mutations: add/remove a component, set one or many serialized
    /// properties, wire an object-reference field (reference.set), and add/remove a UnityEvent
    /// persistent listener. Same no-lease contract as <see cref="SceneCommands"/> - see that
    /// class's own doc comment for why none of these ever touch the <c>gate</c> parameter, and for
    /// the "scene.*" vs "component.*"/"reference.*"/"event.*" method-name convention.
    ///
    /// reference.set / event.add_listener / event.remove_listener live here rather than in a
    /// separate file because each is a SerializedProperty (or UnityEventBase) write on an EXISTING
    /// component - the identical shape as component.set_property, just narrower (one specific
    /// field kind). Every one of these registers Undo.RecordObject on the component BEFORE
    /// mutating it, unlike a newly-created object (SceneCommands), where the single
    /// RegisterCreatedObjectUndo/AddComponent call already covers the whole thing.
    /// </summary>
    public static class ComponentCommands
    {
        // ---------------------------------------------------------------- component.add

        internal static JsonValue AddComponent(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "component.add");
            var typeName = JsonParams.RequireString(@params, "componentType", "component.add");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var type = ComponentTypes.Find(typeName) ?? throw ComponentTypes.NotFoundError(typeName);

            var component = Undo.AddComponent(go, type);

            return JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("added", JsonValue.String(type.Name))
                .SetProperty("fileId", JsonValue.Integer(GameObjectPaths.FileId(component)));
        }

        // ---------------------------------------------------------------- component.remove

        internal static JsonValue RemoveComponent(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "component.remove");
            var typeName = JsonParams.RequireString(@params, "componentType", "component.remove");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var type = ComponentTypes.Find(typeName) ?? throw ComponentTypes.NotFoundError(typeName);
            var component = GameObjectPaths.RequireComponent(go, type, typeName);

            var fileId = GameObjectPaths.FileId(component);
            Undo.DestroyObjectImmediate(component);

            return JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("removed", JsonValue.String(type.Name))
                .SetProperty("fileId", JsonValue.Integer(fileId));
        }

        // ---------------------------------------------------------------- component.set_property

        internal static JsonValue SetProperty(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "component.set_property");
            var typeName = JsonParams.RequireString(@params, "componentType", "component.set_property");
            var propertyName = JsonParams.RequireString(@params, "propertyName", "component.set_property");
            var value = JsonParams.OptionalValue(@params, "value") ?? JsonValue.Null;

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var type = ComponentTypes.Find(typeName) ?? throw ComponentTypes.NotFoundError(typeName);
            var component = GameObjectPaths.RequireComponent(go, type, typeName);

            var so = new SerializedObject(component);
            var resolved = SerializedPropertyJson.ResolvePropertyName(so, propertyName, out var resolveError)
                ?? throw new ArgumentException(resolveError);

            Undo.RecordObject(component, "Hades Set " + propertyName);
            try
            {
                SerializedPropertyJson.Set(so.FindProperty(resolved), value);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Failed to set property '" + propertyName + "' on " + type.Name + ": " + ex.Message);
            }
            so.ApplyModifiedProperties();

            return JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("component", JsonValue.String(type.Name))
                .SetProperty("property", JsonValue.String(propertyName))
                .SetProperty("newValue", value);
        }

        // ---------------------------------------------------------------- component.set_properties

        /// <summary>Batch form: a list of {gameObject, component, properties} operations, one Undo
        /// group for the whole call. A property that fails to resolve or set is recorded in that
        /// operation's own 'failed' list (with the ones that DID apply in 'applied') rather than
        /// aborting the operation or the batch - see ComponentCommandsTests for the partial-failure
        /// shape this produces.</summary>
        internal static JsonValue SetProperties(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("component.set_properties requires an 'operations' array parameter.");

            var results = JsonValue.NewArray();
            var errors = JsonValue.NewArray();
            var totalProps = 0;

            Undo.IncrementCurrentGroup();

            foreach (var op in ops.Items)
            {
                var goPath = JsonParams.OptionalString(op, "gameObject");
                if (string.IsNullOrEmpty(goPath))
                {
                    errors.Add(TopLevelError(null, "'gameObject' is required"));
                    continue;
                }

                var go = GameObjectPaths.FindByPath(goPath);
                if (go == null)
                {
                    errors.Add(TopLevelError(goPath, "GameObject not found: '" + goPath + "'."));
                    continue;
                }

                var componentTypeName = JsonParams.OptionalString(op, "component");
                var type = string.IsNullOrEmpty(componentTypeName) ? null : ComponentTypes.Find(componentTypeName);
                if (type == null)
                {
                    errors.Add(TopLevelError(goPath, "Component type not found: '" + componentTypeName + "'."));
                    continue;
                }

                var component = go.GetComponent(type);
                if (component == null)
                {
                    var existing = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
                    errors.Add(TopLevelError(goPath,
                        "Component '" + componentTypeName + "' not found on '" + GameObjectPaths.GetPath(go)
                        + "'. Existing: " + string.Join(", ", existing) + "."));
                    continue;
                }

                var (applied, failed, propsSet) = ApplyProperties(component, JsonParams.OptionalValue(op, "properties"));
                totalProps += propsSet;

                results.Add(JsonValue.NewObject()
                    .SetProperty("gameObject", JsonValue.String(goPath))
                    .SetProperty("component", JsonValue.String(type.Name))
                    .SetProperty("applied", applied)
                    .SetProperty("failed", failed));
            }

            Undo.SetCurrentGroupName("Hades Set Properties: " + totalProps + " properties across " + results.Items.Count + " operation(s)");

            return JsonValue.NewObject()
                .SetProperty("results", results)
                .SetProperty("errors", errors)
                .SetProperty("summary", JsonValue.String(
                    totalProps + " properties set across " + results.Items.Count + " operation(s), " + errors.Items.Count + " error(s)"));
        }

        static (JsonValue applied, JsonValue failed, int propsSet) ApplyProperties(Component component, JsonValue properties)
        {
            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();
            if (properties == null || properties.Kind != JsonValueKind.Object || properties.Members.Count == 0)
                return (applied, failed, 0);

            var so = new SerializedObject(component);
            Undo.RecordObject(component, "Hades Set Properties " + component.GetType().Name);

            var propsSet = 0;
            foreach (var member in properties.Members)
            {
                var resolved = SerializedPropertyJson.ResolvePropertyName(so, member.Key, out var resolveErr);
                if (resolved == null)
                {
                    failed.Add(JsonValue.NewObject().SetProperty("property", JsonValue.String(member.Key)).SetProperty("error", JsonValue.String(resolveErr)));
                    continue;
                }

                try
                {
                    SerializedPropertyJson.Set(so.FindProperty(resolved), member.Value);
                    applied.Add(JsonValue.String(member.Key));
                    propsSet++;
                }
                catch (Exception ex)
                {
                    failed.Add(JsonValue.NewObject().SetProperty("property", JsonValue.String(member.Key)).SetProperty("error", JsonValue.String(ex.Message)));
                }
            }

            if (propsSet > 0) so.ApplyModifiedProperties();
            return (applied, failed, propsSet);
        }

        static JsonValue TopLevelError(string gameObject, string error)
        {
            var entry = JsonValue.NewObject();
            entry.SetProperty("gameObject", gameObject != null ? JsonValue.String(gameObject) : JsonValue.Null);
            entry.SetProperty("error", JsonValue.String(error));
            return entry;
        }

        // ---------------------------------------------------------------- reference.set

        internal static JsonValue ReferenceSet(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "reference.set");
            var typeName = JsonParams.RequireString(@params, "componentType", "reference.set");
            var propertyName = JsonParams.RequireString(@params, "propertyName", "reference.set");
            var targetPath = JsonParams.OptionalString(@params, "targetPath");
            var targetAssetPath = JsonParams.OptionalString(@params, "targetAssetPath");
            var targetComponentType = JsonParams.OptionalString(@params, "targetComponentType");

            var hasTargetPath = !string.IsNullOrEmpty(targetPath);
            var hasAssetPath = !string.IsNullOrEmpty(targetAssetPath);
            if (!hasTargetPath && !hasAssetPath)
            {
                throw new ArgumentException(
                    "reference.set requires either 'targetPath' (a scene GameObject) or 'targetAssetPath' (a project asset).");
            }
            if (hasTargetPath && hasAssetPath)
                throw new ArgumentException("reference.set requires only ONE of 'targetPath' or 'targetAssetPath', not both.");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var type = ComponentTypes.Find(typeName) ?? throw ComponentTypes.NotFoundError(typeName);
            var component = GameObjectPaths.RequireComponent(go, type, typeName);

            var so = new SerializedObject(component);
            var resolved = SerializedPropertyJson.ResolvePropertyName(so, propertyName, out var resolveError)
                ?? throw new ArgumentException(resolveError);
            var prop = so.FindProperty(resolved);
            if (prop.propertyType != SerializedPropertyType.ObjectReference)
                throw new ArgumentException("Property '" + propertyName + "' is type " + prop.propertyType + ", not ObjectReference.");

            UnityEngine.Object targetObj;
            string targetDescription;

            if (hasTargetPath)
            {
                var targetGo = GameObjectPaths.FindByPath(targetPath) ?? throw GameObjectPaths.NotFoundError(targetPath);

                if (!string.IsNullOrEmpty(targetComponentType))
                {
                    var targetType = ComponentTypes.Find(targetComponentType) ?? throw ComponentTypes.NotFoundError(targetComponentType);
                    var targetComp = GameObjectPaths.RequireComponent(targetGo, targetType, targetComponentType);
                    targetObj = targetComp;
                    targetDescription = targetPath + " (" + targetComponentType + ")";
                }
                else
                {
                    targetObj = targetGo;
                    targetDescription = targetPath;
                }

                var fieldType = GetObjectReferenceFieldType(prop);
                if (fieldType != null && !fieldType.IsInstanceOfType(targetObj))
                {
                    throw new ArgumentException(
                        "Type mismatch: field '" + propertyName + "' expects " + fieldType.Name + ", but target is "
                        + targetObj.GetType().Name + "."
                        + (string.IsNullOrEmpty(targetComponentType)
                            ? " Try specifying targetComponentType to reference a component instead of the GameObject."
                            : ""));
                }
            }
            else
            {
                targetObj = ResolveAsset(targetAssetPath, GetObjectReferenceFieldType(prop));
                targetDescription = targetAssetPath;
            }

            Undo.RecordObject(component, "Hades Set Reference " + propertyName);
            prop.objectReferenceValue = targetObj;
            so.ApplyModifiedProperties();

            return JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("component", JsonValue.String(type.Name))
                .SetProperty("property", JsonValue.String(propertyName))
                .SetProperty("target", JsonValue.String(targetDescription))
                .SetProperty("targetType", JsonValue.String(targetObj.GetType().Name));
        }

        static Type GetObjectReferenceFieldType(SerializedProperty prop)
        {
            var targetObject = prop.serializedObject.targetObject;
            if (targetObject == null) return null;

            var objType = targetObject.GetType();
            var fieldInfo = objType.GetField(prop.name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fieldInfo != null) return fieldInfo.FieldType;

            // Fallback for built-in Unity types: map m_FieldName -> fieldName property.
            var propName = prop.name;
            if (propName.StartsWith("m_") && propName.Length > 2)
            {
                var csName = char.ToLowerInvariant(propName[2]) + propName.Substring(3);
                var propInfo = objType.GetProperty(csName, BindingFlags.Instance | BindingFlags.Public);
                if (propInfo != null) return propInfo.PropertyType;
            }
            return null;
        }

        static UnityEngine.Object ResolveAsset(string assetPath, Type targetType)
        {
            var loaded = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (loaded == null)
            {
                throw new ArgumentException(
                    "Asset not found at path: '" + assetPath + "'. Use search_by_name to find the correct project-relative path.");
            }

            if (targetType == null || targetType.IsInstanceOfType(loaded)) return loaded;

            var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath).Where(a => a != null && targetType.IsInstanceOfType(a)).ToArray();
            if (subAssets.Length == 1) return subAssets[0];
            if (subAssets.Length > 1)
            {
                throw new ArgumentException(
                    "Multiple " + targetType.Name + " sub-assets found in '" + assetPath + "': "
                    + string.Join(", ", subAssets.Select(a => a.name)) + ".");
            }

            throw new ArgumentException(
                "Type mismatch: field expects " + targetType.Name + ", but '" + assetPath + "' is " + loaded.GetType().Name
                + " and contains no " + targetType.Name + " sub-assets.");
        }

        // ---------------------------------------------------------------- event.add_listener

        internal static JsonValue EventAddListener(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "event.add_listener");
            var typeName = JsonParams.RequireString(@params, "componentType", "event.add_listener");
            var eventName = JsonParams.RequireString(@params, "eventName", "event.add_listener");
            var targetPath = JsonParams.RequireString(@params, "targetPath", "event.add_listener");
            var targetMethod = JsonParams.RequireString(@params, "targetMethod", "event.add_listener");
            var argument = JsonParams.OptionalString(@params, "argument");
            var argumentType = JsonParams.OptionalString(@params, "argumentType");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var type = ComponentTypes.Find(typeName) ?? throw ComponentTypes.NotFoundError(typeName);
            var component = GameObjectPaths.RequireComponent(go, type, typeName);

            var eventField = FindUnityEventField(type, eventName) ?? throw EventFieldNotFoundError(type, eventName);
            var unityEvent = eventField.GetValue(component) as UnityEventBase
                ?? throw new ArgumentException("Could not access event '" + eventName + "' on " + typeName + ".");

            var targetGo = GameObjectPaths.FindByPath(targetPath) ?? throw GameObjectPaths.NotFoundError(targetPath);

            var (targetObj, methodInfo) = FindListenerMethod(targetGo, targetMethod, argument != null);
            if (targetObj == null || methodInfo == null)
            {
                throw new ArgumentException(
                    "Method '" + targetMethod + "' not found on any component of '" + targetPath + "'. Available methods: "
                    + string.Join(", ", ListAvailableMethods(targetGo)) + ".");
            }

            Undo.RecordObject(component, "Hades Add Listener " + eventName);

            if (argument == null)
            {
                UnityEventTools.AddVoidPersistentListener(unityEvent, (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), targetObj, methodInfo));
            }
            else
            {
                AddTypedListener(unityEvent, targetObj, methodInfo, argument, argumentType);
            }

            return JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("eventField", JsonValue.String(eventName))
                .SetProperty("target", JsonValue.String(GameObjectPaths.GetPath(targetGo)))
                .SetProperty("method", JsonValue.String(targetMethod))
                .SetProperty("argument", argument != null ? JsonValue.String(argument) : JsonValue.Null)
                .SetProperty("listenerCount", JsonValue.Integer(unityEvent.GetPersistentEventCount()));
        }

        // ---------------------------------------------------------------- event.remove_listener

        internal static JsonValue EventRemoveListener(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "event.remove_listener");
            var typeName = JsonParams.RequireString(@params, "componentType", "event.remove_listener");
            var eventName = JsonParams.RequireString(@params, "eventName", "event.remove_listener");
            var index = JsonParams.RequireInt(@params, "index", "event.remove_listener");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var type = ComponentTypes.Find(typeName) ?? throw ComponentTypes.NotFoundError(typeName);
            var component = GameObjectPaths.RequireComponent(go, type, typeName);

            var eventField = FindUnityEventField(type, eventName) ?? throw EventFieldNotFoundError(type, eventName);
            var unityEvent = eventField.GetValue(component) as UnityEventBase
                ?? throw new ArgumentException("Could not access event '" + eventName + "' on " + typeName + ".");

            var count = unityEvent.GetPersistentEventCount();
            if (index < 0 || index >= count)
            {
                throw new ArgumentException(
                    "Index " + index + " out of range. Event '" + eventName + "' has " + count
                    + " listener(s) (valid indices: 0.." + (count - 1) + ").");
            }

            Undo.RecordObject(component, "Hades Remove Listener " + eventName);
            UnityEventTools.RemovePersistentListener(unityEvent, index);

            return JsonValue.NewObject()
                .SetProperty("removedIndex", JsonValue.Integer(index))
                .SetProperty("remainingListeners", JsonValue.Integer(unityEvent.GetPersistentEventCount()));
        }

        // ---------------------------------------------------------------- event helpers

        static FieldInfo FindUnityEventField(Type componentType, string fieldName)
        {
            var field = componentType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null && typeof(UnityEventBase).IsAssignableFrom(field.FieldType) ? field : null;
        }

        static ArgumentException EventFieldNotFoundError(Type componentType, string eventName)
        {
            var available = componentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => typeof(UnityEventBase).IsAssignableFrom(f.FieldType)).Select(f => f.Name).ToArray();
            return new ArgumentException(
                "UnityEvent field '" + eventName + "' not found on " + componentType.Name + ". Available events: "
                + string.Join(", ", available) + ".");
        }

        static (UnityEngine.Object target, MethodInfo method) FindListenerMethod(GameObject go, string methodName, bool hasArgument)
        {
            var expectedParams = hasArgument ? 1 : 0;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var method = comp.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == expectedParams);
                if (method != null) return (comp, method);
            }

            // No component method matched - also allow methods declared on GameObject itself
            // (e.g. SetActive(bool)), binding the persistent listener to the GameObject.
            var goMethod = typeof(GameObject).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null,
                hasArgument ? new[] { typeof(bool) } : Type.EmptyTypes, null);
            return goMethod != null ? (go, goMethod) : (null, null);
        }

        static string[] ListAvailableMethods(GameObject go)
        {
            var methods = new List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var compType = comp.GetType();
                foreach (var m in compType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.DeclaringType == typeof(object) || m.DeclaringType == typeof(Component)
                        || m.DeclaringType == typeof(MonoBehaviour) || m.DeclaringType == typeof(Behaviour))
                        continue;
                    var parms = m.GetParameters();
                    if (parms.Length <= 1)
                        methods.Add(compType.Name + "." + m.Name + "(" + string.Join(", ", parms.Select(p => p.ParameterType.Name)) + ")");
                }
            }
            return methods.Distinct().ToArray();
        }

        static void AddTypedListener(UnityEventBase unityEvent, UnityEngine.Object target, MethodInfo method, string argument, string argumentType)
        {
            var resolvedType = argumentType?.ToLowerInvariant() ?? DetectArgumentType(argument);
            switch (resolvedType)
            {
                case "int":
                    UnityEventTools.AddIntPersistentListener(unityEvent,
                        (UnityAction<int>)Delegate.CreateDelegate(typeof(UnityAction<int>), target, method), int.Parse(argument));
                    break;
                case "float":
                    UnityEventTools.AddFloatPersistentListener(unityEvent,
                        (UnityAction<float>)Delegate.CreateDelegate(typeof(UnityAction<float>), target, method), float.Parse(argument));
                    break;
                case "bool":
                    UnityEventTools.AddBoolPersistentListener(unityEvent,
                        (UnityAction<bool>)Delegate.CreateDelegate(typeof(UnityAction<bool>), target, method), bool.Parse(argument));
                    break;
                default:
                    UnityEventTools.AddStringPersistentListener(unityEvent,
                        (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), target, method), argument);
                    break;
            }
        }

        static string DetectArgumentType(string value)
        {
            if (int.TryParse(value, out _)) return "int";
            if (float.TryParse(value, out _)) return "float";
            if (bool.TryParse(value, out _)) return "bool";
            return "string";
        }

        // RequireComponent/ComponentNotFoundError moved to GameObjectPaths (SceneCommands.cs) -
        // MaterialCommands/AnimationCommands need the identical fake-null-safe lookup; see that
        // method's own doc comment for why a second hand-rolled copy is exactly what it exists to
        // prevent.
    }

    /// <summary>Component-type-by-name resolution, shared by SceneCommands (scene.setup's
    /// per-object components) and every ComponentCommands handler - port of the old package's
    /// FindComponentType, unchanged in behaviour.</summary>
    internal static class ComponentTypes
    {
        public static Type Find(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                var exact = assembly.GetType(typeName);
                if (exact != null && typeof(Component).IsAssignableFrom(exact)) return exact;

                foreach (var t in types)
                    if (t.Name == typeName && typeof(Component).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        public static ArgumentException NotFoundError(string typeName) =>
            new ArgumentException(
                "Component type not found: '" + typeName + "'. Provide the exact type name (case-sensitive), e.g. "
                + "'Rigidbody' or 'BoxCollider' for built-in types, or your script's class name for a MonoBehaviour. "
                + "Use search_by_name to confirm the exact name and check the assembly containing it is compiled.");
    }

    /// <summary>SerializedProperty <-> JsonValue conversion, plus the fuzzy property-name resolver
    /// (exact path, then case-insensitive display name, then a punctuation/case-normalized match)
    /// every component mutation uses to turn a not-quite-exact property name into an actionable
    /// error listing what IS valid - port of the old package's ComponentTools property helpers,
    /// re-targeted from hand-parsed pseudo-JSON strings onto <see cref="JsonValue"/> now that the
    /// wire actually carries typed JSON.</summary>
    internal static class SerializedPropertyJson
    {
        public static JsonValue Get(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return JsonValue.Integer(prop.intValue);
                case SerializedPropertyType.Boolean: return JsonValue.Bool(prop.boolValue);
                case SerializedPropertyType.Float: return JsonValue.Float(prop.floatValue);
                case SerializedPropertyType.String: return JsonValue.String(prop.stringValue);
                case SerializedPropertyType.LayerMask: return JsonValue.Integer(prop.intValue);
                case SerializedPropertyType.Enum:
                    return prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length
                        ? JsonValue.String(prop.enumNames[prop.enumValueIndex])
                        : JsonValue.Integer(prop.enumValueIndex);
                case SerializedPropertyType.ObjectReference:
                {
                    var obj = prop.objectReferenceValue;
                    if (obj == null) return JsonValue.Null;
                    var assetPath = AssetDatabase.GetAssetPath(obj);
                    return JsonValue.String(string.IsNullOrEmpty(assetPath) ? obj.name : assetPath);
                }
                case SerializedPropertyType.Color:
                {
                    var c = prop.colorValue;
                    return JsonValue.NewObject().SetProperty("r", JsonValue.Float(c.r)).SetProperty("g", JsonValue.Float(c.g))
                        .SetProperty("b", JsonValue.Float(c.b)).SetProperty("a", JsonValue.Float(c.a));
                }
                case SerializedPropertyType.Vector2:
                {
                    var v = prop.vector2Value;
                    return JsonValue.NewObject().SetProperty("x", JsonValue.Float(v.x)).SetProperty("y", JsonValue.Float(v.y));
                }
                case SerializedPropertyType.Vector3:
                {
                    var v = prop.vector3Value;
                    return JsonValue.NewObject().SetProperty("x", JsonValue.Float(v.x)).SetProperty("y", JsonValue.Float(v.y)).SetProperty("z", JsonValue.Float(v.z));
                }
                case SerializedPropertyType.Vector4:
                {
                    var v = prop.vector4Value;
                    return JsonValue.NewObject().SetProperty("x", JsonValue.Float(v.x)).SetProperty("y", JsonValue.Float(v.y))
                        .SetProperty("z", JsonValue.Float(v.z)).SetProperty("w", JsonValue.Float(v.w));
                }
                case SerializedPropertyType.Quaternion:
                {
                    var q = prop.quaternionValue;
                    return JsonValue.NewObject().SetProperty("x", JsonValue.Float(q.x)).SetProperty("y", JsonValue.Float(q.y))
                        .SetProperty("z", JsonValue.Float(q.z)).SetProperty("w", JsonValue.Float(q.w));
                }
                case SerializedPropertyType.Rect:
                {
                    var r = prop.rectValue;
                    return JsonValue.NewObject().SetProperty("x", JsonValue.Float(r.x)).SetProperty("y", JsonValue.Float(r.y))
                        .SetProperty("width", JsonValue.Float(r.width)).SetProperty("height", JsonValue.Float(r.height));
                }
                case SerializedPropertyType.Vector2Int:
                {
                    var v = prop.vector2IntValue;
                    return JsonValue.NewObject().SetProperty("x", JsonValue.Integer(v.x)).SetProperty("y", JsonValue.Integer(v.y));
                }
                case SerializedPropertyType.Vector3Int:
                {
                    var v = prop.vector3IntValue;
                    return JsonValue.NewObject().SetProperty("x", JsonValue.Integer(v.x)).SetProperty("y", JsonValue.Integer(v.y)).SetProperty("z", JsonValue.Integer(v.z));
                }
                default:
                    return JsonValue.String("<unsupported:" + prop.propertyType + ">");
            }
        }

        public static void Set(SerializedProperty prop, JsonValue value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: prop.intValue = (int)RequireNumber(value, prop.name); break;
                case SerializedPropertyType.Boolean: prop.boolValue = RequireBool(value, prop.name); break;
                case SerializedPropertyType.Float: prop.floatValue = (float)RequireNumber(value, prop.name); break;
                case SerializedPropertyType.String: prop.stringValue = RequireStringOrNull(value, prop.name) ?? string.Empty; break;
                case SerializedPropertyType.LayerMask: prop.intValue = (int)RequireNumber(value, prop.name); break;
                case SerializedPropertyType.Color:
                    prop.colorValue = new Color(F(value, "r"), F(value, "g"), F(value, "b"), HasKey(value, "a") ? F(value, "a") : 1f);
                    break;
                case SerializedPropertyType.Vector2:
                    prop.vector2Value = new Vector2(F(value, "x"), F(value, "y"));
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = new Vector3(F(value, "x"), F(value, "y"), F(value, "z"));
                    break;
                case SerializedPropertyType.Vector4:
                    prop.vector4Value = new Vector4(F(value, "x"), F(value, "y"), F(value, "z"), F(value, "w"));
                    break;
                case SerializedPropertyType.Quaternion:
                    prop.quaternionValue = new Quaternion(F(value, "x"), F(value, "y"), F(value, "z"), F(value, "w"));
                    break;
                case SerializedPropertyType.Rect:
                    prop.rectValue = new Rect(F(value, "x"), F(value, "y"), F(value, "width"), F(value, "height"));
                    break;
                case SerializedPropertyType.Vector2Int:
                    prop.vector2IntValue = new Vector2Int((int)F(value, "x"), (int)F(value, "y"));
                    break;
                case SerializedPropertyType.Vector3Int:
                    prop.vector3IntValue = new Vector3Int((int)F(value, "x"), (int)F(value, "y"), (int)F(value, "z"));
                    break;
                case SerializedPropertyType.Enum:
                    SetEnum(prop, value);
                    break;
                case SerializedPropertyType.ObjectReference:
                    SetObjectReference(prop, value);
                    break;
                default:
                    throw new ArgumentException("Property '" + prop.name + "' has unsupported type " + prop.propertyType + " and cannot be set through this API.");
            }
        }

        static void SetEnum(SerializedProperty prop, JsonValue value)
        {
            if (value != null && value.Kind == JsonValueKind.String)
            {
                var name = value.AsString();
                var idx = Array.IndexOf(prop.enumNames, name);
                if (idx < 0)
                    throw new ArgumentException("Invalid enum value '" + name + "' for '" + prop.name + "'. Valid values: " + string.Join(", ", prop.enumNames) + ".");
                prop.enumValueIndex = idx;
            }
            else if (value != null && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float))
            {
                prop.enumValueIndex = (int)value.AsDouble();
            }
            else
            {
                throw new ArgumentException("Enum property '" + prop.name + "' needs a string name or integer index. Valid values: " + string.Join(", ", prop.enumNames) + ".");
            }
        }

        static void SetObjectReference(SerializedProperty prop, JsonValue value)
        {
            if (value == null || value.Kind == JsonValueKind.Null)
            {
                prop.objectReferenceValue = null;
                return;
            }
            if (value.Kind != JsonValueKind.String)
                throw new ArgumentException("ObjectReference property '" + prop.name + "' needs a string asset path, or null to clear.");

            var assetPath = value.AsString();
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                throw new ArgumentException("Asset not found at path: '" + assetPath + "'. Use search_by_name to find the correct project-relative path.");
            prop.objectReferenceValue = asset;
        }

        static double RequireNumber(JsonValue value, string propName)
        {
            if (value != null && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float)) return value.AsDouble();
            throw new ArgumentException("Property '" + propName + "' needs a numeric value.");
        }

        static bool RequireBool(JsonValue value, string propName)
        {
            if (value != null && value.Kind == JsonValueKind.Boolean) return value.AsBoolean();
            throw new ArgumentException("Property '" + propName + "' needs a boolean value.");
        }

        static string RequireStringOrNull(JsonValue value, string propName)
        {
            if (value == null || value.Kind == JsonValueKind.Null) return null;
            if (value.Kind == JsonValueKind.String) return value.AsString();
            throw new ArgumentException("Property '" + propName + "' needs a string value.");
        }

        static bool HasKey(JsonValue obj, string key) =>
            obj != null && obj.Kind == JsonValueKind.Object && obj.TryGetProperty(key, out _);

        static float F(JsonValue obj, string key)
        {
            if (obj == null || obj.Kind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v) || v == null
                || (v.Kind != JsonValueKind.Float && v.Kind != JsonValueKind.Integer))
            {
                throw new ArgumentException("Expected a numeric '" + key + "' field in the JSON value.");
            }
            return (float)v.AsDouble();
        }

        /// <summary>Resolves <paramref name="input"/> to a real SerializedProperty path: an exact
        /// match first, then a case-insensitive display-name match, then a punctuation/case
        /// normalized match (so "Local Position", "localposition" and "local_position" all resolve
        /// to the same field). Returns null with <paramref name="error"/> set - listing every valid
        /// property with its type - when nothing matches or the normalized match is ambiguous.</summary>
        public static string ResolvePropertyName(SerializedObject so, string input, out string error)
        {
            error = null;

            var direct = so.FindProperty(input);
            if (direct != null) return input;

            var displayToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var normalizedToPath = new Dictionary<string, List<string>>();

            var iterator = so.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    var path = iterator.propertyPath;
                    var display = iterator.displayName;
                    if (!displayToPath.ContainsKey(display)) displayToPath[display] = path;

                    var normalized = Normalize(display);
                    if (!normalizedToPath.TryGetValue(normalized, out var list)) normalizedToPath[normalized] = list = new List<string>();
                    list.Add(path);
                } while (iterator.NextVisible(false));
            }

            if (displayToPath.TryGetValue(input, out var byDisplay)) return byDisplay;

            var normalizedInput = Normalize(input);
            if (normalizedToPath.TryGetValue(normalizedInput, out var candidates))
            {
                if (candidates.Count == 1) return candidates[0];
                error = "Ambiguous property name '" + input + "'. Matches: " + string.Join(", ", candidates) + ".";
                return null;
            }

            error = "Property '" + input + "' not found on " + so.targetObject.GetType().Name + ". Valid properties: "
                + string.Join(", ", ListPropertiesWithTypes(so)) + ".";
            return null;
        }

        public static string[] ListPropertiesWithTypes(SerializedObject so)
        {
            var entries = new List<string>();
            var iterator = so.GetIterator();
            if (iterator.NextVisible(true))
            {
                do { entries.Add(iterator.name + " (" + iterator.propertyType + ")"); }
                while (iterator.NextVisible(false));
            }
            return entries.ToArray();
        }

        static string Normalize(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
                if (ch != ' ' && ch != '_' && ch != '-') sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }
    }
}
