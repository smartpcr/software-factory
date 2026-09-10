// -----------------------------------------------------------------------
// <copyright file="DbModelControl.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase D — EF model configuration for the **Control DB** entities.
/// Called from <see cref="ControlDbContext.OnModelCreating"/>. The shape
/// must match the pre-split union-model exactly so the migrations
/// already applied to <c>forge.db</c> remain valid.
///
/// <para>See <c>docs/forge/split-db.md</c> for which tables belong here.</para>
/// </summary>
public static class DbModelControl
{
    public static void Configure(ModelBuilder mb)
    {
        // Phase 6.10 — audit log. EF maps AuditRow → "forge_actions" table.
        mb.Entity<AuditRow>().ToTable("forge_actions").HasKey(x => x.Id);

        // Phase 8 — products. (Phase 80.B: the full `repos` table moved to the
        // per-product DB; see DbModelProduct. The control DB keeps only the
        // `repo_registry` index, configured below, which carries the Q2/Q5
        // GLOBAL uniqueness that SQLite can't enforce across {slug}.db files.)
        mb.Entity<ProductRow>().ToTable("products").HasKey(x => x.Id);
        mb.Entity<ProductRow>().HasIndex(x => x.Slug).IsUnique();

        // Phase 9.6 — identified users.
        mb.Entity<UserRow>().ToTable("users").HasKey(x => x.Id);
        mb.Entity<UserRow>().HasIndex(x => x.Email).IsUnique();

        // Phase 10.1 — per-user preferences.
        mb.Entity<UserSettingsRow>().ToTable("user_settings").HasKey(x => x.UserId);

        // Phase 9.12 — per-user / per-product role override.
        mb.Entity<UserProductRoleRow>()
            .ToTable("user_product_roles")
            .HasKey(x => new { x.UserId, x.ProductId });
        mb.Entity<UserProductRoleRow>().HasIndex(x => x.UserId);
        mb.Entity<UserProductRoleRow>().HasIndex(x => x.ProductId);

        // Phase 10.6 — crew rows.
        mb.Entity<CrewRow>().ToTable("crews").HasKey(x => x.Id);
        // SQLite stores decimal as TEXT; explicit conversion keeps EF happy.
        mb.Entity<CrewRow>().Property(x => x.ModelInputPricePerMTok).HasConversion<double>();
        mb.Entity<CrewRow>().Property(x => x.ModelOutputPricePerMTok).HasConversion<double>();
        // Phase 13.1 — hierarchy index for cheap parent walks.
        mb.Entity<CrewRow>().HasIndex(x => x.ParentCrewId);
        // Phase 16.2 — symmetric pair lookup. The partial-unique semantic
        // ("at most one pair per crew") is enforced in the migration via a
        // SQLite WHERE-clause index; the EF index here is just for fast lookup.
        mb.Entity<CrewRow>().HasIndex(x => x.PairedWithCrewId);
        // Phase 11.10 — crew budgets denormalised onto the crew row.
        mb.Entity<CrewRow>().Property(x => x.DailyBudgetUsd).HasConversion<double?>();
        mb.Entity<CrewRow>().Property(x => x.MaxTaskBudgetUsd).HasConversion<double?>();

        // Phase 13.1 — crew membership.
        mb.Entity<CrewMemberRow>()
            .ToTable("crew_members")
            .HasKey(x => new { x.CrewId, x.AgentId });
        mb.Entity<CrewMemberRow>().HasIndex(x => x.CrewId);
        mb.Entity<CrewMemberRow>().HasIndex(x => x.AgentId);

        // Phase 13.1 — crew_history audit log.
        mb.Entity<CrewHistoryRow>().ToTable("crew_history").HasKey(x => x.Id);
        mb.Entity<CrewHistoryRow>().HasIndex(x => new { x.CrewId, x.At });

        // Phase 13.7 — domain-expert index.
        mb.Entity<DomainExpertRow>().ToTable("domain_experts").HasKey(x => x.Id);
        mb.Entity<DomainExpertRow>().HasIndex(x => x.ContentId).IsUnique();
        mb.Entity<DomainExpertRow>().HasIndex(x => x.RepoId);
        mb.Entity<DomainExpertRow>().HasIndex(x => x.Topic);

        // Phase 11.4 — agent invocation cost ledger.
        mb.Entity<AgentUsageRow>().ToTable("agent_usage").HasKey(x => x.Id);
        mb.Entity<AgentUsageRow>().Property(x => x.CostUsd).HasConversion<double>();
        // Salary roll-ups group by crew and walk by time.
        mb.Entity<AgentUsageRow>().HasIndex(x => new { x.CrewId, x.StartedAt });
        // Per-card cost lookup.
        mb.Entity<AgentUsageRow>().HasIndex(x => x.TaskId);

        // Phase 11.3 — first-class agent records.
        mb.Entity<AgentRow>().ToTable("agents").HasKey(x => x.Id);
        mb.Entity<AgentRow>().HasIndex(x => x.CrewId);
        // Phase 14.1 — agent level + payroll columns on agents.
        mb.Entity<AgentRow>().Property(x => x.HourlyUsd).HasConversion<double>();

        // Phase 11.8 — agent_history audit table.
        mb.Entity<AgentHistoryRow>().ToTable("agent_history").HasKey(x => x.Id);
        mb.Entity<AgentHistoryRow>().HasIndex(x => new { x.AgentId, x.At });

        // Phase 14.1 — agent_level_history (one row per promotion/demotion).
        mb.Entity<AgentLevelHistoryRow>().ToTable("agent_level_history").HasKey(x => x.Id);
        mb.Entity<AgentLevelHistoryRow>().Property(x => x.WindowAvg).HasConversion<double>();
        mb.Entity<AgentLevelHistoryRow>().HasIndex(x => new { x.AgentId, x.CreatedAt });

        // Phase 44 — agent claim tracking (global busy/idle capacity).
        mb.Entity<AgentClaimRow>().ToTable("agent_claims").HasKey(x => x.Id);
        mb.Entity<AgentClaimRow>().HasIndex(x => new { x.AgentId, x.ReleasedAt })
            .HasDatabaseName("IX_agent_claims_agent_released");
        mb.Entity<AgentClaimRow>().HasIndex(x => x.WorkItemId)
            .HasDatabaseName("IX_agent_claims_workitem");

        // Phase G — control-side product "due" watermark. PK is the
        // product id (one row per product). The LastPendingAt index is
        // the hot read path used by every worker tick that needs to
        // decide which products to fan out to.
        mb.Entity<ProductRuntimeRow>().ToTable("product_runtime").HasKey(x => x.ProductId);
        mb.Entity<ProductRuntimeRow>().HasIndex(x => x.LastPendingAt)
            .HasDatabaseName("IX_product_runtime_lastpending");

        // Phase 80 — control-side repo registry (mirror of the per-product
        // repos row). The partial UNIQUE indexes enforce the Q2/Q5 GLOBAL
        // guarantees that SQLite can't enforce across per-product {slug}.db
        // files; the `WHERE DeletedAt IS NULL` filter lets a soft-deleted
        // repo's URL / clone root be reused by a re-add. Index names are
        // pinned via HasDatabaseName so the Phase80Migration raw-SQL
        // `CREATE UNIQUE INDEX IF NOT EXISTS` matches by name and doesn't
        // create a duplicate on a fresh DB (where EnsureCreated lays down the
        // model index first, then the migration's IF-NOT-EXISTS no-ops).
        mb.Entity<RepoRegistryRow>().ToTable("repo_registry").HasKey(x => x.Id);
        mb.Entity<RepoRegistryRow>().HasIndex(x => x.ProductId)
            .HasDatabaseName("IX_repo_registry_ProductId");
        mb.Entity<RepoRegistryRow>().HasIndex(x => x.RemoteUrl)
            .IsUnique()
            .HasFilter("DeletedAt IS NULL")
            .HasDatabaseName("IX_repo_registry_RemoteUrl");
        mb.Entity<RepoRegistryRow>().HasIndex(x => x.CloneRoot)
            .IsUnique()
            .HasFilter("DeletedAt IS NULL")
            .HasDatabaseName("IX_repo_registry_CloneRoot");

        // Phase 95 — chat-bridge outbox. The partial-unique index on DedupeKey
        // (filtered to the OPEN states pending/sent) enforces "at most one
        // outstanding alert per dedupe key" the same way task_queue enforces one
        // open row per work item. Index names are pinned via HasDatabaseName so
        // Phase95Migration's raw-SQL `CREATE ... IF NOT EXISTS` matches by name
        // and no-ops on a fresh DB (where EnsureCreated lays down the model
        // index first).
        mb.Entity<ChatNotificationRow>().ToTable("chat_notifications").HasKey(x => x.Id);
        mb.Entity<ChatNotificationRow>().HasIndex(x => x.Status)
            .HasDatabaseName("IX_chat_notifications_Status");
        mb.Entity<ChatNotificationRow>().HasIndex(x => x.DedupeKey)
            .IsUnique()
            .HasFilter("Status IN ('pending','sent')")
            .HasDatabaseName("IX_chat_notifications_DedupeKey_open");

        // Phase 95 — chat-bridge operator allow-list. One subscriber per
        // (platform, chatId).
        mb.Entity<ChatSubscriberRow>().ToTable("chat_subscribers").HasKey(x => x.Id);
        mb.Entity<ChatSubscriberRow>().HasIndex(x => new { x.Platform, x.ChatId })
            .IsUnique()
            .HasDatabaseName("IX_chat_subscribers_platform_chatid");

        // Phase 95 (Phase 4) — chat-bridge inbound long-poll cursor (single row).
        mb.Entity<ChatBridgeStateRow>().ToTable("chat_bridge_state").HasKey(x => x.Id);

        // Phase 95 (Part B) — spawn → forum-topic mapping (one row per spawn).
        mb.Entity<ChatSpawnTopicRow>().ToTable("chat_spawn_topics").HasKey(x => x.SpawnId);
        ChatConfigurationModel.Configure(mb);
        ChatDeliveryModel.Configure(mb);
    }
}
