using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hades.Control.Client.Tests;

/// <summary>
/// Emits one exemplar of every wire DTO, serialized through the SAME JsonSerializerOptions
/// ControlListener configures, into Core/tests/Fixtures/control-api/.
///
/// Generated on every run rather than captured once by hand: the existing Swift corpus
/// (Mac/HadesControl/Tests/HadesControlTests/Fixtures, 50 files) was produced by a documented
/// manual procedure, so a DTO change could leave a stale fixture passing. Generation makes that
/// impossible. A later task repoints the Swift tests at this same corpus so both clients decode
/// identical bytes.
/// </summary>
public class FixtureGenerationTests
{
    /// <summary>Exactly what ControlListener configures - see ControlListener.cs's
    /// ConfigureHttpJsonOptions call. If that changes, this must change with it, or the fixtures
    /// stop representing the wire.</summary>
    static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The repository root, walked up from the test assembly's own location rather than hardcoded
    /// to one developer's checkout path - same reasoning and anchor directories as
    /// Hades.Core.Tests's own PluginInstallerTests.RepositoryRoot().
    /// </summary>
    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "UnityPlugin"))
                    && Directory.Exists(Path.Combine(directory.FullName, "Core"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   $"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }

    static string FixtureDir() =>
        Path.Combine(RepositoryRoot(), "Core", "tests", "Fixtures", "control-api");

    /// <summary>
    /// Writes one <c>&lt;name&gt;.json</c> per exemplar into <see cref="FixtureDir"/>, deleting
    /// whatever was there first. Called at the start of every <c>[Fact]</c> in this class, not
    /// just <see cref="GenerateTheCorpus"/> - xUnit does not guarantee method order within a
    /// class, and the other two facts read the files this writes, so each must be able to
    /// regenerate a fresh corpus on its own rather than depend on file state some other test left
    /// behind (or on a corpus that was never generated at all, e.g. a fresh checkout).
    /// </summary>
    static void GenerateAll()
    {
        var dir = FixtureDir();

        // Regenerated from scratch every run: a renamed or removed exemplar must not leave a
        // stale fixture file behind for a later test to accidentally pass against.
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                File.Delete(file);
            }
        }
        else
        {
            Directory.CreateDirectory(dir);
        }

        foreach (var (name, value) in Exemplars.All())
        {
            var json = JsonSerializer.Serialize(value, value.GetType(), WireOptions);
            File.WriteAllText(Path.Combine(dir, $"{name}.json"), json);
        }
    }

    [Fact]
    public void GenerateTheCorpus() => GenerateAll();

    [Fact]
    public void EveryFixtureDecodesIntoItsClientType()
    {
        GenerateAll();

        var clientTypes = typeof(Hades.Control.Client.Dtos.SettingsResult).Assembly
            .GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "Hades.Control.Client.Dtos")
            .ToDictionary(t => t.Name);

        var dir = FixtureDir();

        foreach (var (name, value) in Exemplars.All())
        {
            var serverType = value.GetType();
            if (ClientCoverage.IsExcluded(serverType))
            {
                continue;
            }

            Assert.True(clientTypes.TryGetValue(serverType.Name, out var clientType),
                $"No client twin '{serverType.Name}' in Hades.Control.Client.Dtos for fixture '{name}.json'.");

            var path = Path.Combine(dir, $"{name}.json");
            Assert.True(File.Exists(path), $"Missing fixture file: {path}");

            var json = File.ReadAllText(path);
            var decoded = JsonSerializer.Deserialize(json, clientType!);

            Assert.NotNull(decoded);
        }
    }

    [Fact]
    public void NullablePropertiesAreAbsentNotNull()
    {
        GenerateAll();

        var dir = FixtureDir();

        foreach (var (name, _) in Exemplars.All())
        {
            var path = Path.Combine(dir, $"{name}.json");
            var json = File.ReadAllText(path);

            Assert.False(json.Contains(": null", StringComparison.Ordinal),
                $"{name}.json contains a null-valued property - WireOptions does not match " +
                "ControlListener's ConfigureHttpJsonOptions (DefaultIgnoreCondition.WhenWritingNull " +
                "should make a null field ABSENT, never present as null).");
        }
    }
}
