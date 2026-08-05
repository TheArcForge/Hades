using System;
#if UNITY_EDITOR_OSX
using System.Runtime.InteropServices;
#endif

namespace ArcForge.Hades.Editor.MCP
{
    /// <summary>
    /// Holds a macOS "user-initiated" activity assertion (App Nap opt-out) while MCP work is
    /// in flight, so a backgrounded editor's main-thread queue keeps draining. Refcounted: the
    /// assertion begins on the first in-flight request and ends when the last one completes, so
    /// it costs nothing while the editor is idle. No-op on non-macOS editors. Thread-safe —
    /// Acquire/Release are called from transport (thread-pool) threads as well as the main thread.
    /// </summary>
    internal static class AppNapGuard
    {
        static readonly object _lock = new object();
        static int _count;

#if UNITY_EDITOR_OSX
        // NSActivityUserInitiated — prevents App Nap (0x00FFFFFF | NSActivityIdleSystemSleepDisabled bit).
        const ulong NSActivityUserInitiated = 0x00FFFFFFUL | (1UL << 20);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
        static extern IntPtr objc_getClass(string name);
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
        static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        static extern IntPtr msgSend(IntPtr receiver, IntPtr sel);
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        static extern IntPtr msgSend_str(IntPtr receiver, IntPtr sel, string utf8);
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        static extern IntPtr msgSend_begin(IntPtr receiver, IntPtr sel, ulong options, IntPtr reason);
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        static extern void msgSend_end(IntPtr receiver, IntPtr sel, IntPtr token);

        static IntPtr _token = IntPtr.Zero;
#endif

        public static void Acquire()
        {
            lock (_lock)
            {
                if (_count++ == 0)
                    Begin();
            }
        }

        public static void Release()
        {
            lock (_lock)
            {
                if (_count == 0) return;
                if (--_count == 0)
                    End();
            }
        }

        // Test-only visibility into the refcount. Guarded by the same lock for a consistent read.
        internal static int ActiveCount { get { lock (_lock) { return _count; } } }

        static void Begin()
        {
#if UNITY_EDITOR_OSX
            try
            {
                if (_token != IntPtr.Zero) return;
                var processInfo = msgSend(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
                var reason = msgSend_str(objc_getClass("NSString"),
                    sel_registerName("stringWithUTF8String:"), "Hades MCP request in flight");
                var token = msgSend_begin(processInfo,
                    sel_registerName("beginActivityWithOptions:reason:"), NSActivityUserInitiated, reason);
                // beginActivity returns an autoreleased token; retain it so it survives past this scope.
                _token = msgSend(token, sel_registerName("retain"));
            }
            catch
            {
                _token = IntPtr.Zero;
            }
#endif
        }

        static void End()
        {
#if UNITY_EDITOR_OSX
            try
            {
                if (_token == IntPtr.Zero) return;
                var processInfo = msgSend(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
                msgSend_end(processInfo, sel_registerName("endActivity:"), _token);
                msgSend(_token, sel_registerName("release"));
            }
            catch { /* best-effort */ }
            finally
            {
                _token = IntPtr.Zero;
            }
#endif
        }
    }
}
