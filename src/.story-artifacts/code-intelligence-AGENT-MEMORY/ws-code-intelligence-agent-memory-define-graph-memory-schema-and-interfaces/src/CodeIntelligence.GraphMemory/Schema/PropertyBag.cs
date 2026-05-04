using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// An extensible, JSON-friendly property bag backed by <see cref="JsonElement"/> values.
/// Serializes as a plain JSON object. Provides typed accessors for convenience.
///
/// <para><b>Key restrictions:</b> keys must match <see cref="KeyPattern"/>
/// (start with a letter, followed by letters, digits, underscores, or dots).
/// Dots enable namespace-style grouping (e.g. <c>neo4j.label</c>).</para>
/// </summary>
[JsonConverter(typeof(PropertyBagConverter))]
public sealed class PropertyBag : IReadOnlyDictionary<string, JsonElement>
{
    /// <summary>
    /// Regex pattern that all property bag keys must satisfy.
    /// Starts with a letter, then letters/digits/underscores/dots.
    /// </summary>
    public const string KeyPattern = @"^[a-zA-Z][a-zA-Z0-9_.]*$";

    private static readonly Regex KeyRegex = new(KeyPattern, RegexOptions.Compiled);

    private readonly Dictionary<string, JsonElement> _store;

    public PropertyBag() => _store = new(StringComparer.Ordinal);

    public PropertyBag(IEnumerable<KeyValuePair<string, JsonElement>> items)
        => _store = new Dictionary<string, JsonElement>(items, StringComparer.Ordinal);

    /// <summary>
    /// Validates that <paramref name="key"/> conforms to <see cref="KeyPattern"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Key does not match the required pattern.</exception>
    public static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || !KeyRegex.IsMatch(key))
        {
            throw new ArgumentException(
                $"Property bag key '{key}' does not match required pattern {KeyPattern}. " +
                "Keys must start with a letter and contain only letters, digits, underscores, or dots.",
                nameof(key));
        }
    }

    // ── Typed accessors ────────────────────────────────────────────

    /// <summary>Try to deserialize a stored value to <typeparamref name="T"/>.</summary>
    public bool TryGet<T>(string key, [NotNullWhen(true)] out T? value)
    {
        if (_store.TryGetValue(key, out var element))
        {
            try
            {
                value = element.Deserialize<T>()!;
                return value is not null;
            }
            catch (JsonException)
            {
                // Type mismatch — fall through.
            }
        }

        value = default;
        return false;
    }

    /// <summary>Set a value, serializing it to <see cref="JsonElement"/>.</summary>
    /// <exception cref="ArgumentException">Key does not match <see cref="KeyPattern"/>.</exception>
    public void Set<T>(string key, T value)
    {
        ValidateKey(key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(bytes);
        _store[key] = doc.RootElement.Clone();
    }

    /// <summary>Remove a key.</summary>
    public bool Remove(string key) => _store.Remove(key);

    // ── IReadOnlyDictionary ────────────────────────────────────────

    public JsonElement this[string key] => _store[key];
    public IEnumerable<string> Keys => _store.Keys;
    public IEnumerable<JsonElement> Values => _store.Values;
    public int Count => _store.Count;
    public bool ContainsKey(string key) => _store.ContainsKey(key);
    public bool TryGetValue(string key, out JsonElement value) => _store.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => _store.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Serializes <see cref="PropertyBag"/> as a flat JSON object.
/// Validates keys against <see cref="PropertyBag.KeyPattern"/> on deserialization.
/// </summary>
internal sealed class PropertyBagConverter : JsonConverter<PropertyBag>
{
    public override PropertyBag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var pairs = new List<KeyValuePair<string, JsonElement>>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            PropertyBag.ValidateKey(prop.Name);
            pairs.Add(KeyValuePair.Create(prop.Name, prop.Value.Clone()));
        }

        return new PropertyBag(pairs);
    }

    public override void Write(Utf8JsonWriter writer, PropertyBag value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kvp in value)
        {
            writer.WritePropertyName(kvp.Key);
            kvp.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
