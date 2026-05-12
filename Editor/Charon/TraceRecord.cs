// Editor/Charon/TraceRecord.cs
namespace ArcForge.Hades.Editor.Charon
{
    public class TraceRecord
    {
        public string TraceId { get; set; }
        public string RootSpanName { get; set; }
        public long StartTime { get; set; }
        public long? EndTime { get; set; }
        public SpanStatus Status { get; set; }
        public int SpanCount { get; set; }
        public string AttributesJson { get; set; }

        public long? TotalDurationMs => EndTime.HasValue ? EndTime.Value - StartTime : (long?)null;
    }
}
