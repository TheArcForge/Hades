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
    }
}
