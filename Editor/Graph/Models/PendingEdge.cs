// Editor/Graph/Models/PendingEdge.cs
namespace ArcForge.Hades.Editor.Graph.Models
{
    /// <summary>
    /// Represents an edge that couldn't be resolved at scan time because the target node
    /// didn't exist yet (e.g., inherits_from a type in a package that hasn't been scanned).
    /// Stored in the pending_edges table for deferred resolution.
    /// </summary>
    public class PendingEdge
    {
        public long Id { get; set; }
        public long SourceNodeId { get; set; }
        public string EdgeType { get; set; }

        /// <summary>The unresolved type name (e.g. "TMP_Text", "MonoBehaviour")</summary>
        public string TargetTypeName { get; set; }

        /// <summary>Optional namespace hint for disambiguation (e.g. "TMPro")</summary>
        public string TargetNamespace { get; set; }

        /// <summary>The GUID of the source asset file (for incremental cleanup)</summary>
        public string SourceAssetGuid { get; set; }

        public long CreatedAt { get; set; }
    }
}
