namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// Tracks the version of the code knowledge graph schema.
/// 
/// <para><b>Versioning rules:</b></para>
/// <list type="bullet">
///   <item>Major bump → breaking: removing/renaming fields, changing enum semantics, altering ID format.</item>
///   <item>Minor bump → additive: new optional fields, new enum members, new edge/node kinds.</item>
///   <item>Consumers must tolerate unknown enum values (deserialize as <c>Unknown</c>).</item>
///   <item>Consumers must ignore unrecognized JSON properties.</item>
///   <item>Producers must not emit <c>Unknown</c> enum values.</item>
/// </list>
/// </summary>
public sealed record SchemaVersion
{
    /// <summary>Current schema major version.</summary>
    public const int CurrentMajor = 1;

    /// <summary>Current schema minor version.</summary>
    public const int CurrentMinor = 0;

    /// <summary>Major version — incremented on breaking changes.</summary>
    public required int Major { get; init; }

    /// <summary>Minor version — incremented on additive changes.</summary>
    public required int Minor { get; init; }

    /// <summary>Creates a <see cref="SchemaVersion"/> at the current library version.</summary>
    public static SchemaVersion Current => new() { Major = CurrentMajor, Minor = CurrentMinor };

    /// <summary>
    /// Returns true if <paramref name="other"/> is wire-compatible with this version.
    /// Same major + other.Minor &gt;= this.Minor ⇒ compatible.
    /// </summary>
    public bool IsCompatibleWith(SchemaVersion other)
        => other.Major == Major && other.Minor >= Minor;

    public override string ToString() => $"{Major}.{Minor}";
}
