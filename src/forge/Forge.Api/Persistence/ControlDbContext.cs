// -----------------------------------------------------------------------
// <copyright file="ControlDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase D — EF DbContext bound to the **control DB** (<c>{folder}\forge.db</c>).
/// Exposes only cross-product entities: products, repos, users, crews,
/// agents, agent_usage, agent_claims, audit (<c>forge_actions</c>), etc.
///
/// <para>Phase G — the legacy <c>ForgeDbContext</c> union-model class
/// was deleted. Endpoint code now takes <see cref="ControlDbContext"/>
/// for global reads/writes and <see cref="ProductDbContext"/> (resolved
/// via <see cref="IProductDbContextFactory"/>) for product-scoped data.</para>
///
/// <para>After Phase F runs, only this context binds to <c>forge.db</c>;
/// every product entity moves to its own <c>{slug}.db</c> file accessed
/// via <see cref="IProductDbContextFactory"/>.</para>
/// </summary>
public class ControlDbContext : DbContext
{
    public ControlDbContext(DbContextOptions<ControlDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProductRow> Products => this.Set<ProductRow>();

    // Phase 80.B — the full RepoRow moved to ProductDbContext ({slug}.db).
    // The control DB now holds only the thin RepoRegistry index (below).
    // Callers that need a repo's full config resolve it via RepoLookup
    // (repo_registry → owning product's DB); routing / global-uniqueness /
    // sync-due enumeration read RepoRegistry directly.

    public DbSet<UserRow> Users => this.Set<UserRow>();

    public DbSet<UserSettingsRow> UserSettings => this.Set<UserSettingsRow>();

    public DbSet<UserProductRoleRow> UserProductRoles => this.Set<UserProductRoleRow>();

    public DbSet<CrewRow> Crews => this.Set<CrewRow>();

    public DbSet<CrewMemberRow> CrewMembers => this.Set<CrewMemberRow>();

    public DbSet<CrewHistoryRow> CrewHistory => this.Set<CrewHistoryRow>();

    public DbSet<DomainExpertRow> DomainExperts => this.Set<DomainExpertRow>();

    public DbSet<AgentRow> Agents => this.Set<AgentRow>();

    public DbSet<AgentHistoryRow> AgentHistory => this.Set<AgentHistoryRow>();

    public DbSet<AgentLevelHistoryRow> AgentLevelHistory => this.Set<AgentLevelHistoryRow>();

    public DbSet<AgentUsageRow> AgentUsage => this.Set<AgentUsageRow>();

    public DbSet<AgentClaimRow> AgentClaims => this.Set<AgentClaimRow>();

    public DbSet<AuditRow> Audit => this.Set<AuditRow>();

    /// <summary>
    /// Phase G — control-side "product is due" watermark. Single row per
    /// product. Workers read this to skip products with no pending work
    /// without opening the per-product DB.
    /// </summary>
    public DbSet<ProductRuntimeRow> ProductRuntime => this.Set<ProductRuntimeRow>();

    /// <summary>
    /// Phase 80 — control-side repo index/mirror. The full repo row moves to
    /// the per-product DB (Stage 80.B); this thin row enforces the Q2/Q5
    /// global uniqueness invariants, resolves <c>repoId → product</c> routing,
    /// and feeds sync-due enumeration. See <see cref="RepoRegistryRow"/>.
    /// </summary>
    public DbSet<RepoRegistryRow> RepoRegistry => this.Set<RepoRegistryRow>();

    /// <summary>Phase 95 — chat-bridge outbound notification + reply-correlation outbox.</summary>
    public DbSet<ChatNotificationRow> ChatNotifications => this.Set<ChatNotificationRow>();

    /// <summary>Phase 95 — chat-bridge operator allow-list (chat account ↔ Forge user).</summary>
    public DbSet<ChatSubscriberRow> ChatSubscribers => this.Set<ChatSubscriberRow>();

    /// <summary>Phase 95 (Phase 4) — chat-bridge inbound long-poll cursor (single row).</summary>
    public DbSet<ChatBridgeStateRow> ChatBridgeState => this.Set<ChatBridgeStateRow>();

    /// <summary>Phase 95 (Part B) — spawn → Telegram forum-topic mapping.</summary>
    public DbSet<ChatSpawnTopicRow> ChatSpawnTopics => this.Set<ChatSpawnTopicRow>();

    public DbSet<SpawnPluginRow> SpawnPlugins => this.Set<SpawnPluginRow>();
    public DbSet<ChatConnectionRow> ChatConnections => this.Set<ChatConnectionRow>();
    public DbSet<ChatConnectionProductRow> ChatConnectionProducts => this.Set<ChatConnectionProductRow>();
    public DbSet<ChatDestinationRow> ChatDestinations => this.Set<ChatDestinationRow>();
    public DbSet<ChatSubscriptionRow> ChatSubscriptions => this.Set<ChatSubscriptionRow>();
    public DbSet<ChatConversationBindingRow> ChatConversationBindings => this.Set<ChatConversationBindingRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        DbModelControl.Configure(mb);
    }
}
