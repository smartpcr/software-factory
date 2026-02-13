// -----------------------------------------------------------------------
// <copyright file="IncidentMetadata.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat.Helpers;

using System;

/// <summary>
/// Holds incident metadata parsed from metadata.md.
/// </summary>
public readonly struct IncidentMetadata
{
    /// <summary>
    /// Gets the incident ID.
    /// </summary>
    public string IncidentId { get; init; }

    /// <summary>
    /// Gets the incident status (e.g., Active, Mitigated, Resolved).
    /// </summary>
    public string Status { get; init; }

    /// <summary>
    /// Gets the service name (e.g., Azure Stack HCI).
    /// </summary>
    public string Service { get; init; }

    /// <summary>
    /// Gets the owning team name.
    /// </summary>
    public string Team { get; init; }

    /// <summary>
    /// Gets the incident owner.
    /// </summary>
    public string Owner { get; init; }

    /// <summary>
    /// Gets the incident title.
    /// </summary>
    public string Title { get; init; }

    /// <summary>
    /// Gets the AEO device ARM resource URI, if available.
    /// </summary>
    public string AEODeviceARMResourceUri { get; init; }

    /// <summary>
    /// Gets the AEO region, if available.
    /// </summary>
    public string AEORegion { get; init; }

    /// <summary>
    /// Gets the impact start time.
    /// </summary>
    public DateTimeOffset? ImpactStartTime { get; init; }

    /// <summary>
    /// Gets the time range end.
    /// </summary>
    public DateTimeOffset? TimeRangeEnd { get; init; }

    public bool IsHubService()
    {
        return !string.IsNullOrEmpty(this.Service) &&
               (this.Service.Equals("AzureStackIcm", StringComparison.OrdinalIgnoreCase) ||
                this.Service.Contains("AzureStack", StringComparison.OrdinalIgnoreCase));
    }
}