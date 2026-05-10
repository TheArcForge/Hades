using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
                }
            };
            return CreateSuccessResponse(id, result);
        }

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
