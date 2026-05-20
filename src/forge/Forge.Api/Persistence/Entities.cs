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

    /// <summary>
    /// Phase 80 — repo this story belongs to (a product owns many repos; a
    /// story lives under exactly one). Nullable during the 80.A schema window;
    /// the 80.F backfill stamps legacy null rows to the product's
    /// <see cref="ProductRow.DefaultRepoId"/> and create paths make it required.
    /// </summary>
    public string? RepoId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = "planning";

    public int Sort { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Used as the user prompt the architect agent receives when this story
    // is picked up to start design.
    public string Description { get; set; } = string.Empty;

    public int Points { get; set; }

    /// <summary>
    /// Phase 43 — JSON array of operator-uploaded reference documents.
    /// Carried through to <see cref="StorySpawnRow.AttachmentsJson"/> on spawn.
    /// </summary>
    public string? AttachmentsJson { get; set; }

    /// <summary>
    /// Phase 45 — operator-chosen default base branch carried through to
    /// <see cref="StorySpawnRow.BaseBranch"/> when the story is spawned.
    /// Defaults to <c>"main"</c>. Setting this to a non-default value via
    /// PATCH triggers an immediate <c>git branch</c> creation in every repo
    /// of the owning product so the branch is materialised before any spawn
    /// runs against it.
    /// </summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>
    /// Phase 46 — when <c>true</c>, agents may auto-create a PR against the
    /// base branch and merge it after the task completes. Carried through
    /// to <see cref="StorySpawnRow.AutoMerge"/> on spawn. Honoured for any
    /// base, including <c>"main"</c>; the operator arms the flag
    /// deliberately and <c>AutoMergeWatcher</c> still applies build / human
    /// gate / review checks before merging.
    /// </summary>
    public bool AutoMerge { get; set; }
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

    /// <summary>
    /// forge_implementation.md Phase 28 — back-pointer to the parent story
    /// spawn. Null for ad-hoc / pre-spawn workstreams. When set, the
    /// workstream is one of N parallel workstreams produced by the
    /// architect's decomposition.
    /// </summary>
    public string? StorySpawnId { get; set; }

    /// <summary>
    /// Phase 42 — stable slug derived from the originating Stage in
    /// <c>implementation-plan.md</c> (e.g. <c>phase-2/stage-1-add-cancel-button</c>).
    /// The reconciler joins persisted workstreams to plan stages by this anchor;
    /// renames preserve the anchor, deletions drop it. Null on legacy / pre-reconciler
    /// rows — the reconciler leaves those alone.
    /// </summary>
    public string? PlanAnchor { get; set; }

    /// <summary>
    /// Phase 42 — SHA-256 of the Stage's body in <c>implementation-plan.md</c>
    /// at the moment the workstream was last reconciled. A change here with the
    /// same <see cref="PlanAnchor"/> means "same stage, scope edited" — the
    /// reconciler updates Summary + re-prompts engineers on open PRs.
    /// </summary>
    public string? PlanContentHash { get; set; }

    /// <summary>
    /// Phase 46 — JSON <c>string[]</c> of upstream workstream titles this
    /// stage depends on. The orchestrator's stage-level scheduler reads
    /// this directly without re-parsing the plan; the value is the
    /// resolved + phase-expanded set produced by
    /// <c>CrewStoryArtifactStrategy.ProduceDecompositionAsync</c>.
    /// </summary>
    public string? DepsJson { get; set; }

    /// <summary>
    /// Phase 46 — cached <c>phase-{slug}</c> prefix of <see cref="PlanAnchor"/>.
    /// Used by the swarm-control DAG view to group stage cards into
    /// collapsible phase containers without re-parsing the plan.
    /// </summary>
    public string? PhaseAnchor { get; set; }

    /// <summary>
    /// Phase 49 — human-readable reason a workstream is sitting in the
    /// <c>stalled</c> triage state. Set when the orchestrator transitions
    /// <see cref="Status"/> to <c>stalled</c> after a build-gate failure,
    /// a no-progress abort, or a non-pass terminal verdict. Surfaced in
    /// the swarm-control UI as a tooltip on the stalled badge so
    /// operators can decide whether to Restart or escalate without
    /// drilling into the Conversation tab. Cleared by the Restart
    /// endpoint when <see cref="Status"/> flips back to <c>active</c>.
    /// Null on every other status (and historically on rows that were
    /// blocked before this column existed — backfilled to null).
    /// </summary>
    public string? StallReason { get; set; }

    /// <summary>
    /// Phase 63 — stores the status the workstream was in before the
    /// spawn-level pause. <c>null</c> unless <see cref="Status"/> is
    /// <c>"paused"</c>. The spawn resume endpoint restores
    /// <see cref="Status"/> to this value and clears the field.
    /// </summary>
    public string? PausedFromStatus { get; set; }

    /// <summary>
    /// docs/forge/e2e.md Stage 1.1 — discriminator for the kind of work
    /// a workstream owns. <c>"impl"</c> (default) is a normal Stage's
    /// implementation work; <c>"qa-e2e"</c> is the trailing per-stage
    /// QA workstream that owns the Gherkin <c>.feature</c> file, the
    /// step-definition file, and the e2e test run for that Stage.
    /// Backfilled to <c>"impl"</c> for every pre-existing row by the
    /// Phase 79 migration; new rows produced by
    /// <c>CrewStoryArtifactStrategy.ProduceDecompositionAsync</c> set
    /// this explicitly. The DAG view groups stacked impl/qa-e2e rows
    /// using this column.
    /// </summary>
    public string Category { get; set; } = "impl";
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

    /// <summary>Phase 28 — number of updates / iterations on the PR (ADO "Updates" tab count).</summary>
    public int Updates { get; set; }

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

    /// <summary>Phase 27 — PR description body from the provider.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Phase 27 — clickable web URL to open the PR in ADO/GitHub.</summary>
    public string? WebUrl { get; set; }

    /// <summary>Phase 27 — PR creation timestamp from the provider.</summary>
    public string? CreatedAt { get; set; }

    /// <summary>Phase 27 — last updated timestamp from the provider.</summary>
    public string? UpdatedAt { get; set; }
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

    /// <summary>Phase 34 — user reaction: null, "thumbsup", or "thumbsdown".</summary>
    public string? Reaction { get; set; }
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

    /// <summary>
    /// Phase 80 — the product's default repo. Seeds the topbar repo switcher's
    /// initial selection and is the backfill target for legacy rows whose
    /// <c>RepoId</c> is null (stories / work items / workstreams / PRs / cards /
    /// spawns). Nullable: a product with no repos yet has no default. Set to the
    /// lowest-<c>Sort</c> repo on first repo attach (80.B/80.C).
    /// </summary>
    public string? DefaultRepoId { get; set; }
}

/// <summary>
/// Phase G (post-split) — control-side "due/dirty" watermark per product
/// so background workers can skip products with no pending work without
/// opening their per-product DB. Updated on the write paths that produce
/// work (queue enqueue, PR creation, build enqueue) and read by the
/// orchestrator / sync workers / merge watcher to decide which products
/// to fan out to this tick.
///
/// <para>Pre-Phase-F this is just an optimization hint (control DB is
/// the same file as product DBs). Post-Phase-F it avoids the cost of
/// opening every <c>{slug}.db</c> handle when most products have zero
/// pending work.</para>
///
/// <para>Single row per product (PK = ProductId). UPSERTed by writers
/// via SQLite's <c>INSERT … ON CONFLICT</c> against PK. The
/// <see cref="LastPendingAt"/> index is the hot read path used by every
/// worker tick.</para>
/// </summary>
public class ProductRuntimeRow
{
    /// <summary>FK to <see cref="ProductRow.Id"/>. One row per product.</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>
    /// Most recent moment a write path put pending work into the product's
    /// DB (e.g., a task_queue row, an open PR, a build job). Null when no
    /// pending work has been observed since the product was created or the
    /// last reaper sweep.
    /// </summary>
    public DateTimeOffset? LastPendingAt { get; set; }

    /// <summary>
    /// Cached watermark of open PRs in the product's DB. Maintained by the
    /// PR write paths (open / merge / close) so <c>AutoMergeWatcher</c> can
    /// skip products with zero open PRs without opening their DB.
    /// </summary>
    public int ActivePrCount { get; set; }

    /// <summary>Last time any field on this row was written.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Phase 55 (docs/forge/sloppy_guard.md) — per-crew toggle for Layer 2
    /// agent-memory continuity (Claude/CLI --resume, Claude/SDK history
    /// replay, Copilot/SDK long-lived session). Defaults <c>true</c>
    /// (operator decision flipped 2026-05-13 from opt-in to opt-out):
    /// every supported crew gets memory unless explicitly disabled;
    /// agents can also override per-row via
    /// <see cref="AgentRow.AgentMemoryOverride"/>. Phase A worktree
    /// scratch notes are independent of this flag and stay always-on
    /// for every crew.
    /// </summary>
    public bool EnableAgentMemory { get; set; } = true;

    /// <summary>
    /// Phase 55 / Phase F — per-crew override for the input-token
    /// threshold at which the compactor folds older conversation turns
    /// into a single summary. Null = use the global 300K default. The
    /// effective trigger is always clamped down to
    /// <c>model.ContextWindow × Forge:Compaction:ContextWindowRatio</c>
    /// (default 0.5) so a smaller-window evaluator
    /// model (e.g. 200K Sonnet against a 1M-context generator) never
    /// blows past its real context budget. Operators tune this when
    /// they see a crew's compaction summaries lose too much detail —
    /// drop the threshold to compact later (more verbatim turns
    /// retained) or raise it on long-context-tier crews.
    /// </summary>
    public long? CompactionTokenThreshold { get; set; }
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

    /// <summary>
    /// Phase 44 — max concurrent tasks this agent can work on. Default 1 for
    /// regular agents; leads/evaluators may have higher capacity.
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 1;

    /// <summary>
    /// Phase 55 / Phase G — per-agent override for the crew's
    /// <see cref="CrewRow.EnableAgentMemory"/> flag. Null = inherit
    /// from the crew (the default). Set explicitly when an operator
    /// wants to flip a single agent on or off without touching the
    /// rest of the crew — useful for A/B comparing a memory-enabled
    /// agent against a non-memory sibling on the same workstream type.
    /// </summary>
    public bool? AgentMemoryOverride { get; set; }

    /// <summary>
    /// Per-agent avatar override. Stores the PNG filename from the
    /// <c>src/assets/crews/</c> folder (e.g. "01_Ne_Zha.png"). Null
    /// means the agent uses the allocation-manifest default.
    /// </summary>
    public string? Avatar { get; set; }
}

/// <summary>
/// Phase 44 — first-class claim row tracking which agent is working on which
/// task and for how long. An open claim (ReleasedAt IS NULL) means the agent
/// is actively working. Released claims form a durable time-tracking history.
/// </summary>
public class AgentClaimRow
{
    public long Id { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>One of: generator, evaluator.</summary>
    public string Role { get; set; } = "generator";

    public DateTimeOffset ClaimedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null while the agent is actively working; set on release.</summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostUsd { get; set; }
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

    /// <summary>0..100. Derived verdict: ≥90 pass, [reject..89] iterate, &lt;reject reject. Reject threshold varies by work-item points.</summary>
    public int Score { get; set; }

    /// <summary>JSON object mapping each rubric dimension to its 0..weight points.</summary>
    public string DimsJson { get; set; } = "{}";

    /// <summary>One of: <c>pass | iterate | reject | stalled | stalled-budget | stalled-no-convergence | sloppy | stalled-questions | paused-eod</c>. <c>stalled-questions</c> (Phase 66) is a SOFT terminal — the loop returns without spending another generator turn while operator questions are unanswered, then resumes from the prior chain once the answer endpoint clears the gate.</summary>
    public string Verdict { get; set; } = "iterate";

    /// <summary>Evaluator's free-form notes (≤ 4000 chars by spec, not enforced at the row level).</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Generator's iteration summary — extracted from the explicit `## Iteration Summary` / `## Change Summary` / `## Plan Summary` heading the agent emits per prompt instructions; falls back to the response tail when the heading is missing. Capped at 4000 chars.</summary>
    public string GeneratorSummary { get; set; } = string.Empty;

    public decimal GenCostUsd { get; set; }

    public decimal EvalCostUsd { get; set; }

    /// <summary>Phase 14.5 dispute path — set if a judge re-scored this row.</summary>
    public string? JudgeAgentId { get; set; }

    public int? JudgeScore { get; set; }

    public string? JudgeNotes { get; set; }

    /// <summary>
    /// Phase 64 — when <see cref="Verdict"/> is <c>stalled</c> and the
    /// underlying generator invocation aborted (per-task max-turns cap,
    /// hard wall-clock timeout, transport silent, CLI crash), this carries
    /// the <see cref="Services.GeneratorFailureKinds"/> classifier value
    /// (e.g. <c>generator-timeout</c>, <c>generator-max-turns</c>,
    /// <c>generator-inactive</c>). Null on non-failure rows. Lets the
    /// stall handlers surface the underlying cause on
    /// <see cref="WorkstreamRow.StallReason"/> instead of writing the
    /// generic "Evaluator terminal verdict 'stalled'" text that hides
    /// the actual reason from the operator.
    /// </summary>
    public string? FailureKind { get; set; }

    /// <summary>
    /// Phase 64 — the free-form provider error string that produced
    /// <see cref="FailureKind"/> (e.g. <c>"Copilot SDK hard timeout:
    /// exceeded 16m budget"</c>). Captured verbatim from the
    /// dispatcher's Failed event so the operator sees the actual
    /// numbers (16 min, 5.2 min, 90 turns) without parsing prose
    /// out of <see cref="Notes"/>.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Phase 65 — per-iter commit SHA recorded by the producer's
    /// <c>OnIterationCommit</c> callback. Null when the iter produced no
    /// commit (e.g. verification-only iter, or a producer path that
    /// doesn't snapshot per-iter SHAs). Pairs with
    /// <see cref="FilesAdded"/> / <see cref="FilesModified"/> /
    /// <see cref="FilesDeleted"/> / <see cref="FilesChanged"/> so the
    /// Conversation tab's iter strip can show real file counts even when
    /// the <c>task.worktree-tick</c> polling missed the iter window
    /// (worktree removed between iters, very-fast commits, etc.).
    /// </summary>
    public string? CommitSha { get; set; }

    /// <summary>Phase 65 — commit message of <see cref="CommitSha"/>, truncated to 512 chars on capture. Null when no commit.</summary>
    public string? CommitMessage { get; set; }

    /// <summary>Phase 65 — count of files added by this iter's commit (relative to its parent). Null when no commit / no data.</summary>
    public int? FilesAdded { get; set; }

    /// <summary>Phase 65 — count of files modified by this iter's commit. Null when no commit / no data.</summary>
    public int? FilesModified { get; set; }

    /// <summary>Phase 65 — count of files deleted by this iter's commit. Null when no commit / no data.</summary>
    public int? FilesDeleted { get; set; }

    /// <summary>
    /// Phase 65 — denormalised total = <see cref="FilesAdded"/> +
    /// <see cref="FilesModified"/> + <see cref="FilesDeleted"/>. Stored
    /// rather than computed so the iter-KPI endpoint can sort/aggregate
    /// without a column expression that breaks on null arithmetic.
    /// </summary>
    public int? FilesChanged { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 52 — one row per (work_item, pair) attempt. The audit-trail-of-record
/// for "what did this pair cost and produce on this work item". Created on
/// first claim with <c>SequenceNo = 1</c>; the <c>SloppyReassignmentReconciler</c>
/// opens a fresh row on each reassignment (SequenceNo 2, 3). The pair-attempt
/// cap (<see cref="ScoringConstants.MaxPairAttempts"/>) is enforced against
/// <see cref="WorkItemRow.PairAttemptCount"/>, not the count of rows here —
/// the row count is the *audit* shape, the work-item column is the *guard*
/// shape, which lets the reconciler refuse a reassignment without first
/// opening a row.
///
/// <para><see cref="Outcome"/> values: <c>in-progress | passed |
/// reassigned-sloppy | terminal-failed | cancelled</c>. <see cref="SloppyReason"/>
/// values when sloppy-class: <c>score-plateau | token-cap | iter-cap</c>.
/// Phase 14.16's KPI service reads this table for the <c>SloppyRate</c>
/// KPI; Phase 14.7's promotion job treats sustained sloppy-rate as a
/// demotion signal.</para>
/// </summary>
public class AgentPairAssignmentRow
{
    public long Id { get; set; }

    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>1-based, 1..<see cref="ScoringConstants.MaxPairAttempts"/>. Unique on (WorkItemId, SequenceNo).</summary>
    public int SequenceNo { get; set; } = 1;

    public string GeneratorAgentId { get; set; } = string.Empty;

    public string EvaluatorAgentId { get; set; } = string.Empty;

    public string CrewId { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when <see cref="Outcome"/> leaves <c>in-progress</c>.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public int TotalIters { get; set; }

    public int? FinalScore { get; set; }

    public long TotalInputTokens { get; set; }

    public long TotalOutputTokens { get; set; }

    public decimal TotalCostUsd { get; set; }

    /// <summary>One of <see cref="AgentPairAssignmentOutcomes"/>.</summary>
    public string Outcome { get; set; } = AgentPairAssignmentOutcomes.InProgress;

    /// <summary>One of <see cref="SloppyReasons"/> when <see cref="Outcome"/> is sloppy-class; null otherwise.</summary>
    public string? SloppyReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 52 — well-known values for <see cref="AgentPairAssignmentRow.Outcome"/>.
/// </summary>
public static class AgentPairAssignmentOutcomes
{
    /// <summary>Pair is currently working on the iteration loop.</summary>
    public const string InProgress = "in-progress";

    /// <summary>Pair produced a passing deliverable. Terminal happy path.</summary>
    public const string Passed = "passed";

    /// <summary>
    /// Sloppy detector fired. The reconciler will open a fresh pair-attempt
    /// row unless this is the third attempt (cap), in which case it stamps
    /// <see cref="TerminalFailed"/> instead.
    /// </summary>
    public const string ReassignedSloppy = "reassigned-sloppy";

    /// <summary>Cap hit (PairAttemptCount &gt;= MaxPairAttempts). Work item terminates with FailureReason.</summary>
    public const string TerminalFailed = "terminal-failed";

    /// <summary>Operator cancelled the work item; row closed without a verdict.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Phase 52 — well-known values for <see cref="AgentPairAssignmentRow.SloppyReason"/>.
/// </summary>
public static class SloppyReasons
{
    /// <summary>Q52.1 — score plateau against personal best. Latest 3 scores ≤ peak at iter ≤ N-3.</summary>
    public const string ScorePlateau = "score-plateau";

    /// <summary>Q52.2 — generator's cumulative input+output tokens since BudgetWindowStartUtc exceed <see cref="ScoringConstants.SloppyGeneratorTokenCap"/>.</summary>
    public const string TokenCap = "token-cap";

    /// <summary>Phase 14.4 hard 5-iter cap hit without pass. Phase 52 absorbs the legacy <c>stalled</c> path into the same reassignment flow.</summary>
    public const string IterCap = "iter-cap";

    /// <summary>
    /// Legacy sloppy reason kept for historical rows written while the
    /// Phase 56 prompt-bloat hard-stop was enabled.
    /// </summary>
    public const string PromptBloat = "prompt-bloat";

    /// <summary>
    /// Phase 61 — the last
    /// <see cref="ScoringConstants.SloppyConsecutiveZeroThreshold"/>
    /// deliverable scores on this work item were all <c>0</c>. Re-enabled
    /// 2026-05-14 after the Phase 60 budget bump still failed to converge
    /// on the <c>local:UPDATE-HISTORY-IN-CL:architecture.md</c> generator/
    /// model combo (10 iters, $1.3K spent, halted with verdict=stalled).
    /// Fires earlier than the legacy 5-iter cap and cuts the post-failure
    /// spend by aborting the loop ~iter (K+2) instead of iter 10.
    /// </summary>
    public const string ConsecutiveScoreZero = "consecutive-score-zero";

    /// <summary>
    /// Phase 67 — a single iter completed with <c>score == 0</c> AND the
    /// per-iter commit snapshot (Phase 65) recorded
    /// <c>FilesAdded + FilesModified + FilesDeleted == 0</c> on the
    /// branch. The agent ran but produced nothing useful; continuing
    /// would burn $9+/iter rebuilding the same context the agent has
    /// already shown it can't use.
    /// <para>Observed 2026-05-15 on
    /// <c>code-intelligence:AGENT-MEMORY</c> plan-doc
    /// <c>e2e-scenarios.md</c> iter 3: 12m52 / 600K tokens / $9.81 burned,
    /// branch HEAD unchanged, evaluator scored 0. Phase 61's
    /// 3-trailing-zero threshold would have waited two more iters at
    /// the same cost each before triggering; Phase 67 fires after the
    /// first such iter.</para>
    /// <para>Distinct from Phase 47 NoProgressException (3 consecutive
    /// no-change iters, impl-only) — Phase 67 needs just 1 iter and
    /// applies to all roles (plan-doc / workstream / review-fix / QA).
    /// Routes through the existing Phase 52 sloppy infrastructure
    /// (CrewQueueWorker pending-reassign, SloppyReassignmentReconciler)
    /// so the UI surface, pair-reassign flow, and PairAttemptCount
    /// terminal cap all behave identically to legacy sloppy reasons.</para>
    /// </summary>
    public const string NoProgressScoreZero = "no-progress-score-zero";
}

/// conversation-tab wizard; answers are prepended to the next iteration's
/// user prompt and the evaluator's pass verdict is overridden to `iterate`
/// while any rows for this work item have <see cref="Answer"/> = null.
///
/// One row per (workItemId, externalId, iter) — the architect can re-emit a
/// question in a later iteration if the original answer wasn't sufficient,
/// and we keep the history. The "what's still unanswered" query dedupes by
/// externalId taking the most-recent row.
/// </summary>
public class DeliverableQuestionRow
{
    public long Id { get; set; }

    /// <summary>Plain-text id from the generator's JSON block ("q1", "auth-method", etc.). Stable across iters.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Workstream id or synthetic plan-doc id (matches <see cref="DeliverableRow.WorkItemId"/>).</summary>
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>Iteration that emitted this question (1-based, matches Deliverables.Iter).</summary>
    public int Iter { get; set; }

    /// <summary>Question text shown to the operator.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>One of: <c>choice</c> (radio) | <c>open</c> (free-text).</summary>
    public string Kind { get; set; } = "open";

    /// <summary>JSON array of options when <see cref="Kind"/>=choice. Null/empty for open questions.</summary>
    public string? ChoicesJson { get; set; }

    /// <summary>Operator-supplied answer. Null = still unanswered (blocks pass verdict).</summary>
    public string? Answer { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    public string? AnsweredBy { get; set; }

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

    /// <summary>
    /// Phase 54 — JSON array of per-turn token snapshots for the invocation
    /// that wrote this row. Shape is
    /// <c>[{ "turnIndex": int, "inputTokens": long, "outputTokens": long,
    /// "atUtc": "iso8601" }, ...]</c>. Under the Phase 54 "1 invocation =
    /// 1 turn" definition the array is either null (pre-Phase-54 rows / CLI
    /// runners with no per-event usage) or carries a single sample whose
    /// values mirror <see cref="InputTokens"/> + <see cref="OutputTokens"/>.
    /// Reserved for richer sub-invocation samples (one entry per
    /// <c>AssistantUsageEvent</c> from the Copilot SDK) when the turn
    /// granularity is bumped.
    /// </summary>
    public string? TurnSamplesJson { get; set; }

    /// <summary>
    /// 2026-05-16 — prompt-cache read tokens (cheap; billed at the
    /// cache-read discount, typically ~10% of fresh input). Source:
    /// Copilot SDK <c>AssistantUsageEvent.Data.CacheReadTokens</c>,
    /// accumulated across every usage event the runner observed for
    /// this invocation. Non-zero means the SDK's session reuse +
    /// provider prompt cache hit on the prompt prefix. Null on
    /// pre-2026-05-16 rows; backfill leaves them null.
    /// </summary>
    public long? CacheReadTokens { get; set; }

    /// <summary>
    /// 2026-05-16 — prompt-cache write tokens (the first turn that
    /// processes a given prefix pays this; subsequent turns hit the
    /// cache-read path instead). Source: Copilot SDK
    /// <c>AssistantUsageEvent.Data.CacheWriteTokens</c>. Null on
    /// pre-2026-05-16 rows.
    /// </summary>
    public long? CacheWriteTokens { get; set; }

    /// <summary>
    /// 2026-05-16 — reasoning tokens (internal model chain-of-thought;
    /// Anthropic <c>thinking</c> / OpenAI o1 reasoning). Billed
    /// separately from output. Surfaced in iter-kpis so reasoning-
    /// heavy iters don't look "free" relative to visible output.
    /// Null on pre-2026-05-16 rows.
    /// </summary>
    public long? ReasoningTokens { get; set; }

    /// <summary>
    /// 2026-05-16 — conversation context size reported by the
    /// Copilot SDK on <c>SessionUsageInfoEvent.Data.ConversationTokens</c>
    /// (the live session's total context after the model processed
    /// this turn). Useful for spotting context bloat: a small prompt
    /// with a huge conversationTokens means the SDK is replaying the
    /// entire prior dialog. Null on pre-2026-05-16 rows and on
    /// backends that don't emit this event (Claude SDK / Copilot CLI).
    /// </summary>
    public long? ConversationTokens { get; set; }
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

    // Phase 21's SkipSoulPrompt flag is removed — the agent always loads
    // soul.md + per-kind preamble. The DB column lingers (default 0) for
    // schema-stability; the migration in Phase21Migration.cs is idempotent
    // and a future cleanup can drop it.

    /// <summary>
    /// Phase 24 — discriminator for the runner to pick the prompt template.
    /// 'work' (default) runs the standard work-item brief; 'comment-response'
    /// loads the open PR comments needing response and replies / commits
    /// fixes against the existing PR.
    /// </summary>
    public string Kind { get; set; } = TaskQueueKinds.Work;

    /// <summary>
    /// Phase 24 Q24.14 — pin the original generator agent for continuity. When
    /// set, the Sweep 2 picker prefers this agent over round-robin and falls
    /// through to the lead only if the pinned agent is paused / retired /
    /// missing. Round-robin to a fresh member is never automatic for a
    /// pinned row. Null on regular work tasks; populated on comment-response
    /// enqueue from <c>WorkItemRow.AssignedAgent.agents[0]</c>.
    /// </summary>
    public string? PreferredAgentId { get; set; }

    /// <summary>
    /// Phase 24 Q24.14 — recorded reason when the pin falls through. One of
    /// 'preferred-agent-paused' | 'preferred-agent-not-in-crew' |
    /// 'preferred-agent-missing' | 'original-agent-pin' (succeeded). Null
    /// before claim. Surfaces on the queue history audit row so the trail is
    /// loud about why round-robin was skipped or why the lead self-assigned.
    /// </summary>
    public string? PinReason { get; set; }

    /// <summary>
    /// Phase 24 Q24.14 — surfaces why a row went to <see cref="TaskQueueStates.Blocked"/>.
    /// Always null for non-blocked rows. Read by the lead inbox banner so the
    /// operator can see <c>comment-response.no_original_agent</c> at a glance.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Phase 47 — agent the worker resolved as the row's evaluator at claim
    /// time, so the deliverable loop's evaluator side reads the same agent
    /// that <see cref="WorkItemRow.AssignedAgentJson"/> advertised. Without
    /// this, a Phase 47 diversity pick at claim and the
    /// <c>RunAgentAsync</c> resolution path would re-derive the evaluator
    /// from <c>CrewRow.DefaultEvaluatorAgentId</c> and disagree (UI shows
    /// agent X, agent Y actually scores). Null on legacy rows + on claims
    /// pre-dating Phase 47; the deliverable loop falls back to the
    /// existing default-evaluator resolution in that case.
    /// </summary>
    public string? RoutedEvaluatorAgentId { get; set; }
}

/// <summary>Phase 24 — well-known values for <see cref="TaskQueueRow.Kind"/>.</summary>
public static class TaskQueueKinds
{
    public const string Work = "work";
    public const string CommentResponse = "comment-response";

    /// <summary>
    /// Phase 27 — fix a failed CI build for an existing PR. Enters the same
    /// queue / worker / DeliverableLoop as <see cref="CommentResponse"/>;
    /// pinned to the original generator agent (Q27.7). Idempotent against
    /// the per-work-item open-rows partial unique index.
    /// </summary>
    public const string BuildFix = "build-fix";

    /// <summary>
    /// Phase 27 — work-item-level coding kind, used by the original Phase 17
    /// pipeline. Re-declared here so consumers can reference all queue kinds
    /// from a single static table for switch / display.
    /// </summary>
    public const string Coding = "coding";

    /// <summary>
    /// Plan task kind — architecture agents produce design documents and
    /// work-item trees, not application code. The runner loads
    /// <c>plan-system.md</c> and restricts the tool surface to read/write
    /// under <c>docs/specs/</c> plus WIT operations.
    /// </summary>
    public const string Plan = "plan";

    /// <summary>
    /// Resume-delivery — re-runs only the post-loop delivery phase
    /// (server-side push + build/test gate + PR creation) when the iter
    /// loop already finished with verdict=pass but the previous delivery
    /// failed (e.g. transient build break, push reject). The agent runner
    /// skips the prompt + DeliverableLoop call and synthesizes the result
    /// from the last persisted deliverable. Refused if no prior pass exists.
    /// </summary>
    public const string ResumeDelivery = "resume-delivery";
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

    /// <summary>
    /// Q24.14 — preferred agent unavailable AND lead unavailable; the row is
    /// awaiting human PO-tier intervention via the manual-assign endpoint.
    /// Counts as open so a duplicate auto-enqueue returns the existing
    /// blocked row instead of duplicating; the operator unblocks by manually
    /// reassigning a different generator.
    /// </summary>
    public const string Blocked      = "blocked";

    /// <summary>Open states count against the per-work-item idempotency guard.</summary>
    public static readonly string[] OpenStates = { AwaitingAck, AwaitingRoute, Queued, PickedUp, Blocked };
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
    /// task.worktree-tick | task.build-started | task.build-passed |
    /// task.build-failed | task.test-started | task.test-passed |
    /// task.test-failed</c>.
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

    // -- Phase 26/29 — build/test gate before PR creation --------------------

    /// <summary>
    /// Init command to run as the first pipeline step (e.g., project-specific bootstrap).
    /// When null/empty the init step is skipped.
    /// </summary>
    public string? InitCommand { get; set; }

    /// <summary>
    /// Restore / dependency-install command (e.g., <c>dotnet restore Workflow.sln</c>, <c>npm install</c>).
    /// When null/empty the restore step is skipped.
    /// </summary>
    public string? RestoreCommand { get; set; }

    /// <summary>
    /// Build command to run before creating a PR (e.g., <c>dotnet build Workflow.sln</c>).
    /// When null/empty the build gate is skipped. The first token is the executable;
    /// the rest are arguments passed via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public string? BuildCommand { get; set; }

    /// <summary>
    /// Test command to run before creating a PR (e.g., <c>dotnet test src/Foo.Tests/Foo.Tests.csproj</c>).
    /// When null/empty the test gate is skipped. The first token is the executable;
    /// the rest are arguments passed via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public string? TestCommand { get; set; }

    /// <summary>Build command timeout in seconds. Also used for init and restore. Defaults to 600 (10 min) when null.</summary>
    public int? BuildTimeoutSec { get; set; }

    /// <summary>Test command timeout in seconds. Defaults to 600 (10 min) when null.</summary>
    public int? TestTimeoutSec { get; set; }

    /// <summary>
    /// Phase 59 Stage A — per-repo override for the impacted-test
    /// rewrite. <c>null</c> = inherit the global
    /// <c>Forge:Test:ImpactedOnly</c> config value (defaults to true);
    /// <c>false</c> = run the operator-supplied test command verbatim
    /// (the legacy whole-suite behaviour); <c>true</c> = always narrow
    /// to impacted test projects when the command starts with
    /// <c>dotnet test</c> and carries no explicit project / filter.
    /// </summary>
    public bool? TestImpactedOnly { get; set; }

    // -- Phase 33 — persisted sync settings ----------------------------------

    /// <summary>Default assignee filter for WI sync (email or display name).</summary>
    public string? WiSyncAssignedTo { get; set; }

    /// <summary>Relative days back from today for WI sync date window (e.g. 30). Null means use absolute dates or no filter.</summary>
    public int? WiSyncDateFromDays { get; set; }

    /// <summary>Absolute start date for WI sync window (ISO 8601). Used when WiSyncDateFromDays is null.</summary>
    public string? WiSyncFromDate { get; set; }

    /// <summary>Absolute end date for WI sync window (ISO 8601). Null means today.</summary>
    public string? WiSyncToDate { get; set; }

    /// <summary>Default PR author filter (email or display name).</summary>
    public string? PrSyncCreatedBy { get; set; }

    /// <summary>Default PR state filter: active, completed, or all.</summary>
    public string? PrSyncStatus { get; set; }

    /// <summary>JSON array of pipeline definition IDs to sync builds from.</summary>
    public string? BuildPipelineIdsJson { get; set; }

    // -- Phase 34 — per-repo sync cadence + upstream-changedate cursor ------

    /// <summary>Per-repo work-item sync interval in seconds. Null = inherit <see cref="SyncConstants.WorkItemSyncIntervalSeconds"/>.</summary>
    public int? WorkItemSyncIntervalSeconds { get; set; }

    /// <summary>Per-repo PR sync interval in seconds. Null = inherit <see cref="SyncConstants.PrSyncIntervalSeconds"/>.</summary>
    public int? PrSyncIntervalSeconds { get; set; }

    /// <summary>Per-repo build sync interval in seconds. Null = inherit <see cref="SyncConstants.BuildSyncIntervalSeconds"/>.</summary>
    public int? BuildSyncIntervalSeconds { get; set; }

    /// <summary>
    /// ISO-8601 upstream-changedate cursor for delta sync (max <c>System.ChangedDate</c> /
    /// <c>updated_at</c> observed in the last successful run). Distinct from
    /// <see cref="LastSyncAt"/> which is the local clock at run start; this cursor is the
    /// real "everything changed before this point is already in our cache" boundary.
    /// </summary>
    public string? LastUpstreamChangedDate { get; set; }
}

/// <summary>
/// Phase 80 — control-side index / mirror of a repo. The full
/// <see cref="RepoRow"/> config moves to the per-product <c>{slug}.db</c>
/// (Stage 80.B); this thin row stays in the control DB so Forge can:
/// <list type="number">
///   <item>enforce the Q2/Q5 <b>global</b> uniqueness invariants
///         (<see cref="RemoteUrl"/> / <see cref="CloneRoot"/>) that SQLite
///         cannot enforce across separate <c>{slug}.db</c> files;</item>
///   <item>resolve <c>repoId → product</c> for request routing
///         (<c>X-Forge-Repo-Id</c>) without opening a product DB
///         (<see cref="ProductSlug"/> is denormalized for exactly this);</item>
///   <item>let the sync workers enumerate <i>due</i> repos in one control-side
///         query — which is why the mutable sync-due cursor
///         (<see cref="LastSyncStatus"/> etc.) and the per-repo cadence
///         overrides live here, not on the per-product row.</item>
/// </list>
///
/// <para>Maintained in lockstep with the per-product <c>repos</c> row on
/// create / update / delete (write product first, then upsert this mirror —
/// Stage 80.C). In Stage 80.A the table is created but left empty; nothing
/// reads or writes it yet.</para>
/// </summary>
public class RepoRegistryRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string ProductId { get; set; } = string.Empty;

    /// <summary>Denormalized product slug so a repo id resolves a <c>{slug}.db</c> without a Products join.</summary>
    public string ProductSlug { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    /// <summary>Q5 — globally unique across all products (partial unique index over active rows).</summary>
    public string RemoteUrl { get; set; } = string.Empty;

    /// <summary>Q2 — globally unique across all products (partial unique index over active rows).</summary>
    public string CloneRoot { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Soft-delete tombstone (ISO-8601). Non-null rows are excluded from the
    /// unique indexes so a re-add can reuse the same remote URL / clone root.
    /// </summary>
    public string? DeletedAt { get; set; }

    // -- sync-due cursor (authoritative; mirrors the per-product repos row) --

    /// <summary>ISO-8601 start time of the last successful sync run (mirror of <see cref="RepoRow.LastSyncAt"/>).</summary>
    public string? LastSyncAt { get; set; }

    /// <summary>Opaque per-provider page cursor for mid-run resume.</summary>
    public string? LastSyncCursor { get; set; }

    /// <summary>One of: idle, running, failed.</summary>
    public string LastSyncStatus { get; set; } = "idle";

    /// <summary>Last sync error message (surfaced in the UI).</summary>
    public string? LastSyncError { get; set; }

    /// <summary>Wall-clock guard for the 19-minute stale-run cancellation rule.</summary>
    public string? IsRunningSince { get; set; }

    /// <summary>ISO-8601 upstream-changedate delta cursor.</summary>
    public string? LastUpstreamChangedDate { get; set; }

    // -- per-repo sync cadence overrides (read at enumeration time) ----------

    /// <summary>Per-repo work-item sync interval (s). Null = inherit the global default.</summary>
    public int? WorkItemSyncIntervalSeconds { get; set; }

    /// <summary>Per-repo PR sync interval (s). Null = inherit the global default.</summary>
    public int? PrSyncIntervalSeconds { get; set; }

    /// <summary>Per-repo build sync interval (s). Null = inherit the global default.</summary>
    public int? BuildSyncIntervalSeconds { get; set; }
}

/// <summary>
/// Phase 29 — persistent build-validation run history. One row per
/// <c>POST /api/repos/{id}/validate-build</c> invocation. Steps are
/// stored as a JSON array in <see cref="StepsJson"/>.
/// </summary>
public class BuildValidationRunRow
{
    public string Id { get; set; } = string.Empty;

    public string RepoId { get; set; } = string.Empty;

    /// <summary>One of: queued, running, succeeded, failed, cancelled.</summary>
    public string Status { get; set; } = "queued";

    public string StartedAt { get; set; } = string.Empty;

    public string? FinishedAt { get; set; }

    public bool AllPassed { get; set; }

    public string? Ecosystem { get; set; }

    /// <summary>JSON array of step results: <c>[{ step, command, passed, exitCode, timedOut, stdout, stderr }]</c>.</summary>
    public string? StepsJson { get; set; }

    /// <summary>User who triggered the validation run.</summary>
    public string? TriggeredBy { get; set; }

    /// <summary>Error message if the run failed for infrastructure reasons (not a step failure).</summary>
    public string? ErrorMessage { get; set; }
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

    /// <summary>
    /// Phase 24 Q24.13 livelock guard — counts consecutive stalled
    /// comment-response deliverables on this work item. Reset to zero on any
    /// non-stalled outcome (pass / iterate that ends in pass / reject).
    /// When this hits <see cref="CommentResponseStallCap"/>, the next
    /// auto-enqueue refuses with <c>state='blocked'</c> and a lead inbox
    /// banner so the agent doesn't loop forever on a comment it can't
    /// satisfy.
    /// </summary>
    public int CommentResponseStallCount { get; set; }

    /// <summary>
    /// Phase 42 — stable slug from the originating Implementation Step in
    /// <c>implementation-plan.md</c> (e.g. <c>phase-2/stage-1/step-3</c>). Joins
    /// step-level work items back to the plan so the reconciler can detect
    /// added / removed / re-parented steps. Null on legacy rows.
    /// </summary>
    public string? PlanAnchor { get; set; }

    /// <summary>
    /// Phase 42 — SHA-256 of the Step's body at last reconcile. Mirrors
    /// <see cref="WorkstreamRow.PlanContentHash"/>; lets the reconciler
    /// distinguish "step text changed" from "step moved/renamed".
    /// </summary>
    public string? PlanContentHash { get; set; }

    /// <summary>
    /// Phase 51 — when the DeliverableLoop returns <c>"stalled-budget"</c>
    /// (per-task <c>MaxTaskBudgetUsd</c> hit), the work item is parked in
    /// <see cref="ExecutionStatuses.CooldownBudget"/> with this timestamp set
    /// to <c>UtcNow + Forge:Agents:BudgetCooldown:MaxWait</c> (default 10m).
    /// <see cref="Services.BudgetCooldownReconciler"/> ticks every 15s and
    /// resumes the work item under one of two conditions: (a) the operator
    /// has raised the crew's <c>MaxTaskBudgetUsd</c> so the cumulative cost
    /// is now under cap — resume immediately, same pair; (b) this timestamp
    /// has passed without a cap raise — resume with the pair rotated away
    /// from <see cref="LastPairAgentIdsJson"/>.
    /// Cleared on resume and on Cancel/Restart.
    /// </summary>
    public DateTimeOffset? BudgetCooldownUntilUtc { get; set; }

    /// <summary>
    /// Phase 51 — JSON-encoded <c>[generatorAgentId, evaluatorAgentId]</c>
    /// captured at the moment the work item entered cooldown. Used by the
    /// claim picker to exclude these agents on the cooldown-elapsed resume
    /// path (fresh pair gets a turn). Null when no prior pair (first run
    /// or a non-budget terminal verdict).
    /// </summary>
    public string? LastPairAgentIdsJson { get; set; }

    /// <summary>
    /// Phase 51 — start of the current budget window. When the
    /// cooldown-elapsed recovery fires the reconciler sets this to
    /// <c>UtcNow</c>; cap-vs-cost comparisons (in
    /// <see cref="Services.BudgetCooldownReconciler"/> and the
    /// orchestrator's <c>stalled-budget</c> re-evaluation) only sum
    /// <c>agent_usage</c> rows with <c>StartedAt &gt;= BudgetWindowStartUtc</c>.
    /// Lifetime spend remains in the ledger for crew salary roll-ups,
    /// but each cooldown window grants a fresh $0 starting point so
    /// recovered tasks aren't permanently locked out by their pre-window
    /// burn.
    /// Null = no window has elapsed yet (count from row creation).
    /// </summary>
    public DateTimeOffset? BudgetWindowStartUtc { get; set; }

    /// <summary>
    /// Phase 52 — total pair-attempts so far on this work item. Starts at
    /// 1 when the first pair is claimed; the <c>SloppyReassignmentReconciler</c>
    /// increments to 2 on the first reassignment and 3 on the second. When
    /// the row hits attempt 3 AND another sloppy trigger fires, the
    /// reconciler does NOT pick a fourth pair — it stamps
    /// <see cref="ExecutionStatus"/>=<c>failed</c> with
    /// <see cref="FailureReason"/>=<see cref="ScoringConstants.SloppyTerminalFailureReason"/>.
    /// Independent of <see cref="TrialCount"/> (per-claim retry counter
    /// from Phase 19).
    /// </summary>
    public int PairAttemptCount { get; set; } = 1;

    /// <summary>
    /// Phase 52 — JSON array of <c>[generatorAgentId, evaluatorAgentId]</c>
    /// tuples appended on every reassignment. The claim picker excludes
    /// ALL prior pairs (not just the most recent) so reassignment never
    /// rotates back to a previously-rejected pair. Null when no
    /// reassignment has happened yet.
    /// </summary>
    public string? PriorPairsJson { get; set; }

    /// <summary>
    /// Phase 52 — structured failure reason set on the sloppy-3-attempts
    /// terminal path. Today only set to
    /// <see cref="ScoringConstants.SloppyTerminalFailureReason"/>; reserved
    /// for future terminal-failure paths so the UI has one column to
    /// read regardless of the underlying cause.
    /// </summary>
    public string? FailureReason { get; set; }
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

    /// <summary>
    /// Phase 51 — per-task <c>MaxTaskBudgetUsd</c> cap hit. The work item is
    /// parked; <see cref="Services.BudgetCooldownReconciler"/> auto-resumes
    /// when the cap is raised (immediate) or after the cooldown window
    /// (with pair rotation). Distinct from <see cref="Failed"/> so the
    /// operator sees an auto-recovery is in flight rather than a dead end.
    /// </summary>
    public const string CooldownBudget = "cooldown-budget";

    /// <summary>
    /// Phase 52 — sloppy verdict fired; the work item is parked waiting
    /// for the <c>SloppyReassignmentReconciler</c> to either rotate to a
    /// fresh pair (full clean slate: transcript / deliverables / budget /
    /// worktree wiped) or — when <c>PairAttemptCount &gt;= MaxPairAttempts</c>
    /// — flip to <see cref="Failed"/> with the locked terminal reason.
    /// Distinct from <see cref="Failed"/> so the UI shows an auto-recovery
    /// is in flight.
    /// </summary>
    public const string PendingReassign = "pending-reassign";

    /// <summary>
    /// Phase 66 — generator emitted <c>## Open Questions</c> that the
    /// operator has not yet answered. The gen-eval loop refuses to spend
    /// another generator turn while questions remain unanswered (iterating
    /// without operator input cannot converge — observed on
    /// <c>code-intelligence:AGENT-MEMORY</c> plan-doc
    /// <c>implementation-plan.md</c>: 105 iters, score 94, $1860 burned
    /// before manual halt). The
    /// <c>POST /api/work-items/{id}/questions/answer</c> endpoint clears
    /// this status back to <see cref="Queued"/> (workstream) or
    /// <see cref="Idle"/> (plan-doc) and fire-and-forgets a resume so the
    /// loop seeds the prior chain and continues at iter N+1 with the
    /// answers folded into the generator's <c>## Operator answers</c>
    /// prelude (Phase 39 Layer C). Distinct from <see cref="Failed"/>
    /// so the UI renders a blue "awaiting answers" banner instead of the
    /// red orchestrator-halted banner.
    /// </summary>
    public const string AwaitingAnswers = "awaiting-answers";
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

/// <summary>
/// Phase 25 — per-item detail for a sync run. Records each work item or PR
/// that was created, updated, or removed so the sync history drawer can show
/// exactly what changed.
/// </summary>
public class SyncRunItemRow
{
    public long Id { get; set; }

    public string RunId { get; set; } = string.Empty;

    /// <summary>"workitem" or "pr".</summary>
    public string EntityKind { get; set; } = "workitem";

    /// <summary>The work item or PR id.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Title at the time of sync.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>"added", "updated", or "removed".</summary>
    public string Action { get; set; } = "updated";

    /// <summary>Optional — the provider reference number (ADO WIT id or GitHub issue #).</summary>
    public int? ProviderRef { get; set; }
}

/// <summary>
/// forge_implementation.md Phase 27 — one row per upstream pipeline run.
/// Keyed (Provider, ProviderBuildId); FK loose to <see cref="PrRow"/>. Carries
/// the per-build state (queued | running | not-started | succeeded | failed |
/// partially-succeeded | canceled), result (provider-native string), the
/// commit SHA that triggered the run, and the standard sync quintet.
/// <see cref="IsActive"/> tracks whether the build is still running so the
/// scheduled <c>BuildSyncJob</c> can stop polling terminal builds (Q27.11).
/// </summary>
public class BuildRow
{
    public long Id { get; set; }

    /// <summary>One of: <c>ado | github</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Upstream-native build id (numeric for ADO, run id for GitHub Actions).</summary>
    public string ProviderBuildId { get; set; } = string.Empty;

    /// <summary>FK → <see cref="PrRow.Id"/>; null when the build was discovered before the PR was tracked.</summary>
    public string? PrId { get; set; }

    public string RepoId { get; set; } = string.Empty;

    /// <summary>Pipeline / workflow id (ADO definition id; GitHub workflow id).</summary>
    public string? PipelineId { get; set; }

    public string? PipelineName { get; set; }

    /// <summary>One of: queued | running | not-started | succeeded | failed | partially-succeeded | canceled.</summary>
    public string State { get; set; } = "queued";

    /// <summary>Provider-native result (ADO: succeeded|failed|partiallySucceeded|canceled; GH: success|failure|cancelled|...).</summary>
    public string? Result { get; set; }

    /// <summary>The commit SHA that triggered this build run.</summary>
    public string? TriggeringCommit { get; set; }

    public string? Branch { get; set; }

    public string? WebUrl { get; set; }

    public string? StartedAt { get; set; }

    public string? FinishedAt { get; set; }

    public string? LastSyncAt { get; set; }

    public string LastSyncStatus { get; set; } = "idle";

    public string? LastSyncError { get; set; }

    public string? IsRunningSince { get; set; }

    /// <summary>True while <see cref="State"/> is in (queued|running|not-started); flips false on first terminal transition (Q27.11).</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Phase 27 — per-leaf-stage detail for a build. One row per failed leaf
/// (Q27.3). Successful builds skip stage population.
/// </summary>
public class BuildStageRow
{
    public long Id { get; set; }

    public long BuildId { get; set; }

    /// <summary>Provider-native ref — ADO timeline record id; GH job id.</summary>
    public string ProviderStageRef { get; set; } = string.Empty;

    public string StageName { get; set; } = string.Empty;

    public int StageOrder { get; set; }

    public string State { get; set; } = string.Empty;

    public string? Result { get; set; }

    public string? StartedAt { get; set; }

    public string? FinishedAt { get; set; }

    /// <summary>Provider-side log id / job id for the per-step log fetch.</summary>
    public string? LogRef { get; set; }

    /// <summary>SHA-256 of the persisted log tail content; identical hash on re-fetch is a no-op (Q27.4).</summary>
    public string? LogTailHash { get; set; }
}

/// <summary>
/// Phase 27 — tail-truncated log content for a stage. Composite key
/// (StageId, LogIndex). <see cref="TruncatedFromBytes"/> records the
/// upstream size before tail-truncation so the UI can show
/// "log truncated from 1.2 MB" (Q27.4).
/// </summary>
public class BuildStageLogRow
{
    public long StageId { get; set; }

    public int LogIndex { get; set; }

    public string Content { get; set; } = string.Empty;

    public int SizeBytes { get; set; }

    public int TruncatedFromBytes { get; set; }

    public string FetchedAt { get; set; } = string.Empty;
}

/// <summary>
/// Phase 27 — extracted artifact metadata (Q27.5). The actual files live
/// under <see cref="ExtractedPath"/>; <see cref="BuildArtifactFileRow"/>
/// indexes them.
/// </summary>
public class BuildArtifactRow
{
    public long Id { get; set; }

    public long BuildId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? DownloadUrl { get; set; }

    public long SizeBytes { get; set; }

    public string? ExtractedPath { get; set; }

    public string? FetchedAt { get; set; }

    public string? ExpiredAt { get; set; }
}

/// <summary>
/// Phase 27 — indexed file inside an extracted artifact. <see cref="IsLog"/>
/// is set when the file extension is in (.log | .txt | .trx | .xml) so the
/// agent prompt can prioritize them (Q27.5).
/// </summary>
public class BuildArtifactFileRow
{
    public long Id { get; set; }

    public long ArtifactId { get; set; }

    public string RelPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public bool IsLog { get; set; }
}

/// <summary>
/// Phase 27 — parsed FailurePoint row, persisted by
/// <c>BuildFailureParser</c>. Multi-failure logs produce multiple rows
/// (Q27.6). <see cref="SourceLogTailOffset"/> is the byte offset of the
/// match within the persisted log tail (or -1 when the match was found in
/// an extracted artifact log per Pass 1.5).
/// </summary>
public class BuildFailurePointRow
{
    public long Id { get; set; }

    public long StageId { get; set; }

    /// <summary>One of: compile | build | test | runtime | npm | other.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? FilePath { get; set; }

    public int? Line { get; set; }

    public int? Column { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Byte offset in the stage tail (or -1 when the match was in an artifact log; payload carries name + relPath).</summary>
    public int SourceLogTailOffset { get; set; }

    public string? Severity { get; set; }

    /// <summary>Optional JSON payload — used by Pass 1.5 to carry { artifactName, relPath } for artifact-found matches.</summary>
    public string? PayloadJson { get; set; }
}

/// <summary>
/// Phase 27 Q27.12 — staging row for a deferred build-fix enqueue. Written
/// when a build failure lands while another open queue row for the same
/// work item is in flight (typically <c>comment-response</c> or
/// <c>coding</c>). The completion hook in <c>TaskQueueService</c> drains
/// these rows and promotes them to real <c>task_queue</c> rows when the
/// blocking row reaches a terminal state.
/// </summary>
public class BuildFixPendingRow
{
    public long Id { get; set; }

    public string WorkItemId { get; set; } = string.Empty;

    public long BuildId { get; set; }

    public string EnqueuedAt { get; set; } = string.Empty;

    public string? ReasonJson { get; set; }
}

/// <summary>
/// Phase 27 — operator annotation on a build (Q27.6 fall-through path).
/// <see cref="Category"/> is one of <c>infra | flaky-test | env | code</c>;
/// the auto-enqueue suppresses subsequent failures with the same fingerprint
/// when category != <c>code</c>.
/// </summary>
public class BuildAnnotationRow
{
    public long Id { get; set; }

    public long BuildId { get; set; }

    public string Category { get; set; } = string.Empty;

    public string? RootCause { get; set; }

    /// <summary>JSON array of file paths the operator suggests inspecting.</summary>
    public string? SuggestedFiles { get; set; }

    public string? ByUserId { get; set; }

    public string At { get; set; } = string.Empty;
}

/// <summary>
/// forge_implementation.md Phase 28 — story spawn row. The unit of end-to-end
/// delivery: a multi-team orchestrated lifecycle that progresses through
/// plan → decompose → implement → review → qa → done. Each phase is gated
/// (the next phase cannot start until the current phase's artifacts are
/// committed via a merged PR).
/// </summary>
public class StorySpawnRow
{
    /// <summary>Primary key, e.g. "spawn-{storyId}".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>ADO work item id (Feature / Epic / PBI) the story tracks.</summary>
    public string StoryId { get; set; } = string.Empty;

    /// <summary>Display title sourced from the upstream work item.</summary>
    public string StoryTitle { get; set; } = string.Empty;

    /// <summary>Repo this story spawns into. FK to <see cref="RepoRow"/>; nullable for legacy / unbound rows.</summary>
    public string? RepoId { get; set; }

    /// <summary>One of: <c>plan | decompose | implement | review | qa | done</c>.</summary>
    public string Phase { get; set; } = StorySpawnPhases.Plan;

    /// <summary>
    /// Operator-chosen base branch for every PR produced under this spawn
    /// (plan PR, workstream PRs, QA PR, step-def PRs). Defaults to <c>"main"</c>.
    /// When non-default, the orchestrator branches it from <c>main</c> on first
    /// drive; rebases on change pull main forward and replay every
    /// already-merged spawn PR onto the new tip so downstream work stays
    /// consistent.
    /// </summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>Branch holding planning artifacts, e.g. "story/{storyId}/plan".</summary>
    public string? PlanBranch { get; set; }

    /// <summary>PR id for the planning artifacts; null until plan PR is opened.</summary>
    public string? PlanPrId { get; set; }

    /// <summary>JSON: array of <c>{ id, title, description, acceptanceCriteria, deps[], deliverables[], timeline, estimatedEffort }</c>.</summary>
    public string? DecompositionJson { get; set; }

    /// <summary>True when the operator has approved the decomposition (gate to phase=implement).</summary>
    public bool DecompositionApproved { get; set; }

    /// <summary>Branch holding the QA feature files, e.g. "story/{storyId}/qa".</summary>
    public string? QaBranch { get; set; }

    /// <summary>PR id for the QA feature files; null until the QA PR is opened.</summary>
    public string? QaPrId { get; set; }

    /// <summary>
    /// Phase 53 — sequential plan-doc state. Ordered JSON array; each entry
    /// captures one plan doc's branch and PR:
    /// <c>[{ "key": "architecture", "branch": "story/{id}/plan-architecture", "prId": "..." | null }, ...]</c>.
    /// Order is canonical: <c>architecture → tech-spec → implementation-plan
    /// → e2e-scenarios</c>. Doc N+1's gen↔eval loop only starts after
    /// doc N's <c>prId</c> references a PR with <c>Col='merged'</c> AND
    /// the latest doc deliverable carries <c>Verdict='pass'</c>.
    ///
    /// <para><c>null</c> on legacy spawns (created before Phase 53); they
    /// continue to drive the single-PR <see cref="PlanPrId"/> path. New
    /// spawns initialise to <c>"[]"</c> at create time so the
    /// orchestrator's discriminator is unambiguous.</para>
    /// </summary>
    public string? PlanDocPrsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Architect agent's user prompt (sourced from the originating
    /// <see cref="ProductStoryRow"/>'s description, or the upstream work item's
    /// description). Empty until set at spawn time.
    /// </summary>
    public string StoryDescription { get; set; } = string.Empty;

    /// <summary>Story points carried over from the originating story for sprint planning.</summary>
    public int StoryPoints { get; set; }

    /// <summary>
    /// Operator-visible error message captured the last time the orchestrator
    /// failed to drive this spawn forward (architect crew below pass, plan PR
    /// merge conflict, decomposition empty, etc.). Null while healthy. Cleared
    /// when the failing step succeeds or the operator clicks Retry from the
    /// workstream view.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>Timestamp paired with <see cref="LastError"/>; null when error is null.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>
    /// Phase 41 — JSON array of plan-doc artifact keys whose gen↔eval Resume
    /// loop is currently in flight. Set by <c>ResumeArtifact</c> before the
    /// fire-and-forget Task.Run starts, cleared in the task's finally block.
    /// On boot, <c>StoryResumeRecoveryService</c> sweeps non-empty entries and
    /// re-fires the loop so a process kill mid-Resume self-heals.
    /// </summary>
    public string? ResumePendingJson { get; set; }

    /// <summary>
    /// Phase 43 — JSON array of operator-uploaded reference documents.
    /// Each entry: <c>{ "id": "guid", "fileName": "spec.md", "storagePath": "relative/path", "mimeType": "text/markdown", "sizeBytes": 1234 }</c>.
    /// Files live on disk under <c>E:\forge\attachments\{spawnId}\</c>.
    /// Architect agents read these before producing plan docs.
    /// </summary>
    public string? AttachmentsJson { get; set; }

    /// <summary>
    /// Phase 46 — when <c>true</c>, the orchestrator sets
    /// <see cref="PrRow.AutoMerge"/> on every PR it opens so
    /// <c>AutoMergeWatcher</c> auto-merges them after build + review gates
    /// pass. Also carried through to workstream work items so engineer
    /// agents know their task PRs will be merged automatically. Honoured
    /// for any base, including <c>"main"</c>; the operator arms the flag
    /// deliberately.
    /// </summary>
    public bool AutoMerge { get; set; }

    /// <summary>
    /// Phase 50 — operator-authored decision pins. Each pin is a binding
    /// directive that gets prepended to EVERY future iter prompt for
    /// every plan-doc / workstream under this spawn. Used to break out
    /// of cross-doc convergence loops (e.g. "DefaultAction lives in
    /// sidecar — STOP flipping. tech-spec HC-3 is authoritative.")
    /// where the agent keeps re-litigating the same decision because
    /// sibling docs change between iters.
    ///
    /// <para>JSON shape:
    /// <c>[{ "id": "guid", "text": "...", "workItemId": "plan-doc:..." | null, "createdAt": "ISO-8601", "createdBy": "actor" }]</c>.
    /// <c>workItemId</c> null means "applies to every iter on this
    /// spawn"; non-null scopes the pin to one specific doc / workstream.</para>
    /// </summary>
    public string? DecisionPinsJson { get; set; }

    /// <summary>
    /// Phase 63 — stores the phase the spawn was in before the operator
    /// paused it. <c>null</c> unless <see cref="Phase"/> is <c>"paused"</c>.
    /// The resume endpoint restores <see cref="Phase"/> to this value and
    /// clears the field.
    /// </summary>
    public string? PausedFromPhase { get; set; }
}

/// <summary>Well-known phase constants for <see cref="StorySpawnRow.Phase"/>.</summary>
public static class StorySpawnPhases
{
    public const string Plan = "plan";
    public const string Decompose = "decompose";
    public const string Implement = "implement";
    public const string Review = "review";
    public const string Qa = "qa";
    public const string Done = "done";

    /// <summary>
    /// Terminal sentinel set by <see cref="Forge.Api.Endpoints.StoriesEndpoints.DeleteSpawn"/>
    /// at the top of the delete pipeline. The orchestrator's <c>switch</c>
    /// on <see cref="StorySpawnRow.Phase"/> does not match this value, so
    /// <see cref="Forge.Api.Services.Stories.StorySpawnOrchestrator.RunOneStepAsync"/>
    /// no-ops and returns <c>false</c> — preventing the regression-to-Plan
    /// cascade from re-claiming plan-doc agents while the delete is wiping
    /// deliverables. Without this, observed 2026-05-13 on
    /// <c>code-intelligence:AGENT-MEMORY</c>: DELETE wiped
    /// <c>agent_usage</c> + deliverables → Phase 48 gate regressed
    /// <c>implement</c> → <c>plan</c> → 5 plan-doc agents got re-claimed
    /// (architecture/tech-spec/impl-plan/e2e × parallel) → SQLite write-lock
    /// contention crashed the DELETE's SaveChangesAsync with
    /// <c>An error occurred while saving the entity changes</c>.
    /// </summary>
    public const string Deleting = "deleting";

    /// <summary>
    /// Phase 63 — operator-initiated pause. The orchestrator's <c>switch</c>
    /// does not match this value so <see cref="Forge.Api.Services.Stories.StorySpawnOrchestrator.RunOneStepAsync"/>
    /// no-ops. All in-flight agents are killed and workstreams are set to
    /// <c>Status="paused"</c> on entry. The resume endpoint restores
    /// <see cref="StorySpawnRow.PausedFromPhase"/> and flips workstreams back
    /// to their prior status.
    /// </summary>
    public const string Paused = "paused";

    /// <summary>
    /// Ordered phase list. Phase rollback is rejected (Phase 28 non-goal),
    /// so an advance is allowed only when the target index is exactly
    /// current + 1, or terminal "done" when the completion check passes.
    /// </summary>
    public static readonly string[] Order = { Plan, Decompose, Implement, Review, Qa, Done };

    public static int IndexOf(string phase) => Array.IndexOf(Order, phase);
}

/// <summary>
/// forge_implementation.md Phase 28 — inter-workstream dependency edge. A
/// workstream cannot start until every <see cref="DependsOnId"/> entry has
/// its PR merged. Composite primary key (WorkstreamId, DependsOnId).
/// </summary>
public class WorkstreamDepRow
{
    public string WorkstreamId { get; set; } = string.Empty;

    public string DependsOnId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 55 (docs/forge/sloppy_guard.md) — persistent cross-iteration
/// memory for a (work item, agent, role) tuple. Each call to
/// <c>IAgentRunner.StartAsync</c> opens a fresh provider session by
/// default, so without this row Layer 2 (Claude/CLI session resume,
/// Claude/SDK history replay, Copilot/SDK long-lived session) cannot
/// reconnect across iterations.
///
/// <para>One row per <c>(WorkItemId, AgentId, Role)</c> — enforced by a
/// unique index. A reassigned pair (Phase 52, when re-enabled) starts
/// with no row because the new agent has a different
/// <see cref="AgentId"/>; that's the "fresh agent = fresh memory" rule
/// decided in the plan.</para>
///
/// <para>Two payload columns are mutually exclusive in practice but both
/// nullable on the schema so a single row shape works for every backend
/// cell:</para>
/// <list type="bullet">
/// <item><see cref="SessionId"/> — Claude/CLI's <c>--resume</c> id and
/// the Copilot SDK's session handle. Opaque to Forge.</item>
/// <item><see cref="MessagesJson"/> — Claude/SDK's full conversation
/// history serialized as a JSON array of <c>{role, content, iter,
/// tokens}</c> turns. Compaction (Phase F) rewrites this in place.</item>
/// </list>
/// </summary>
public class AgentMemoryRow
{
    public long Id { get; set; }

    /// <summary>Matches <see cref="DeliverableRow.WorkItemId"/> — workstream id for impl, synthetic plan-doc id for plan/decompose.</summary>
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>Agent that owns this conversation. Memory is scoped to the agent so reassigned pairs start fresh.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>One of <see cref="AgentMemoryRoles"/>.</summary>
    public string Role { get; set; } = AgentMemoryRoles.Generator;

    /// <summary><c>"claude"</c> | <c>"copilot"</c>. Snapshot of the backend at row creation; informs which payload column the consumer reads.</summary>
    public string Backend { get; set; } = string.Empty;

    /// <summary><c>"cli"</c> | <c>"sdk"</c>. Snapshot of the invoke mode at row creation.</summary>
    public string InvokeMode { get; set; } = string.Empty;

    /// <summary>Provider-native session handle (Claude/CLI <c>--resume</c> id; Copilot/SDK pool key). Null until the first Layer-2 invocation lands.</summary>
    public string? SessionId { get; set; }

    /// <summary>JSON array of <c>ConversationTurn</c> records. Null when the backend doesn't replay history (CLI / Copilot/SDK).</summary>
    public string? MessagesJson { get; set; }

    /// <summary>Most recent iteration that wrote to this row. 0 before the first write.</summary>
    public int LastIter { get; set; }

    /// <summary>Best-effort estimate of input tokens the next invocation will see (sum of prior turns; informs compaction trigger).</summary>
    public long ApproxInputTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 55 — well-known values for <see cref="AgentMemoryRow.Role"/>.
/// Memory is keyed on role so a crew's generator and evaluator agents
/// can each maintain their own continuity without crosstalk.
/// </summary>
public static class AgentMemoryRoles
{
    public const string Generator = "generator";

    public const string Evaluator = "evaluator";
}
