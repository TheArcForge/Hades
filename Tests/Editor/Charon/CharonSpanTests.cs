// Tests/Editor/Charon/CharonSpanTests.cs
using System.Collections.Generic;
using NUnit.Framework;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    public class CharonSpanTests
    {
        [Test]
        public void Constructor_SetsFields()
        {
            var span = new CharonSpan("mcp.tool.test", SpanKind.Server, "traceid123456789012345678901234");

            Assert.AreEqual("mcp.tool.test", span.Name);
            Assert.AreEqual(SpanKind.Server, span.Kind);
            Assert.AreEqual("traceid123456789012345678901234", span.TraceId);
            Assert.IsNotNull(span.SpanId);
            Assert.AreEqual(16, span.SpanId.Length);
            Assert.IsNull(span.ParentSpanId);
            Assert.IsTrue(span.StartTime > 0);
            Assert.IsFalse(span.IsFinished);
        }

        [Test]
        public void Constructor_WithParent_SetsParentSpanId()
        {
            var parent = new CharonSpan("parent", SpanKind.Server, "traceid123456789012345678901234");
            var child = new CharonSpan("child", SpanKind.Internal, parent.TraceId, parent.SpanId);

            Assert.AreEqual(parent.SpanId, child.ParentSpanId);
            Assert.AreEqual(parent.TraceId, child.TraceId);
        }

        [Test]
        public void SetAttribute_String_Stores()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            var result = span.SetAttribute("key", "value");

            Assert.AreSame(span, result);

            var record = span.ToRecord();
            Assert.AreEqual("value", record.Attributes["key"]);
        }

        [Test]
        public void SetAttribute_Long_StoresAsString()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            span.SetAttribute("count", 42L);

            var record = span.ToRecord();
            Assert.AreEqual("42", record.Attributes["count"]);
        }

        [Test]
        public void AddEvent_Stores()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            var result = span.AddEvent("query.start", new Dictionary<string, string> { { "table", "nodes" } });

            Assert.AreSame(span, result);

            var record = span.ToRecord();
            Assert.AreEqual(1, record.Events.Count);
            Assert.AreEqual("query.start", record.Events[0].Name);
            Assert.AreEqual("nodes", record.Events[0].Attributes["table"]);
        }

        [Test]
        public void SetStatus_Stores()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            span.SetStatus(SpanStatus.Error);

            var record = span.ToRecord();
            Assert.AreEqual(SpanStatus.Error, record.Status);
        }

        [Test]
        public void Dispose_SetsEndTimeAndFinished()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            Assert.IsFalse(span.IsFinished);

            span.Dispose();

            Assert.IsTrue(span.IsFinished);
            var record = span.ToRecord();
            Assert.IsTrue(record.EndTime.HasValue);
            Assert.IsTrue(record.EndTime.Value >= record.StartTime);
        }

        [Test]
        public void Dispose_DefaultsStatusToOk()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            span.Dispose();

            var record = span.ToRecord();
            Assert.AreEqual(SpanStatus.Ok, record.Status);
        }

        [Test]
        public void Dispose_PreservesExplicitStatus()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            span.SetStatus(SpanStatus.Error);
            span.Dispose();

            var record = span.ToRecord();
            Assert.AreEqual(SpanStatus.Error, record.Status);
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            var span = new CharonSpan("test", SpanKind.Internal, TraceIdGenerator.NewTraceId());
            span.Dispose();
            var endTime1 = span.ToRecord().EndTime;

            span.Dispose();
            var endTime2 = span.ToRecord().EndTime;

            Assert.AreEqual(endTime1, endTime2);
        }

        [Test]
        public void ToRecord_ProducesCompleteRecord()
        {
            var traceId = TraceIdGenerator.NewTraceId();
            var span = new CharonSpan("mcp.tool.ping", SpanKind.Server, traceId);
            span.SetAttribute("tool.name", "hades_ping");
            span.AddEvent("start");
            span.SetStatus(SpanStatus.Ok);
            span.Dispose();

            var record = span.ToRecord();
            Assert.AreEqual(span.SpanId, record.SpanId);
            Assert.AreEqual(traceId, record.TraceId);
            Assert.AreEqual("mcp.tool.ping", record.Name);
            Assert.AreEqual(SpanKind.Server, record.Kind);
            Assert.AreEqual(SpanStatus.Ok, record.Status);
            Assert.IsTrue(record.EndTime.HasValue);
            Assert.AreEqual("hades_ping", record.Attributes["tool.name"]);
            Assert.AreEqual(1, record.Events.Count);
        }
    }
}
