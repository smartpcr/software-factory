using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeIntelligence.GraphMemory.Serialization;

/// <summary>
/// A JSON converter that deserializes unknown enum string values to
/// <c>(T)(object)0</c> (conventionally <c>Unknown</c>) instead of throwing.
/// This enables forward-compatible deserialization: consumers on an older
/// schema version can safely read enum values added in newer minor versions.
///
/// <para><b>Important:</b> this is <em>tolerant deserialization</em>, not
/// lossless round-trip. An unrecognized value such as <c>"futureKind"</c>
/// deserializes to <c>Unknown</c> and re-serializes as <c>"unknown"</c>,
/// losing the original token.</para>
/// </summary>
internal sealed class LenientEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private readonly JsonNamingPolicy _namingPolicy = JsonNamingPolicy.CamelCase;

    // Pre-compute mappings at construction time for performance.
    private readonly Dictionary<string, T> _readMap;
    private readonly Dictionary<T, string> _writeMap;

    public LenientEnumConverter()
    {
        var values = Enum.GetValues<T>();
        _readMap = new Dictionary<string, T>(values.Length, StringComparer.OrdinalIgnoreCase);
        _writeMap = new Dictionary<T, string>(values.Length);

        foreach (var value in values)
        {
            var name = _namingPolicy.ConvertName(value.ToString());
            _readMap[name] = value;
            _writeMap[value] = name;
        }
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected string token for enum {typeof(T).Name}, got {reader.TokenType}.");
        }

        var text = reader.GetString()!;
        if (_readMap.TryGetValue(text, out var result))
        {
            return result;
        }

        // Unknown value → fall back to default(T), which is 0 = Unknown.
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (_writeMap.TryGetValue(value, out var name))
        {
            writer.WriteStringValue(name);
        }
        else
        {
            writer.WriteStringValue(_namingPolicy.ConvertName(value.ToString()));
        }
    }
}
