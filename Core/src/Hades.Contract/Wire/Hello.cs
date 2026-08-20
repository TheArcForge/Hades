// C# 9 only in this file - see the file banner in Wire/MiniJson.cs for why (Hades.Contract is
// compiled a second time inside Unity's Editor, which caps out at C# 9). Block-scoped namespace
// and ordinary mutable properties are deliberate here, not an oversight.
using System;

namespace Hades.Contract.Wire
{
    /// <summary>
    /// The handshake payload a Unity Editor plugin sends when it connects to the app: enough to
    /// identify which project, which Editor, and which process, without the app having to ask.
    /// <see cref="ProjectGuid"/> is Unity's own productGUID (lowercase 32-hex, not a
    /// <see cref="Guid"/>) - see Hades.Core.Projects.ProjectIdentity, which reads the same value
    /// from ProjectSettings.asset on the app side.
    /// </summary>
    public sealed class Hello
    {
        public string? ProjectGuid { get; set; }
        public string? ProjectPath { get; set; }
        public string? UnityVersion { get; set; }
        public string? PluginVersion { get; set; }
        public long ProcessId { get; set; }

        public JsonValue ToJson()
        {
            var obj = JsonValue.NewObject();
            obj.SetProperty("projectGuid", JsonValue.String(ProjectGuid ?? string.Empty));
            obj.SetProperty("projectPath", JsonValue.String(ProjectPath ?? string.Empty));
            obj.SetProperty("unityVersion", JsonValue.String(UnityVersion ?? string.Empty));
            obj.SetProperty("pluginVersion", JsonValue.String(PluginVersion ?? string.Empty));
            obj.SetProperty("processId", JsonValue.Integer(ProcessId));
            return obj;
        }

        public static bool TryParse(JsonValue? json, out Hello? hello, out string? error)
        {
            hello = null;
            error = null;
            try
            {
                if (json is null || json.Kind != JsonValueKind.Object)
                {
                    error = "Hello must be a JSON object.";
                    return false;
                }

                if (!TryGetRequiredString(json, "projectGuid", out var projectGuid, out error)) return false;
                if (!TryGetRequiredString(json, "projectPath", out var projectPath, out error)) return false;
                if (!TryGetRequiredString(json, "unityVersion", out var unityVersion, out error)) return false;
                if (!TryGetRequiredString(json, "pluginVersion", out var pluginVersion, out error)) return false;

                if (!json.TryGetProperty("processId", out var processIdValue) || processIdValue!.Kind != JsonValueKind.Integer)
                {
                    error = "Hello must have an integer 'processId'.";
                    return false;
                }

                hello = new Hello
                {
                    ProjectGuid = projectGuid,
                    ProjectPath = projectPath,
                    UnityVersion = unityVersion,
                    PluginVersion = pluginVersion,
                    ProcessId = processIdValue.AsInteger()
                };
                return true;
            }
            catch (Exception e)
            {
                error = "Failed to parse hello: " + e.Message;
                hello = null;
                return false;
            }
        }

        public static bool TryParse(string? text, out Hello? hello, out string? error)
        {
            if (!MiniJson.TryParse(text, out var json, out error))
            {
                hello = null;
                return false;
            }
            return TryParse(json, out hello, out error);
        }

        static bool TryGetRequiredString(JsonValue json, string name, out string? value, out string? error)
        {
            if (!json.TryGetProperty(name, out var propValue) || propValue!.Kind != JsonValueKind.String)
            {
                value = null;
                error = $"Hello must have a string '{name}'.";
                return false;
            }

            value = propValue.AsString();
            error = null;
            return true;
        }
    }
}
