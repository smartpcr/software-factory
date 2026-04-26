// -----------------------------------------------------------------------
// <copyright file="ForgeContracts.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Contracts;

/// <summary>
/// Epic — top-level grouping mirrored from <c>docs/examples/forge-board.html</c>.
/// </summary>
public sealed record Epic(
    string Id,
    string Name,
    string Status,
    int Progress,
    IReadOnlyList<string> Crew,
    double Cost,
    string Color);

/// <summary>
/// Agent reference attached to a card (architect / engineer / reviewer / human).
/// </summary>
public sealed record Agent(
    string Id,
    string Role,
    string Name);

/// <summary>
/// Card on the plan-and-design board. Mirrors the prototype's <c>cards</c> array.
/// </summary>
public sealed record Card(
    string Id,
    string Epic,
    string Col,
    string Type,
    string Title,
    IReadOnlyList<string> Tags,
    int Pts,
    string Priority,
    Agent? Agent,
    double Cost,
    string? AutoNext = null,
    bool Live = false,
    int? Progress = null,
    string? Branch = null,
    bool HumanGate = false,
    string? GateReason = null,
    bool Blocked = false,
    string? BlockedReason = null,
    string? DoneAt = null);

/// <summary>
/// Board column descriptor.
/// </summary>
public sealed record Column(
    string Id,
    string Name,
    int? WipLimit);

/// <summary>
/// Workstream — branch + worktree mapping per parallel feature thread.
/// </summary>
public sealed record Workstream(
    string Id,
    string Name,
    string Branch,
    string Base,
    string Epic,
    string Status,
    IReadOnlyList<string> Worktree,
    double Created,
    double LastActive,
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> Prs,
    IReadOnlyList<string> Cards,
    int Commits,
    int Ahead,
    int Behind,
    int Conflicts,
    string Summary,
    double BarStart,
    double BarEnd,
    bool Planned = false);

/// <summary>
/// Workstream timeline event (PR / merge / review / block marker).
/// </summary>
public sealed record WsEvent(
    string Ws,
    double Day,
    string Kind,
    string Label);

/// <summary>
/// Build status for a PR.
/// </summary>
public sealed record BuildStatus(
    string Status,
    string Step,
    int Percent,
    string? FailingTest = null);

/// <summary>
/// Implementation status for an in-flight PR (matches prototype's <c>impl</c> object).
/// </summary>
public sealed record ImplStatus(
    string Phase,
    string Step,
    int Percent);

/// <summary>
/// Review verdict for a PR.
/// </summary>
public sealed record ReviewVerdict(
    string Agent,
    string Name,
    string Verdict,
    string Summary);

/// <summary>
/// Pull request shown on the PR view.
/// </summary>
public sealed record Pr(
    string Id,
    string Col,
    string Epic,
    string EpicColor,
    string Workstream,
    string Base,
    string Branch,
    string Title,
    Agent Author,
    int Additions,
    int Deletions,
    int Files,
    int Commits,
    string Age,
    BuildStatus? Build,
    ReviewVerdict? Review,
    bool Conflicts,
    bool HumanGate,
    string? GateReason = null,
    string? ConflictPaths = null,
    bool AutoMerge = false,
    bool Blocked = false,
    ImplStatus? Impl = null);

/// <summary>
/// Stat tile shown at the top of the workstreams view.
/// </summary>
public sealed record WsStat(
    string Label,
    string Val,
    string Unit,
    string Trend,
    string TrendVal);

/// <summary>
/// Activity-feed line on the overview view.
/// </summary>
public sealed record OverviewActivity(
    string Time,
    string Who,
    string Msg);

/// <summary>
/// Agent-roster row on the overview view.
/// </summary>
public sealed record OverviewAgent(
    string Id,
    string Name,
    string Role,
    string Status,
    string Task);

/// <summary>
/// Aggregated payload returned by GET /api/state.
/// </summary>
public sealed record ForgeState(
    IReadOnlyList<Epic> Epics,
    IReadOnlyList<Card> Cards,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<Workstream> Workstreams,
    IReadOnlyList<WsEvent> WsEvents,
    IReadOnlyList<Pr> Prs,
    IReadOnlyList<WsStat> WsStats,
    IReadOnlyList<OverviewActivity> OverviewActivity,
    IReadOnlyList<OverviewAgent> OverviewAgents);
