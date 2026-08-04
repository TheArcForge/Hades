using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class HadesPathsTests
    {
        const string Home = "/Users/tester";
        const string Project = "/Work/MyGame";

        static string Expected(params string[] parts) => Path.Combine(parts);

        [Test]
        public void EnvOverride_WinsOverEverything()
        {
            var result = HadesPaths.ResolveHubDir("/custom/hub", HadesScope.Local, Project, Home);
            Assert.AreEqual("/custom/hub", result);
        }

        [Test]
        public void EnvOverride_WinsEvenInGlobalScope()
        {
            var result = HadesPaths.ResolveHubDir("/custom/hub", HadesScope.Global, Project, Home);
            Assert.AreEqual("/custom/hub", result);
        }

        [Test]
        public void EnvOverride_IsIgnored_WhenWhitespaceOnly()
        {
            var result = HadesPaths.ResolveHubDir("   ", HadesScope.Local, Project, Home);
            Assert.AreEqual(Expected(Project, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void EnvOverride_IsTrimmed()
        {
            var result = HadesPaths.ResolveHubDir("  /custom/hub  ", HadesScope.Local, Project, Home);
            Assert.AreEqual("/custom/hub", result);
        }

        [Test]
        public void LocalScope_ResolvesInsideTheProject()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Local, Project, Home);
            Assert.AreEqual(Expected(Project, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void GlobalScope_ResolvesUnderHome()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Global, Project, Home);
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void LocalScope_FallsBackToHome_WhenProjectRootIsUnknown()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Local, null, Home);
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void LocalScope_FallsBackToHome_WhenProjectRootIsEmpty()
        {
            var result = HadesPaths.ResolveHubDir(null, HadesScope.Local, "", Home);
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), result);
        }

        [Test]
        public void GlobalHubDir_IsHomeArcforgeHadesHub()
        {
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"), HadesPaths.GlobalHubDir(Home));
        }

        // The regression these guard: the stable launcher copy used to be written into the *resolved*
        // hub dir, so global hub scope put $HOME/.arcforge/hades-hub/launcher.js into .mcp.json's
        // args[0] — and .mcp.json is committed. The launcher copy must stay project-local under every
        // scope; only hub.json's location follows scope. Do not "simplify" LauncherDir to HubDir.
        [Test]
        public void LauncherDir_IsProjectLocal_ByConstruction()
        {
            Assert.AreEqual(Path.Combine(HadesPaths.ArcforgeDir, HadesPaths.HubDirName),
                HadesPaths.LauncherDir);
        }

        [Test]
        public void GlobalScope_MovesTheHub_ButNotTheLauncherArg()
        {
            Assert.AreEqual(Expected(Home, ".arcforge", "hades-hub"),
                HadesPaths.ResolveHubDir(null, HadesScope.Global, Project, Home),
                "hub.json still belongs under $HOME in global scope");

            var launcher = Expected(Project, ".arcforge", "hades-hub", "launcher.js");
            Assert.AreEqual(".arcforge/hades-hub/launcher.js",
                MCPClientConfig.McpLauncherArg(launcher, Project),
                "the launcher does not move with the hub, so args[0] has no machine path");
        }

        // Regression: MCPServer runs its heartbeat on a System.Threading.Timer and documents that
        // "the timer only touches pure I/O". It reads HubDir via HubClient.DetectHubChange. When
        // HubDir resolved live it reached PathSandbox.ProjectRoot -> Application.dataPath, which
        // Unity allows only on the main thread, so every heartbeat threw.
        [Test]
        public void HubDir_IsReadableFromABackgroundThread_AfterPrime()
        {
            HadesPaths.Prime();
            var expected = HadesPaths.HubDir;

            string fromBackground = null;
            System.Exception thrown = null;
            var done = new System.Threading.ManualResetEventSlim(false);

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { fromBackground = HadesPaths.HubDir; }
                catch (System.Exception ex) { thrown = ex; }
                finally { done.Set(); }
            });

            Assert.IsTrue(done.Wait(System.TimeSpan.FromSeconds(5)), "Background read timed out.");
            Assert.IsNull(thrown, $"Reading HubDir off the main thread threw: {thrown}");
            Assert.AreEqual(expected, fromBackground);
        }
    }

    public class LegacyHubNoticeTests
    {
        const string GlobalDir = "/Users/tester/.arcforge/hades-hub";
        const string LocalDir = "/Work/MyGame/.arcforge/hades-hub";

        [Test]
        public void ShouldShow_IsTrue_WhenLocalAndGlobalDirExists()
        {
            Assert.IsTrue(LegacyHubNotice.ShouldShow(LocalDir, GlobalDir, false, true));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenAlreadyShown()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(LocalDir, GlobalDir, true, true));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenGlobalDirAbsent()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(LocalDir, GlobalDir, false, false));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenStillUsingTheGlobalDir()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(GlobalDir, GlobalDir, false, true));
        }

        [Test]
        public void ShouldShow_IsFalse_WhenGlobalDirDiffersOnlyByTrailingSlash()
        {
            Assert.IsFalse(LegacyHubNotice.ShouldShow(GlobalDir + "/", GlobalDir, false, true));
        }
    }
}
