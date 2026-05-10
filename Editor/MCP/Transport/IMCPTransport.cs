using System;
using System.Threading.Tasks;

namespace ArcForge.Hades.Editor.MCP
{
    public interface IMCPTransport : IDisposable
    {
        void Start(int port = 0);
        void Stop();
        bool IsRunning { get; }
        string Endpoint { get; }
        void SetRequestHandler(Func<string, Task<string>> handler);
    }
}
