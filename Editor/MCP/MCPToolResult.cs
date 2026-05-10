using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.MCP
{
    public class MCPToolResult
    {
        public bool IsError { get; }
        public string Text { get; }

        MCPToolResult(bool isError, string text)
        {
            IsError = isError;
            Text = text;
        }

        public static MCPToolResult Success(object content)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            var text = content is string s ? s : JsonConvert.SerializeObject(content, Formatting.Indented);
            return new MCPToolResult(false, text);
        }

        public static MCPToolResult Error(string message)
        {
            if (string.IsNullOrEmpty(message))
                throw new ArgumentException("Error message must not be null or empty.", nameof(message));

            return new MCPToolResult(true, message);
        }

        public JObject ToMCPResponse()
        {
            var result = new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = Text
                    }
                }
            };

            if (IsError)
                result["isError"] = true;

            return result;
        }
    }
}
