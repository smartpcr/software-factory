// -----------------------------------------------------------------------
// <copyright file="AnnotationKeyRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Schema;

using System.Collections.Concurrent;
using Ast.Memory.Models;

/// <summary>
/// Code-defined registry of well-known <c>Metadata</c> dictionary keys
/// with typed schemas. Each built-in key is mapped to a specific CLR type
/// (not <c>object</c>). Keys without an <c>ext:</c> prefix that are not
/// registered produce a validation warning. Registered keys whose runtime
/// value type is incompatible with the declared schema type also produce a
/// validation warning.
/// Per architecture.md §4.5, HC11.
/// </summary>
public sealed class AnnotationKeyRegistry
{
    private readonly ConcurrentDictionary<string, AnnotationKeyDescriptor> registeredKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance, populating all well-known keys from
    /// <see cref="MemoryMetadataKeys"/> with their concrete value types.
    /// </summary>
    public AnnotationKeyRegistry()
    {
        // String-typed keys
        this.RegisterBuiltIn(MemoryMetadataKeys.MemoryLayer, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.SemanticRelation, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.ContentHash, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.SinceCommit, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.SinceIndex, typeof(int));
        this.RegisterBuiltIn(MemoryMetadataKeys.UntilCommit, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.UntilIndex, typeof(int));
        this.RegisterBuiltIn(MemoryMetadataKeys.Summary, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.OriginalEdgeType, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.QualifiedName, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.EmbeddingModel, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.EmbeddingStatus, typeof(string));
        this.RegisterBuiltIn(MemoryMetadataKeys.Package, typeof(string));

        // Numeric keys
        this.RegisterBuiltIn(MemoryMetadataKeys.MemberCount, typeof(int));
        this.RegisterBuiltIn(MemoryMetadataKeys.SimilarityScore, typeof(double));
        this.RegisterBuiltIn(MemoryMetadataKeys.Confidence, typeof(double));

        // Boolean keys
        this.RegisterBuiltIn(MemoryMetadataKeys.Stale, typeof(bool));

        // DateTime keys
        this.RegisterBuiltIn(MemoryMetadataKeys.LastTouched, typeof(DateTimeOffset));

        // Array keys
        this.RegisterBuiltIn(MemoryMetadataKeys.Embedding, typeof(float[]));
    }

    /// <summary>
    /// Registers a custom well-known key with an expected value type.
    /// </summary>
    public void Register(string key, Type? valueType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        this.registeredKeys[key] = new AnnotationKeyDescriptor(key, valueType ?? typeof(object), BuiltIn: false);
    }

    /// <summary>
    /// Returns true if the key is registered (built-in or custom).
    /// </summary>
    public bool IsRegistered(string key)
    {
        return this.registeredKeys.ContainsKey(key);
    }

    /// <summary>
    /// Returns the descriptor for a registered key, or null if not registered.
    /// </summary>
    public AnnotationKeyDescriptor? GetDescriptor(string key)
    {
        return this.registeredKeys.TryGetValue(key, out var descriptor) ? descriptor : null;
    }

    /// <summary>
    /// Returns all registered key descriptors.
    /// </summary>
    public IReadOnlyCollection<AnnotationKeyDescriptor> GetRegisteredKeys()
    {
        return this.registeredKeys.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Validates a metadata dictionary. Extension keys (<c>ext:</c> prefix) are
    /// always accepted. Unregistered keys without the prefix produce a validation
    /// warning. Registered keys whose runtime value is incompatible with the
    /// declared schema type also produce a validation warning.
    /// </summary>
    public AnnotationValidationResult Validate(IDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var warnings = new List<string>();
        foreach (var kvp in metadata)
        {
            if (kvp.Key.StartsWith("ext:", StringComparison.Ordinal))
            {
                continue;
            }

            if (!this.registeredKeys.TryGetValue(kvp.Key, out var descriptor))
            {
                warnings.Add($"Unknown metadata key '{kvp.Key}' is not registered. Use the 'ext:' prefix for extension keys or register via AnnotationKeyRegistry.Register().");
                continue;
            }

            // Null values are acceptable for any type
            if (kvp.Value is null)
            {
                continue;
            }

            if (!descriptor.ValueType.IsInstanceOfType(kvp.Value))
            {
                warnings.Add(
                    $"Metadata key '{kvp.Key}' expects type '{descriptor.ValueType.Name}' " +
                    $"but received '{kvp.Value.GetType().Name}'.");
            }
        }

        return new AnnotationValidationResult(warnings);
    }

    private void RegisterBuiltIn(string key, Type valueType)
    {
        this.registeredKeys[key] = new AnnotationKeyDescriptor(key, valueType, BuiltIn: true);
    }
}

/// <summary>
/// Descriptor for a registered annotation key.
/// </summary>
public record AnnotationKeyDescriptor(string Key, Type ValueType, bool BuiltIn);

/// <summary>
/// Result of metadata key validation.
/// </summary>
public record AnnotationValidationResult(IReadOnlyList<string> Warnings)
{
    /// <summary>True when no warnings were produced.</summary>
    public bool IsValid => this.Warnings.Count == 0;
}