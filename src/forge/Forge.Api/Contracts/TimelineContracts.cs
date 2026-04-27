// -----------------------------------------------------------------------
// <copyright file="TimelineContracts.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Contracts;

/// <summary>
/// Normalized timeline bar — one per workstream, clipped to the requested window.
/// </summary>
public sealed record TimelineBar(
    string WorkstreamId,
    string Name,
    string Branch,
    string Epic,
    string EpicColor,
    string Status,
    double BarStart,
    double BarEnd,
    bool Planned,
    string Summary,
    IReadOnlyList<string> Agents,
    int Ahead,
    int Behind,
    int Conflicts,
    double LastActive);

/// <summary>
/// Normalized timeline marker — one per <c>WsEvent</c> falling within the window.
/// </summary>
public sealed record TimelineMarker(
    string WorkstreamId,
    double Day,
    string Kind,
    string Label);

/// <summary>
/// Aggregated payload for <c>GET /api/timeline?from=…&amp;to=…</c>.
/// </summary>
public sealed record TimelineResponse(
    double From,
    double To,
    IReadOnlyList<TimelineBar> Bars,
    IReadOnlyList<TimelineMarker> Markers);
