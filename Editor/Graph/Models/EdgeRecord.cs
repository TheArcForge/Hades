using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArcForge.Hades.Editor.Graph.Models
{
    public class EdgeRecord
    {
        public long Id { get; set; }
        public string Type { get; }
        public string SourceGuid { get; set; }
        public long SourceFileId { get; set; }
        public string TargetGuid { get; set; }
        public long TargetFileId { get; set; }
        public long SourceNodeId { get; set; }
        public long TargetNodeId { get; set; }
        public Dictionary<string, object> Properties { get; set; }

        public EdgeRecord(string type, string sourceGuid, long sourceFileId, string targetGuid, long targetFileId)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            Type = type;
            SourceGuid = sourceGuid;
            SourceFileId = sourceFileId;
            TargetGuid = targetGuid;
            TargetFileId = targetFileId;
        }

        public string PropertiesJson
        {
            get => Properties == null ? null : JsonConvert.SerializeObject(Properties);
            set => Properties = value == null ? null : JsonConvert.DeserializeObject<Dictionary<string, object>>(value);
        }
    }
}
