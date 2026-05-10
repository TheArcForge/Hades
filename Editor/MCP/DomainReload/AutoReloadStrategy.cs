using System;
using UnityEditor;

namespace ArcForge.Hades.Editor.MCP
{
    public class AutoReloadStrategy : IDomainReloadStrategy
    {
        readonly int _timeoutSeconds;
        double _lastToolCallTime;
        bool _locked;
        bool _disposed;

        public bool IsLocked => _locked;
        public event Action<string> OnLog;

        public AutoReloadStrategy(int timeoutSeconds = 120)
        {
            _timeoutSeconds = timeoutSeconds;
            EditorApplication.update += CheckTimeout;
        }

        public void OnToolCallStart(string toolName)
        {
            _lastToolCallTime = EditorApplication.timeSinceStartup;

            if (!_locked)
            {
                _locked = true;
                EditorApplication.LockReloadAssemblies();
                OnLog?.Invoke("Locked domain reload (first tool call of turn)");
            }
        }

        public void OnToolCallEnd(string toolName)
        {
            _lastToolCallTime = EditorApplication.timeSinceStartup;
        }

        public void OnTurnComplete()
        {
            if (_locked)
            {
                _locked = false;
                EditorApplication.UnlockReloadAssemblies();
                OnLog?.Invoke("Unlocked domain reload (turn complete)");
                AssetDatabase.Refresh();
            }
        }

        void CheckTimeout()
        {
            if (!_locked || _lastToolCallTime == 0) return;

            if (EditorApplication.timeSinceStartup - _lastToolCallTime > _timeoutSeconds)
            {
                _locked = false;
                _lastToolCallTime = 0;
                EditorApplication.UnlockReloadAssemblies();
                OnLog?.Invoke($"Unlocked domain reload (safety timeout — {_timeoutSeconds}s)");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorApplication.update -= CheckTimeout;

            if (_locked)
            {
                _locked = false;
                EditorApplication.UnlockReloadAssemblies();
            }
        }
    }
}
