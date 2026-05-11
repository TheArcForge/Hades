using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArcForge.Hades.Editor.Graph.Models
{
    public class NodeRecord
    {
        public long Id { get; set; }
        public string Type { get; }
        public string Guid { get; set; }
        public long? FileId { get; set; }
        public long? ParentNodeId { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string SourceRange { get; set; }
        public Dictionary<string, object> Properties { get; set; }

        public NodeRecord(string type, string guid = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.Length == 0) throw new ArgumentException("Type must not be empty.", nameof(type));
            Type = type;
            Guid = guid;
        }

        public string PropertiesJson
        {
            get => Properties == null ? null : JsonConvert.SerializeObject(Properties);
            set => Properties = value == null ? null : JsonConvert.DeserializeObject<Dictionary<string, object>>(value);
        }
    }
}
