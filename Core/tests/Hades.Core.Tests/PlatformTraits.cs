namespace Hades.Core.Tests;

/// <summary>
/// Platform gating for tests that can only run on one OS.
///
/// xUnit 2.9.3 has no dynamic skip (verified 2026-08-25: Assert.Skip does not exist,
/// Xunit.SkipException is not public, and the raw DynamicSkipToken is reported as a FAILURE by
/// the v2 runner). Rather than add Xunit.SkippableFact or migrate to xUnit v3, platform-specific
/// tests carry a trait and each CI job filters out the traits it cannot run:
///
///   macOS:   dotnet test --filter "Platform!=Windows"
///   Windows: dotnet test --filter "Platform!=Unix"
///
/// A filtered test is not reported at all - better than the early-return convention used
/// elsewhere in this suite, which xUnit reports as PASSED.
///
/// Usage:  [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
/// </summary>
public static class PlatformTraits
{
    public const string Key = "Platform";
    public const string Windows = "Windows";
    public const string Unix = "Unix";
}
