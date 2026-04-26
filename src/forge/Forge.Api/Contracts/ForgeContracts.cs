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
    bool Planned = false,
    /// <summary>Phase 8.11 — repo this workstream belongs to. Null on legacy rows that pre-date Phase 8.</summary>
    string? RepoId = null);

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

// -- Phase 8 — products & repos -------------------------------------------

/// <summary>
/// Product — a top-level grouping that owns one or more <see cref="Repo"/> rows.
/// Resolved-decision Q5: a repo belongs to exactly one product (strict 1:N).
/// </summary>
public sealed record Product(
    string Id,
    string Name,
    string Slug,
    string Description,
    string? OwnerUserId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Repo — a single git repository attached to a product. A repo's provider is
/// fixed at attach time (resolved-decision Q4): ADO or GitHub, never both.
/// Sprint / iteration concepts follow that provider for this repo.
/// </summary>
public sealed record Repo(
    string Id,
    string ProductId,
    string Kind,
    string RemoteUrl,
    string DisplayName,
    string DefaultBranch,
    string CloneRoot,
    string? AdoOrg = null,
    string? AdoProject = null,
    string? GhOwner = null,
    string? GhRepo = null);

/// <summary>
/// POST /api/products body.
/// </summary>
public sealed record CreateProductRequest(
    string Id,
    string Name,
    string? Slug = null,
    string? Description = null,
    string? OwnerUserId = null);

/// <summary>
/// PATCH /api/products/{id} body.
/// </summary>
public sealed record UpdateProductRequest(
    string? Name = null,
    string? Description = null,
    string? OwnerUserId = null);

/// <summary>
/// POST /api/products/{id}/repos body.
/// </summary>
public sealed record CreateRepoRequest(
    string Id,
    string Kind,
    string RemoteUrl,
    string DisplayName,
    string CloneRoot,
    string? DefaultBranch = null,
    string? AdoOrg = null,
    string? AdoProject = null,
    string? GhOwner = null,
    string? GhRepo = null);

/// <summary>
/// One row in the server-driven repo picker (Phase 8.5). Returned by
/// <c>GET /api/providers/{kind}/repos?org=…</c> so the UI never asks the
/// operator to type a remote URL by hand.
/// </summary>
public sealed record DiscoverableRepo(
    string Kind,
    string DisplayName,
    string RemoteUrl,
    string DefaultBranch,
    string? AdoOrg = null,
    string? AdoProject = null,
    string? GhOwner = null,
    string? GhRepo = null);

// -- Phase 9 — current-user identity --------------------------------------

/// <summary>Per-provider connection state surfaced on <c>GET /api/me</c>.</summary>
public sealed record CurrentUserProvider(
    bool Connected,
    string? Account,
    DateTimeOffset? ExpiresAt,
    string? Error);

/// <summary>Aggregated identity for the operator currently using Forge.</summary>
public sealed record CurrentUser(
    string Id,
    string DisplayName,
    string? Email,
    string? Login,
    string Initials,
    string? AvatarUrl,
    string Role,
    CurrentUserProviders Providers);

public sealed record CurrentUserProviders(
    CurrentUserProvider Ado,
    [property: System.Text.Json.Serialization.JsonPropertyName("github")] CurrentUserProvider GitHub);

// -- Phase 10 — preferences + crews --------------------------------------

public sealed record UserSettings(
    string UserId,
    string? DefaultAdoOrgUrl,
    string? DefaultAdoProject,
    string? DefaultGitHubOwner,
    string? DefaultProductId,
    string? DefaultRepoId,
    string? BrowserPreference,
    string? Theme,
    bool NotificationsEnabled,
    string? PreferredAdoAccount,
    string? PreferredGitHubAccount,
    DateTimeOffset UpdatedAt);

public sealed record UpdateUserSettingsRequest(
    string? DefaultAdoOrgUrl = null,
    string? DefaultAdoProject = null,
    string? DefaultGitHubOwner = null,
    string? DefaultProductId = null,
    string? DefaultRepoId = null,
    string? BrowserPreference = null,
    string? Theme = null,
    bool? NotificationsEnabled = null,
    string? PreferredAdoAccount = null,
    string? PreferredGitHubAccount = null);

public sealed record Crew(
    string Id,
    string Name,
    string? ProductId,
    string Summary,
    string? Lead,
    string Model,
    decimal ModelInputPricePerMTok,
    decimal ModelOutputPricePerMTok,
    decimal? DailyBudgetUsd = null,
    decimal? MaxTaskBudgetUsd = null);

public sealed record UpdateRepoRequest(
    string? DisplayName = null,
    string? CloneRoot = null,
    string? DefaultBranch = null,
    string? AdoOrg = null,
    string? AdoProject = null,
    string? GhOwner = null,
    string? GhRepo = null);

/// <summary>
/// Phase 11.3 — agent listing payload.
/// </summary>
public sealed record AgentDetail(
    string Id,
    string Name,
    string Role,
    string? CrewId,
    string? SoulPath,
    IReadOnlyList<string> Capabilities,
    string State);

/// <summary>Phase 11.3 — soul.md PUT body.</summary>
public sealed record SoulWriteRequest(string Contents);

/// <summary>Phase 11.3 — soul.md GET response.</summary>
public sealed record SoulPayload(
    string AgentId,
    string Path,
    string Contents,
    IReadOnlyList<string> Capabilities,
    bool Exists);

/// <summary>Phase 11.8 — reassign body.</summary>
public sealed record ReassignAgentRequest(string CrewId);

/// <summary>Phase 11.8 — agent_history listing entry.</summary>
public sealed record AgentHistoryEntry(
    long Id,
    DateTimeOffset At,
    string AgentId,
    string? FromCrewId,
    string? ToCrewId,
    string? FromModel,
    string? ToModel,
    string? Actor);

/// <summary>Phase 11.10 — crew budget snapshot.</summary>
public sealed record CrewBudget(
    string CrewId,
    decimal? DailyBudgetUsd,
    decimal? MaxTaskBudgetUsd,
    decimal SpentTodayUsd,
    decimal? RemainingUsd);

/// <summary>
/// Phase 11.9 — crew salary roll-up. <see cref="Sprint"/> is the current ISO
/// week-bucket (Monday → Sunday) as a v0.1 simplification; real iteration
/// lookup against the linked repo's provider is a Phase R upgrade.
/// <see cref="Sparkline"/> is 14 daily totals (oldest first → today).
/// </summary>
public sealed record CrewSalary(
    string CrewId,
    decimal Today,
    decimal Sprint,
    decimal Month,
    IReadOnlyList<decimal> Sparkline);

/// <summary>Phase 11.9 — TopBar chip payload: total spend today across all crews.</summary>
public sealed record SalaryTodaySummary(decimal TotalUsd, int ActiveAgents);

/// <summary>
/// Phase 11.2 — model registry entry. Pricing is denormalised onto each
/// <see cref="Crew"/> at assignment time so historical salary lookups are
/// reproducible after a price change here.
/// </summary>
public sealed record ModelInfo(
    string Id,
    string Vendor,
    string Family,
    string DisplayName,
    int ContextWindow,
    decimal InputPricePerMTok,
    decimal OutputPricePerMTok,
    bool SupportsTools,
    bool SupportsStreaming,
    string Status);
