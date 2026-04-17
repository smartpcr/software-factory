// -----------------------------------------------------------------------
// <copyright file="GoldenSnapshotModel.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests.Golden;

/// <summary>
/// Dual-surface snapshot model for golden-file comparison.
/// Contains both canonical YAML and hydrated DTO JSON for a single fixture.
/// </summary>
public class GoldenSnapshotModel
{
    /// <summary>
    /// Gets or sets the fixture identifier.
    /// </summary>
    public string FixtureId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fixture family (task, control, composition, connections).
    /// </summary>
    public string Family { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical YAML content.
    /// </summary>
    public string CanonicalYaml { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hydrated DTO JSON content.
    /// </summary>
    public string HydratedDtoJson { get; set; } = string.Empty;
}
