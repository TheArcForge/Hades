// Editor/Charon/RegressionSnapshot.cs
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Charon
{
    public class EvalDataset
    {
        public string DatasetId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public long CreatedAt { get; set; }
    }

    public class EvalDatasetMember
    {
        public string DatasetId { get; set; }
        public string TraceId { get; set; }
        public string ToolName { get; set; }
        public string InputJson { get; set; }
        public string ExpectedOutputJson { get; set; }
        public string Notes { get; set; }
    }
}
