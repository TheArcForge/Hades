// Tests/Editor/Asphodel/Inference/SyntheticTraceFixtures.cs
using System;
using System.Collections.Generic;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public static class SyntheticTraceFixtures
    {
        static long ToMs(DateTimeOffset dt) => dt.ToUnixTimeMilliseconds();

        public static (List<TraceRecord> traces, List<SpanRecord> spans) AcceptanceRateFixture()
        {
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();
            var baseTime = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

            // 55 traces with repeating pattern: find_prefabs_with_component(ObjectPool)
            // Session: traces within 10-min gaps
            for (int i = 0; i < 55; i++)
            {
                var traceTime = baseTime.AddMinutes(i * 15); // same session every ~15 min
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.find_prefabs_with_component",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.find_prefabs_with_component",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "find_prefabs_with_component";
                span.Attributes["component_type"] = "ObjectPool";
                spans.Add(span);
            }

            // 5 traces that are retries (same tool, different params — contradicting)
            for (int i = 0; i < 5; i++)
            {
                var traceTime = baseTime.AddMinutes(55 * 15 + i * 15);
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.find_prefabs_with_component",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.find_prefabs_with_component",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "find_prefabs_with_component";
                span.Attributes["component_type"] = "EnemySpawner";
                spans.Add(span);

                // Follow immediately with same tool, different param (retry signal)
                var retryTraceId = TraceIdGenerator.NewTraceId();
                var retrySpanId = TraceIdGenerator.NewSpanId();
                var retryTime = traceTime.AddSeconds(5);
                traces.Add(new TraceRecord
                {
                    TraceId = retryTraceId,
                    RootSpanName = "mcp.tool.find_prefabs_with_component",
                    StartTime = ToMs(retryTime),
                    EndTime = ToMs(retryTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var retrySpan = new SpanRecord
                {
                    SpanId = retrySpanId,
                    TraceId = retryTraceId,
                    Name = "mcp.tool.find_prefabs_with_component",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(retryTime),
                    EndTime = ToMs(retryTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok
                };
                retrySpan.Attributes[SpanAttributes.ToolName] = "find_prefabs_with_component";
                retrySpan.Attributes["component_type"] = "EnemySpawnerV2";
                spans.Add(retrySpan);
            }

            // 10 control traces with varied tools (no pattern)
            var controlTools = new[] { "get_project_summary", "search_by_name", "get_scene_summary",
                "find_references_to", "trace_dependencies", "analyze_render_pipeline",
                "find_orphan_scripts", "get_recently_changed", "hades_status", "query_graph" };
            for (int i = 0; i < 10; i++)
            {
                var traceTime = baseTime.AddDays(20).AddMinutes(i * 30);
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = $"mcp.tool.{controlTools[i]}",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(150)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = $"mcp.tool.{controlTools[i]}",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(150)),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = controlTools[i];
                spans.Add(span);
            }

            return (traces, spans);
        }

        public static (List<TraceRecord> traces, List<SpanRecord> spans) TopicClusterFixture()
        {
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();
            var baseTime = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

            // 80 audio-related traces (~40%)
            for (int i = 0; i < 80; i++)
            {
                var traceTime = baseTime.AddMinutes(i * 10);
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                var toolName = i % 2 == 0 ? "search_by_name" : "find_prefabs_with_component";
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = $"mcp.tool.{toolName}",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(100)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = $"mcp.tool.{toolName}",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(100)),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = toolName;
                span.Attributes["query"] = i % 3 == 0 ? "AudioSource" : i % 3 == 1 ? "AudioMixer" : "AudioClip";
                spans.Add(span);
            }

            // 60 networking-related traces (~30%)
            for (int i = 0; i < 60; i++)
            {
                var traceTime = baseTime.AddDays(5).AddMinutes(i * 10);
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.search_by_name",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(100)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.search_by_name",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(100)),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "search_by_name";
                span.Attributes["query"] = i % 2 == 0 ? "NetworkManager" : "NetworkBehaviour";
                spans.Add(span);
            }

            // 60 mixed traces (~30%)
            var mixedTerms = new[] { "PlayerController", "HealthSystem", "UIManager",
                "SaveSystem", "InventorySlot", "SceneLoader" };
            for (int i = 0; i < 60; i++)
            {
                var traceTime = baseTime.AddDays(10).AddMinutes(i * 10);
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.search_by_name",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(100)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.search_by_name",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(100)),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "search_by_name";
                span.Attributes["query"] = mixedTerms[i % mixedTerms.Length];
                spans.Add(span);
            }

            return (traces, spans);
        }

        public static (List<TraceRecord> traces, List<SpanRecord> spans) TimeOfDayFixture()
        {
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();

            // 30 days of traces, 80% during weekday 09:00-17:00
            var rng = new Random(42);
            var startDate = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

            for (int day = 0; day < 30; day++)
            {
                var date = startDate.AddDays(day);
                var isWeekday = date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;

                // Weekday work hours: 8 traces
                if (isWeekday)
                {
                    for (int t = 0; t < 8; t++)
                    {
                        var hour = 9 + rng.Next(8); // 09-16
                        var minute = rng.Next(60);
                        var traceTime = new DateTimeOffset(date.Year, date.Month, date.Day,
                            hour, minute, 0, TimeSpan.Zero);
                        AddSimpleTrace(traces, spans, traceTime, "mcp.tool.search_by_name");
                    }
                }

                // Off-hours: 2 traces (20%)
                var offCount = isWeekday ? 2 : 3;
                for (int t = 0; t < offCount; t++)
                {
                    var hour = rng.Next(2) == 0 ? rng.Next(0, 9) : rng.Next(17, 24);
                    var minute = rng.Next(60);
                    var traceTime = new DateTimeOffset(date.Year, date.Month, date.Day,
                        hour, minute, 0, TimeSpan.Zero);
                    AddSimpleTrace(traces, spans, traceTime, "mcp.tool.get_project_summary");
                }
            }

            return (traces, spans);
        }

        public static (List<TraceRecord> traces, List<SpanRecord> spans) FailureCorrelationFixture()
        {
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();
            var baseTime = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

            // 80 successful traces — mixed tools
            for (int i = 0; i < 80; i++)
            {
                var traceTime = baseTime.AddMinutes(i * 10);
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                var isPrefabVariant = i % 10 == 0; // 8 of 80 success involve prefab_variant
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.trace_dependencies",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.trace_dependencies",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "trace_dependencies";
                span.Attributes["asset_type"] = isPrefabVariant ? "prefab_variant" : "script";
                spans.Add(span);
            }

            // 20 error traces — 15 involve prefab_variant (3x over-representation)
            for (int i = 0; i < 20; i++)
            {
                var traceTime = baseTime.AddDays(10).AddMinutes(i * 10);
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                var isPrefabVariant = i < 15;
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.trace_dependencies",
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Error,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.trace_dependencies",
                    Kind = SpanKind.Server,
                    StartTime = ToMs(traceTime),
                    EndTime = ToMs(traceTime.AddMilliseconds(200)),
                    Status = SpanStatus.Error
                };
                span.Attributes[SpanAttributes.ToolName] = "trace_dependencies";
                span.Attributes["asset_type"] = isPrefabVariant ? "prefab_variant" : "script";
                if (isPrefabVariant)
                    span.Attributes["error.message"] = "Failed to resolve variant override chain";
                else
                    span.Attributes["error.message"] = "Asset not found";
                spans.Add(span);
            }

            return (traces, spans);
        }

        public static (List<TraceRecord>, List<SpanRecord>) EmptyFixture()
        {
            return (new List<TraceRecord>(), new List<SpanRecord>());
        }

        static void AddSimpleTrace(List<TraceRecord> traces, List<SpanRecord> spans,
            DateTimeOffset time, string toolName)
        {
            var traceId = TraceIdGenerator.NewTraceId();
            var spanId = TraceIdGenerator.NewSpanId();
            traces.Add(new TraceRecord
            {
                TraceId = traceId,
                RootSpanName = toolName,
                StartTime = ToMs(time),
                EndTime = ToMs(time.AddMilliseconds(100)),
                Status = SpanStatus.Ok,
                SpanCount = 1
            });
            var span = new SpanRecord
            {
                SpanId = spanId,
                TraceId = traceId,
                Name = toolName,
                Kind = SpanKind.Server,
                StartTime = ToMs(time),
                EndTime = ToMs(time.AddMilliseconds(100)),
                Status = SpanStatus.Ok
            };
            span.Attributes[SpanAttributes.ToolName] = toolName.Replace("mcp.tool.", "");
            spans.Add(span);
        }
    }
}
