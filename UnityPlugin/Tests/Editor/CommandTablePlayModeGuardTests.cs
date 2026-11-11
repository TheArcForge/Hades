// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-01 §6.2's mutation guard (<see cref="CommandTable.AllowedWhilePlaying"/>/
    /// <see cref="CommandTable.Dispatch"/>) outlives the tool that introduced it: play_mode,
    /// capture_view, and scene_bake were removed (Unity's own CLI/pipeline covers those Editor
    /// actions now), but a user can still press Play manually, and every mutating Hades tool must
    /// still refuse while that Editor is playing. These two tests were formerly part of
    /// PlayModeCommandsTests.cs (deleted along with play_mode's own tests) - moved here rather than
    /// deleted, since both are generic guard coverage over every CURRENTLY REGISTERED
    /// <see cref="CommandTable.Handlers"/> entry, not anything play_mode-specific.
    /// </summary>
    [TestFixture]
    public sealed class CommandTablePlayModeGuardTests
    {
        static readonly Func<bool> RealIsPlaying = CommandTable.EditorPlayModeApi.IsPlaying;

        [TearDown]
        public void TearDown() => CommandTable.EditorPlayModeApi.IsPlaying = RealIsPlaying;

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static (ReloadGate gate, MainThreadPump pump) NoopGateParts()
        {
            var pump = new MainThreadPump();
            var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, pump);
        }

        [Test]
        public void Dispatch_WhilePlaying_RefusesEveryMethodNotOnTheAllowList()
        {
            CommandTable.EditorPlayModeApi.IsPlaying = () => true;

            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                foreach (var method in CommandTable.Handlers.Keys)
                {
                    var allowed = CommandTable.AllowedWhilePlaying.TryGetValue(method, out var overrideMessage) && overrideMessage == null;

                    if (allowed) continue; // proven not to throw the guard by the other tests in this file/class

                    var ex = Assert.Throws<InvalidOperationException>(
                        () => CommandTable.Dispatch(gate, Request(method, JsonValue.NewObject())),
                        "method '" + method + "' must be refused while playing unless explicitly allow-listed");
                    Assert.IsNotEmpty(ex.Message);
                }
            }
        }

        [Test]
        public void AllowedWhilePlaying_IsExactlyTheExpectedSet()
        {
            // 36 -> 33: play_mode/capture_view/scene_bake removed, and their wire methods' guard
            // entries went with them - 8 remain (see CommandTable.AllowedWhilePlaying's own doc
            // comment).
            var expectedAllowed = new[]
            {
                "lease.renew", "lease.release",
                "project.end_script_editing",
                "inspector.inspect", "inspector.select",
                "project.get_console_log", "project.get_test_results",
            };
            var expectedExplicitlyRefused = new[] { "project.run_tests" };

            var actual = CommandTable.AllowedWhilePlaying;
            Assert.AreEqual(expectedAllowed.Length + expectedExplicitlyRefused.Length, actual.Count);

            foreach (var key in expectedAllowed)
            {
                Assert.IsTrue(actual.TryGetValue(key, out var v), "missing allow entry: " + key);
                Assert.IsNull(v, "'" + key + "' must be allowed (null override)");
            }

            foreach (var key in expectedExplicitlyRefused)
            {
                Assert.IsTrue(actual.TryGetValue(key, out var v), "missing explicit-refuse entry: " + key);
                Assert.IsNotNull(v, "'" + key + "' must be an EXPLICIT refuse (non-null override), not merely absent");
            }

            // Never re-introduced - see DESIGN-01 §6.2's "Refused, deliberately" table.
            CollectionAssert.DoesNotContain(actual.Keys, "lease.acquire");
            CollectionAssert.DoesNotContain(actual.Keys, "project.begin_script_editing");
        }
    }
}
