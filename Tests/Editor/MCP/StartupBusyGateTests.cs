using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Tests.MCP
{
    public class StartupBusyGateTests
    {
        [TearDown]
        public void TearDown() => GraphBuilder.SetStartupInProgressForTests(false);

        [Test]
        public void IsBusyForRequests_TrueDuringStartup()
        {
            GraphBuilder.SetStartupInProgressForTests(true);
            Assert.IsTrue(GraphBuilder.IsBusyForRequests);
        }

        [Test]
        public void IsBusyForRequests_FalseWhenIdle()
        {
            GraphBuilder.SetStartupInProgressForTests(false);
            Assert.IsFalse(GraphBuilder.IsBusyForRequests);
        }
    }
}
