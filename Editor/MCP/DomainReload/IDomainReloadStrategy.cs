using System;

namespace ArcForge.Hades.Editor.MCP
{
    public interface IDomainReloadStrategy : IDisposable
    {
        void OnToolCallStart(string toolName);
        void OnToolCallEnd(string toolName);
        void OnTurnComplete();
        bool IsLocked { get; }
        event Action<string> OnLog;
    }
}
