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

    public DbSet<RepoRow> Repos => this.Set<RepoRow>();

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

    protected override void OnModelCreating(ModelBuilder mb)
    {
        DbModelControl.Configure(mb);
    }
}
