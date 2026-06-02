using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class ProjectTools
    {
        [MCPTool("project_run_tests",
            "Start a test run with an optional name filter. EditMode runs trigger a domain reload, so this returns " +
            "immediately with status 'started' — poll project_get_test_results until it reports 'complete' for " +
            "total/passed/failed/skipped counts and failure details. The filter is a regex matched against full test " +
            "names, so a class or namespace name (e.g. 'MyScannerTests') selects all tests under it.")]
        public static MCPToolResult RunTests(
            [MCPToolParam("Test name filter — regex matched against full test names (e.g. 'MyTests'). Empty = all tests.")] string filter,
            [MCPToolParam("Test mode: EditMode, PlayMode, or All (default EditMode)")] string test_mode)
        {
            var mode = TestMode.EditMode;
            if (!string.IsNullOrEmpty(test_mode))
            {
                switch (test_mode.ToLowerInvariant())
                {
                    case "playmode":
                        mode = TestMode.PlayMode;
                        break;
                    case "all":
                        mode = TestMode.EditMode | TestMode.PlayMode;
                        break;
                }
            }

            var executionFilter = new Filter
            {
                testMode = mode
            };

            // groupNames does regex matching against full test names, so a bare class or
            // namespace name selects all tests beneath it. (testNames requires the exact
            // full name of each test and silently matches nothing for a class-name filter.)
            if (!string.IsNullOrEmpty(filter))
                executionFilter.groupNames = new[] { filter };

            SaveDirtyScenesWithPath();

            // Results only exist after RunFinished, which for EditMode fires on a later tick
            // (past a domain reload) — so we cannot return them from this call. Unity writes the
            // completed run to TestResults.xml; MarkStarted records its baseline mtime so the
            // poll (project_get_test_results) can tell this run's output from a stale file.
            TestRunResultStore.MarkStarted();

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.Execute(new ExecutionSettings(executionFilter));

            return MCPToolResult.Success(new
            {
                status = "started",
                test_mode = mode.ToString(),
                filter = string.IsNullOrEmpty(filter) ? "(all)" : filter,
                note = "Run started. EditMode triggers a domain reload; poll project_get_test_results until status='complete'."
            });
        }

        [MCPTool("project_get_test_results",
            "Retrieve results from the most recent project_run_tests run. Returns status 'running' until the run " +
            "(and any domain reload) finishes, then status 'complete' with total/passed/failed/skipped counts and failures.")]
        public static MCPToolResult GetTestResults()
        {
            if (!TestRunResultStore.HasStarted)
                return MCPToolResult.Success(new
                {
                    status = "none",
                    note = "No test run has been started this session. Call project_run_tests first."
                });

            if (!TestRunResultStore.IsComplete())
                return MCPToolResult.Success(new
                {
                    status = "running",
                    note = "Test run in progress (EditMode runs include a domain reload). Poll again shortly."
                });

            var path = TestRunResultStore.ResultsPath;
            try
            {
                var run = XDocument.Load(path).Root;
                int passed = (int?)run.Attribute("passed") ?? 0;
                int failed = (int?)run.Attribute("failed") ?? 0;
                int skipped = (int?)run.Attribute("skipped") ?? 0;
                int inconclusive = (int?)run.Attribute("inconclusive") ?? 0;
                int total = (int?)run.Attribute("total")
                            ?? (int?)run.Attribute("testcasecount")
                            ?? (passed + failed + skipped + inconclusive);

                var failures = run.Descendants("test-case")
                    .Where(tc => (string)tc.Attribute("result") == "Failed")
                    .Select(tc => new
                    {
                        name = (string)tc.Attribute("fullname") ?? (string)tc.Attribute("name"),
                        message = (tc.Element("failure")?.Element("message")?.Value ?? "").Trim()
                    })
                    .ToArray();

                return MCPToolResult.Success(new
                {
                    status = "complete",
                    total,
                    passed,
                    failed,
                    skipped,
                    inconclusive,
                    duration = (string)run.Attribute("duration"),
                    failures
                });
            }
            catch (Exception ex)
            {
                return MCPToolResult.Error($"Failed to parse test results at {path}: {ex.Message}");
            }
        }

        // Persist only scenes that are dirty AND already saved to disk. The previous
        // EditorSceneManager.SaveOpenScenes() popped a blocking Save-As modal for untitled
        // scenes, which froze the MCP bridge until a human dismissed it.
        static void SaveDirtyScenesWithPath()
        {
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isDirty && !string.IsNullOrEmpty(scene.path))
                    EditorSceneManager.SaveScene(scene);
            }
        }

        [MCPTool("project_get_console_log", "Get recent Unity console log entries with optional type filter (Error, Warning, Log)")]
        public static MCPToolResult GetConsoleLog(
            [MCPToolParam("Number of entries to return (default 50, max 200)")] string count,
            [MCPToolParam("Filter by type: Error, Warning, Log, or empty for all")] string type_filter)
        {
            int n = 50;
            if (!string.IsNullOrEmpty(count) && int.TryParse(count, out var parsed))
                n = Math.Max(1, Math.Min(parsed, 200));

            var entries = ConsoleLogBuffer.GetRecent(n);

            LogType? filterType = null;
            if (!string.IsNullOrEmpty(type_filter))
            {
                switch (type_filter.ToLowerInvariant())
                {
                    case "error":
                        filterType = LogType.Error;
                        break;
                    case "warning":
                        filterType = LogType.Warning;
                        break;
                    case "log":
                        filterType = LogType.Log;
                        break;
                }
            }

            var filtered = filterType.HasValue
                ? entries.Where(e => e.type == filterType.Value).ToArray()
                : entries;

            var result = filtered.Select(e => new
            {
                type = e.type.ToString(),
                message = e.message,
                stackTrace = TruncateStackTrace(e.stackTrace)
            }).ToArray();

            return MCPToolResult.Success(new
            {
                entries = result,
                count = result.Length,
                totalBuffered = entries.Length
            });
        }

        [MCPTool("project_get_settings", "Read a project setting by name. " +
            "Important settings to check early: 'PlayerSettings.activeInputHandler' (0=Both, 1=Input Manager/Legacy, 2=Input System/New), " +
            "'PlayerSettings.colorSpace' (Linear/Gamma), 'PlayerSettings.scriptingBackend', " +
            "'EditorSettings.serializationMode'. Examples: 'PlayerSettings.productName', 'PlayerSettings.activeInputHandler'.")]
        public static MCPToolResult GetProjectSettings(
            [MCPToolParam("Setting name, e.g. 'PlayerSettings.productName' or 'productName'", required: true)]
            string settingName)
        {
            if (string.IsNullOrWhiteSpace(settingName))
                return MCPToolResult.Error("Setting name is required.");

            // Parse "Class.Property" or just "Property"
            string className;
            string propertyName;

            var dotIndex = settingName.LastIndexOf('.');
            if (dotIndex >= 0)
            {
                className = settingName.Substring(0, dotIndex);
                propertyName = settingName.Substring(dotIndex + 1);
            }
            else
            {
                className = "PlayerSettings";
                propertyName = settingName;
            }

            // Try to find the settings class
            Type settingsType = null;
            switch (className)
            {
                case "PlayerSettings":
                    settingsType = typeof(PlayerSettings);
                    break;
                case "EditorSettings":
                    settingsType = typeof(EditorSettings);
                    break;
                default:
                    // Try to find by name in UnityEditor namespace
                    settingsType = typeof(EditorSettings).Assembly.GetType("UnityEditor." + className);
                    break;
            }

            if (settingsType == null)
                return MCPToolResult.Error(
                    $"Settings class '{className}' not found. Supported: PlayerSettings, EditorSettings.");

            // Try static property
            var prop = settingsType.GetProperty(propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop != null)
            {
                var value = prop.GetValue(null);
                return MCPToolResult.Success(new
                {
                    setting = settingName,
                    value = value?.ToString(),
                    type = prop.PropertyType.Name
                });
            }

            // Try static field
            var field = settingsType.GetField(propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field != null)
            {
                var value = field.GetValue(null);
                return MCPToolResult.Success(new
                {
                    setting = settingName,
                    value = value?.ToString(),
                    type = field.FieldType.Name
                });
            }

            // Some PlayerSettings values (e.g. activeInputHandler) are exposed only through
            // the serialized ProjectSettings asset, not as public static members. Fall back
            // to reading them directly so the documented settings actually resolve.
            if (className == "PlayerSettings")
            {
                var psAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                if (psAssets != null && psAssets.Length > 0)
                {
                    var so = new SerializedObject(psAssets[0]);
                    var sp = so.FindProperty(propertyName);
                    if (sp != null)
                    {
                        return MCPToolResult.Success(new
                        {
                            setting = settingName,
                            value = SerializedPropertyToString(sp),
                            type = sp.propertyType.ToString()
                        });
                    }
                }
            }

            // List available properties as suggestions
            var available = settingsType
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(p => p.Name)
                .OrderBy(n => n)
                .Take(30)
                .ToArray();

            return MCPToolResult.Error(
                $"Property '{propertyName}' not found on {className}.\n\nAvailable properties (first 30):\n  " +
                string.Join("\n  ", available));
        }

        static string SerializedPropertyToString(SerializedProperty sp)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: return sp.intValue.ToString();
                case SerializedPropertyType.Boolean: return sp.boolValue.ToString();
                case SerializedPropertyType.Float: return sp.floatValue.ToString();
                case SerializedPropertyType.String: return sp.stringValue;
                case SerializedPropertyType.Enum: return sp.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    return sp.objectReferenceValue != null ? sp.objectReferenceValue.name : "null";
                default: return sp.propertyType.ToString();
            }
        }

        [MCPTool("project_refresh_assets", "Force a full refresh of the Unity AssetDatabase")]
        public static MCPToolResult RefreshAssetDatabase()
        {
            AssetDatabase.Refresh();
            return MCPToolResult.Success(new { refreshed = true });
        }

        static string TruncateStackTrace(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "";
            var lines = stackTrace.Split('\n');
            if (lines.Length <= 5) return stackTrace;
            return string.Join("\n", lines.Take(5)) + "\n... (truncated)";
        }

    }

    public static class ConsoleLogBuffer
    {
        const int BufferSize = 200;
        const string SessionStateKey = "Hades.ConsoleBuffer";

        static (LogType type, string message, string stackTrace)[] _buffer
            = new (LogType, string, string)[BufferSize];
        static int _head;
        static int _count;
        static bool _subscribed;
        static bool _dirty;

        [InitializeOnLoadMethod]
        public static void Initialize()
        {
            RestoreFromSessionState();

            if (!_subscribed)
            {
                _subscribed = true;
                Application.logMessageReceivedThreaded += OnLogReceived;
                EditorApplication.update += FlushToSessionState;
            }
        }

        static void OnLogReceived(string message, string stackTrace, LogType type)
        {
            lock (_buffer)
            {
                _buffer[_head] = (type, message, stackTrace);
                _head = (_head + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
                _dirty = true;
            }
        }

        static void FlushToSessionState()
        {
            if (!_dirty) return;
            _dirty = false;
            SaveToSessionState();
        }

        public static (LogType type, string message, string stackTrace)[] GetRecent(int count)
        {
            lock (_buffer)
            {
                var n = Math.Min(count, _count);
                var result = new (LogType, string, string)[n];
                for (int i = 0; i < n; i++)
                {
                    var idx = (_head - n + i + _buffer.Length) % _buffer.Length;
                    result[i] = _buffer[idx];
                }
                return result;
            }
        }

        internal static void SaveToSessionState()
        {
            lock (_buffer)
            {
                var entries = GetRecent(_count);
                var jsonEntries = new string[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    // Manual JSON to avoid Newtonsoft dependency in this hot path
                    var msg = EscapeJson(e.message ?? "");
                    var st = EscapeJson(TruncateForStorage(e.stackTrace ?? ""));
                    jsonEntries[i] = $"{{\"t\":{(int)e.type},\"m\":\"{msg}\",\"s\":\"{st}\"}}";
                }
                var json = "[" + string.Join(",", jsonEntries) + "]";
                SessionState.SetString(SessionStateKey, json);
            }
        }

        internal static void RestoreFromSessionState()
        {
            var json = SessionState.GetString(SessionStateKey, "");
            if (string.IsNullOrEmpty(json) || json == "[]")
                return;

            try
            {
                // Minimal JSON array parsing — entries are {t:int, m:string, s:string}
                var entries = JsonConvert.DeserializeObject<LogEntry[]>(json);
                if (entries == null) return;

                lock (_buffer)
                {
                    foreach (var e in entries)
                    {
                        _buffer[_head] = ((LogType)e.t, e.m, e.s);
                        _head = (_head + 1) % _buffer.Length;
                        if (_count < _buffer.Length) _count++;
                    }
                }
            }
            catch (Exception)
            {
                // Corrupted session state — start fresh
            }
        }

        internal static void ClearBuffer()
        {
            lock (_buffer)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                _head = 0;
                _count = 0;
            }
        }

        static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        static string TruncateForStorage(string s) =>
            s.Length > 500 ? s.Substring(0, 500) : s;

        [Serializable]
        class LogEntry
        {
            public int t;
            public string m;
            public string s;
        }
    }
}
