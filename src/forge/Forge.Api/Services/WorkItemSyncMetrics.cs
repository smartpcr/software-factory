// -----------------------------------------------------------------------
// <copyright file="WorkItemSyncMetrics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;

/// <summary>
/// Phase 23.11 — telemetry surface for the work-item sync job. Re-uses the
/// existing <see cref="LeadAutoDispatcher.Meter"/> ("Forge.Api.Phase16") so
/// dashboards built off Phase 16 don't need to swap meters; that's the
/// stated rationale in the spec ("re-used; adding a new meter would
/// fragment dashboards").
/// </summary>
public static class WorkItemSyncMetrics
{
    private static readonly Meter Meter = LeadAutoDispatcher.Meter;

    /// <summary>Histogram — duration of one full sync run, ms. Tags: <c>provider</c>, <c>kind</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<double> DurationMs =
        Meter.CreateHistogram<double>("forge.sync.workitem.duration_ms", unit: "ms");

    /// <summary>Counter — items affected. Tags: <c>provider</c>, <c>kind</c> ∈ <c>added | updated | conflicted</c>.</summary>
    public static readonly Counter<long> Items =
        Meter.CreateCounter<long>("forge.sync.workitem.items");

    /// <summary>Counter — number of pagination batches walked. Tag: <c>provider</c>.</summary>
    public static readonly Counter<long> PaginationBatches =
        Meter.CreateCounter<long>("forge.sync.workitem.pagination.batches");

    /// <summary>Counter — failed sync runs. Tags: <c>provider</c>, <c>errorClass</c> ∈ <c>rate-limit | auth | network | shape</c>.</summary>
    public static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("forge.sync.workitem.errors");

    /// <summary>Counter — agent-ready auto-team-assigns fired by sync. Tag: <c>team</c>.</summary>
    public static readonly Counter<long> AgentReadyAssignments =
        Meter.CreateCounter<long>("forge.sync.workitem.agent_ready_assignments");

    /// <summary>
    /// Phase 23.11 — bucket exception types into the four error classes the
    /// counter tags. Network / shape stays the default for anything that
    /// isn't an obvious 401/403 (auth) or 429/503 (rate-limit).
    /// </summary>
    public static string ClassifyError(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            var sc = (int?)http.StatusCode;
            return sc switch
            {
                401 or 403 => "auth",
                429 or 503 => "rate-limit",
                _ => "network",
            };
        }
        if (ex is System.Text.Json.JsonException || ex is FormatException) return "shape";
        return "network";
    }
}
