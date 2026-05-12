// Editor/Charon/CharonSpan.cs
using System;
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Charon
{
    public class CharonSpan : IDisposable
    {
        public string SpanId { get; }
        public string TraceId { get; }
        public string ParentSpanId { get; }
        public string Name { get; }
        public SpanKind Kind { get; }
        public long StartTime { get; }
        public bool IsFinished { get; private set; }
        internal CharonSpan ParentSpanRef { get; set; }

        long? _endTime;
        SpanStatus _status = SpanStatus.Unset;
        readonly Dictionary<string, string> _attributes = new Dictionary<string, string>();
        readonly List<SpanEvent> _events = new List<SpanEvent>();
        Action<CharonSpan> _onDispose;

        public CharonSpan(string name, SpanKind kind, string traceId, string parentSpanId = null, Action<CharonSpan> onDispose = null)
        {
            SpanId = TraceIdGenerator.NewSpanId();
            TraceId = traceId;
            ParentSpanId = parentSpanId;
            Name = name;
            Kind = kind;
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _onDispose = onDispose;
        }

        public CharonSpan SetAttribute(string key, string value)
        {
            _attributes[key] = value;
            return this;
        }

        public CharonSpan SetAttribute(string key, long value)
        {
            _attributes[key] = value.ToString();
            return this;
        }

        public CharonSpan AddEvent(string name, Dictionary<string, string> attributes = null)
        {
            _events.Add(new SpanEvent(name, attributes));
            return this;
        }

        public CharonSpan SetStatus(SpanStatus status)
        {
            _status = status;
            return this;
        }

        public SpanRecord ToRecord()
        {
            var record = new SpanRecord
            {
                SpanId = SpanId,
                TraceId = TraceId,
                ParentSpanId = ParentSpanId,
                Name = Name,
                Kind = Kind,
                StartTime = StartTime,
                EndTime = _endTime,
                Status = _status
            };

            foreach (var kv in _attributes)
                record.Attributes[kv.Key] = kv.Value;

            record.Events.AddRange(_events);
            return record;
        }

        public void Dispose()
        {
            if (IsFinished) return;
            IsFinished = true;
            _endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_status == SpanStatus.Unset)
                _status = SpanStatus.Ok;

            _onDispose?.Invoke(this);
        }
    }
}
