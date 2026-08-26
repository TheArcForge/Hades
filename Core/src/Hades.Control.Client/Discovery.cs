using System.Text.Json;

namespace Hades.Control.Client;

/// <summary>
/// Reads the control API's discovery file - never a hardcoded port. A missing file means Hades is
/// not running, which is an ordinary state, not an error: every failure mode here returns null
/// rather than throwing. Mirrors Mac/HadesControl's Discovery.swift, which holds the same
/// contract for the Swift shell.
/// </summary>
public static class Discovery
{
    /// <param name="root">The application-data root that holds <c>control.token</c>. Callers
    /// resolve this themselves (HADES_HOME, else the per-platform default) so this type stays a
    /// pure reader with no environment or platform knowledge of its own.</param>
    public static ControlConnection? Read(string root)
    {
        try
        {
            var path = Path.Combine(root, "control.token");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ControlConnection>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing, unreadable, malformed, or caught mid-write by the core: every one of these
            // means "not usable right now", never a condition worth surfacing as a client error.
            return null;
        }
    }
}
