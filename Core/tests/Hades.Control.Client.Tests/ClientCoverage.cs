namespace Hades.Control.Client.Tests;

/// <summary>
/// The single, named record of which parts of the control API the .NET client deliberately does
/// not cover. Shared by the conformance test and (later) the golden-fixture generator, so "which
/// client speaks which endpoints" has one home instead of becoming tribal knowledge.
///
/// Any new control endpoint lands in BOTH clients, or gets an entry here with a reason.
/// </summary>
public static class ClientCoverage
{
    /// <summary>v1.2 migration. macOS-only by construction: v1.2 never shipped on Windows, so no
    /// Windows user can have an install to migrate from. The Swift client covers these; the .NET
    /// client deliberately does not.</summary>
    static bool IsMigrationType(Type serverType) =>
        serverType.Name.StartsWith("Migration", StringComparison.Ordinal);

    /// <summary><c>ControlConnectionInfo</c> is not a control-API response: it is the discovery
    /// file's JSON shape, written once by <c>ControlListener.Start</c> and read from disk (never
    /// over HTTP) by the client's own <c>Discovery.Read</c>. The .NET client already covers this
    /// exact shape as <c>Hades.Control.Client.ControlConnection</c> - deliberately outside the
    /// <c>Dtos</c> namespace this walk pairs by name, since it is not one of the request/response
    /// types <c>Dtos</c> exists to pin. Duplicating it into <c>Dtos</c> under the server's own name
    /// would only add a second, never-referenced copy for this walk's benefit.</summary>
    static bool IsDiscoveryFileType(Type serverType) =>
        serverType.Name == "ControlConnectionInfo";

    public static bool IsExcluded(Type serverType) =>
        IsMigrationType(serverType) || IsDiscoveryFileType(serverType);
}
