using System;
using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.Tests.MCP
{
    public class WorkItemDeadlineTests
    {
        [Test]
        public void IsExpired_TrueWhenPastDeadline()
        {
            var deadline = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
            var now = new DateTime(2026, 1, 1, 0, 0, 31, DateTimeKind.Utc);
            Assert.IsTrue(MCPServer.IsExpired(deadline, now));
        }

        [Test]
        public void IsExpired_FalseWhenBeforeDeadline()
        {
            var deadline = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
            var now = new DateTime(2026, 1, 1, 0, 0, 29, DateTimeKind.Utc);
            Assert.IsFalse(MCPServer.IsExpired(deadline, now));
        }
    }
}
