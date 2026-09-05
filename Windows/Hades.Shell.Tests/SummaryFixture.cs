using System.Text.Json;
using Hades.Control.Client.Dtos;

namespace Hades.Shell.Tests;

/// <summary>
/// Builds <see cref="SummaryResult"/> values for menu tests by decoding the GENERATED GOLDEN CORPUS
/// at <c>Core/tests/Fixtures/control-api/</c>, rather than hand-writing DTOs here.
///
/// The point is that these fixtures are what the server actually emits, serialized through the real
/// listener options - so a menu test cannot quietly pass against a shape the server never produces
/// (a field the shell expects to be present that is really absent under WhenWritingNull, say). A
/// hand-written DTO would encode this test's assumptions twice and prove neither.
///
/// Variations are made with record `with` expressions off that decoded base, so every value still
/// starts from real server output and only the field under test differs.
/// </summary>
static class SummaryFixture
{
    /// <summary>A running core with one healthy project and no lease held.</summary>
    public static SummaryResult Idle() => Load() with
    {
        IconState = ControlIconState.Idle,
        Lease = null,
    };

    /// <summary>
    /// One project row carrying exactly the given name and status text, no lease. Used to prove the
    /// shell prints the core's own status string rather than re-wording it.
    /// </summary>
    public static SummaryResult WithProject(string name, string status)
    {
        var baseline = Load();
        var row = baseline.Rows[0] with { Project = name, Status = status };

        return baseline with { Rows = [row], Lease = null };
    }

    /// <summary>A running core with no projects adopted at all.</summary>
    public static SummaryResult WithNoProjects() => Load() with { Rows = [], Lease = null };

    /// <summary>
    /// A held reload lease. The golden fixture already carries one - it was captured in exactly this
    /// state - so this overrides only the fields a test varies and leaves the rest as the server
    /// really emitted them.
    /// </summary>
    public static SummaryResult WithHeldLease(
        string leaseId,
        bool releasable,
        int heldForSeconds = 42,
        string project = "Hades-Unity-Client")
    {
        var baseline = Load();

        return baseline with
        {
            IconState = ControlIconState.LeaseHeld,
            Lease = baseline.Lease! with
            {
                LeaseId = leaseId,
                Releasable = releasable,
                HeldForSeconds = heldForSeconds,
                Project = project,
            },
        };
    }

    static SummaryResult Load()
    {
        var path = Path.Combine(FixtureDir(), "summary_result.json");
        var json = File.ReadAllText(path);

        // No custom JsonSerializerOptions, deliberately: ControlClient.SendAsync calls
        // JsonSerializer.Deserialize with the defaults too, so the DTO attributes and the
        // UnknownFallbackConverter are doing all the work in both places. Passing options here that
        // the client does not use would make this fixture decode things the real client cannot.
        return JsonSerializer.Deserialize<SummaryResult>(json)
               ?? throw new InvalidOperationException($"{path} decoded to null");
    }

    static string FixtureDir() =>
        Path.Combine(TestRepo.Root(), "Core", "tests", "Fixtures", "control-api");
}
