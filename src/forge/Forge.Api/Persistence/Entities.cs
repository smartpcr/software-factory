// -----------------------------------------------------------------------
// <copyright file="Entities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

/// <summary>
/// Persistence entities for the Forge cache. Phase-0 keeps wide JSON columns
/// for the "shape" fields (tags, crew, agent, build, review, etc.) so the
/// prototype data round-trips without an over-eager normalisation. Phase 1+
/// will narrow these once the write-paths solidify.
/// </summary>
public class EpicRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int Progress { get; set; }

    public string CrewJson { get; set; } = "[]";

    public double Cost { get; set; }

    public string Color { get; set; } = string.Empty;
}

public class CardRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    /// <summary>Phase 8.2 — repo this card belongs to. Backfilled by <see cref="ForgeSeed"/> to the default repo.</summary>
    public string? RepoId { get; set; }

    public string Epic { get; set; } = string.Empty;

    public string Col { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string TagsJson { get; set; } = "[]";

    public int Pts { get; set; }

    public string Priority { get; set; } = string.Empty;

    public string? AgentJson { get; set; }

    public double Cost { get; set; }

    public string? AutoNext { get; set; }

    public bool Live { get; set; }

    public int? Progress { get; set; }

    public string? Branch { get; set; }

    public bool HumanGate { get; set; }

    public string? GateReason { get; set; }

    public bool Blocked { get; set; }

    public string? BlockedReason { get; set; }

    public string? DoneAt { get; set; }

    /// <summary>Phase 5.10 — opaque marker that bumps when a provider sync replaces the row.</summary>
    public string? RemoteRevision { get; set; }
}

public class ColumnRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Sort { get; set; }

    public int? WipLimit { get; set; }
}

public class WorkstreamRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    /// <summary>Phase 8.2 — repo this workstream belongs to. Backfilled by <see cref="ForgeSeed"/> to the default repo.</summary>
    public string? RepoId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string Base { get; set; } = string.Empty;

    public string Epic { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string WorktreeJson { get; set; } = "[]";

    public double Created { get; set; }

    public double LastActive { get; set; }

    public string AgentsJson { get; set; } = "[]";

    public string PrsJson { get; set; } = "[]";

    public string CardsJson { get; set; } = "[]";

    public int Commits { get; set; }

    public int Ahead { get; set; }

    public int Behind { get; set; }

    public int Conflicts { get; set; }

    public string Summary { get; set; } = string.Empty;

    public double BarStart { get; set; }

    public double BarEnd { get; set; }

    public bool Planned { get; set; }
}

public class WsEventRow
{
    public int Id { get; set; }

    public int Sort { get; set; }

    public string Ws { get; set; } = string.Empty;

    public double Day { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

public class PrRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    /// <summary>Phase 8.2 — repo this PR belongs to. Backfilled by <see cref="ForgeSeed"/> to the default repo.</summary>
    public string? RepoId { get; set; }

    public string Col { get; set; } = string.Empty;

    public string Epic { get; set; } = string.Empty;

    public string EpicColor { get; set; } = string.Empty;

    public string Workstream { get; set; } = string.Empty;

    public string Base { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string AuthorJson { get; set; } = "{}";

    public int Additions { get; set; }

    public int Deletions { get; set; }

    public int Files { get; set; }

    public int Commits { get; set; }

    public string Age { get; set; } = string.Empty;

    public string? BuildJson { get; set; }

    public string? ReviewJson { get; set; }

    public bool Conflicts { get; set; }

    public bool HumanGate { get; set; }

    public string? GateReason { get; set; }

    public string? ConflictPaths { get; set; }

    public bool AutoMerge { get; set; }

    public bool Blocked { get; set; }

    public string? ImplJson { get; set; }
}

public class WsStatRow
{
    public int Id { get; set; }

    public int Sort { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Val { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public string Trend { get; set; } = string.Empty;

    public string TrendVal { get; set; } = string.Empty;
}

public class OverviewActivityRow
{
    public int Id { get; set; }

    public int Sort { get; set; }

    public string Time { get; set; } = string.Empty;

    public string Who { get; set; } = string.Empty;

    public string Msg { get; set; } = string.Empty;
}

/// <summary>Phase 6.10 + 10.8 — single row per write action recorded by AuditMiddleware.</summary>
public class AuditRow
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public string User { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int Status { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? RequestBodySnippet { get; set; }

    /// <summary>Phase 10.8 — true when a PO acts against a product / crew they don't own.</summary>
    public bool Supervising { get; set; }
}

/// <summary>Phase 6.12 — cached response for an Idempotency-Key.</summary>
public class IdempotencyRow
{
    public string Key { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    public int Status { get; set; }

    public string ContentType { get; set; } = "application/json";

    public string Body { get; set; } = string.Empty;
}

public class OverviewAgentRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Task { get; set; } = string.Empty;
}

/// <summary>Phase 8 — top-level product row. Owns one or more <see cref="RepoRow"/>.</summary>
public class ProductRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 10.1 — per-user preferences. PK is <see cref="UserId"/> = the
/// owning user's id. In single-user mode the singleton row keys on
/// <c>"default"</c> until a real signed-in identity exists; once Phase 9
/// creates a UserRow the settings get re-keyed on first save.
/// </summary>
public class UserSettingsRow
{
    public string UserId { get; set; } = "default";

    public string? DefaultAdoOrgUrl { get; set; }

    public string? DefaultAdoProject { get; set; }

    public string? DefaultGitHubOwner { get; set; }

    public string? DefaultProductId { get; set; }

    public string? DefaultRepoId { get; set; }

    public string? BrowserPreference { get; set; }

    public string? Theme { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Phase 9.11 — preferred Microsoft account for ADO sign-in. Used as
    /// MSAL's <c>WithLoginHint</c> on interactive sign-in and to pick the
    /// matching cached account when multiple are present.
    /// </summary>
    public string? PreferredAdoAccount { get; set; }

    /// <summary>
    /// Phase 9.11 — preferred GitHub login (gh username) for sign-in. When
    /// set, <c>gh auth switch</c> activates this account; <c>gh auth token --user</c>
    /// reads its token. Empty → use whatever <c>gh</c> reports as the active account.
    /// </summary>
    public string? PreferredGitHubAccount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 10.6 — crew row. One model per crew, denormalised pricing so historical
/// salary lookups are reproducible across price changes. Crew membership lives
/// on the agent (each agent has one crew) per the Q3 resolution.
/// </summary>
public class CrewRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? ProductId { get; set; }

    public string Summary { get; set; } = string.Empty;

    /// <summary>Agent id of the crew lead.</summary>
    public string? Lead { get; set; }

    public string Model { get; set; } = "claude-opus-4-7";

    public decimal ModelInputPricePerMTok { get; set; }

    public decimal ModelOutputPricePerMTok { get; set; }

    /// <summary>Phase 11.10 — soft daily cap on this crew's total spend. Null = no cap.</summary>
    public decimal? DailyBudgetUsd { get; set; }

    /// <summary>Phase 11.10 — hard cap per single invocation. Null = no cap (only daily applies).</summary>
    public decimal? MaxTaskBudgetUsd { get; set; }
}

/// <summary>
/// Phase 11.3 — first-class agent record. Each agent belongs to exactly one
/// crew (Q3 implication). <see cref="SoulPath"/> overrides the default
/// <c>%LOCALAPPDATA%\Forge\agents\{id}\soul.md</c> location.
/// </summary>
public class AgentRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>One of: arch, eng, rev, qa, human, product-owner, tech-lead.</summary>
    public string Role { get; set; } = string.Empty;

    public string? CrewId { get; set; }

    /// <summary>Override path for soul.md. Null = default location.</summary>
    public string? SoulPath { get; set; }

    public string CapabilitiesJson { get; set; } = "[]";

    /// <summary>One of: idle, working, blocked, paused.</summary>
    public string State { get; set; } = "idle";
}

/// <summary>
/// Phase 11.8 — append-only history of agent crew + model assignments.
/// Each reassign writes one row so historical salary attribution stays correct
/// even after an agent moves crews or a crew swaps models.
/// </summary>
public class AgentHistoryRow
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string AgentId { get; set; } = string.Empty;

    public string? FromCrewId { get; set; }

    public string? ToCrewId { get; set; }

    public string? FromModel { get; set; }

    public string? ToModel { get; set; }

    /// <summary>X-Forge-User who triggered the move.</summary>
    public string? Actor { get; set; }
}

/// <summary>
/// Phase 9.6 — identified user. Forge keeps one row per signed-in identity
/// across providers. The first user to register on a fresh DB is auto-promoted
/// to <c>product-owner</c> per Phase 9.9; subsequent users default to <c>human</c>
/// and must be promoted by an existing PO.
/// </summary>
public class UserRow
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Login { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>One of: human, eng, rev, qa, arch, tech-lead, product-owner.</summary>
    public string Role { get; set; } = "human";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>
/// Phase 9.12 — per-product role override. A user has a global
/// <see cref="UserRow.Role"/> (their default), plus optional per-product
/// roles that override the default when scoped to that product. The same
/// person can be product-owner on one product and an engineer on another.
/// Composite key: (UserId, ProductId).
/// </summary>
public class UserProductRoleRow
{
    public string UserId { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    /// <summary>One of: human, eng, rev, qa, arch, tech-lead, product-owner.</summary>
    public string Role { get; set; } = "human";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}


/// <summary>
/// Phase 11.4 — cost ledger. One row per agent invocation; the runner writes
/// it on finish (or partial-finish on cancel). Salary roll-ups (today / sprint
/// / month) read this table grouped by <see cref="CrewId"/>; per-card cost reads
/// it grouped by <see cref="TaskId"/>. <see cref="Status"/> is one of
/// <c>running</c>, <c>finished</c>, <c>failed</c>, <c>cancelled</c>.
/// </summary>
public class AgentUsageRow
{
    public long Id { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public string CrewId { get; set; } = string.Empty;

    /// <summary>The card / task this invocation was started for. Empty for ad-hoc invocations.</summary>
    public string TaskId { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>Computed at write time = input × inputPricePerMTok / 1e6 + output × outputPricePerMTok / 1e6.</summary>
    public decimal CostUsd { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; set; }

    public string Status { get; set; } = "running";
}

/// <summary>
/// Phase 8 — repo row. <see cref="Kind"/> is fixed at "ado" or "github" (Q4: never both).
/// <see cref="RemoteUrl"/> is unique across ALL products (Q5: a remote belongs to exactly one product).
/// <see cref="CloneRoot"/> is unique across ALL products (Q2: two products can't share a worktree root).
/// </summary>
public class RepoRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string ProductId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string RemoteUrl { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string DefaultBranch { get; set; } = "main";

    public string CloneRoot { get; set; } = string.Empty;

    public string? AdoOrg { get; set; }

    public string? AdoProject { get; set; }

    public string? GhOwner { get; set; }

    public string? GhRepo { get; set; }
}

/// <summary>
/// Phase 12.1 — first-class work item record. Replaces the lightweight
/// <see cref="CardRow"/> projection for the WIT workspace; the existing
/// /api/cards endpoint becomes a view over <c>work_items</c> WHERE
/// <c>iterationRef = activeSprint AND state IN (proposed, active, resolved)</c>.
/// Two parallel three-level ladders are encoded by <see cref="Track"/>:
///   * dev:    epic → feature → task | bug
///   * design: phase → stage → step
/// </summary>
public class WorkItemRow
{
    public string Id { get; set; } = string.Empty;

    public string? RepoId { get; set; }

    /// <summary>One of: ado, github, local.</summary>
    public string Provider { get; set; } = "local";

    /// <summary>ADO work item id or GitHub issue number. Null for local-only.</summary>
    public int? ProviderRef { get; set; }

    /// <summary>One of: dev, design.</summary>
    public string Track { get; set; } = "dev";

    /// <summary>One of: epic, feature, task, bug, phase, stage, step.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>One of: proposed, active, resolved, closed.</summary>
    public string State { get; set; } = "proposed";

    public string Title { get; set; } = string.Empty;

    public string DescriptionMd { get; set; } = string.Empty;

    /// <summary>Self-FK; cycle prevention enforced at the API layer.</summary>
    public string? ParentId { get; set; }

    /// <summary>Optional product feature / functional area reference (Q12.1).</summary>
    public string? FeatureRef { get; set; }

    /// <summary>"{repoId}:{providerSprintId}" — namespaced per Q4.</summary>
    public string IterationRef { get; set; } = string.Empty;

    public string AreaPath { get; set; } = string.Empty;

    /// <summary>JSON of <c>{ kind, id, displayName }</c>; null when unassigned.</summary>
    public string? AssignedToJson { get; set; }

    public int Points { get; set; }

    public string Priority { get; set; } = "med";

    public string TagsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Phase 5.10 — opaque marker advanced on every server write.</summary>
    public string? RemoteRevision { get; set; }
}

/// <summary>
/// Phase 12.2 — non-hierarchy links between work items. The primary parent
/// stays denormalised on <see cref="WorkItemRow.ParentId"/>; this table
/// captures sibling / blocks / duplicates / related cross-references.
/// </summary>
public class WorkItemLinkRow
{
    public long Id { get; set; }

    public string FromId { get; set; } = string.Empty;

    public string ToId { get; set; } = string.Empty;

    /// <summary>One of: related, blocks, blocked-by, duplicates.</summary>
    public string Kind { get; set; } = "related";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
