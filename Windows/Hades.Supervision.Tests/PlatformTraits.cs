namespace Hades.Supervision.Tests;

/// <summary>
/// Platform gating for tests that can only run on one OS — a small equivalent of
/// Core/tests/Hades.Core.Tests/PlatformTraits.cs, duplicated rather than shared because Windows/
/// projects must not reference anything under Core/ (see Windows/Directory.Build.props'
/// EnsureShellIsAClient target) and this is a test-only, three-constant type not worth a shared
/// package for.
///
/// xUnit 2.9.3 has no dynamic skip (see Core's copy of this file for the verification notes), so
/// platform-only tests carry a trait and are excluded with `dotnet test --filter "Platform!=X"`
/// rather than skipped — a filtered-out test is not reported at all, unlike an early-return, which
/// xUnit counts as PASSED.
///
/// Usage: [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
/// </summary>
public static class PlatformTraits
{
    public const string Key = "Platform";
    public const string Windows = "Windows";
    public const string Unix = "Unix";
}
