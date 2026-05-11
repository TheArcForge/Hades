using System;
using System.Collections.Generic;
using System.Linq;

namespace ArcForge.Hades.Editor.Graph.Updates
{
    public class UpdateDebouncer
    {
        readonly Action<string[]> _onFlush;
        readonly HashSet<string> _pendingGuids = new HashSet<string>();
        readonly int _batchCap;

        double _lastEnqueueTime;
        double _firstEnqueueTime;
        bool _hasPending;

        const double IdleThresholdMs = 250;
        const double MaxDelayMs = 2000;
        const int DefaultBatchCap = 1000;

        public int PendingCount => _pendingGuids.Count;

        public UpdateDebouncer(Action<string[]> onFlush, int batchCap = DefaultBatchCap)
        {
            _onFlush = onFlush;
            _batchCap = batchCap;
        }

        public void Enqueue(string[] guids)
        {
            foreach (var guid in guids)
            {
                if (!string.IsNullOrEmpty(guid))
                    _pendingGuids.Add(guid);
            }

            var now = CurrentTimeMs();
            _lastEnqueueTime = now;

            if (!_hasPending)
            {
                _firstEnqueueTime = now;
                _hasPending = true;
            }
        }

        public void Tick()
        {
            if (!_hasPending || _pendingGuids.Count == 0) return;

            var now = CurrentTimeMs();
            var idleTime = now - _lastEnqueueTime;
            var totalTime = now - _firstEnqueueTime;

            if (idleTime >= IdleThresholdMs || totalTime >= MaxDelayMs)
            {
                Flush();
            }
        }

        public void ForceFlush()
        {
            if (_pendingGuids.Count == 0) return;
            Flush();
        }

        void Flush()
        {
            var allGuids = _pendingGuids.ToArray();
            _pendingGuids.Clear();
            _hasPending = false;

            if (allGuids.Length <= _batchCap)
            {
                _onFlush(allGuids);
            }
            else
            {
                for (int i = 0; i < allGuids.Length; i += _batchCap)
                {
                    var batch = allGuids.Skip(i).Take(_batchCap).ToArray();
                    _onFlush(batch);
                }
            }
        }

        static double CurrentTimeMs()
        {
            return (double)System.Diagnostics.Stopwatch.GetTimestamp() /
                   System.Diagnostics.Stopwatch.Frequency * 1000.0;
        }
    }
}
