// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Hades.Contract.Wire;
using Hades.Tools;
using Hades.Transport;
using UnityEditor;
using UnityEngine;

namespace Hades.Runtime
{
    /// <summary>
    /// Entry point: constructs the pump, the reload gate, and the client, and starts the pump and
    /// client the moment this Editor loads this assembly - which happens on first load and again
    /// after every domain reload, since <c>[InitializeOnLoad]</c> re-runs the static constructor
    /// each time and reload wipes all prior managed state anyway (see the editor-link plan's
    /// architecture section). Constructing <see cref="Gate"/> here (rather than only in tests) is
    /// what makes its boot reconciliation real: every one of these re-runs is exactly the moment
    /// a lock leaked by the PREVIOUS managed instance (killed by the reload before it could clean
    /// up) needs to be found and released - see <c>ReloadGate</c>'s own class doc comment.
    ///
    /// Proactively tears both down on <see cref="AssemblyReloadEvents.beforeAssemblyReload"/>
    /// rather than relying on the reload itself to end the I/O thread - "no thread survives" is a
    /// hard requirement, and an explicit stop is provable where relying on reload semantics alone
    /// is not. Also tears down on <see cref="EditorApplication.quitting"/>, so the thread does not
    /// survive the Editor closing either.
    /// </summary>
    [InitializeOnLoad]
    public static class HadesBoot
    {
        /// <summary>Reported in <see cref="Hello"/> so the app can prompt an in-place update on a
        /// mismatch (see the plugin design spec's installation section) rather than silently
        /// refusing to work. Track this against package.json's version at each release; nothing
        /// in this netstandard2.1, zero-dependency folder can read that file once installed into
        /// a stranger's project, so it cannot be computed automatically.</summary>
        public const string PluginVersion = "1.3.0";

        static readonly MainThreadPump Pump;
        static readonly ReloadGate Gate;
        static readonly HadesClient Client;

        static HadesBoot()
        {
            // Unity runs asset-import worker PROCESSES alongside the Editor, and they load editor
            // assemblies too - so [InitializeOnLoad] fires in each of them and, without this guard,
            // every one dials the app. Measured on a real Editor: main process plus two workers,
            // three ESTABLISHED connections for one project.
            //
            // Two things break as a result. EditorRegistry is keyed by project GUID with a
            // newest-wins policy, so a worker's registration silently replaces the real Editor's -
            // hades_charon_status then reports a worker's pid. And a worker has no
            // EditorApplication.update loop draining MainThreadPump, so it never answers the
            // main-thread probe and the app reports busy FOREVER, for an Editor that is idle.
            //
            // Only the main Editor process may dial.
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;

            // Installed here, at boot, rather than lazily inside project_get_console_log's own
            // handler - a message logged before this Editor's first tool call is exactly the one
            // an agent is most likely asking about, and a subscription installed only once a call
            // happened to arrive would have already missed it. See ConsoleLogBuffer's own class
            // doc comment (Tools/ProjectCommands.cs) for the full reasoning, including why it does
            // NOT persist across a domain reload the way Gate's own SessionState reconciliation
            // does.
            Tools.ConsoleLogBuffer.Install();

            Pump = new MainThreadPump();
            Pump.Start();

            // Constructing this here is what makes boot reconciliation real rather than merely
            // unit-tested: this constructor call is what runs SessionState's leaked-lock check on
            // every Editor load and every post-domain-reload reconstruction - see ReloadGate's own
            // class doc comment. Built on the real EditorApplication.Lock/UnlockReloadAssemblies
            // (never faked here, unlike in tests) and given Pump so its off-thread release paths
            // (TTL, ReleaseOnDisconnect) have somewhere to defer the actual Unlock() to - see
            // ReloadGate's doc comment for why they cannot call it inline.
            Gate = new ReloadGate(new EditorLockApi(), Pump);

            Client = new HadesClient(
                () => HadesConnectionFile.TryRead(HadesConnectionFile.DefaultPath),
                BuildHello(),
                Pump,
                HandleRequest,
                onDisconnected: Gate.ReleaseOnDisconnect);
            Client.Start();

            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        // Mirrors Hades.Core.Projects.ProjectIdentity.TryReadProductGuid on the app side (that
        // class's own doc comment: "Project identity is Unity's own productGUID from
        // ProjectSettings.asset"). Deliberately NOT PlayerSettings.productGUID: verified
        // empirically (see the editor-link plan's investigation notes) that Unity's live,
        // in-memory PlayerSettings.productGUID can disagree with what is actually persisted to
        // ProjectSettings.asset - measured directly, on a project file untouched since May.
        // EditorRegistry is keyed on the app's file-based read, so a
        // Hello built from the live API value can hand-shake perfectly and still register under
        // a key hades_charon_status never looks up - a real Editor connection that is
        // permanently invisible, with no error anywhere. Reading the same file the same way, on
        // both sides, makes the two values agree by construction instead of by coincidence -
        // same reasoning as why Contract/ is a byte-identical copy rather than a second
        // hand-rolled wire format.
        static readonly Regex ProductGuidPattern = new Regex(@"productGUID:\s*([0-9a-fA-F]{32})");

        /// <summary>Reads productGUID from &lt;projectRoot&gt;/ProjectSettings/ProjectSettings.asset.
        /// Returns null if it cannot be read, which makes EditorListener reject the Hello and the
        /// client retry - deliberately, because there is NO safe fallback here.
        ///
        /// There is specifically no fallback to PlayerSettings.productGUID. Measured on a real
        /// Editor: the file read gives 15c012f27331e49229cef25e74537816 while
        /// PlayerSettings.productGUID.ToString("N") gives 2f210c511337294e92ec2fe547358761 for
        /// the same untouched project. Those are not different GUIDs - each 4-byte word is a
        /// digit-permutation of the corresponding word in the other, so the API applies some
        /// encoding transform to the persisted value. Whatever the transform is, the string does
        /// not match what the app keys its registry on.
        ///
        /// That makes the API value worse than no value. Sending it produces a handshake that
        /// succeeds, a registration under a key hades_charon_status never looks up, and no error
        /// on either side - an attached Editor that is permanently invisible. A rejected Hello at
        /// least fails where someone can see it.</summary>
        static string ReadProductGuid(string projectRoot)
        {
            try
            {
                var path = Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset");
                if (File.Exists(path))
                {
                    var match = ProductGuidPattern.Match(File.ReadAllText(path));
                    if (match.Success) return match.Groups[1].Value.ToLowerInvariant();
                }

                UnityEngine.Debug.LogError("[Hades] Could not read productGUID from " + path
                    + ". Hades cannot identify this project and will not connect. "
                    + "This is not a transient reconnect - it needs attention.");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[Hades] Failed reading productGUID: " + ex.Message);
            }

            return null;
        }

        static Hello BuildHello()
        {
            // Application.dataPath is "<projectRoot>/Assets" - its parent is the project root,
            // matching ProjectIdentity's SettingsPath/FindProjectRoot convention (the folder
            // containing ProjectSettings/ and Assets/, not Assets/ itself).
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

            return new Hello
            {
                ProjectGuid = ReadProductGuid(projectRoot),
                ProjectPath = projectRoot,
                UnityVersion = Application.unityVersion,
                PluginVersion = PluginVersion,
                ProcessId = Process.GetCurrentProcess().Id,
            };
        }

        /// <summary>
        /// Dispatches every JSON-RPC method this plugin actually answers - see
        /// <see cref="Tools.CommandTable"/> for the method-name-to-handler mapping itself (this
        /// used to be a switch statement here; it moved so <c>CommandTable</c> could become the
        /// one place every future Editor tool's handler registers, instead of this switch growing
        /// by one case per tool - see the "52 Editor tools" plan). Still answered correctly for an
        /// unknown method: on the main thread, within its deadline, as a real JSON-RPC error.
        /// </summary>
        static JsonValue HandleRequest(JsonRpcRequest request) => HandleRequest(Gate, request);

        /// <summary>
        /// Takes an explicit <paramref name="gate"/> rather than reading the static <see cref="Gate"/>
        /// singleton directly, purely so LeaseCommandTests can exercise real dispatch logic
        /// against a ReloadGate built on a FakeEditorLockApi - see that suite's class doc comment.
        /// Forwards straight to <see cref="Tools.CommandTable.Dispatch"/>; every lease.* handler
        /// there calls straight through to ReloadGate's synchronous Acquire/Renew/Release, so -
        /// same as any other lease.release RPC handler - this needs no MainThreadPump involvement
        /// of its own: HadesClient already dispatches every non-keepalive request (this one
        /// included) through the pump before this method ever runs - see HadesClient's own class
        /// doc comment.
        /// </summary>
        public static JsonValue HandleRequest(ReloadGate gate, JsonRpcRequest request) => CommandTable.Dispatch(gate, request);

        static void Shutdown()
        {
            // Null in an asset-import worker, where the static ctor returns before constructing
            // any of these. The handlers are never subscribed there, so this should not run at
            // all - the null-conditionals are belt and braces, not an expected path.
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            Client?.Dispose();
            Gate?.Dispose();
            Pump?.Dispose();
        }
    }
}
