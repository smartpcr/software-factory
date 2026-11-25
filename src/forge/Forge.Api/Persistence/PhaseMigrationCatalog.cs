// -----------------------------------------------------------------------
// <copyright file="PhaseMigrationCatalog.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

/// <summary>
/// Phase G.4 — single source of truth classifying each phase migration as
/// control-side, product-side, or mixed. Used by the boot migration runner
/// (Program.cs) and as a reference for the future post-split fan-out work.
///
/// <para>
/// <b>Classification rules</b> (matched against <see cref="Split.DbBoundary"/>):
/// </para>
/// <list type="bullet">
///   <item><see cref="PhaseMigrationKind.Control"/> — migration only
///         touches tables in <see cref="Split.DbKind.Control"/>. Runs
///         once against the control DB.</item>
///   <item><see cref="PhaseMigrationKind.Product"/> — migration only
///         touches tables in <see cref="Split.DbKind.Product"/>. Today
///         this runs once against <c>ProductDbContext</c> with no slug
///         (which falls back to the control file during Phase D interim).
///         Post-Phase-F splits, this must fan out and run once per product
///         DB so all <c>{slug}.db</c> files share the same schema.</item>
///   <item><see cref="PhaseMigrationKind.Mixed"/> — migration touches
///         tables on both sides. Needs both contexts. Long-term, these
///         should be refactored into separate Control + Product passes;
///         flagged here so the future per-product fan-out can decide
///         whether to also re-run the mixed migration once per product DB
///         or only the product-side ops.</item>
/// </list>
///
/// <para>
/// Adding a new phase migration: place it in the appropriate bucket below.
/// The runtime invariant check in <c>Program.cs</c> verifies the catalog
/// stays in sync with the invocation list.
/// </para>
/// </summary>
public enum PhaseMigrationKind
{
    /// <summary>Touches only control tables — products, repos, users, agents,
    /// crews, agent_usage, agent_claims, audit, forge_seed_state.</summary>
    Control,

    /// <summary>Touches only product tables — work items, workstreams,
    /// story spawns, plan docs, builds, PRs, task queue, deliverables, etc.</summary>
    Product,

    /// <summary>Touches both. Either splits across both files post-Phase-F
    /// or needs refactoring into two passes. Currently runs against both
    /// contexts (pre-Phase-F this is effectively a single SQLite file).</summary>
    Mixed,
}

/// <summary>
/// Per-migration metadata. <see cref="PhaseNumber"/> is the integer phase
/// suffix (e.g., 8 for <c>Phase8Migration</c>).
/// </summary>
/// <param name="PhaseNumber">The phase number suffix in the class name.</param>
/// <param name="Kind">DB-boundary classification.</param>
/// <param name="ControlTables">Lowercase control-table names touched.</param>
/// <param name="ProductTables">Lowercase product-table names touched.</param>
public sealed record PhaseMigrationSpec(
    int PhaseNumber,
    PhaseMigrationKind Kind,
    IReadOnlyList<string> ControlTables,
    IReadOnlyList<string> ProductTables);

/// <summary>
/// Classification catalog for every Phase migration shipped in
/// <c>Forge.Api.Persistence</c>. Kept alphabetised by phase number.
/// </summary>
public static class PhaseMigrationCatalog
{
    public static readonly IReadOnlyList<PhaseMigrationSpec> All = new[]
    {
        // ── Mixed: touch both control + product tables ───────────────────
        new PhaseMigrationSpec(8,  PhaseMigrationKind.Mixed,
            ControlTables: new[] { "crews", "forge_actions", "products", "repos", "user_settings", "users" },
            ProductTables: new[] { "cards", "epics", "product_stories", "prs", "workstreams" }),
        new PhaseMigrationSpec(14, PhaseMigrationKind.Mixed,
            ControlTables: new[] { "agent_level_history", "agents", "crews" },
            ProductTables: new[] { "deliverables" }),
        new PhaseMigrationSpec(15, PhaseMigrationKind.Mixed,
            ControlTables: new[] { "products" },
            ProductTables: new[] { "product_area_paths", "product_iterations", "product_saved_prefixes", "repo_iteration_bindings" }),
        new PhaseMigrationSpec(16, PhaseMigrationKind.Mixed,
            ControlTables: new[] { "agent_usage", "crews" },
            ProductTables: new[] { "task_transcript_event", "work_items" }),
        new PhaseMigrationSpec(22, PhaseMigrationKind.Mixed,
            ControlTables: new[] { "forge_seed_state" },
            ProductTables: new[] { "agent_assignments", "work_items" }),
        new PhaseMigrationSpec(23, PhaseMigrationKind.Mixed,
            ControlTables: new[] { "repos" },
            ProductTables: new[] { "work_item_sync_runs" }),
        new PhaseMigrationSpec(29, PhaseMigrationKind.Mixed,
            ControlTables: new[] { "repos" },
            ProductTables: new[] { "build_validation_runs" }),
        new PhaseMigrationSpec(55, PhaseMigrationKind.Mixed,
            ControlTables: new[] { "agents", "crews" },
            ProductTables: new[] { "agent_memory", "idempotency" }),

        // ── Control-only ─────────────────────────────────────────────────
        new PhaseMigrationSpec(11, PhaseMigrationKind.Control,
            ControlTables: new[] { "agent_history", "agent_usage", "agents", "crew_history", "crew_members", "crews", "domain_experts", "user_product_roles", "user_settings" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(18, PhaseMigrationKind.Control,
            ControlTables: new[] { "crews" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(26, PhaseMigrationKind.Control,
            ControlTables: new[] { "repos" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(33, PhaseMigrationKind.Control,
            ControlTables: new[] { "repos" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(34, PhaseMigrationKind.Control,
            ControlTables: new[] { "repos" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(35, PhaseMigrationKind.Control,
            ControlTables: new[] { "crews", "products" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(44, PhaseMigrationKind.Control,
            ControlTables: new[] { "agent_claims", "agents" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(54, PhaseMigrationKind.Control,
            ControlTables: new[] { "agent_usage" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(59, PhaseMigrationKind.Control,
            ControlTables: new[] { "repos" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(72, PhaseMigrationKind.Control,
            ControlTables: new[] { "agent_usage" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(77, PhaseMigrationKind.Control,
            ControlTables: new[] { "user_settings" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(78, PhaseMigrationKind.Control,
            ControlTables: new[] { "agents" },
            ProductTables: Array.Empty<string>()),
        // Phase 80 (Stage 80.A) — control-side repo registry + products.DefaultRepoId.
        new PhaseMigrationSpec(80, PhaseMigrationKind.Control,
            ControlTables: new[] { "products", "repo_registry" },
            ProductTables: Array.Empty<string>()),

        // ── Product-only ─────────────────────────────────────────────────
        // 79 lives in the Product bucket below alongside the other
        // workstreams-touching migrations; entry is in the Product
        // section to keep the alphabetised buckets honest.
        new PhaseMigrationSpec(12, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "work_item_links", "work_items" }),
        new PhaseMigrationSpec(17, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "task_queue", "task_queue_history" }),
        new PhaseMigrationSpec(19, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "work_items" }),
        new PhaseMigrationSpec(20, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "deliverables", "task_queue" }),
        new PhaseMigrationSpec(21, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "task_queue" }),
        new PhaseMigrationSpec(24, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "pr_comment_history", "pr_comments", "prs", "task_queue" }),
        new PhaseMigrationSpec(25, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "sync_run_items" }),
        new PhaseMigrationSpec(27, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "prs" }),
        new PhaseMigrationSpec(28, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "prs" }),
        new PhaseMigrationSpec(30, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "task_queue", "work_items" }),
        new PhaseMigrationSpec(31, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "build_annotations", "build_artifact_files", "build_artifacts", "build_failure_points", "build_fix_pending", "build_stage_logs", "build_stages", "builds" }),
        new PhaseMigrationSpec(32, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns", "workstream_deps", "workstreams" }),
        new PhaseMigrationSpec(36, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "pr_comments" }),
        new PhaseMigrationSpec(37, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns" }),
        new PhaseMigrationSpec(38, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns", "work_items", "workstreams" }),
        new PhaseMigrationSpec(39, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "deliverable_questions" }),
        new PhaseMigrationSpec(40, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns", "work_items" }),
        new PhaseMigrationSpec(41, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns" }),
        new PhaseMigrationSpec(42, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "work_items", "workstreams" }),
        new PhaseMigrationSpec(43, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "product_stories", "story_spawns" }),
        new PhaseMigrationSpec(45, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "product_stories", "story_spawns" }),
        new PhaseMigrationSpec(46, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "product_stories", "story_spawns" }),
        new PhaseMigrationSpec(47, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns", "workstreams" }),
        new PhaseMigrationSpec(48, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "task_queue" }),
        new PhaseMigrationSpec(49, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "workstreams" }),
        new PhaseMigrationSpec(50, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns" }),
        new PhaseMigrationSpec(51, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "work_items" }),
        new PhaseMigrationSpec(52, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "agent_pair_assignment", "work_items" }),
        new PhaseMigrationSpec(53, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns" }),
        new PhaseMigrationSpec(60, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "work_items" }),
        new PhaseMigrationSpec(63, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "story_spawns", "workstreams" }),
        new PhaseMigrationSpec(64, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "deliverables" }),
        new PhaseMigrationSpec(65, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "deliverables" }),
        new PhaseMigrationSpec(79, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "workstreams" }),
        // Phase 80 (Stage 80.A) — product_stories.RepoId. Product-kind so the
        // runner fans it out to every {slug}.db (a Mixed split would skip them).
        new PhaseMigrationSpec(81, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "product_stories" }),
        // Phase 82 — iteration_prompt audit table (per-iter generator +
        // evaluator prompts). Product-only so the migration fans out to
        // each {slug}.db. Lives outside transcript retention so the audit
        // trail survives the TranscriptRetentionJanitor + TaskChannel cap.
        new PhaseMigrationSpec(82, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "iteration_prompt" }),
        // Phase 83 — repos.PlanDocBaseDir (per-repo plan-doc base dir override).
        // Product-only: post-Phase-80.B `repos` is a ProductDbContext entity in
        // each {slug}.db, so the runner fans this out per product DB.
        new PhaseMigrationSpec(83, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "repos" }),
        // Phase 84 — repos.RunTestsElevated (per-repo build/test elevation flag).
        // Product-only: post-Phase-80.B `repos` is a ProductDbContext entity in
        // each {slug}.db, so the runner fans this out per product DB.
        new PhaseMigrationSpec(84, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "repos" }),
        // Phase 87 — deliverables.PairAttempt (per-row 1-based pair-attempt
        // counter mirroring WorkItem.PairAttemptCount at deliverable-write
        // time). Disambiguates rows whose `Iter` column has been reset by an
        // operator Retry / SoftResetForResumeAsync path. Product-only, fans
        // out per {slug}.db. (Phases 85 / 86 are non-migration code-phase
        // numbers used by Phase85GitignoreAutoFixTests, Phase85AgentLruRotationTests
        // and Phase86E2eGitignoreStallTests — they do not need a schema row.)
        new PhaseMigrationSpec(87, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "deliverables" }),
        // Phase 88 — PR merge metadata for already-merged PR evaluation fallback.
        new PhaseMigrationSpec(88, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "prs" }),
        // Phase 89 — agent_level_history.Actor for per-agent PO override
        // actions (promote / demote / hold) from the Performance view's
        // DecisionCard. Control-only, additive nullable column.
        new PhaseMigrationSpec(89, PhaseMigrationKind.Control,
            ControlTables: new[] { "agent_level_history" },
            ProductTables: Array.Empty<string>()),
        // Phase 90 — deliverables.ReasoningText (the generator model's
        // reasoning/"thinking" text per iteration, from the Copilot SDK's
        // AssistantReasoningDeltaEvent). Product-only, additive nullable
        // column; fans out per {slug}.db. Surfaced per-iteration in the
        // Conversation Summary tab.
        new PhaseMigrationSpec(90, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "deliverables" }),
        // Phase 91 — deliverable_questions.ExplanationJson (agent-authored +
        // on-demand operator-facing rationale for the Questions-tab Explain
        // popup). Product-only; additive nullable column; fans out per {slug}.db.
        new PhaseMigrationSpec(91, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "deliverable_questions" }),
        // Phase 92 — workstreams.LastBuildGate / LastTestGate (durable mirror of
        // the latest build/test gate outcome so the phase-board BUILD/TEST badges
        // survive transcript-event eviction). Product-only; additive nullable
        // columns; fans out per {slug}.db.
        new PhaseMigrationSpec(92, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "workstreams" }),
        // Phase 93 — operator_prompts (per-workstream human operator steering
        // prompts injected into both generator + evaluator). Product-only,
        // fans out per {slug}.db. New table; no ordering dependency. (Renumbered
        // from 92 on merge — origin's operator_prompts and local's build/test-gate
        // migration both claimed 92; see Phase92Migration / Phase93Migration.)
        new PhaseMigrationSpec(93, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "operator_prompts" }),
        // Phase 94 — repos.E2eUseBddPattern (per-repo qa-e2e test-style override:
        // BDD/Gherkin vs plain idiomatic test methods). Product-only: post-Phase-80.B
        // `repos` is a ProductDbContext entity in each {slug}.db, so the runner fans
        // this out per product DB. Additive nullable column; no ordering dependency.
        new PhaseMigrationSpec(94, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "repos" }),
        // Phase 95 (chat bridge) — control-side chat_notifications outbox +
        // chat_subscribers allow-list. Control-only (operator + chat identity
        // are cross-product); new tables, no ordering dependency.
        new PhaseMigrationSpec(95, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_notifications", "chat_subscribers" },
            ProductTables: Array.Empty<string>()),
        // Phase 96 (chat bridge, Phase 4) — control-side chat_bridge_state
        // (inbound long-poll cursor). Control-only; new single-row table.
        new PhaseMigrationSpec(96, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_bridge_state" },
            ProductTables: Array.Empty<string>()),
        // Phase 97 (chat bridge, Part B) — chat_spawn_topics + chat_notifications.SpawnId.
        new PhaseMigrationSpec(97, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_spawn_topics", "chat_notifications" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(98, PhaseMigrationKind.Control,
            ControlTables: new[] { "spawn_plugins", "chat_connections", "chat_connection_products", "chat_destinations", "chat_subscriptions", "chat_conversation_bindings" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(99, PhaseMigrationKind.Control,
            ControlTables: new[] { "spawn_plugins" }, ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(100, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_notifications", "chat_subscriptions", "chat_deliveries", "chat_delivery_messages" },
            ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(101, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(), ProductTables: new[] { "chat_action_receipts" }),
        new PhaseMigrationSpec(102, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_inbox", "chat_connection_state", "chat_receiver_state" }, ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(103, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_operator_links", "chat_link_challenges", "chat_action_tokens" }, ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(104, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_inbox", "chat_action_tokens", "chat_conversation_bindings" }, ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(105, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_receiver_state", "chat_conversation_bindings" }, ProductTables: Array.Empty<string>()),
        new PhaseMigrationSpec(106, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_migration_state", "chat_migration_items" }, ProductTables: Array.Empty<string>()),
        // Phase 107 — per-workstream chat conversations + All-products connections.
        new PhaseMigrationSpec(107, PhaseMigrationKind.Control,
            ControlTables: new[] { "chat_connections", "chat_subscriptions", "chat_conversation_bindings" }, ProductTables: Array.Empty<string>()),
        // Phase 108 — product rule catalog: tags, rules, per-workstream tag
        // overrides. Product-only, new tables, no ordering dependency.
        new PhaseMigrationSpec(108, PhaseMigrationKind.Product,
            ControlTables: Array.Empty<string>(),
            ProductTables: new[] { "catalog_rules", "rule_policies", "rule_tags", "work_item_policies", "work_item_rules", "workstream_tag_overrides" }),
    };

    /// <summary>Lookup table keyed by phase number.</summary>
    public static readonly IReadOnlyDictionary<int, PhaseMigrationSpec> ByPhase =
        All.ToDictionary(s => s.PhaseNumber);

    /// <summary>Counts by kind — used by smoke tests + ops dashboards.</summary>
    public static (int Control, int Product, int Mixed) Counts =>
        (
            All.Count(s => s.Kind == PhaseMigrationKind.Control),
            All.Count(s => s.Kind == PhaseMigrationKind.Product),
            All.Count(s => s.Kind == PhaseMigrationKind.Mixed)
        );
}
