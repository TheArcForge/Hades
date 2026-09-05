namespace Hades.Control.Client;

/// <summary>
/// Where a client looks for the application-data root, resolved WITHOUT referencing Hades.Core.
///
/// This is deliberately not folded into <see cref="Discovery"/>, which documents itself as a pure
/// reader with no environment or platform knowledge - it takes the root as a parameter for exactly
/// that reason. This is its sibling: the one place every client answers "which root?", so that the
/// answer is written down once rather than copied into each of them.
///
/// It must agree with <c>AppPaths.DefaultRoot()</c> in the core. Nothing enforces that by
/// construction - the whole point of the §2 boundary is that a client cannot reference the core to
/// find out - so it is held in step by the conformance and fixture suites, and by this comment.
/// </summary>
public static class ClientPaths
{
    /// <summary>
    /// <c>HADES_HOME</c> if set, else the per-platform default: LocalApplicationData on Windows,
    /// ApplicationData elsewhere.
    ///
    /// Windows uses the machine-LOCAL folder rather than the roaming one on purpose: everything
    /// under the root is either derived (the databases) or meaningless on another machine (the
    /// tokens name a port on this one), so roaming it would sync a large cache that is wrong at the
    /// far end.
    /// </summary>
    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable("HADES_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(OperatingSystem.IsWindows()
                ? Environment.SpecialFolder.LocalApplicationData
                : Environment.SpecialFolder.ApplicationData),
            "Hades");
}
