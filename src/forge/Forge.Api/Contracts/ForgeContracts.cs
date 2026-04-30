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

public sealed record ProductStory(
    string Id,
    string ProductId,
    string Name,
    string Status,
    int Sort,
    string CreatedAt);

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
    ImplStatus? Impl = null,
    // -- Phase 24 — PR sync metadata + comment counts --
    bool? IsActive = null,
    int? OpenCommentCount = null,
    int? AttentionCommentCount = null,
    string? LastSyncAt = null,
    string? LastSyncStatus = null,
    string? Provider = null,
    string? WorkItemId = null);

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
    DateTimeOffset CreatedAt,
    // ActiveIterationId is the Phase 15.6 explicit override; null = fall back
    // to the primary repo's RepoIterationBinding with role='current' (Q15.7).
    string? ActiveIterationId = null);

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
    string? GhRepo = null,
    // Phase 23 — sync fields.
    IReadOnlyList<string>? AreaPaths = null,
    string? LastSyncAt = null,
    string? LastSyncCursor = null,
    string? LastSyncStatus = null,
    string? LastSyncError = null,
    string? IsRunningSince = null,
    string? AgentReadyDefaultTeam = null,
    int? InitialSyncWindowDays = null);

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
    string? OwnerUserId = null,
    // Phase 15.6 — null leaves it unchanged; empty string clears the override.
    string? ActiveIterationId = null);

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
    decimal? MaxTaskBudgetUsd = null,
    // Phase 13.1 additions.
    string? ParentCrewId = null,
    int Depth = 0,
    string Backend = "copilot",
    string InvokeMode = "sdk",
    IReadOnlyList<CrewMember>? Roster = null,
    IReadOnlyList<string>? Capabilities = null,
    int MaxConcurrent = 2,
    int WeeklyCap = 20,
    // Phase 13b — default pair (generator + evaluator) and per-half model.
    string? DefaultGeneratorAgentId = null,
    string? DefaultEvaluatorAgentId = null,
    string? EvaluatorModel = null,
    // Phase 16.2 — pair-crew topology. PairRole is "standalone" (default),
    // "generator", or "evaluator"; PairedWithCrewId is the symmetric peer.
    string PairRole = "standalone",
    string? PairedWithCrewId = null,
    // Phase 18 — schedule band. "day" = 09:00–21:00 local; "night" =
    // 21:00–09:00 next morning. Defaults to "day" for back-compat.
    string Shift = "day");

/// <summary>
/// Phase 13b — one row in a crew's roster. The lead is rendered separately
/// (top of the roster) but is also represented here with role=<c>lead</c>
/// when the lead is a member of the crew (lead may be set without being a
/// member, which is the "external supervisor" case).
/// </summary>
public sealed record CrewMember(
    string AgentId,
    string Role,
    DateTimeOffset AddedAt,
    string? AddedBy);

/// <summary>Phase 13.1 — append-only audit row for a single crew change.</summary>
public sealed record CrewHistoryEntry(
    long Id,
    DateTimeOffset At,
    string CrewId,
    string Kind,
    string? FromValue,
    string? ToValue,
    string? ByUserId);

/// <summary>Phase 13.7 — domain-expert artifact summary surfaced to the capability picker.</summary>
public sealed record DomainExpert(
    string ContentId,
    string RepoId,
    string Path,
    string Topic,
    string Summary,
    DateTimeOffset IndexedAt);

/// <summary>
/// Phase 13.3 / 13b — assign body. Tasks run as a <strong>pair</strong>:
/// <see cref="GeneratorAgentId"/> writes the spec / code, <see cref="EvaluatorAgentId"/>
/// reviews. When <c>EvaluatorAgentId</c> is null the crew's
/// <c>DefaultEvaluatorAgentId</c> applies; same fallback for the generator.
/// <c>MemberAgentId</c> is kept as a back-compat alias for callers that
/// don't yet know about pairs — when set with no Generator/Evaluator, it
/// becomes the generator.
/// </summary>
public sealed record AssignTaskRequest(
    string WorkItemId,
    string? MemberAgentId = null,
    string? GeneratorAgentId = null,
    string? EvaluatorAgentId = null,
    bool Force = false);

/// <summary>Phase 13.8 — capability ingestion / removal body.</summary>
public sealed record CapabilityRequest(string ContentId);

public sealed record UpdateRepoRequest(
    string? DisplayName = null,
    string? CloneRoot = null,
    string? DefaultBranch = null,
    string? AdoOrg = null,
    string? AdoProject = null,
    string? GhOwner = null,
    string? GhRepo = null,
    // Phase 23 — scheduled sync configuration.
    IReadOnlyList<string>? AreaPaths = null,
    string? AgentReadyDefaultTeam = null);

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
    string State,
    // Phase 13e — per-agent runtime overrides (null = inherit crew).
    string? Backend = null,
    string? InvokeMode = null,
    string? GeneratorModel = null,
    string? EvaluatorModel = null);

/// <summary>
/// Phase 13b/e — PATCH body for /api/agents/{id}. Empty-string on any
/// override field clears it (reverts to crew inheritance); a non-empty
/// value pins this agent to that override.
/// </summary>
public sealed record PatchAgentRequest(
    string? Name = null,
    string? Role = null,
    string? SoulPath = null,
    string? Backend = null,
    string? InvokeMode = null,
    string? GeneratorModel = null,
    string? EvaluatorModel = null);

/// <summary>Phase 13b — POST /api/crews/auto-assign result.</summary>
public sealed record AutoAssignResult(
    int Inspected,
    int Added,
    IReadOnlyList<AutoAssignAction> Actions);

public sealed record AutoAssignAction(
    string AgentId,
    string CrewId,
    string Role);

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
/// Phase 14.11 — TopBar payroll chip aggregator. Replaces the client-side
/// fan-out (one /payroll request per crew) with a single server-side SUM.
/// </summary>
public sealed record PayrollTodaySummary(decimal TotalUsd, int CrewCount);

// -- Phase 12 — Work Item Tracking ---------------------------------------

/// <summary>
/// Phase 12 — assignee on a <see cref="WorkItem"/>. <see cref="Kind"/> is
/// "agent", "human", or (Phase 17.2) "team" when the work item is routed
/// through the team → lead → crew → member pipeline rather than pinned to
/// a specific agent up-front. <see cref="Id"/> carries the team name (e.g.
/// "Architecture") in the team case, the agent / user id otherwise.
/// </summary>
public sealed record WorkItemAssignee(
    string Kind,
    string Id,
    string DisplayName,
    string Initials);

/// <summary>
/// Phase 22 — runtime-assigned agent(s). Independent of
/// <see cref="WorkItemAssignee"/> which carries the WIT-level owner.
/// </summary>
public sealed record AssignedAgent(
    IReadOnlyList<string> Agents,
    string? Team,
    string? CrewId,
    string Role,
    string AssignedAt,
    string? AssignedBy);

/// <summary>
/// Phase 22 — a single row from the <c>agent_assignments</c> history table.
/// </summary>
public sealed record AgentAssignmentEntry(
    long Id,
    string WorkItemId,
    string Kind,
    IReadOnlyList<string> AgentIds,
    string? Team,
    string? CrewId,
    string? Role,
    string? FromExecutionStatus,
    string? ToExecutionStatus,
    string? AssignedBy,
    string? Reason,
    DateTimeOffset At);

/// <summary>
/// Phase 12.1 — work item record. Two parallel three-level ladders are
/// distinguished by <see cref="Track"/> ("dev" or "design"). See Phase 12
/// Q12.1 for the type / state matrix and parent-rank invariants.
/// </summary>
public sealed record WorkItem(
    string Id,
    string? RepoId,
    string Provider,
    int? ProviderRef,
    string Track,
    string Type,
    string State,
    string Title,
    string DescriptionMd,
    string? ParentId,
    string? FeatureRef,
    string IterationRef,
    string AreaPath,
    WorkItemAssignee? AssignedTo,
    int Points,
    string Priority,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? RemoteRevision,
    // Phase 16.1 — set when LeadAutoDispatcher picks the row up.
    DateTimeOffset? AutoDispatchedAt = null,
    // Phase 16.5 — for step rows: planning-time file-budget.
    int? ExpectedFileChanges = null,
    // Phase 19 — execution status and trial count.
    string ExecutionStatus = "idle",
    int TrialCount = 0,
    // Phase 22 — runtime-assigned agent(s).
    AssignedAgent? AssignedAgent = null,
    // Phase 25 — primary linked PR id and phase (implementation/review/build/merged).
    string? PrimaryPrId = null,
    string? PrimaryPrStatus = null);

public sealed record CreateWorkItemRequest(
    string Id,
    string Track,
    string Type,
    string Title,
    string? RepoId = null,
    string? Provider = null,
    int? ProviderRef = null,
    string? State = null,
    string? DescriptionMd = null,
    string? ParentId = null,
    string? FeatureRef = null,
    string? IterationRef = null,
    string? AreaPath = null,
    WorkItemAssignee? AssignedTo = null,
    int? Points = null,
    string? Priority = null,
    IReadOnlyList<string>? Tags = null,
    // Phase 16.5 — required for step rows; null for non-step rows.
    int? ExpectedFileChanges = null);

public sealed record PatchWorkItemRequest(
    string? Title = null,
    string? DescriptionMd = null,
    string? State = null,
    string? IterationRef = null,
    string? AreaPath = null,
    WorkItemAssignee? AssignedTo = null,
    bool? ClearAssignedTo = null,
    int? Points = null,
    string? Priority = null,
    IReadOnlyList<string>? Tags = null,
    string? FeatureRef = null,
    bool? ClearFeatureRef = null,
    // Phase 14 follow-up — let the operator re-attach a work item to a
    // different repo from the detail rail. Empty string clears the link.
    string? RepoId = null,
    bool? ClearRepoId = null,
    // Phase 14 follow-up — type changes auto-flip Track per trackOf().
    string? Type = null,
    // Phase 16.5 — let the operator adjust the planning-time file budget on
    // step rows. Validated against PlanRubricConstants.MaxFilesPerStep.
    int? ExpectedFileChanges = null,
    bool? ClearExpectedFileChanges = null);

public sealed record MoveWorkItemRequest(
    string? State = null,
    string? IterationRef = null);

public sealed record ReparentWorkItemRequest(
    string? ParentId);

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

/// <summary>
/// Phase 14 — one row per (work-item × iteration). Score + verdict are
/// authoritative per <see cref="Forge.Api.ScoringConstants"/>; judge fields
/// are only set after a Phase 14.5 dispute.
/// </summary>
public sealed record Deliverable(
    long Id,
    string WorkItemId,
    string CrewId,
    string GeneratorAgentId,
    string EvaluatorAgentId,
    int Iter,
    int Score,
    IReadOnlyDictionary<string, int> Dims,
    string Verdict,
    string Notes,
    decimal GenCostUsd,
    decimal EvalCostUsd,
    string? JudgeAgentId,
    int? JudgeScore,
    string? JudgeNotes,
    DateTimeOffset CreatedAt);

/// <summary>
/// Phase 14.5 — dispute body. <c>JudgeScore</c> is the judge agent's
/// re-score of the deliverable; ≥ 90 flips the linked work item to
/// <c>closed</c> retroactively.
/// </summary>
public sealed record DeliverableDisputeRequest(
    string JudgeAgentId,
    int JudgeScore,
    string? JudgeNotes);

/// <summary>
/// Phase 14.8 — per-crew HR payroll snapshot. Distinct from the
/// existing token-spend rollup (Q14.7 renames the old payload to
/// <c>CrewSpend</c>); same field shape so the UI can render both
/// side-by-side.
/// </summary>
public sealed record CrewPayroll(
    string CrewId,
    decimal Today,
    decimal Sprint,
    decimal Month,
    /// <summary>14 daily totals, oldest first → today.</summary>
    IReadOnlyList<decimal> Sparkline);

/// <summary>Phase 14.8 — one row in the per-member payroll grid.</summary>
public sealed record CrewPayrollMember(
    string AgentId,
    int Level,
    decimal HourlyUsd,
    int OnShiftHoursToday,
    decimal TodayUsd);

/// <summary>
/// Phase 14.7 — outcome of a single promotion run. <c>Decisions</c> is
/// every agent visited (including no-ops + insufficient-data) so the UI
/// can render a full audit table.
/// </summary>
public sealed record PromotionRunResponse(
    int AgentsScanned,
    int Promoted,
    int Demoted,
    DateTimeOffset At,
    IReadOnlyList<PromotionDecision> Decisions);

/// <summary>Phase 14.7 — one decision row in <see cref="PromotionRunResponse"/>.</summary>
public sealed record PromotionDecision(
    string AgentId,
    int OldLevel,
    int NewLevel,
    decimal RollingAvg,
    string Reason,
    int WindowSize);

// ─────────────────────────────────────────────────────────────────────
// Phase 15 — sprint &amp; area-path management contracts. Every entity is
// pinned to a single product (Q15.1, Q15.2). Iteration / area path /
// saved-prefix rows carry <c>ProductId</c>; bindings sit on the repo
// (which itself belongs to a product per Q5).
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Phase 15.5 — one repo binding for a <see cref="ProductIteration"/>.
/// </summary>
public sealed record IterationBinding(
    string RepoId,
    string RepoName,
    // Role is "current" | "next" | "reference".
    string Role);

/// <summary>
/// Phase 15.1 — product-scoped iteration imported from a provider.
/// Bound to exactly one product (Q15.1). <see cref="ProviderNodeId"/>
/// is the stable upstream identity used to resolve renames.
/// </summary>
public sealed record ProductIteration(
    string Id,
    string ProductId,
    // ProviderKind is "ado" | "github".
    string ProviderKind,
    string ProviderOrg,
    string ProviderProject,
    string ProviderNodeId,
    string Path,
    string Name,
    DateTimeOffset? StartDate,
    DateTimeOffset? FinishDate,
    // State is "past" | "current" | "future" | "provider-orphan".
    string State,
    IReadOnlyList<IterationBinding> Bindings,
    int PointsTotal,
    int PointsDone,
    DateTimeOffset ImportedAt,
    DateTimeOffset RefreshedAt);

/// <summary>
/// Phase 15.2 — product-scoped area path imported from ADO. Tree-structured
/// via <see cref="ParentPath"/>. ADO-only at v0.1 (Q15.6). Bound to a product.
/// </summary>
public sealed record ProductAreaPath(
    string Id,
    string ProductId,
    string ProviderKind,
    string ProviderOrg,
    string ProviderProject,
    string ProviderNodeId,
    string Path,
    string? ParentPath,
    string? ParentNodeId,
    bool IsDefaultFilter,
    int WorkItemCount,
    DateTimeOffset ImportedAt);

/// <summary>
/// Phase 15.10 — saved prefix shorthand. Bound to a product so refreshing
/// it scoped-imports under the same product.
/// </summary>
public sealed record SavedPrefix(
    string Id,
    string ProductId,
    string Prefix,
    // Kind is "iterations" | "areas".
    string Kind,
    string RepoId,
    string RepoName,
    string AddedBy,
    DateTimeOffset? LastRefreshedAt,
    SavedPrefixDelta Delta);

/// <summary>
/// Phase 15.10 — diff captured by the most recent refresh of a saved prefix.
/// Drives the <c>+N</c> badge on the chip.
/// </summary>
public sealed record SavedPrefixDelta(int Added, int Removed, int Renamed);

/// <summary>
/// Phase 15.2 — flat node returned by the ADO classification discovery
/// API. Operator multi-selects from this list to feed the import endpoint.
/// </summary>
public sealed record DiscoveryNode(
    string ProviderNodeId,
    string Path,
    string Name,
    bool HasChildren,
    int Depth,
    DiscoveryNodeAttributes Attributes,
    bool AlreadyImported,
    int? WorkItemCount = null);

/// <summary>Provider-specific extras on a discovery node.</summary>
public sealed record DiscoveryNodeAttributes(
    DateTimeOffset? StartDate = null,
    DateTimeOffset? FinishDate = null,
    // TimeFrame is "past" | "current" | "future" — ADO timeFrame mirrored for iterations.
    string? TimeFrame = null);

// ── Request bodies ──────────────────────────────────────────────────

/// <summary>
/// Phase 15.2 — request body for <c>POST /api/products/{id}/sprint-discovery</c>.
/// </summary>
public sealed record SprintDiscoveryRequest(
    string RepoId,
    // Kind is "iterations" | "areas".
    string Kind,
    string? Prefix = null);

/// <summary>
/// Phase 15.3 — request body for the bulk import endpoints
/// <c>POST /api/products/{id}/iterations/import</c> and
/// <c>/api/products/{id}/area-paths/import</c>.
/// </summary>
public sealed record ImportNodesRequest(
    string RepoId,
    IReadOnlyList<string> ProviderNodeIds,
    string? SavedFromPrefix = null);

/// <summary>
/// Phase 15.5 — request body for <c>POST /api/repos/{id}/iteration-binding</c>.
/// </summary>
public sealed record IterationBindingRequest(
    string ProductIterationId,
    // Role is "current" | "next" | "reference".
    string Role);

/// <summary>Phase 15.10 — POST body for saving a new prefix shorthand.</summary>
public sealed record SavePrefixRequest(
    string Prefix,
    // Kind is "iterations" | "areas".
    string Kind,
    string RepoId);

// ─── Phase 16 — Crew execution & live observability ────────────────────────

/// <summary>
/// Phase 16.2 — POST /api/crews/{id}/pair body. Symmetric write: both rows
/// reference each other after the call returns. Refused with 400 if either
/// crew is not arch/eng, with 409 if either is already paired or if the two
/// crews don't share a product.
/// </summary>
public sealed record PairCrewRequest(string OtherCrewId);

/// <summary>
/// Phase 16.5 — one row in the live-execution transcript. The payload shape
/// depends on <see cref="Kind"/>; clients render token deltas as inline text,
/// tool-call blocks as collapsibles, and iteration boundaries as score chips.
/// </summary>
public sealed record TaskTranscriptEvent(
    long Id,
    string WorkItemId,
    string Kind,
    string? AgentId,
    string? CrewId,
    System.Text.Json.JsonElement Payload,
    DateTimeOffset At);

/// <summary>Phase 16.6 — most recent worktree snapshot for a work item.</summary>
public sealed record WorktreeSnapshot(
    string WorkstreamId,
    /// <summary>Counts by category — added / modified / deleted. Caller can fetch the path arrays from the most recent <c>task.worktree-tick</c>.</summary>
    int Added,
    int Modified,
    int Deleted,
    int Ahead,
    int Behind,
    int Dirty,
    string? HeadSha,
    string? HeadMessage,
    string? HeadAuthor,
    DateTimeOffset? HeadAt,
    bool Truncated,
    DateTimeOffset At);

/// <summary>
/// Phase 16.9 — snapshot of a work item's execution state. Returned by
/// <c>GET /api/work-items/{id}/execution</c>; the UI hydrates the Live
/// Execution view from this and then attaches a SignalR subscription on
/// <c>task.{workItemId}</c> for the live tail.
/// </summary>
public sealed record TaskExecutionSnapshot(
    string WorkItemId,
    string? GeneratorCrewId,
    string? GeneratorCrewName,
    string? EvaluatorCrewId,
    string? EvaluatorCrewName,
    string? GeneratorAgentId,
    string? GeneratorAgentName,
    string? EvaluatorAgentId,
    string? EvaluatorAgentName,
    int CurrentIteration,
    int? CurrentScore,
    string? CurrentVerdict,
    decimal GeneratorCostUsd,
    decimal EvaluatorCostUsd,
    decimal TotalCostUsd,
    long GeneratorInputTokens,
    long GeneratorOutputTokens,
    long EvaluatorInputTokens,
    long EvaluatorOutputTokens,
    decimal? CrewDailyBudgetUsd,
    decimal? CrewSpendTodayUsd,
    decimal? MaxTaskBudgetUsd,
    DateTimeOffset? AutoDispatchedAt,
    int? ExpectedFileChanges,
    IReadOnlyList<Deliverable> Deliverables,
    IReadOnlyList<TaskTranscriptEvent> RecentTranscript,
    WorktreeSnapshot? Worktree,
    string ExecutionStatus = "idle",
    int TrialCount = 0,
    /// <summary>Phase 20 — effective max iterations (override or default constant).</summary>
    int MaxIterations = 5,
    /// <summary>Total number of transcript events for this work item (supports paging).</summary>
    int TranscriptTotalCount = 0);

// ─── Phase 17 — Persistent task queue & team-to-crew routing ──────────────

/// <summary>
/// Phase 17.1 — one row in the unified task queue. Mirrors
/// <see cref="Forge.Api.Persistence.TaskQueueRow"/>; field names map 1:1.
/// </summary>
public sealed record TaskQueueEntry(
    long Id,
    string WorkItemId,
    /// <summary>"team" | "crew".</summary>
    string TargetKind,
    string TargetId,
    /// <summary>One of TaskQueueStates: awaiting-ack | awaiting-route | queued | picked-up | completed | cancelled | reassigned.</summary>
    string State,
    /// <summary>0=high, 1=med, 2=low.</summary>
    int PriorityRank,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? AckRequiredBy,
    DateTimeOffset? AckedAt,
    string? AckByUserId,
    bool AutoAcked,
    string? RoutedToCrewId,
    DateTimeOffset? PickedUpAt,
    string? PickedUpByAgent,
    DateTimeOffset? CompletedAt,
    DateTimeOffset LastUpdatedAt);

/// <summary>Phase 17 history row, used by <c>GET /api/queue/{id}/history</c>.</summary>
public sealed record TaskQueueHistoryEntry(
    long Id,
    long QueueId,
    string Kind,
    string? FromValue,
    string? ToValue,
    string? ByUserId,
    int? AtDepth,
    int? EtaMinutes,
    System.Text.Json.JsonElement Payload,
    DateTimeOffset At);

/// <summary>
/// Phase 17 — per-crew queue stats. Returned by <c>GET /api/crews/{id}/queue-stats</c>
/// and embedded in the <c>queue.strained</c> SignalR payload.
/// </summary>
public sealed record CrewQueueStats(
    string CrewId,
    int Depth,
    int EtaMinutes,
    bool Strained,
    DateTimeOffset? OldestQueuedAt,
    string? HeadWorkItemId);

/// <summary>Body for <c>POST /api/queue/{id}/route</c>.</summary>
public sealed record RouteQueueRequest(string CrewId);

/// <summary>Body for <c>POST /api/queue/{id}/reassign</c>.</summary>
public sealed record ReassignQueueRequest(string NewCrewId);

/// <summary>Body for <c>POST /api/queue/{id}/cancel</c>.</summary>
public sealed record CancelQueueRequest(string? Reason = null);


