using System.Reflection;
using System.Text.Json.Serialization;

namespace Hades.Control.Client.Tests;

/// <summary>
/// Reflection conformance test proving the .NET client's duplicated wire DTOs
/// (<c>Hades.Control.Client.Dtos</c>) agree, field for field, with the server's real Control API
/// types (<c>Hades.Server.Control</c>). See <see cref="ClientCoverage"/> for the deliberate,
/// named exceptions.
/// </summary>
public class ConformanceTests
{
    [Fact]
    public void EveryServerWireTypeHasAClientTwin()
    {
        var clientTypes = ClientTypes();

        var missing = ServerWireTypes()
            .Where(t => !clientTypes.ContainsKey(t.Name))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Server wire type(s) with no client twin in Hades.Control.Client.Dtos:\n{string.Join("\n", missing)}");
    }

    [Fact]
    public void EveryWirePropertyAgreesFieldForField()
    {
        var clientTypes = ClientTypes();
        var mismatches = new List<string>();

        foreach (var serverType in ServerWireTypes())
        {
            if (!clientTypes.TryGetValue(serverType.Name, out var clientType))
            {
                // Reported by EveryServerWireTypeHasAClientTwin - not re-reported here.
                continue;
            }

            var serverProps = WireNames(serverType);
            var clientProps = WireNames(clientType);

            foreach (var name in serverProps.Keys.Except(clientProps.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                mismatches.Add($"{serverType.Name}: wire name '{name}' is on the server but missing on the client.");
            }

            foreach (var name in clientProps.Keys.Except(serverProps.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                mismatches.Add($"{serverType.Name}: wire name '{name}' is on the client but missing on the server.");
            }

            foreach (var name in serverProps.Keys.Intersect(clientProps.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                var serverProp = serverProps[name];
                var clientProp = clientProps[name];

                var serverNullable = IsNullable(serverProp);
                var clientNullable = IsNullable(clientProp);
                if (serverNullable != clientNullable)
                {
                    mismatches.Add(
                        $"{serverType.Name}.{name}: nullability differs (server={serverNullable}, client={clientNullable}).");
                }

                var serverRequired = IsRequired(serverProp);
                var clientRequired = IsRequired(clientProp);
                if (serverRequired != clientRequired)
                {
                    mismatches.Add(
                        $"{serverType.Name}.{name}: required-ness differs (server={serverRequired}, client={clientRequired}).");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"Wire property mismatches:\n{string.Join("\n", mismatches)}");
    }

    [Fact]
    public void NoServerWireTypeIsOnlyPartiallyAttributed()
    {
        var partiallyAttributed = typeof(Hades.Server.Control.SettingsResult).Assembly
            .GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "Hades.Server.Control")
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => !ClientCoverage.IsExcluded(t))
            .Where(t =>
            {
                var props = t.GetProperties();
                var attributed = props.Count(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null);
                return attributed > 0 && attributed < props.Length;
            })
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(partiallyAttributed.Count == 0,
            $"Type(s) with SOME but not ALL properties [JsonPropertyName]-attributed - IsWireType would mis-walk these:\n{string.Join("\n", partiallyAttributed)}");
    }

    [Fact]
    public void TheWalkIsNotVacuous()
    {
        var count = ServerWireTypes().Count();
        Assert.True(count >= 25, $"Expected at least 25 server wire types; found {count}. ServerWireTypes() may be broken.");
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>A server type participates only if at least one property carries
    /// [JsonPropertyName]. See the class doc comment for why this rule, and not a hand-maintained
    /// list, is what excludes the six non-wire records.</summary>
    static bool IsWireType(Type t) =>
        t.GetProperties().Any(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null);

    static IEnumerable<Type> ServerWireTypes() =>
        typeof(Hades.Server.Control.SettingsResult).Assembly
            .GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "Hades.Server.Control")
            .Where(t => t.IsClass && !t.IsAbstract)   // records are classes; this also drops enums
            .Where(IsWireType)
            .Where(t => !ClientCoverage.IsExcluded(t));

    static Dictionary<string, Type> ClientTypes() =>
        typeof(Hades.Control.Client.Dtos.SettingsResult).Assembly
            .GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "Hades.Control.Client.Dtos")
            .ToDictionary(t => t.Name);

    static Dictionary<string, PropertyInfo> WireNames(Type t) =>
        t.GetProperties()
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name);

    static bool IsNullable(PropertyInfo p) =>
        new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable;

    static bool IsRequired(PropertyInfo p) =>
        p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null;
}
