namespace Hades.Core.Editors;

/// <summary>
/// How far a Unity plugin's self-reported version (Hello.PluginVersion - see
/// Hades.Contract.Wire.Hello - or the file-scanned <see cref="PluginInstaller.InstalledPluginVersion"/>
/// when nothing is live attached) has drifted from the version this app itself ships
/// (<see cref="PluginInstaller.AppPluginVersion"/>) - see <see cref="PluginVersionComparison.Classify"/>.
///
/// Spec #4 §6: "a plugin one minor version behind still serves what it can, with a warning" and
/// "degrades rather than refuses" at every one of these except <see cref="Unknown"/> (nothing to
/// compare, so nothing to claim). Direction (older vs newer) never changes which bucket a pair
/// falls into - only whether the MAJOR component differs does. See the plan report ("Task 5:
/// version skew") for the reasoning: a same-major skew is treated identically regardless of
/// magnitude or direction (matching the pre-existing behaviour this app already shipped for a
/// two-minor-version gap), and only a different major version escalates the warning's wording -
/// never to a refusal. Nothing in this codebase's editor-link transport (<see cref="EditorListener"/>)
/// ever inspects this at all: it is used purely to decide what a caller (ProjectsEndpoint,
/// SummaryTools' hades_charon_status) SAYS about an already-accepted connection, never whether to
/// accept one.
/// </summary>
public enum PluginVersionSkew
{
    /// <summary>Identical major.minor.patch (a missing patch component is treated as 0). No
    /// warning.</summary>
    Same,

    /// <summary>Same major version, but not identical - any minor or patch difference, in either
    /// direction. Degrades: still connects, still serves every RPC method both sides support,
    /// warns with the ordinary ("does not match") wording.</summary>
    Minor,

    /// <summary>Different major version, in either direction - spec #4 §6's "a plugin from the
    /// future may genuinely speak a protocol the app cannot" case, and its mirror (a plugin left
    /// badly out of date). Still degrades rather than refusing (see this enum's own doc comment),
    /// but the warning's wording says so plainly rather than reusing the same wording a same-major
    /// skew gets.</summary>
    Major,

    /// <summary>One or both version strings did not parse as "major.minor" or "major.minor.patch"
    /// (including null/blank). Nothing trustworthy to compare - same convention
    /// <see cref="PluginInstaller.InstalledPluginVersion"/> already uses for an unreadable file:
    /// treated as "no version available", not as evidence of a problem.</summary>
    Unknown,
}

/// <summary>Pure classification - see <see cref="PluginVersionSkew"/> for what each result means.
/// No I/O, no knowledge of where either version string came from (a live Hello, a file scan, or a
/// hand-typed test literal) - callers decide that.</summary>
public static class PluginVersionComparison
{
    public static PluginVersionSkew Classify(string? pluginVersion, string? appVersion)
    {
        if (!TryParse(pluginVersion, out var plugin) || !TryParse(appVersion, out var app)) return PluginVersionSkew.Unknown;
        if (plugin.Major != app.Major) return PluginVersionSkew.Major;
        return plugin == app ? PluginVersionSkew.Same : PluginVersionSkew.Minor;
    }

    /// <summary>Parses the numeric MAJOR.MINOR(.patch) core of <paramref name="version"/> - a
    /// missing patch defaults to 0. First strips a trailing semver prerelease/build suffix ("-" or
    /// "+", e.g. "2.0.0-beta" or "1.2.0+build5") and ignores any component past the third (e.g.
    /// "1.2.3.4" reads as 1.2.3) - a plugin ahead of the app's own release cadence (a beta, or a
    /// build carrying a fourth component) still has a comparable numeric core, and suppressing the
    /// skew warning for it entirely (the previous behaviour: anything that didn't match exactly
    /// "major.minor"/"major.minor.patch" fell to <see cref="PluginVersionSkew.Unknown"/>) hid a
    /// real major-version gap - see PluginVersionSkewTests' own "should warn" case. Ints must still
    /// be non-negative; MAJOR and MINOR missing or non-numeric (e.g. "1.x.0", "not-a-version", just
    /// "1" with no minor at all) still fails to parse rather than guessing.</summary>
    static bool TryParse(string? version, out (int Major, int Minor, int Patch) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version)) return false;

        var suffixStart = version.IndexOfAny(['-', '+']);
        var core = suffixStart >= 0 ? version[..suffixStart] : version;

        var parts = core.Split('.');
        if (parts.Length < 2) return false;

        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        if (!int.TryParse(parts[1], out var minor) || minor < 0) return false;

        var patch = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0)) return false;
        // Any component beyond the third (parts[3], ...) is ignored, not validated - e.g. "1.2.3.4".

        parsed = (major, minor, patch);
        return true;
    }
}
