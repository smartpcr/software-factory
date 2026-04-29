// -----------------------------------------------------------------------
// <copyright file="Entities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Forge.Api;

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

    public string? ProductId { get; set; }

    public string Status { get; set; } = string.Empty;

    public int Progress { get; set; }

    public string CrewJson { get; set; } = "[]";

    public double Cost { get; set; }

    public string Color { get; set; } = string.Empty;
}

/// <summary>
/// A user story scoped to a product. Groups cards and work items on
/// the Plan &amp; Design board. Unlike the legacy <see cref="EpicRow"/>
/// (global, seed-only), stories are first-class, product-bound entities.
/// </summary>
public class ProductStoryRow
{
    public string Id { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = "planning";

    public int Sort { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
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

    /// <summary>Phase 16.18 — work item this PR was created for. Null for externally-discovered PRs.</summary>
    public string? WorkItemId { get; set; }

    /// <summary>Phase 24 — false when PR reaches terminal state (completed/abandoned). Sweep skips inactive PRs.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Phase 24 — ISO-8601 cursor for last successful sync.</summary>
    public string? LastSyncAt { get; set; }

    /// <summary>Phase 24 — idle | running | failed.</summary>
    public string LastSyncStatus { get; set; } = "idle";

    /// <summary>Phase 24 — last error message from sync.</summary>
    public string? LastSyncError { get; set; }

    /// <summary>Phase 24 — wall-clock guard for stale-running detection.</summary>
    public string? IsRunningSince { get; set; }

    /// <summary>Phase 24 — opaque per-provider cursor for comment pagination.</summary>
    public string? CommentSyncCursor { get; set; }

    /// <summary>Phase 24 — upstream's updatedAt from last successful sync.</summary>
    public string? LastUpstreamUpdatedAt { get; set; }

    /// <summary>Phase 24 — one of: ado, github. Null for seed data.</summary>
    public string? Provider { get; set; }

    /// <summary>Phase 24 — provider's PR id/number.</summary>
    public string? ProviderRef { get; set; }

    /// <summary>Phase 24 — cursor for the comment-↔-commit linker sweep.</summary>
    public string? LastLinkerCommitSha { get; set; }
}

/// <summary>Phase 24 — mirrors upstream PR comment threads + individual comments.</summary>
public class PrCommentRow
{
    public long Id { get; set; }

    public string PrId { get; set; } = string.Empty;

    public string ProviderCommentId { get; set; } = string.Empty;

    public string ThreadId { get; set; } = string.Empty;

    public long? ParentCommentId { get; set; }

    public string? AuthorId { get; set; }

    public string AuthorKind { get; set; } = "human";

    public string Body { get; set; } = string.Empty;

    public string? FilePath { get; set; }

    public int? Line { get; set; }

    public string State { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? ResolvedByUserId { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public string? LinkedCommitSha { get; set; }

    public long? LinkedAgentResponseId { get; set; }

    public bool RequiresAttention { get; set; }
}

/// <summary>Phase 24 — append-only audit per comment state transition.</summary>
public class PrCommentHistoryRow
{
    public long Id { get; set; }

    public long CommentId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? FromValue { get; set; }

    public string? ToValue { get; set; }

    public string? ByUserId { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string? PayloadJson { get; set; }
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

    /// <summary>
    /// Phase 15.6 — product-level override for the active iteration. When
    /// non-null, the Plan &amp; Design sprint pill reads from this; null
    /// means fall back to the primary repo's <c>RepoIterationBindingRow</c>
    /// flagged <c>role='current'</c> (Q15.7).
    /// </summary>
    public string? ActiveIterationId { get; set; }
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

    /// <summary>Phase 13.1 — parent crew for hierarchy (Org → Team → Squad). Null = root.</summary>
    public string? ParentCrewId { get; set; }

    /// <summary>Phase 13.1 — depth from root (0/1/2). Persisted for cheap roll-ups; computed on insert / parent-change.</summary>
    public int Depth { get; set; }

    /// <summary>Phase 13.1 — the (Phase 11.5b) backend this crew runs against. 'copilot' (default) | 'claude'.</summary>
    public string Backend { get; set; } = "copilot";

    /// <summary>Phase 13.1 — the (Phase 11.5b) invoke mode. 'sdk' (default) | 'cli'. Tool-using kinds (plan/coding) require 'sdk'.</summary>
    public string InvokeMode { get; set; } = "sdk";

    /// <summary>Phase 13.1 — JSON string[] of domain-expert content-ids the crew has ingested as capabilities (Q13.3).</summary>
    public string CapabilitiesJson { get; set; } = "[]";

    /// <summary>Phase 13.5 — how many tasks the crew can claim at once across members.</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Phase 13.5 — hard ceiling on tasks completed per ISO week.</summary>
    public int WeeklyCap { get; set; } = 20;

    /// <summary>
    /// Phase 13b — default generator agent for plan / coding tasks. Every
    /// task spawned by the crew uses this pair unless the assign endpoint
    /// overrides it. Null means "use the lead".
    /// </summary>
    public string? DefaultGeneratorAgentId { get; set; }

    /// <summary>
    /// Phase 13b — default evaluator agent (the second half of the pair).
    /// Reads the generator's diff / plan and approves before a PR comes off
    /// draft (coding) or before the WIT tree commits (plan).
    /// </summary>
    public string? DefaultEvaluatorAgentId { get; set; }

    /// <summary>
    /// Phase 13b — model the evaluator runs against. Distinct from
    /// <see cref="Model"/> (which becomes the generator's model) so the pair
    /// always uses two different models, per the user's "diff-model" rule.
    /// Null = use the same as <see cref="Model"/> (warns in the UI).
    /// </summary>
    public string? EvaluatorModel { get; set; }

    /// <summary>
    /// Phase 14.1 — IANA timezone the crew works in (drives the 9-9-6
    /// schedule gate). Default <c>America/Los_Angeles</c>.
    /// </summary>
    public string Timezone { get; set; } = ScoringConstants.DefaultTimezone;

    /// <summary>
    /// Phase 14.1 — when false, the schedule gate is bypassed for this crew
    /// (e.g. CI fixtures, off-hours sweep crews). Defaults to true.
    /// </summary>
    public bool ScheduleEnabled { get; set; } = true;

    /// <summary>
    /// Phase 18 — which 12-hour band of <see cref="ScoringConstants.WorkDays"/>
    /// this crew runs in. <c>day</c> = 09:00–21:00 local; <c>night</c> =
    /// 21:00–09:00 next morning (the night block belongs to the work day on
    /// which it starts, so Sat 21:00 → Sun 09:00 is on-shift but Sun 21:00
    /// → Mon 09:00 is not). Defaults to <c>day</c> for back-compat.
    /// </summary>
    public string Shift { get; set; } = "day";

    /// <summary>
    /// Phase 16.2 — pair role. <c>standalone</c> (default) | <c>generator</c>
    /// | <c>evaluator</c>. Only arch + eng crews may enter a pair (Q16.2).
    /// </summary>
    public string PairRole { get; set; } = "standalone";

    /// <summary>
    /// Phase 16.2 — id of the crew this one is paired with. Symmetric: when A
    /// pairs with B, both rows reference each other. Null when standalone.
    /// </summary>
    public string? PairedWithCrewId { get; set; }
}

/// <summary>
/// Phase 13.1 — many-to-many crew membership. Lead is stored on
/// <see cref="CrewRow.Lead"/>; this table holds the rest of the roster.
/// Composite PK (CrewId, AgentId).
/// </summary>
public class CrewMemberRow
{
    public string CrewId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Phase 13b — role this agent plays inside this crew. Free-form by
    /// design (the spec only enforces a few well-known values: <c>coder</c>,
    /// <c>reviewer</c>, <c>architect</c>, <c>qa</c>, <c>generator</c>,
    /// <c>evaluator</c>). The crew lead is on <see cref="CrewRow.Lead"/>,
    /// not in this table — but a member CAN have a per-crew role of
    /// <c>generator</c> / <c>evaluator</c> independent of being lead.
    /// </summary>
    public string Role { get; set; } = "coder";

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? AddedBy { get; set; }
}

/// <summary>
/// Phase 13.1 — append-only audit of crew-shape changes. Kind ∈
/// <c>model | backend | mode | lead | member-add | member-remove |
/// parent-change | quota-override | capability-add | capability-remove</c>.
/// </summary>
public class CrewHistoryRow
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string CrewId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? FromValue { get; set; }

    public string? ToValue { get; set; }

    public string? ByUserId { get; set; }
}

/// <summary>
/// Phase 13.7 — index of <c>domain-expert.md</c> artifacts authored by
/// crews on coding-task close. <see cref="ContentId"/> is sha256(file)
/// at ingestion — content-addressed so two crews referencing the same
/// expert produce identical prompt-cache prefixes.
/// </summary>
public class DomainExpertRow
{
    public long Id { get; set; }

    public string ContentId { get; set; } = string.Empty;

    public string RepoId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Phase 13e — per-agent runtime overrides. Each is nullable; null
    /// means "inherit the crew's value". When set, this agent runs against
    /// the override even if the crew's default differs. Used to give a
    /// generator + evaluator inside the same crew their own model + backend
    /// pairing (e.g. evaluator on Copilot/sonnet while generator stays on
    /// Claude/opus).
    /// </summary>
    public string? Backend { get; set; }

    public string? InvokeMode { get; set; }

    /// <summary>Model when this agent acts as the pair's generator.</summary>
    public string? GeneratorModel { get; set; }

    /// <summary>Model when this agent acts as the pair's evaluator.</summary>
    public string? EvaluatorModel { get; set; }

    /// <summary>
    /// Phase 14.1 — agent level (0..10). Drives <see cref="HourlyUsd"/> and
    /// promotion eligibility. Backfilled to 0 on migration.
    /// </summary>
    public int Level { get; set; }

    /// <summary>Phase 14.1 — last time <see cref="Level"/> changed; null if never promoted/demoted.</summary>
    public DateTimeOffset? LevelUpdatedAt { get; set; }

    /// <summary>
    /// Phase 14.1 — derived: <c>base + level * step</c>. Persisted so a
    /// historical payroll roll-up doesn't need to know the formula version
    /// at the time of the row.
    /// </summary>
    public decimal HourlyUsd { get; set; } = ScoringConstants.HourlyBaseUsd;
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
/// Phase 14.1 — one row per deliverable iteration. A "deliverable" is a
/// single (work-item × iteration) attempt: the generator runs, the
/// evaluator scores, and the result is persisted here. Sub-90 verdicts
/// re-iterate up to <see cref="ScoringConstants.MaxIterations"/> times,
/// each iteration getting its own row.
/// </summary>
public class DeliverableRow
{
    public long Id { get; set; }

    public string WorkItemId { get; set; } = string.Empty;

    public string CrewId { get; set; } = string.Empty;

    public string GeneratorAgentId { get; set; } = string.Empty;

    public string EvaluatorAgentId { get; set; } = string.Empty;

    /// <summary>1-based iteration counter, capped at <see cref="ScoringConstants.MaxIterations"/>.</summary>
    public int Iter { get; set; } = 1;

    /// <summary>0..100. Derived verdict: ≥90 pass, 70..89 iterate, &lt;70 reject.</summary>
    public int Score { get; set; }

    /// <summary>JSON object mapping each rubric dimension to its 0..weight points.</summary>
    public string DimsJson { get; set; } = "{}";

    /// <summary>One of: <c>pass | iterate | reject | stalled | stalled-budget | paused-eod</c>.</summary>
    public string Verdict { get; set; } = "iterate";

    /// <summary>Evaluator's free-form notes (≤ 800 chars by spec, not enforced at the row level).</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Generator's change summary from the last token output (truncated to 800 chars).</summary>
    public string GeneratorSummary { get; set; } = string.Empty;

    public decimal GenCostUsd { get; set; }

    public decimal EvalCostUsd { get; set; }

    /// <summary>Phase 14.5 dispute path — set if a judge re-scored this row.</summary>
    public string? JudgeAgentId { get; set; }

    public int? JudgeScore { get; set; }

    public string? JudgeNotes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 14.1 — append-only level history. One row per promotion/demotion;
/// the nightly <c>PromotionJob</c> writes here when it bumps an agent's
/// level. Backfill on migration writes no rows (existing agents enter at
/// level 0 with no history).
/// </summary>
public class AgentLevelHistoryRow
{
    public long Id { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public int OldLevel { get; set; }

    public int NewLevel { get; set; }

    /// <summary>Trailing-window average score that triggered the change.</summary>
    public decimal WindowAvg { get; set; }

    /// <summary>One of: <c>promote | demote | grandfathered</c>.</summary>
    public string Reason { get; set; } = "promote";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Phase 16.1 — which side of a paired-crew deliverable this row covers.
    /// One of <c>generator | evaluator | judge</c>. Default <c>generator</c>
    /// matches the pre-pair behaviour; backfill rewrites every existing row
    /// to that value.
    /// </summary>
    public string CostCategory { get; set; } = "generator";
}

/// <summary>
/// Phase 17.1 — single persistent queue table. One row per active routing
/// of one work item through the team → lead → crew → member pipeline.
/// State machine — see <see cref="TaskQueueStates"/>; transitions are
/// atomic <c>UPDATE … WHERE id=? AND state=?</c> compare-and-swap so two
/// concurrent workers can never double-claim the same row.
/// </summary>
public class TaskQueueRow
{
    public long Id { get; set; }

    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>"team" | "crew".</summary>
    public string TargetKind { get; set; } = "team";

    /// <summary>Team name (e.g. "Architecture") when TargetKind="team"; crew id otherwise.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>One of <see cref="TaskQueueStates"/>.</summary>
    public string State { get; set; } = TaskQueueStates.AwaitingAck;

    /// <summary>0=high, 1=med, 2=low. Denormalised from <see cref="WorkItemRow.Priority"/> at enqueue.</summary>
    public int PriorityRank { get; set; } = 1;

    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set while State='awaiting-ack'. Worker auto-acks when now() &gt; this.</summary>
    public DateTimeOffset? AckRequiredBy { get; set; }

    public DateTimeOffset? AckedAt { get; set; }

    public string? AckByUserId { get; set; }

    /// <summary>Q17.8 — true when the ack happened via timeout rather than the lead clicking.</summary>
    public bool AutoAcked { get; set; }

    /// <summary>The crew that was eventually picked. Set on transition to State='queued'.</summary>
    public string? RoutedToCrewId { get; set; }

    public DateTimeOffset? PickedUpAt { get; set; }

    public string? PickedUpByAgent { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Phase 20 — runtime override for <see cref="ScoringConstants.MaxIterations"/>. When set via
    /// PATCH /api/work-items/{id}/execution, the deliverable loop reads this each iteration.</summary>
    public int? MaxIterOverride { get; set; }

    /// <summary>Phase 21 — when true, the agent runner skips loading soul.md (system prompt)
    /// for both generator and evaluator. Used by force-restart to run lean.</summary>
    public bool SkipSoulPrompt { get; set; }

    /// <summary>
    /// Phase 24 — discriminator for the runner to pick the prompt template.
    /// 'work' (default) runs the standard work-item brief; 'comment-response'
    /// loads the open PR comments needing response and replies / commits
    /// fixes against the existing PR.
    /// </summary>
    public string Kind { get; set; } = TaskQueueKinds.Work;
}

/// <summary>Phase 24 — well-known values for <see cref="TaskQueueRow.Kind"/>.</summary>
public static class TaskQueueKinds
{
    public const string Work = "work";
    public const string CommentResponse = "comment-response";
}

/// <summary>
/// Phase 17.1 — append-only audit per <see cref="TaskQueueRow"/> state
/// transition + strain events.
/// </summary>
public class TaskQueueHistoryRow
{
    public long Id { get; set; }

    public long QueueId { get; set; }

    /// <summary>One of: enqueued | acked | ack-timeout-auto | routed | claimed | completed | cancelled | reassigned | strain-detected | strain-confirmed | abandoned-recovered.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? FromValue { get; set; }

    public string? ToValue { get; set; }

    public string? ByUserId { get; set; }

    /// <summary>Strain payload — queue depth at the time of the event. Null on non-strain transitions.</summary>
    public int? AtDepth { get; set; }

    /// <summary>Strain payload — computed ETA in minutes at the time of the event.</summary>
    public int? EtaMinutes { get; set; }

    /// <summary>JSON blob for transition-specific extras (reason on cancel, reassign target, etc.).</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>State-machine constants for <see cref="TaskQueueRow.State"/>.</summary>
public static class TaskQueueStates
{
    public const string AwaitingAck   = "awaiting-ack";
    public const string AwaitingRoute = "awaiting-route";
    public const string Queued        = "queued";
    public const string PickedUp      = "picked-up";
    public const string Completed     = "completed";
    public const string Cancelled     = "cancelled";
    public const string Reassigned    = "reassigned";

    /// <summary>Open states count against the per-work-item idempotency guard.</summary>
    public static readonly string[] OpenStates = { AwaitingAck, AwaitingRoute, Queued, PickedUp };
}

/// <summary>
/// Phase 16.5 — one row per persisted live-execution event. Bounded per
/// work item (cap 10 000, oldest evicted when the cap is hit) so the
/// transcript replay-on-open returns in O(N) without choking on a runaway
/// deliverable. <c>PayloadJson</c> is loosely typed by <see cref="Kind"/> —
/// the channel can grow without breaking old clients.
/// </summary>
public class TaskTranscriptEventRow
{
    public long Id { get; set; }

    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>
    /// One of: <c>task.assigned | task.delegated | task.iteration.started |
    /// task.iteration.scored | task.iteration.iterated | task.iteration.passed |
    /// task.iteration.rejected | task.token | task.tool-call | task.tool-result |
    /// task.commit | task.file-changed | task.pr-opened | task.budget-warning |
    /// task.worktree-tick</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Originating agent (token / tool events). Null on aggregate events.</summary>
    public string? AgentId { get; set; }

    /// <summary>Originating crew (token / tool events). Null on aggregate events.</summary>
    public string? CrewId { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
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

    // -- Phase 23 — scheduled work-item sync fields --------------------------

    /// <summary>JSON <c>string[]</c> of ADO area-path prefixes. Null / empty = whole project (Q23.3).</summary>
    public string? AreaPathsJson { get; set; }

    /// <summary>ISO-8601 cursor: the start time of the last successful sync run (Q23.7).</summary>
    public string? LastSyncAt { get; set; }

    /// <summary>Opaque per-provider page cursor for mid-run resume (Q23.6).</summary>
    public string? LastSyncCursor { get; set; }

    /// <summary>One of: idle, running, failed (Q23.2).</summary>
    public string LastSyncStatus { get; set; } = "idle";

    /// <summary>Last error message, surfaced in the UI (Q23.3).</summary>
    public string? LastSyncError { get; set; }

    /// <summary>Wall-clock guard for the 19-minute cancellation rule (Q23.2).</summary>
    public string? IsRunningSince { get; set; }

    /// <summary>Overrides the default "Engineering" team for <c>agent-ready</c> auto-assign (Q23.8).</summary>
    public string? AgentReadyDefaultTeam { get; set; }

    /// <summary>Overrides the ADO 365-day initial sync window; ignored for GitHub (Q23.5).</summary>
    public int? InitialSyncWindowDays { get; set; }
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

    /// <summary>
    /// Phase 16.1 — set when <c>LeadAutoDispatcher</c> picks the row up and
    /// hands it to <see cref="DeliverableRow"/>. Null = not yet auto-dispatched
    /// (or the row predates Phase 16). Used by the dispatcher's idempotency
    /// guard (don't re-dispatch the same row) and for SLO histograms.
    /// </summary>
    public DateTimeOffset? AutoDispatchedAt { get; set; }

    /// <summary>
    /// Phase 16.5 — for <c>step</c> rows only: the planned PR-size budget.
    /// Phase 16.5 enforces ≤ 20 at create time. Phase 16.6 surfaces a yellow
    /// pill when the actual PR exceeds this. Null on non-step rows.
    /// </summary>
    public int? ExpectedFileChanges { get; set; }

    /// <summary>
    /// One of <see cref="ExecutionStatuses"/>: idle | queued | live |
    /// completed | failed | cancelled. Updated by the queue state machine.
    /// </summary>
    public string ExecutionStatus { get; set; } = ExecutionStatuses.Idle;

    /// <summary>
    /// Number of times execution has been attempted (incremented on claim,
    /// not on enqueue, so routing-only events don't inflate the count).
    /// </summary>
    public int TrialCount { get; set; }

    /// <summary>
    /// Phase 22 — runtime-assigned agent(s). JSON of <see cref="Forge.Api.Contracts.AssignedAgent"/>
    /// shape: <c>{ agents, team?, crewId?, role, assignedAt, assignedBy? }</c>.
    /// Null when no agent is actively working on this item. Independent of
    /// <see cref="AssignedToJson"/> which carries the WIT-level assignee.
    /// </summary>
    public string? AssignedAgentJson { get; set; }
}

/// <summary>
/// Well-known values for <see cref="WorkItemRow.ExecutionStatus"/>.
/// </summary>
public static class ExecutionStatuses
{
    public const string Idle = "idle";
    public const string Queued = "queued";
    public const string Live = "live";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Phase 22 — append-only assignment history. Each row records a transition
/// of the runtime <see cref="WorkItemRow.AssignedAgentJson"/> field, giving
/// the operator a timeline of "who has been on this and when".
/// </summary>
public class AgentAssignmentRow
{
    public long Id { get; set; }

    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>One of <see cref="AgentAssignmentKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Snapshot of agent IDs at this moment — JSON array of strings.</summary>
    public string AgentIdsJson { get; set; } = "[]";

    public string? Team { get; set; }

    public string? CrewId { get; set; }

    public string? Role { get; set; }

    public string? FromExecutionStatus { get; set; }

    public string? ToExecutionStatus { get; set; }

    /// <summary>Who triggered this transition: 'queue-worker', 'lead-dispatcher', userId, 'backfill', null.</summary>
    public string? AssignedBy { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Well-known values for <see cref="AgentAssignmentRow.Kind"/>.
/// </summary>
public static class AgentAssignmentKinds
{
    public const string Claimed = "claimed";
    public const string Reassigned = "reassigned";
    public const string ClearedRestart = "cleared-restart";
    public const string ClearedManual = "cleared-manual";
    public const string PrOpened = "pr-opened";
    public const string ReviewerAssigned = "reviewer-assigned";
    public const string Completed = "completed";
    public const string Backfill = "backfill";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Claimed, Reassigned, ClearedRestart, ClearedManual,
        PrOpened, ReviewerAssigned, Completed, Backfill,
    };
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

/// <summary>
/// Phase 15.1 — product-scoped iteration registry. Imported from a
/// repo's provider (ADO classification node or GitHub milestone) and
/// pinned to a single <see cref="ProductRow"/>. The pair
/// <c>(ProductId, ProviderNodeId)</c> is unique so re-importing the same
/// node updates the row in place rather than duplicating it.
/// </summary>
public class ProductIterationRow
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Owning product (Q15.1 — iterations are bound to a product, not a repo).</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>"ado" | "github".</summary>
    public string ProviderKind { get; set; } = "ado";

    public string ProviderOrg { get; set; } = string.Empty;

    public string ProviderProject { get; set; } = string.Empty;

    /// <summary>
    /// Stable upstream identity. ADO uses the classification node id;
    /// GitHub uses <c>milestone:{number}</c>. Renames upstream do NOT
    /// change this — bindings re-resolve by id, not path (Q15.4).
    /// </summary>
    public string ProviderNodeId { get; set; } = string.Empty;

    /// <summary>Backslash-delimited path string (ADO) or "#N title" (GitHub milestone).</summary>
    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset? FinishDate { get; set; }

    /// <summary>One of: past, current, future, provider-orphan.</summary>
    public string State { get; set; } = "future";

    /// <summary>JSON blob of provider-specific extras (e.g. ADO timeFrame, GH state-reason).</summary>
    public string AttributesJson { get; set; } = "{}";

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset RefreshedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 15.2 — product-scoped area-path registry. Tree-structured via
/// <see cref="ParentNodeId"/>. ADO-only at v0.1 (Q15.6); GitHub repos
/// use labels for area scoping. Pinned to a single <see cref="ProductRow"/>.
/// </summary>
public class ProductAreaPathRow
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Owning product (Q15.2 — areas are bound to a product).</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>"ado" — GitHub label-mapping deferred to Phase R / forge_v2.md.</summary>
    public string ProviderKind { get; set; } = "ado";

    public string ProviderOrg { get; set; } = string.Empty;

    public string ProviderProject { get; set; } = string.Empty;

    public string ProviderNodeId { get; set; } = string.Empty;

    /// <summary>Backslash-delimited path string.</summary>
    public string Path { get; set; } = string.Empty;

    public string? ParentPath { get; set; }

    public string? ParentNodeId { get; set; }

    /// <summary>
    /// Q15.2 — when true, Plan &amp; Design + Work items apply this path as a
    /// pre-filter before any user-supplied filter. At most one row per
    /// product is the default; flipping it on flips the previous default off.
    /// </summary>
    public bool IsDefaultFilter { get; set; }

    public string AttributesJson { get; set; } = "{}";

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 15.5 — repo ↔ iteration binding. A single repo can bind to
/// multiple <see cref="ProductIterationRow"/>s with different roles
/// (current / next / reference). The pair (RepoId, Role) is unique for
/// roles <c>current</c> and <c>next</c> — a repo has exactly one of each.
/// </summary>
public class RepoIterationBindingRow
{
    public long Id { get; set; }

    public string RepoId { get; set; } = string.Empty;

    public string ProductIterationId { get; set; } = string.Empty;

    /// <summary>One of: current, next, reference.</summary>
    public string Role { get; set; } = "reference";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }
}

/// <summary>
/// Phase 15.10 — saved prefix shorthand. Lets the operator click
/// "Refresh Atlas\Sprint*" and re-run discovery + import in one shot,
/// picking up newly-created upstream nodes (e.g. Sprint 15 once the
/// admin cuts it in ADO).
/// </summary>
public class ProductSavedPrefixRow
{
    public long Id { get; set; }

    /// <summary>Owning product (saved prefixes are bound to a product, Q15.10).</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>The prefix string the operator typed, e.g. <c>Atlas\Sprint</c>.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>"iterations" | "areas".</summary>
    public string Kind { get; set; } = "iterations";

    /// <summary>Repo whose provider was queried; FK to <see cref="RepoRow"/>.</summary>
    public string RepoId { get; set; } = string.Empty;

    public string AddedBy { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>JSON <c>{ added, removed, renamed }</c> from the most recent refresh.</summary>
    public string LastDeltaJson { get; set; } = "{\"added\":0,\"removed\":0,\"renamed\":0}";
}

/// <summary>
/// Phase 23 — one row per sync invocation. Captures run-level metrics
/// (items added/updated/conflicted) and the success/error outcome. The
/// <c>/api/repos/{id}/sync-work-items/runs</c> endpoint surfaces the last
/// 20 rows as a simple history view.
/// </summary>
public class WorkItemSyncRunRow
{
    public string Id { get; set; } = string.Empty;

    public string RepoId { get; set; } = string.Empty;

    /// <summary>One of: full, delta, manual.</summary>
    public string Kind { get; set; } = "delta";

    /// <summary>ISO-8601 timestamp when the run started.</summary>
    public string StartedAt { get; set; } = string.Empty;

    /// <summary>ISO-8601 timestamp when the run finished (null if still running).</summary>
    public string? FinishedAt { get; set; }

    public int ItemsAdded { get; set; }

    public int ItemsUpdated { get; set; }

    public int ItemsConflicted { get; set; }

    public string? ErrorMessage { get; set; }

    public bool CompletedSuccessfully { get; set; }
}
