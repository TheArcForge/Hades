// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
namespace Hades.Runtime
{
    /// <summary>
    /// Seam over <c>EditorApplication.LockReloadAssemblies</c> / <c>UnlockReloadAssemblies</c>.
    /// Unity exposes no getter for its native lock counter, so routing both calls through this
    /// interface is the only way a test can assert "never exceeds 1" - it substitutes
    /// a fake that maintains its own signed counter in place of <see cref="EditorLockApi"/>. See
    /// <see cref="ReloadGate"/>'s class doc comment for why exactly one type may hold a reference
    /// to the real implementation.
    /// </summary>
    public interface IEditorLockApi
    {
        void Lock();
        void Unlock();
    }

    /// <summary>Straight passthrough to the real Unity API - no state, no logic, nothing to get
    /// wrong. <see cref="ReloadGate"/> is the only place that decides WHEN to call
    /// <see cref="Lock"/>/<see cref="Unlock"/>; this class exists solely so that decision logic
    /// can be tested against a fake instead of the real Editor.</summary>
    public sealed class EditorLockApi : IEditorLockApi
    {
        public void Lock() => UnityEditor.EditorApplication.LockReloadAssemblies();
        public void Unlock() => UnityEditor.EditorApplication.UnlockReloadAssemblies();
    }
}
