// hades - a small console client over the control API (Plan 11 Task 7). NOT a product deliverable:
// its purpose is diagnostic - proof that the API is complete and usable without a UI. See Commands.cs
// for the actual command bodies and the "deliberately dumb" rule they follow; this file is only
// discovery (find the port/token exactly as the Swift shell will - never hardcoded, see
// Hades.Control.Client.Discovery) and argument dispatch.
using Hades.Cli;
using Hades.Control.Client;

// The application-data root, resolved without Hades.Core: HADES_HOME wins, else the same
// per-platform default AppPaths.DefaultRoot() computes - LocalApplicationData on Windows
// (machine-local: the databases under it are derived, and the tokens name ports meaningless on
// another machine), ApplicationData elsewhere. Kept in step with the core by the conformance and
// fixture suites, not by a shared reference.
var root = Environment.GetEnvironmentVariable("HADES_HOME")
           ?? Path.Combine(
               Environment.GetFolderPath(OperatingSystem.IsWindows()
                   ? Environment.SpecialFolder.LocalApplicationData
                   : Environment.SpecialFolder.ApplicationData),
               "Hades");

var connection = Discovery.Read(root);

if (connection is null)
{
    Console.Error.WriteLine($"No Hades control API found at {Path.Combine(root, "control.token")} — is Hades running?");
    return 1;
}

var client = new ControlClient(connection);

return await DispatchAsync(args, client);

static async Task<int> DispatchAsync(string[] args, ControlClient client)
{
    if (args.Length == 0) return Usage();

    return args[0] switch
    {
        "status" => await Commands.StatusAsync(client, Console.Out),
        "projects" => await Commands.ProjectsAsync(client, Console.Out),
        "release" when args.Length == 2 => await Commands.ReleaseAsync(client, Console.Out, args[1]),
        "release" => Usage("release requires exactly one argument: hades release <leaseId>"),
        _ => Usage($"Unknown command '{args[0]}'."),
    };
}

static int Usage(string? message = null)
{
    if (message is not null) Console.Error.WriteLine(message);

    Console.Error.WriteLine("""
        Usage: hades <command> [args]

        Commands:
          status              Menu bar summary: icon state, headline, per-project rows, held lease
          projects            Known projects: state, index freshness, node/edge counts, warnings
          release <leaseId>   Force-release a held reload lease (the leaseId reported by 'status')
        """);

    return 1;
}
