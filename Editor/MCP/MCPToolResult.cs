using System;
using ArcForge.Hades.Editor.Graph.Models;
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

        public static MCPToolResult SuccessWithConfidence(object content, ConfidenceBlock confidence)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (confidence == null) throw new ArgumentNullException(nameof(confidence));

            var wrapper = new JObject
            {
                ["result"] = content is string s ? JToken.Parse(s) : JToken.FromObject(content),
                ["confidence"] = confidence.ToJObject()
            };
            return new MCPToolResult(false, wrapper.ToString(Formatting.Indented));
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
