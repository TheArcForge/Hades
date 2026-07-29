using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class HadesSettingsTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "hades_settings_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        HadesSettings NewSettings() => new HadesSettings(HadesConfig.Load(_dir));

        [Test]
        public void Defaults_MatchSpec()
        {
            var s = NewSettings();
            Assert.AreEqual(HadesScope.Local, s.HubScope);
            Assert.AreEqual(HadesScope.Local, s.SkillsScope);
            Assert.AreEqual(true, s.DesktopIntegration);
            Assert.AreEqual(0, s.Port);
            Assert.AreEqual(true, s.Enabled);
            Assert.AreEqual(true, s.AutoStart);
            Assert.AreEqual(1, s.LogLevel);
            Assert.AreEqual(ReloadStrategy.Auto, s.DomainReloadStrategy);
            Assert.AreEqual(120, s.ReloadTimeoutSeconds);
            Assert.AreEqual(true, s.CharonEnabled);
            Assert.AreEqual(30, s.CharonRetentionDays);
            Assert.AreEqual(500, s.CharonMaxSizeMb);
        }

        [Test]
        public void Setters_PersistAcrossReload()
        {
            var s = NewSettings();
            s.HubScope = HadesScope.Global;
            s.SkillsScope = HadesScope.Global;
            s.DesktopIntegration = false;
            s.Port = 51234;
            s.Enabled = false;
            s.AutoStart = false;
            s.LogLevel = 3;
            s.DomainReloadStrategy = ReloadStrategy.Manual;
            s.ReloadTimeoutSeconds = 45;
            s.CharonEnabled = false;
            s.CharonRetentionDays = 7;
            s.CharonMaxSizeMb = 0;

            var reloaded = NewSettings();
            Assert.AreEqual(HadesScope.Global, reloaded.HubScope);
            Assert.AreEqual(HadesScope.Global, reloaded.SkillsScope);
            Assert.AreEqual(false, reloaded.DesktopIntegration);
            Assert.AreEqual(51234, reloaded.Port);
            Assert.AreEqual(false, reloaded.Enabled);
            Assert.AreEqual(false, reloaded.AutoStart);
            Assert.AreEqual(3, reloaded.LogLevel);
            Assert.AreEqual(ReloadStrategy.Manual, reloaded.DomainReloadStrategy);
            Assert.AreEqual(45, reloaded.ReloadTimeoutSeconds);
            Assert.AreEqual(false, reloaded.CharonEnabled);
            Assert.AreEqual(7, reloaded.CharonRetentionDays);
            Assert.AreEqual(0, reloaded.CharonMaxSizeMb);
        }

        [Test]
        public void HubScope_ParsesTheStringForm()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), "hub_scope: global\n");
            Assert.AreEqual(HadesScope.Global, NewSettings().HubScope);
        }

        [Test]
        public void HubScope_FallsBackToLocal_OnGarbage()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), "hub_scope: sideways\n");
            Assert.AreEqual(HadesScope.Local, NewSettings().HubScope);
        }

        [Test]
        public void HubScope_IsCaseInsensitive()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), "hub_scope: GLOBAL\n");
            Assert.AreEqual(HadesScope.Global, NewSettings().HubScope);
        }

        [Test]
        public void DomainReloadStrategy_ParsesTheStringForm()
        {
            File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName),
                "domain_reload_strategy: manual\n");
            Assert.AreEqual(ReloadStrategy.Manual, NewSettings().DomainReloadStrategy);
        }

        [Test]
        public void ImportFromEditorPrefs_CopiesLegacyValues()
        {
            UnityEditor.EditorPrefs.SetInt("Hades_MCP_Port", 51999);
            UnityEditor.EditorPrefs.SetBool("Hades_MCP_CharonEnabled", false);
            try
            {
                var config = HadesConfig.Load(_dir);
                HadesSettings.ImportFromEditorPrefs(config);
                config.Save();

                var s = NewSettings();
                Assert.AreEqual(51999, s.Port);
                Assert.AreEqual(false, s.CharonEnabled);
            }
            finally
            {
                UnityEditor.EditorPrefs.DeleteKey("Hades_MCP_Port");
                UnityEditor.EditorPrefs.DeleteKey("Hades_MCP_CharonEnabled");
            }
        }

        [Test]
        public void HasLegacyEditorPrefs_IsTrue_WhenAnyLegacyKeyExists()
        {
            UnityEditor.EditorPrefs.SetInt("Hades_MCP_Port", 51999);
            try
            {
                Assert.IsTrue(HadesSettings.HasLegacyEditorPrefs());
            }
            finally
            {
                UnityEditor.EditorPrefs.DeleteKey("Hades_MCP_Port");
            }
        }
    }
}
