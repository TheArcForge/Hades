using System;
using UnityEditor;

namespace ArcForge.Hades.Editor.MCP
{
    public class ManualReloadStrategy : IDomainReloadStrategy
    {
        bool _locked;
        bool _disposed;

        public bool IsLocked => _locked;

        public event Action<string> OnLog;

        public void Lock()
        {
            if (!_locked)
            {
                _locked = true;
                EditorApplication.LockReloadAssemblies();
                OnLog?.Invoke("Locked domain reload (manual — BeginScriptEditing)");
            }
        }

        public void Unlock()
        {
            if (_locked)
            {
                _locked = false;
                EditorApplication.UnlockReloadAssemblies();
                OnLog?.Invoke("Unlocked domain reload (manual — EndScriptEditing)");
            }
        }

        public void OnToolCallStart(string toolName) { }

        public void OnToolCallEnd(string toolName) { }

        public void OnTurnComplete()
        {
            if (_locked)
            {
                _locked = false;
                EditorApplication.UnlockReloadAssemblies();
                OnLog?.Invoke("Unlocked domain reload (safety — turn complete with assemblies still locked)");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_locked)
            {
                _locked = false;
                EditorApplication.UnlockReloadAssemblies();
            }
        }
    }
}
