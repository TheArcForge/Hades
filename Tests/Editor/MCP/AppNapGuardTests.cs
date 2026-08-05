using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.Tests.MCP
{
    public class AppNapGuardTests
    {
        [Test]
        public void AcquireRelease_Balances()
        {
            var start = AppNapGuard.ActiveCount;
            AppNapGuard.Acquire();
            Assert.AreEqual(start + 1, AppNapGuard.ActiveCount);
            AppNapGuard.Release();
            Assert.AreEqual(start, AppNapGuard.ActiveCount);
        }

        [Test]
        public void NestedAcquire_IsRefcounted()
        {
            var start = AppNapGuard.ActiveCount;
            AppNapGuard.Acquire();
            AppNapGuard.Acquire();
            Assert.AreEqual(start + 2, AppNapGuard.ActiveCount);
            AppNapGuard.Release();
            AppNapGuard.Release();
            Assert.AreEqual(start, AppNapGuard.ActiveCount);
        }

        [Test]
        public void Release_BelowZero_IsIgnored()
        {
            var start = AppNapGuard.ActiveCount;
            AppNapGuard.Release();
            Assert.AreEqual(start, AppNapGuard.ActiveCount, "release with no outstanding acquire is a no-op");
        }
    }
}
