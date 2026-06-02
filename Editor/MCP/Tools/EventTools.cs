using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class EventTools
    {
        [MCPTool("event_add_listener", "Add a persistent listener to a UnityEvent (e.g. Button.onClick). " +
            "Supports void methods and methods with a single argument (string, int, float, bool).")]
        public static MCPToolResult AddListener(
            [MCPToolParam("GameObject with the event source component", required: true)] string game_object_path,
            [MCPToolParam("Component type containing the event (e.g. 'Button')", required: true)] string component_type,
            [MCPToolParam("Event field name (e.g. 'm_OnClick')", required: true)] string event_name,
            [MCPToolParam("Target GameObject path (the object with the method to call)", required: true)] string target_path,
            [MCPToolParam("Method name to call on target", required: true)] string target_method,
            [MCPToolParam("Argument value (omit for void methods)")] string argument = null,
            [MCPToolParam("Argument type: string, int, float, bool (auto-detected if omitted)")] string argument_type = null)
        {
            var go = ComponentTools.FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = ComponentTools.FindComponentType(component_type);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{component_type}'.");

            var component = go.GetComponent(type);
            if (component == null)
                return MCPToolResult.Error($"Component '{component_type}' not found on '{ComponentTools.GetPath(go)}'.");

            var eventField = FindUnityEventField(type, event_name);
            if (eventField == null)
            {
                var available = ListUnityEventFields(type);
                return MCPToolResult.Error(
                    $"UnityEvent field '{event_name}' not found on {component_type}. " +
                    $"Available events: {string.Join(", ", available)}");
            }

            var unityEvent = eventField.GetValue(component) as UnityEventBase;
            if (unityEvent == null)
                return MCPToolResult.Error($"Could not access event '{event_name}' on {component_type}.");

            var targetGO = GameObjectResolver.FindByPath(target_path);
            if (targetGO == null)
                return GameObjectNotFoundError(target_path);

            var (targetObj, methodInfo) = FindMethod(targetGO, target_method, argument != null);
            if (targetObj == null || methodInfo == null)
            {
                var available = ListAvailableMethods(targetGO);
                return MCPToolResult.Error(
                    $"Method '{target_method}' not found on any component of '{target_path}'. " +
                    $"Available methods: {string.Join(", ", available)}");
            }

            Undo.RecordObject(component, $"MCP Add Listener {event_name}");

            if (argument == null)
            {
                UnityEventTools.AddVoidPersistentListener(
                    unityEvent, methodInfo.CreateDelegate(typeof(UnityAction), targetObj) as UnityAction);
            }
            else
            {
                AddTypedListener(unityEvent, targetObj, target_method, argument, argument_type);
            }

            return MCPToolResult.Success(new
            {
                gameObject = ComponentTools.GetPath(go),
                eventField = event_name,
                target = ComponentTools.GetPath(targetGO),
                method = target_method,
                argument = argument,
                listenerCount = unityEvent.GetPersistentEventCount()
            });
        }

        [MCPTool("event_remove_listener", "Remove a persistent listener by index from a UnityEvent")]
        public static MCPToolResult RemoveListener(
            [MCPToolParam("GameObject with the event source component", required: true)] string game_object_path,
            [MCPToolParam("Component type containing the event", required: true)] string component_type,
            [MCPToolParam("Event field name", required: true)] string event_name,
            [MCPToolParam("Listener index to remove (0-based)", required: true)] string index)
        {
            var go = ComponentTools.FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = ComponentTools.FindComponentType(component_type);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{component_type}'.");

            var component = go.GetComponent(type);
            if (component == null)
                return MCPToolResult.Error($"Component '{component_type}' not found on '{ComponentTools.GetPath(go)}'.");

            var eventField = FindUnityEventField(type, event_name);
            if (eventField == null)
                return MCPToolResult.Error($"UnityEvent field '{event_name}' not found on {component_type}.");

            var unityEvent = eventField.GetValue(component) as UnityEventBase;
            if (unityEvent == null)
                return MCPToolResult.Error($"Could not access event '{event_name}'.");

            if (!int.TryParse(index, out var idx))
                return MCPToolResult.Error($"Invalid index: '{index}'. Must be an integer.");

            if (idx < 0 || idx >= unityEvent.GetPersistentEventCount())
                return MCPToolResult.Error(
                    $"Index {idx} out of range. Event has {unityEvent.GetPersistentEventCount()} listener(s).");

            Undo.RecordObject(component, $"MCP Remove Listener {event_name}[{idx}]");
            UnityEventTools.RemovePersistentListener(unityEvent, idx);

            return MCPToolResult.Success(new
            {
                removed = idx,
                remainingListeners = unityEvent.GetPersistentEventCount()
            });
        }

        [MCPTool("event_list_listeners", "List all persistent listeners on a UnityEvent field")]
        public static MCPToolResult ListListeners(
            [MCPToolParam("GameObject with the event source component", required: true)] string game_object_path,
            [MCPToolParam("Component type containing the event", required: true)] string component_type,
            [MCPToolParam("Event field name", required: true)] string event_name)
        {
            var go = ComponentTools.FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = ComponentTools.FindComponentType(component_type);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{component_type}'.");

            var component = go.GetComponent(type);
            if (component == null)
                return MCPToolResult.Error($"Component '{component_type}' not found on '{ComponentTools.GetPath(go)}'.");

            var eventField = FindUnityEventField(type, event_name);
            if (eventField == null)
                return MCPToolResult.Error($"UnityEvent field '{event_name}' not found on {component_type}.");

            var unityEvent = eventField.GetValue(component) as UnityEventBase;
            if (unityEvent == null)
                return MCPToolResult.Error($"Could not access event '{event_name}'.");

            var listeners = new List<object>();
            for (int i = 0; i < unityEvent.GetPersistentEventCount(); i++)
            {
                var target = unityEvent.GetPersistentTarget(i);
                listeners.Add(new
                {
                    index = i,
                    target = target != null ? target.name : "null",
                    targetType = target != null ? target.GetType().Name : "null",
                    method = unityEvent.GetPersistentMethodName(i),
                    callState = unityEvent.GetPersistentListenerState(i).ToString()
                });
            }

            return MCPToolResult.Success(new
            {
                gameObject = ComponentTools.GetPath(go),
                eventField = event_name,
                listeners,
                count = listeners.Count
            });
        }

        [MCPTool("event_find_all", "Find all UnityEvent fields on a GameObject's components")]
        public static MCPToolResult FindAllEvents(
            [MCPToolParam("GameObject name or hierarchy path", required: true)] string game_object_path,
            [MCPToolParam("Component type to scan (omit to scan all)")] string component_type = null)
        {
            var go = ComponentTools.FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var components = new List<Component>();
            if (!string.IsNullOrEmpty(component_type))
            {
                var type = ComponentTools.FindComponentType(component_type);
                if (type == null)
                    return MCPToolResult.Error($"Component type not found: '{component_type}'.");
                var comp = go.GetComponent(type);
                if (comp == null)
                    return MCPToolResult.Error($"Component '{component_type}' not found on '{ComponentTools.GetPath(go)}'.");
                components.Add(comp);
            }
            else
            {
                components.AddRange(go.GetComponents<Component>().Where(c => c != null));
            }

            var events = new List<object>();
            foreach (var comp in components)
            {
                var fields = comp.GetType().GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var field in fields)
                {
                    if (typeof(UnityEventBase).IsAssignableFrom(field.FieldType))
                    {
                        var evt = field.GetValue(comp) as UnityEventBase;
                        events.Add(new
                        {
                            component = comp.GetType().Name,
                            field = field.Name,
                            eventType = field.FieldType.Name,
                            listenerCount = evt?.GetPersistentEventCount() ?? 0
                        });
                    }
                }
            }

            return MCPToolResult.Success(new
            {
                gameObject = ComponentTools.GetPath(go),
                events,
                count = events.Count
            });
        }

        // ── Helpers ──

        static MCPToolResult GameObjectNotFoundError(string path)
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects()
                .Select(r => r.name).ToArray();
            return MCPToolResult.Error(
                $"GameObject not found: '{path}'. Root objects in scene: {string.Join(", ", roots)}");
        }

        static FieldInfo FindUnityEventField(Type componentType, string fieldName)
        {
            var field = componentType.GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && typeof(UnityEventBase).IsAssignableFrom(field.FieldType))
                return field;
            return null;
        }

        static string[] ListUnityEventFields(Type componentType)
        {
            return componentType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => typeof(UnityEventBase).IsAssignableFrom(f.FieldType))
                .Select(f => f.Name)
                .ToArray();
        }

        static (UnityEngine.Object target, MethodInfo method) FindMethod(GameObject go, string methodName, bool hasArgument)
        {
            int expectedParams = hasArgument ? 1 : 0;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var method = comp.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == expectedParams);
                if (method != null)
                    return (comp, method);
            }

            // No component method matched — also allow methods declared on GameObject
            // itself (e.g. SetActive(bool)), binding the persistent listener to the
            // GameObject as the target object.
            var goMethod = typeof(GameObject).GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                hasArgument ? new[] { typeof(bool) } : Type.EmptyTypes,
                null);
            if (goMethod == null && !hasArgument)
                goMethod = typeof(GameObject).GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (goMethod != null)
                return (go, goMethod);

            return (null, null);
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
                        methods.Add($"{compType.Name}.{m.Name}({string.Join(", ", parms.Select(p => p.ParameterType.Name))})");
                }
            }
            return methods.Distinct().ToArray();
        }

        static void AddTypedListener(UnityEventBase unityEvent, UnityEngine.Object target, string methodName, string argument, string argumentType)
        {
            var resolvedType = argumentType?.ToLowerInvariant() ?? DetectArgumentType(argument);
            var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == methodName);

            switch (resolvedType)
            {
                case "int":
                    var intAction = (UnityAction<int>)Delegate.CreateDelegate(typeof(UnityAction<int>), target, method);
                    UnityEventTools.AddIntPersistentListener(unityEvent, intAction, int.Parse(argument));
                    break;
                case "float":
                    var floatAction = (UnityAction<float>)Delegate.CreateDelegate(typeof(UnityAction<float>), target, method);
                    UnityEventTools.AddFloatPersistentListener(unityEvent, floatAction, float.Parse(argument));
                    break;
                case "string":
                    var stringAction = (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), target, method);
                    UnityEventTools.AddStringPersistentListener(unityEvent, stringAction, argument);
                    break;
                case "bool":
                    var boolAction = (UnityAction<bool>)Delegate.CreateDelegate(typeof(UnityAction<bool>), target, method);
                    UnityEventTools.AddBoolPersistentListener(unityEvent, boolAction, bool.Parse(argument));
                    break;
                default:
                    var voidAction = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), target, method);
                    UnityEventTools.AddVoidPersistentListener(unityEvent, voidAction);
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
    }
}
