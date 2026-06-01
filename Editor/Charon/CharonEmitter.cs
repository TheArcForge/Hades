// Editor/Charon/CharonEmitter.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace ArcForge.Hades.Editor.Charon
{
    public static class CharonEmitter
    {
        static readonly AsyncLocal<CharonSpan> CurrentSpan = new AsyncLocal<CharonSpan>();
        static readonly ConcurrentQueue<SpanRecord> Buffer = new ConcurrentQueue<SpanRecord>();
        static readonly ConcurrentDictionary<string, TraceState> ActiveTraces = new ConcurrentDictionary<string, TraceState>();

        static CharonDatabase _database;
        static double _lastFlushTime;

        const int FlushThreshold = 1000;
        const double FlushIntervalSeconds = 0.5;

        public static bool IsEnabled => _database != null;
        public static int BufferCount => Buffer.Count;
        public static CharonDatabase Database => _database;

        public static void Initialize(CharonDatabase database)
        {
            _database = database;
            _lastFlushTime = CurrentTimeSeconds();
        }

        public static void Shutdown()
        {
            if (_database != null)
                Flush();

            _database = null;
            CurrentSpan.Value = null;
            ActiveTraces.Clear();
        }

        public static CharonSpan StartSpan(string name, SpanKind kind, string explicitTraceId = null)
        {
            if (!IsEnabled)
                return new CharonSpan(name, kind, explicitTraceId ?? TraceIdGenerator.NewTraceId());

            var parent = CurrentSpan.Value;
            string traceId;
            string parentSpanId = null;

            if (parent != null && !parent.IsFinished)
            {
                traceId = parent.TraceId;
                parentSpanId = parent.SpanId;
            }
            else
            {
                traceId = explicitTraceId ?? TraceIdGenerator.NewTraceId();
            }

            var span = new CharonSpan(name, kind, traceId, parentSpanId, OnSpanDisposed);

            if (parent != null && !parent.IsFinished)
                span.ParentSpanRef = parent;

            CurrentSpan.Value = span;

            if (parentSpanId == null)
            {
                ActiveTraces.TryAdd(traceId, new TraceState
                {
                    RootSpanName = name,
                    StartTime = span.StartTime
                });
            }

            return span;
        }

        public static void Flush()
        {
            if (_database == null) return;

            var spans = new List<SpanRecord>();
            while (Buffer.TryDequeue(out var record))
                spans.Add(record);

            if (spans.Count == 0) return;

            var traceSpans = new Dictionary<string, List<SpanRecord>>();
            foreach (var span in spans)
            {
                if (!traceSpans.ContainsKey(span.TraceId))
                    traceSpans[span.TraceId] = new List<SpanRecord>();
                traceSpans[span.TraceId].Add(span);
            }

            // Batch the whole flush into one transaction. Previously each trace did
            // its own GetTrace + InsertTrace/UpdateTraceEnd + InsertSpans commits, so
            // a single flush triggered dozens of autocheckpoint passes against a
            // large traces.db on the main thread — a freeze source on its own.
            _database.RunInTransaction(() =>
            {
                foreach (var kv in traceSpans)
                {
                    var traceId = kv.Key;
                    var traceSpanList = kv.Value;

                    TraceState state;
                    ActiveTraces.TryGetValue(traceId, out state);

                    long startTime = state?.StartTime ?? traceSpanList[0].StartTime;
                    string rootName = state?.RootSpanName ?? traceSpanList[0].Name;

                    long? endTime = null;
                    var status = SpanStatus.Ok;
                    foreach (var s in traceSpanList)
                    {
                        if (s.EndTime.HasValue && (!endTime.HasValue || s.EndTime.Value > endTime.Value))
                            endTime = s.EndTime;
                        if (s.Status == SpanStatus.Error)
                            status = SpanStatus.Error;
                    }

                    var existingTrace = _database.GetTrace(traceId);
                    if (existingTrace == null)
                    {
                        _database.InsertTrace(new TraceRecord
                        {
                            TraceId = traceId,
                            RootSpanName = rootName,
                            StartTime = startTime,
                            EndTime = endTime,
                            Status = status,
                            SpanCount = traceSpanList.Count
                        });
                    }
                    else
                    {
                        var totalSpans = existingTrace.SpanCount + traceSpanList.Count;
                        if (endTime.HasValue)
                            _database.UpdateTraceEnd(traceId, endTime.Value, status, totalSpans);
                    }

                    _database.InsertSpans(traceSpanList);

                    if (IsTraceComplete(traceId, traceSpanList))
                        ActiveTraces.TryRemove(traceId, out _);
                }
            });

            _lastFlushTime = CurrentTimeSeconds();
        }

        public static void TickFlush()
        {
            if (!IsEnabled) return;

            if (Buffer.Count >= FlushThreshold || CurrentTimeSeconds() - _lastFlushTime >= FlushIntervalSeconds)
                Flush();
        }

        static void OnSpanDisposed(CharonSpan span)
        {
            if (!IsEnabled) return;

            Buffer.Enqueue(span.ToRecord());

            if (CurrentSpan.Value == span)
                CurrentSpan.Value = span.ParentSpanRef;
        }

        static bool IsTraceComplete(string traceId, List<SpanRecord> justFlushed)
        {
            foreach (var s in justFlushed)
            {
                if (s.ParentSpanId == null && s.EndTime.HasValue)
                    return true;
            }
            return false;
        }

        static double CurrentTimeSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        }

        class TraceState
        {
            public string RootSpanName;
            public long StartTime;
        }
    }
}
