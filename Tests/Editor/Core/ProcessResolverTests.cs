// Tests/Editor/Core/ProcessResolverTests.cs
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests.Core
{
    public class ProcessResolverTests
    {
        [Test]
        public void Start_ReturnsRunningProcess_ThatExitsWithoutBlocking()
        {
            // `node -e "process.exit(0)"` exits ~immediately; Start must return without
            // blocking on WaitForExit, and the returned handle reports the exit.
            var node = ProcessResolver.FindExecutable("node");
            if (node == null) Assert.Ignore("node not installed");

            var handle = ProcessResolver.Start(node, "-e \"process.exit(3)\"", System.IO.Path.GetTempPath());
            Assert.IsNotNull(handle);

            // Drain (no output expected, but model the correct non-deadlocking usage).
            var outTask = handle.StandardOutput.ReadToEndAsync();
            var errTask = handle.StandardError.ReadToEndAsync();

            // Poll for exit on the calling thread (no WaitForExit).
            for (int i = 0; i < 100 && !handle.HasExited; i++) System.Threading.Thread.Sleep(20);

            Assert.IsTrue(handle.HasExited, "process should have exited");
            Assert.AreEqual(3, handle.ExitCode, "exit code must round-trip");
            System.Threading.Tasks.Task.WaitAll(new[] { outTask, errTask }, 1000);
            handle.Dispose();
        }
    }
}
