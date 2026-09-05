using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hades.Control.Client;

/// <summary>
/// Decodes a closed string enum, mapping any unrecognised value to the enum's <c>Unknown</c>
/// member instead of throwing - the .NET equivalent of Swift's <c>ControlEnum.unknownFallback</c>
/// (see Mac/HadesControl/Sources/HadesControl/DTOs.swift).
///
/// This matters because the app ADOPTS an already-running core rather than always spawning one,
/// so a newer core can serve an older client: a case added server-side must degrade, never crash.
/// Every control-API enum in this client uses this converter without exception, and requiring an
/// <c>Unknown</c> member (enforced in the static constructor below) is what makes "apply it
/// everywhere" a mechanical rule rather than a judgement call - the enum someone forgets to opt
/// in is exactly the one that will crash.
///
/// Like its Swift counterpart, this adds NO other behaviour: no case ever maps to display text.
/// </summary>
public sealed class UnknownFallbackConverter<T> : JsonConverter<T> where T : struct, Enum
{
    static readonly T Fallback;

    static UnknownFallbackConverter()
    {
        if (!Enum.TryParse("Unknown", out Fallback))
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} must declare an 'Unknown' member to use UnknownFallbackConverter.");
        }
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A non-string token is as much "a value this client does not understand" as an
        // unrecognised string is - degrade identically rather than throwing.
        if (reader.TokenType != JsonTokenType.String) return Fallback;

        var raw = reader.GetString();
        return raw is not null && Enum.TryParse<T>(raw, ignoreCase: true, out var value) ? value : Fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..]);
}
