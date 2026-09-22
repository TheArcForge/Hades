// C# 9 only in this file - see the file banner in Wire/MiniJson.cs for why (Hades.Contract is
// compiled a second time inside Unity's Editor, which caps out at C# 9). Block-scoped namespace
// and ordinary mutable properties are deliberate here, not an oversight.
using System;
using System.Collections.Generic;

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

        /// <summary>DESIGN-06 §6.2 Layer 2: which Editor-only capabilities this attached Editor's
        /// AppDomain can actually reach, computed once at boot and sent with every handshake so
        /// hades_charon_status can serve it with zero round trips, including while the Editor is
        /// busy. <b>OPTIONAL - <see cref="TryParse(JsonValue?,out Hello?,out string?)"/> must never
        /// require this field.</b> Defaults to an empty list, both here and on parse, so a plugin
        /// built before this field existed still hands back a Hello TryParse accepts in full -
        /// plugin/app version skew is a supported state (see <see cref="CharonStatus.PluginVersionOnDisk"/>'s
        /// own doc comment on the app side for the general policy this follows). No capability IDs
        /// are currently defined - the bake-target set DESIGN-06 introduced was retired along with
        /// scene_bake; always sent as an empty list today.</summary>
        public List<Capability> Capabilities { get; set; } = new List<Capability>();

        /// <summary>One entry of <see cref="Capabilities"/>: an id, whether it is available in this
        /// AppDomain right now, and - only when unavailable - one actionable sentence naming why
        /// (DESIGN-06 §6.3's error-message layer, reused here as the discovery layer's own detail
        /// rather than a second wording).</summary>
        public sealed class Capability
        {
            public string? Id { get; set; }
            public bool Available { get; set; }
            public string? Reason { get; set; }
        }

        public JsonValue ToJson()
        {
            var obj = JsonValue.NewObject();
            obj.SetProperty("projectGuid", JsonValue.String(ProjectGuid ?? string.Empty));
            obj.SetProperty("projectPath", JsonValue.String(ProjectPath ?? string.Empty));
            obj.SetProperty("unityVersion", JsonValue.String(UnityVersion ?? string.Empty));
            obj.SetProperty("pluginVersion", JsonValue.String(PluginVersion ?? string.Empty));
            obj.SetProperty("processId", JsonValue.Integer(ProcessId));

            var capabilities = JsonValue.NewArray();
            foreach (var cap in Capabilities ?? new List<Capability>())
            {
                var entry = JsonValue.NewObject()
                    .SetProperty("id", JsonValue.String(cap.Id ?? string.Empty))
                    .SetProperty("available", JsonValue.Bool(cap.Available));
                if (cap.Reason != null) entry.SetProperty("reason", JsonValue.String(cap.Reason));
                capabilities.Add(entry);
            }
            obj.SetProperty("capabilities", capabilities);

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
                    ProcessId = processIdValue.AsInteger(),
                    Capabilities = ParseCapabilities(json),
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

        /// <summary>OPTIONAL - absent, null, or malformed all read as "no capabilities reported",
        /// never a parse failure: <see cref="Capabilities"/> must not become a fifth required field
        /// TryParse rejects an older plugin's handshake over (see that property's own doc
        /// comment).</summary>
        static List<Capability> ParseCapabilities(JsonValue json)
        {
            var result = new List<Capability>();
            if (!json.TryGetProperty("capabilities", out var capsValue) || capsValue == null || capsValue.Kind != JsonValueKind.Array)
                return result;

            foreach (var item in capsValue.Items)
            {
                if (item == null || item.Kind != JsonValueKind.Object) continue;

                var cap = new Capability();
                if (item.TryGetProperty("id", out var idValue) && idValue != null && idValue.Kind == JsonValueKind.String)
                    cap.Id = idValue.AsString();
                if (item.TryGetProperty("available", out var availableValue) && availableValue != null && availableValue.Kind == JsonValueKind.Boolean)
                    cap.Available = availableValue.AsBoolean();
                if (item.TryGetProperty("reason", out var reasonValue) && reasonValue != null && reasonValue.Kind == JsonValueKind.String)
                    cap.Reason = reasonValue.AsString();

                result.Add(cap);
            }

            return result;
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
