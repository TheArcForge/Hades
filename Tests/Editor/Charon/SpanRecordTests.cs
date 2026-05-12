// Tests/Editor/Charon/SpanRecordTests.cs
using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    public class SpanRecordTests
    {
        [Test]
        public void SpanRecord_StoresAllFields()
        {
            var record = new SpanRecord
            {
                SpanId = "abcd1234abcd1234",
                TraceId = "abcd1234abcd1234abcd1234abcd1234",
                ParentSpanId = "1234abcd1234abcd",
                Name = "mcp.tool.hades_ping",
                Kind = SpanKind.Server,
                StartTime = 1000,
                EndTime = 2000,
                Status = SpanStatus.Ok
            };

            Assert.AreEqual("abcd1234abcd1234", record.SpanId);
            Assert.AreEqual("mcp.tool.hades_ping", record.Name);
            Assert.AreEqual(SpanKind.Server, record.Kind);
            Assert.AreEqual(1000, record.StartTime);
            Assert.AreEqual(2000, record.EndTime);
            Assert.AreEqual(SpanStatus.Ok, record.Status);
            Assert.AreEqual(1000, record.DurationMs);
        }

        [Test]
        public void SpanRecord_DurationMs_NullWhenNoEndTime()
        {
            var record = new SpanRecord { StartTime = 1000 };
            Assert.IsNull(record.DurationMs);
        }

        [Test]
        public void SpanRecord_AttributesJson_SerializesDict()
        {
            var record = new SpanRecord();
            record.Attributes["tool.name"] = "hades_ping";
            record.Attributes["results.count"] = "5";

            var json = record.AttributesJson;
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            Assert.AreEqual("hades_ping", parsed["tool.name"]);
            Assert.AreEqual("5", parsed["results.count"]);
        }

        [Test]
        public void SpanRecord_EventsJson_SerializesList()
        {
            var record = new SpanRecord();
            record.Events.Add(new SpanEvent("query.start"));
            record.Events.Add(new SpanEvent("query.end", new Dictionary<string, string> { { "rows", "10" } }));

            var json = record.EventsJson;
            Assert.IsNotNull(json);
            Assert.IsTrue(json.Contains("query.start"));
            Assert.IsTrue(json.Contains("query.end"));
        }

        [Test]
        public void TraceRecord_StoresAllFields()
        {
            var record = new TraceRecord
            {
                TraceId = "abcd1234abcd1234abcd1234abcd1234",
                RootSpanName = "mcp.tool.hades_ping",
                StartTime = 1000,
                EndTime = 2000,
                Status = SpanStatus.Ok,
                SpanCount = 3
            };

            Assert.AreEqual("mcp.tool.hades_ping", record.RootSpanName);
            Assert.AreEqual(1000, record.TotalDurationMs);
            Assert.AreEqual(3, record.SpanCount);
        }

        [Test]
        public void TraceRecord_TotalDurationMs_NullWhenNoEndTime()
        {
            var record = new TraceRecord { StartTime = 1000 };
            Assert.IsNull(record.TotalDurationMs);
        }
    }
}
