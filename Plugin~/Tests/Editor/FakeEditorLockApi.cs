// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System.Threading;
using Hades.Runtime;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// Test double for <see cref="IEditorLockApi"/>. Unity exposes no getter for its real native
    /// lock counter, so this fake's <see cref="Counter"/> is the only way any test in this suite
    /// can observe what Unity's counter would have done. Deliberately UNCLAMPED: if
    /// <see cref="ReloadGate"/> ever calls <see cref="Unlock"/> from the Released state, this
    /// counter goes negative instead of silently floor-ing at zero - that excursion is exactly
    /// the bug this whole plan exists to make unrepresentable, so the fake must be able to show it.
    ///
    /// Thread-safe: the TTL watchdog (see <see cref="ReloadGate"/>) calls <see cref="Unlock"/>
    /// from a background timer thread while a test's own thread may be calling
    /// <see cref="Lock"/>/<see cref="Unlock"/> too, so every counter here is updated with
    /// <see cref="Interlocked"/>.
    /// </summary>
    sealed class FakeEditorLockApi : IEditorLockApi
    {
        int _counter;
        int _lockCalls;
        int _unlockCalls;
        int _lastCallerThreadId = -1;

        /// <summary>Signed on purpose - see class doc comment.</summary>
        public int Counter => Volatile.Read(ref _counter);

        public int LockCalls => Volatile.Read(ref _lockCalls);
        public int UnlockCalls => Volatile.Read(ref _unlockCalls);

        /// <summary>Managed thread id that made the most recent Lock/Unlock call - lets a test
        /// prove a release happened off whatever thread the test itself runs on (standing in for
        /// "not the main thread").</summary>
        public int LastCallerThreadId => Volatile.Read(ref _lastCallerThreadId);

        public void Lock()
        {
            Interlocked.Increment(ref _lockCalls);
            Interlocked.Increment(ref _counter);
            Volatile.Write(ref _lastCallerThreadId, Thread.CurrentThread.ManagedThreadId);
        }

        public void Unlock()
        {
            Interlocked.Increment(ref _unlockCalls);
            Interlocked.Decrement(ref _counter);
            Volatile.Write(ref _lastCallerThreadId, Thread.CurrentThread.ManagedThreadId);
        }
    }
}
