// hades - the supported headless path into Hades, on Windows and macOS alike.
//
// This was once a diagnostic proof that the control API needed no UI to be usable. It is now a
// product surface in its own right (Spec #5 §5.4): the second shipping consumer of
// Hades.Control.Client, alongside the WPF shell, and the way to drive Hades from a terminal, a
// script, or a machine with no desktop session at all.
//
// It is a CLIENT, held to the same boundary the shells are: it may not reference Hades.Core or
// Hades.Server, and the guard in Hades.Cli.csproj fails the build if it ever does. That is why
// `hades serve` RUNS the core as a child process rather than hosting it - see Serve.cs.
//
// The commands themselves stay deliberately dumb: print what the core decided, compute nothing,
// invent no text. See Commands.cs for that rule stated in full.
//
// This file is only discovery (find the port and token exactly as the shells do - never hardcoded)
// and argument dispatch.
using Hades.Cli;
using Hades.Control.Client;

// `serve` is handled BEFORE discovery, and must be: it exists to START a core, so requiring a
// running one first would make the command impossible to use for its only purpose.
if (args.Length > 0 && args[0] == "serve")
{
    return await Serve.RunAsync(Console.Out, args.Skip(1).ToArray());
}

// The application-data root, resolved without Hades.Core. ClientPaths is where that answer lives
// for every client - the WPF shell needs the identical rule, and a second copy of it here is how
// the two would drift. See ClientPaths.DefaultRoot for why Windows uses the machine-local folder.
var root = ClientPaths.DefaultRoot();

var connection = Discovery.Read(root);

// `diagnose` runs WITH OR WITHOUT a core, and must: "no core is running" is the most likely state
// when someone runs it in anger, and refusing there would fail exactly the person it exists for.
if (args.Length == 1 && args[0] == "diagnose")
{
    return await Commands.DiagnoseAsync(
        connection is null ? null : new ControlClient(connection), Console.Out, root);
}

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

        "add-project" when args.Length == 2 => await Commands.AddProjectAsync(client, Console.Out, args[1]),
        "add-project" => Usage("add-project requires exactly one argument: hades add-project <path>"),

        "remove-project" when args.Length == 2 => await Commands.RemoveProjectAsync(client, Console.Out, args[1]),
        "remove-project" => Usage("remove-project requires exactly one argument: hades remove-project <productGuid>"),

        "rebuild" when args.Length == 2 => await Commands.RebuildAsync(client, Console.Out, args[1]),
        "rebuild" => Usage("rebuild requires exactly one argument: hades rebuild <productGuid>"),

        "install-plugin" when args.Length == 2 => await Commands.InstallPluginAsync(client, Console.Out, args[1]),
        "install-plugin" => Usage("install-plugin requires exactly one argument: hades install-plugin <productGuid>"),

        "operation" when args.Length == 2 => await Commands.OperationAsync(client, Console.Out, args[1]),
        "operation" => Usage("operation requires exactly one argument: hades operation <operationId>"),

        // The optional project argument matters once more than one project is known: every traces
        // and memory route answers a 400 asking for it, and the server's own message is what gets
        // printed if it is omitted.
        "traces" when args.Length <= 2 => await Commands.TracesAsync(client, Console.Out, args.ElementAtOrDefault(1)),
        "memory" when args.Length <= 2 => await Commands.MemoryAsync(client, Console.Out, args.ElementAtOrDefault(1)),

        _ => Usage($"Unknown command '{args[0]}'."),
    };
}

static int Usage(string? message = null)
{
    if (message is not null) Console.Error.WriteLine(message);

    Console.Error.WriteLine("""
        Usage: hades <command> [args]

        Commands:
          serve                       Run the core in this terminal until you stop it
          diagnose                    Environment report for a bug report (works with no core running)
          status                      Summary: icon state, headline, per-project rows, held lease
          projects                    Known projects: state, index freshness, counts, warnings
          add-project <path>          Adopt a Unity project folder
          remove-project <guid>       Forget a project (nothing on disk is deleted)
          rebuild <guid>              Start a rebuild; prints the operation id to poll
          operation <id>              One operation's current state
          install-plugin <guid>       Install the Unity plugin into a project
          traces [project]            Sequences, failures and slow tools
          memory [project]            Authored documents and the proposal queue
          release <leaseId>           Force-release a held reload lease (the id 'status' reports)
        """);

    return 1;
}
