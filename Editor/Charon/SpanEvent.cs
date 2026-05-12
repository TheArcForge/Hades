// Editor/Charon/SpanEvent.cs
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArcForge.Hades.Editor.Charon
{
    public class SpanEvent
    {
        public long Timestamp { get; }
        public string Name { get; }
        public Dictionary<string, string> Attributes { get; }

        public SpanEvent(string name, Dictionary<string, string> attributes = null)
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Name = name;
            Attributes = attributes ?? new Dictionary<string, string>();
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
