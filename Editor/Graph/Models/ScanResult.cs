using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Graph.Models
{
    public class ScanResult
    {
        public List<NodeRecord> Nodes { get; set; } = new List<NodeRecord>();
        public List<EdgeRecord> Edges { get; set; } = new List<EdgeRecord>();
        public List<ScanWarning> Warnings { get; set; } = new List<ScanWarning>();
    }
}
