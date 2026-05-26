using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.MCP
{
    public class ParameterRegistration
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
        public Type Type { get; set; }

        public string JsonType
        {
            get
            {
                if (Type == typeof(string)) return "string";
                if (Type == typeof(int)) return "integer";
                if (Type == typeof(float) || Type == typeof(double)) return "number";
                if (Type == typeof(bool)) return "boolean";
                return "string";
            }
        }
    }

    public class ToolRegistration
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public MethodInfo Method { get; set; }
        public object Instance { get; set; }
        public ParameterRegistration[] Parameters { get; set; }

        public JObject GetInputSchema()
        {
            var schema = new JObject { ["type"] = "object" };
            if (Parameters.Length == 0) return schema;

            var properties = new JObject();
            var required = new JArray();

            foreach (var param in Parameters)
            {
                properties[param.Name] = new JObject
                {
                    ["type"] = param.JsonType,
                    ["description"] = param.Description
                };
                if (param.Required)
                    required.Add(param.Name);
            }

            schema["properties"] = properties;
            if (required.Count > 0)
                schema["required"] = required;

            return schema;
        }
    }

    public class MCPDispatcher
    {
        readonly Dictionary<string, ToolRegistration> _tools;

        public MCPDispatcher()
        {
            _tools = DiscoverTools();
        }

        public IReadOnlyList<ToolRegistration> GetTools()
        {
            return _tools.Values.ToList().AsReadOnly();
        }

        public string HandleRequest(string json)
        {
            JObject request;
            try
            {
                request = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return CreateErrorResponse(null, -32700, "Parse error");
            }

            var id = request["id"];
            var method = request["method"]?.ToString();

            if (string.IsNullOrEmpty(method))
                return CreateErrorResponse(id, -32600, "Invalid Request: missing method");

            switch (method)
            {
                case "initialize":
                    return HandleInitialize(id);
                case "notifications/initialized":
                    return null;
                case "tools/list":
                    return HandleToolsList(id);
                case "tools/call":
                    return HandleToolsCall(id, request["params"] as JObject);
                default:
                    return CreateErrorResponse(id, -32601, $"Method not found: {method}");
            }
        }

        public MCPToolResult CallTool(string name, JObject arguments)
        {
            if (!_tools.TryGetValue(name, out var tool))
                return MCPToolResult.Error($"Unknown tool: {name}");

            try
            {
                var args = BindArguments(tool, arguments);
                var result = tool.Method.Invoke(tool.Instance, args);
                return result as MCPToolResult ?? MCPToolResult.Error("Tool did not return MCPToolResult");
            }
            catch (TargetInvocationException ex)
            {
                return MCPToolResult.Error(ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                return MCPToolResult.Error(ex.Message);
            }
        }

        public MCPToolResult CallToolWithTracing(string name, JObject arguments, string traceId = null)
        {
            using (var span = CharonEmitter.StartSpan($"mcp.tool.{name}", SpanKind.Server, traceId))
            {
                span.SetAttribute("tool.name", name);
                span.SetAttribute("tool.input", arguments.ToString(Newtonsoft.Json.Formatting.None));

                var result = CallTool(name, arguments);

                if (result.IsError)
                    span.SetStatus(SpanStatus.Error);

                return result;
            }
        }

        string HandleInitialize(JToken id)
        {
            var result = new JObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject()
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "hades",
                    ["version"] = "0.1.0"
                },
                ["instructions"] = McpInstructions
            };
            return CreateSuccessResponse(id, result);
        }

        const string McpInstructions =
            "This is a Unity project with Hades — a semantic knowledge graph of every scene, prefab, script, asset, and their dependencies. " +
            "Before answering questions about this project or writing code, query the graph for structural context. " +
            "Do NOT default to bash/grep/find for project understanding — use Hades MCP tools instead:\n" +
            "- get_project_summary: structured project overview (start here for 'tell me about this project')\n" +
            "- get_scene_summary: parsed scene structure (not raw YAML)\n" +
            "- search_by_name: find any asset/script/component by name pattern\n" +
            "- find_references_to: what references a given asset (incoming deps)\n" +
            "- trace_dependencies: what an asset depends on (outgoing deps)\n" +
            "- find_prefabs_with_component: find prefabs using a component type\n" +
            "- recall_memory: search project decisions/conventions in .arcforge/memory/\n" +
            "- inspector_inspect: full property dump of a GameObject\n" +
            "- prefab_get_contents: inspect prefab hierarchy without instantiating\n" +
            "Use bash only for reading file contents, running commands, or editing code — not for navigating project structure. " +
            "For modifying scenes/prefabs/materials, use the MCP tools (scene_setup, component_add, prefab_edit_property, etc.) instead of editing Unity files directly.";

        string HandleToolsList(JToken id)
        {
            var toolsArray = new JArray();
            foreach (var tool in _tools.Values)
            {
                toolsArray.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = tool.GetInputSchema()
                });
            }
            return CreateSuccessResponse(id, new JObject { ["tools"] = toolsArray });
        }

        string HandleToolsCall(JToken id, JObject parameters)
        {
            var toolName = parameters?["name"]?.ToString();
            if (string.IsNullOrEmpty(toolName))
                return CreateErrorResponse(id, -32602, "Missing tool name");

            var arguments = parameters["arguments"] as JObject ?? new JObject();
            var toolResult = CallTool(toolName, arguments);
            return CreateSuccessResponse(id, toolResult.ToMCPResponse());
        }

        object[] BindArguments(ToolRegistration tool, JObject arguments)
        {
            var args = new object[tool.Parameters.Length];
            for (int i = 0; i < tool.Parameters.Length; i++)
            {
                var param = tool.Parameters[i];
                var token = arguments?[param.Name];

                if (token == null || token.Type == JTokenType.Null)
                {
                    if (param.Required)
                        throw new ArgumentException($"Missing required parameter: {param.Name}");
                    args[i] = param.Type.IsValueType ? Activator.CreateInstance(param.Type) : null;
                }
                else
                {
                    args[i] = token.ToObject(param.Type);
                }
            }
            return args;
        }

        Dictionary<string, ToolRegistration> DiscoverTools()
        {
            var tools = new Dictionary<string, ToolRegistration>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                foreach (var type in types)
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var attr = method.GetCustomAttribute<MCPToolAttribute>();
                        if (attr == null) continue;
                        if (method.ReturnType != typeof(MCPToolResult)) continue;

                        var parameters = method.GetParameters()
                            .Select(p =>
                            {
                                var paramAttr = p.GetCustomAttribute<MCPToolParamAttribute>();
                                return new ParameterRegistration
                                {
                                    Name = p.Name,
                                    Description = paramAttr?.Description ?? p.Name,
                                    Required = paramAttr?.Required ?? false,
                                    Type = p.ParameterType
                                };
                            })
                            .ToArray();

                        tools[attr.Name] = new ToolRegistration
                        {
                            Name = attr.Name,
                            Description = attr.Description,
                            Method = method,
                            Instance = null,
                            Parameters = parameters
                        };
                    }
                }
            }

            return tools;
        }

        string CreateSuccessResponse(JToken id, JObject result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = result
            }.ToString(Formatting.None);
        }

        string CreateErrorResponse(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            }.ToString(Formatting.None);
        }
    }
}
