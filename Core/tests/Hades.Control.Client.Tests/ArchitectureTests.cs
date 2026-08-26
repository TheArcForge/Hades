using System.Reflection;

namespace Hades.Control.Client.Tests;

/// <summary>
/// Layer 2 of the client/core boundary guard. Layer 1 (the EnsureShellIsAClient target on
/// Windows/Directory.Build.props and Hades.Cli.csproj) reads project-file metadata: an XML
/// &lt;ProjectReference&gt; or &lt;Reference&gt; item on the project that carries the guard. It
/// cannot see anything that never takes that shape - a renamed project, a &lt;Reference
/// HintPath="..."&gt; to a prebuilt DLL declared on some OTHER project in the chain (unlike
/// ProjectReference, that kind of item is not inherited into a consumer's own item list), or a
/// dependency that only becomes real the moment code is written to use it. This test instead loads
/// the BUILT assembly and walks the actual AssemblyRef closure recorded in its compiled metadata -
/// what the assembly truly depends on to run, independent of how the source project files describe
/// it - so it catches a real Hades.Core/Hades.Server/SQLite dependency no matter which project in
/// the chain introduced it or under what name.
///
/// Uses MetadataLoadContext, never Assembly.Load: this suite runs on the macOS CI leg, and once a
/// net10.0-windows Hades.Shell.dll joins the assemblies under test, loading it for EXECUTION would
/// drag in WPF and fail for entirely the wrong reason. MetadataLoadContext reads PE/metadata tables
/// only - it never runs a module initializer, a static constructor, or JITs a single method.
/// </summary>
public class ArchitectureTests
{
    // Assembly FILE names, not project names: Hades.Cli.csproj sets <AssemblyName>hades</AssemblyName>,
    // so the CLI's build output is hades.dll, not Hades.Cli.dll.
    private static readonly string[] ForbiddenAssemblies =
    {
        "Hades.Core",
        "Hades.Server",
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw.core",
    };

    [Theory]
    [InlineData("Hades.Control.Client.dll")]
    [InlineData("hades.dll")]
    public void ClientAssembly_DoesNotDependOnCoreOrSqlite(string assemblyFileName)
    {
        string baseDir = AppContext.BaseDirectory;
        string assemblyPath = Path.Combine(baseDir, assemblyFileName);

        Assert.True(
            File.Exists(assemblyPath),
            $"Expected a built client assembly at '{assemblyPath}' but it was not there. " +
            "This test cannot verify what it cannot find - check the ProjectReferences on " +
            "Hades.Control.Client.Tests.csproj (it must pull in whatever project produces " +
            $"'{assemblyFileName}' so the build copies it next to the test binaries).");

        // Standard MetadataLoadContext setup (per Microsoft's own docs): seed the resolver with
        // every assembly the currently-running test host trusts, so it can satisfy the core
        // assembly and any BCL types referenced in metadata, without ever loading them for
        // execution. Local build output is layered on top so the resolver prefers OUR copies of
        // OUR own assemblies over anything same-named on the trusted list.
        var runtimeAssemblyPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);
        var localAssemblyPaths = Directory.GetFiles(baseDir, "*.dll");

        var resolverPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in runtimeAssemblyPaths)
        {
            resolverPaths[Path.GetFileNameWithoutExtension(path)] = path;
        }
        var localPathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in localAssemblyPaths)
        {
            resolverPaths[Path.GetFileNameWithoutExtension(path)] = path;
            localPathsByName[Path.GetFileNameWithoutExtension(path)] = path;
        }

        using var mlc = new MetadataLoadContext(
            new PathAssemblyResolver(resolverPaths.Values),
            coreAssemblyName: "System.Private.CoreLib");

        // Recursion is deliberately restricted to LOCAL build output (localPathsByName), not the
        // full trusted-platform list: that is where a genuine Hades.Core/Hades.Server/SQLite
        // dependency would show up if this assembly (or anything it references, transitively)
        // pulled one in, and it keeps the walk to a handful of project/package assemblies instead
        // of the entire BCL graph.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();
        WalkLocalReferenceClosure(mlc, assemblyPath, localPathsByName, visited, offenders);

        Assert.True(
            offenders.Count == 0,
            $"{assemblyFileName} depends (directly or transitively) on forbidden assembly/assemblies: " +
            $"{string.Join(", ", offenders)}. Clients reach the core only through its HTTP control " +
            "API - see the module doc comment on this class.");
    }

    private static void WalkLocalReferenceClosure(
        MetadataLoadContext mlc,
        string assemblyPath,
        IReadOnlyDictionary<string, string> localPathsByName,
        HashSet<string> visited,
        List<string> offenders)
    {
        string simpleName = Path.GetFileNameWithoutExtension(assemblyPath);
        if (!visited.Add(simpleName))
        {
            return;
        }

        Assembly assembly = mlc.LoadFromAssemblyPath(assemblyPath);

        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name is null)
            {
                continue;
            }

            if (ForbiddenAssemblies.Contains(reference.Name, StringComparer.OrdinalIgnoreCase))
            {
                offenders.Add(reference.Name);
            }

            if (localPathsByName.TryGetValue(reference.Name, out var referencePath))
            {
                WalkLocalReferenceClosure(mlc, referencePath, localPathsByName, visited, offenders);
            }
        }
    }
}
