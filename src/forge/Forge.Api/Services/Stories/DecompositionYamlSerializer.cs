// -----------------------------------------------------------------------
// <copyright file="DecompositionYamlSerializer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Phase 46 — round-trip helper for the architect's
/// <c>workstreams.yaml</c> decomposition artefact.
///
/// <para>The on-disk YAML uses camelCase keys to mirror the previous
/// <c>workstreams.json</c> shape field-for-field. The serializer is
/// deliberately strict on input (no implicit type coercion) and lenient
/// on output (omits null / empty collections so the file is readable
/// for the operator's pre-approval review).</para>
///
/// <para>Schema (one mapping per workstream):
/// <code>
/// - title: "string"
///   description: "string"
///   acceptanceCriteria:
///     - "string"
///   deps:
///     - "string"
///   deliverables:
///     - "string"
///   timeline: "string"
///   estimatedEffort: "string"
///   category: "impl" | "qa-e2e"   # docs/forge/e2e.md Stage 1.2,
///                                 # omitted in the YAML output when the
///                                 # value is the default ("impl") so the
///                                 # operator's pre-approval diff stays
///                                 # quiet for the common case.
/// </code>
/// </para>
/// </summary>
internal static class DecompositionYamlSerializer
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(
            DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    /// <summary>
    /// Parse a YAML sequence-of-mappings into the canonical row DTOs.
    /// Returns <c>null</c> when the input is empty or whitespace-only;
    /// throws <see cref="YamlDotNet.Core.YamlException"/> on malformed
    /// YAML (caller wraps with the workspace path for the error message).
    /// </summary>
    public static List<DecompositionYamlRow>? Deserialize(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        return Deserializer.Deserialize<List<DecompositionYamlRow>>(yaml);
    }

    /// <summary>
    /// Emit YAML for a sequence of rows. <see cref="DecompositionYamlRow"/>
    /// + the camel-case naming convention produce the exact schema the
    /// prompt teaches the architect.
    /// </summary>
    public static string Serialize<T>(IEnumerable<T> rows)
    {
        return Serializer.Serialize(rows);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 1.2 — collapse a category string to the
    /// YAML wire shape. Default ("impl") writes as <c>null</c> so the
    /// serializer's <see cref="DefaultValuesHandling.OmitNull"/> drops the
    /// key from the emitted YAML; "qa-e2e" passes through verbatim;
    /// unknown values fall back to <c>null</c> (i.e. impl) on the wire.
    /// </summary>
    public static string? CategoryToYaml(string? category) =>
        category == WorkstreamCategories.QaE2e ? WorkstreamCategories.QaE2e : null;

    /// <summary>
    /// docs/forge/e2e.md Stage 1.2 — expand a YAML category value into
    /// the canonical record default. Null / unknown / "impl" → "impl";
    /// "qa-e2e" → "qa-e2e". Mirrors <see cref="CategoryToYaml"/> so the
    /// round-trip is lossless for the closed set.
    /// </summary>
    public static string CategoryFromYaml(string? category) =>
        category == WorkstreamCategories.QaE2e ? WorkstreamCategories.QaE2e : WorkstreamCategories.Impl;
}

/// <summary>
/// Phase 46 — DTO matching the architect-emitted <c>workstreams.yaml</c>
/// row shape. Camel-case keys come from the serializer's naming convention.
/// All fields are nullable so a partially-emitted row still parses; the
/// caller's null-coalescing supplies safe defaults.
/// </summary>
internal sealed class DecompositionYamlRow
{
    public string? Title { get; set; }

    public string? Description { get; set; }

    public List<string>? AcceptanceCriteria { get; set; }

    public List<string>? Deps { get; set; }

    public List<string>? Deliverables { get; set; }

    public string? Timeline { get; set; }

    public string? EstimatedEffort { get; set; }

    /// <summary>
    /// docs/forge/e2e.md Stage 1.2 — one of <see cref="WorkstreamCategories.Impl"/>
    /// or <see cref="WorkstreamCategories.QaE2e"/>. Null on the wire when
    /// the row defaults to <see cref="WorkstreamCategories.Impl"/>;
    /// emitted as <c>category: qa-e2e</c> otherwise. Use
    /// <see cref="DecompositionYamlSerializer.CategoryToYaml"/> /
    /// <see cref="DecompositionYamlSerializer.CategoryFromYaml"/> when
    /// converting between this wire field and the canonical record value.
    /// </summary>
    public string? Category { get; set; }
}
