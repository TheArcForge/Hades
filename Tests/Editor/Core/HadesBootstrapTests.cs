using NUnit.Framework;
using ArcForge.Hades.Editor.Core;
using ArcForge.Hades.Editor.Asphodel;

namespace ArcForge.Hades.Editor.Tests.Core
{
    public class HadesBootstrapTests
    {
        [Test]
        public void Boot_StartsServerBeforeStartupSync()
        {
            // BootTrace is populated when the editor loaded (HadesBootstrap ran once).
            var trace = HadesBootstrap.BootTrace;
            var server = trace.IndexOf("MCPServer");
            var sync = trace.IndexOf("StartupSyncScheduled");
            Assert.Greater(server, -1, "server step not recorded");
            Assert.Greater(sync, -1, "startup-sync step not recorded");
            Assert.Less(server, sync, "MCP server must start before startup sync is scheduled");
        }

        [Test]
        public void Settings_BootsBeforeCharon()
        {
            var trace = HadesBootstrap.BootTrace;
            Assert.Less(trace.IndexOf("Settings"), trace.IndexOf("Charon"),
                "Charon constructs HadesSettings, so settings migration must run first.");
        }

        [Test]
        public void Boot_InitializesCharonBeforeAsphodel_SoInferenceEngineIsNotNull()
        {
            // #6 regression guard: Charon runs before Asphodel, so CharonEmitter.Database
            // is non-null when Asphodel reads it -> InferenceEngine is created.
            Assert.IsNotNull(AsphodeInitializer.InferenceEngine,
                "InferenceEngine is null - Charon/Asphodel init order regressed (#6)");
        }
    }
}
