using System;

namespace ArcForge.Hades.Editor.MCP
{
    [AttributeUsage(AttributeTargets.Method)]
    public class MCPToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        public MCPToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class MCPToolParamAttribute : Attribute
    {
        public string Description { get; }
        public bool Required { get; }

        public MCPToolParamAttribute(string description, bool required = false)
        {
            Description = description;
            Required = required;
        }
    }
}
