// Editor/Charon/SpanRecord.cs
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArcForge.Hades.Editor.Charon
{
    public class SpanRecord
    {
        public string SpanId { get; set; }
        public string TraceId { get; set; }
        public string ParentSpanId { get; set; }
        public string Name { get; set; }
        public SpanKind Kind { get; set; }
        public long StartTime { get; set; }
        public long? EndTime { get; set; }
        public SpanStatus Status { get; set; }
        public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>();
        public List<SpanEvent> Events { get; } = new List<SpanEvent>();

        public long? DurationMs => EndTime.HasValue ? EndTime.Value - StartTime : (long?)null;

        public string AttributesJson =>
            Attributes.Count > 0 ? JsonConvert.SerializeObject(Attributes) : null;

        public string EventsJson =>
            Events.Count > 0 ? JsonConvert.SerializeObject(Events) : null;
    }
}
