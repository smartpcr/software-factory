// -----------------------------------------------------------------------
// <copyright file="ForgeDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core DbContext for the Forge SQLite cache.
/// </summary>
public class ForgeDbContext : DbContext
{
    public ForgeDbContext(DbContextOptions<ForgeDbContext> options)
        : base(options)
    {
    }

    public DbSet<EpicRow> Epics => this.Set<EpicRow>();

    public DbSet<CardRow> Cards => this.Set<CardRow>();

    public DbSet<ColumnRow> Columns => this.Set<ColumnRow>();

    public DbSet<WorkstreamRow> Workstreams => this.Set<WorkstreamRow>();

    public DbSet<WsEventRow> WsEvents => this.Set<WsEventRow>();

    public DbSet<PrRow> Prs => this.Set<PrRow>();

    public DbSet<WsStatRow> WsStats => this.Set<WsStatRow>();

    public DbSet<OverviewActivityRow> OverviewActivity => this.Set<OverviewActivityRow>();

    public DbSet<OverviewAgentRow> OverviewAgents => this.Set<OverviewAgentRow>();

    public DbSet<AuditRow> Audit => this.Set<AuditRow>();

    public DbSet<IdempotencyRow> Idempotency => this.Set<IdempotencyRow>();

    public DbSet<ProductRow> Products => this.Set<ProductRow>();

    public DbSet<RepoRow> Repos => this.Set<RepoRow>();

    public DbSet<UserRow> Users => this.Set<UserRow>();

    public DbSet<UserSettingsRow> UserSettings => this.Set<UserSettingsRow>();

    public DbSet<CrewRow> Crews => this.Set<CrewRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<EpicRow>().ToTable("epics").HasKey(x => x.Id);
        mb.Entity<CardRow>().ToTable("cards").HasKey(x => x.Id);
        mb.Entity<ColumnRow>().ToTable("columns").HasKey(x => x.Id);
        mb.Entity<WorkstreamRow>().ToTable("workstreams").HasKey(x => x.Id);
        mb.Entity<WsEventRow>().ToTable("ws_events").HasKey(x => x.Id);
        mb.Entity<PrRow>().ToTable("prs").HasKey(x => x.Id);
        mb.Entity<WsStatRow>().ToTable("ws_stats").HasKey(x => x.Id);
        mb.Entity<OverviewActivityRow>().ToTable("overview_activity").HasKey(x => x.Id);
        mb.Entity<OverviewAgentRow>().ToTable("overview_agents").HasKey(x => x.Id);
        mb.Entity<AuditRow>().ToTable("forge_actions").HasKey(x => x.Id);
        mb.Entity<IdempotencyRow>().ToTable("idempotency").HasKey(x => x.Key);

        // Phase 8 — products / repos with Q5 invariants enforced at the DB layer.
        mb.Entity<ProductRow>().ToTable("products").HasKey(x => x.Id);
        mb.Entity<ProductRow>().HasIndex(x => x.Slug).IsUnique();

        mb.Entity<RepoRow>().ToTable("repos").HasKey(x => x.Id);
        // Q5: same remote URL cannot live under two products.
        mb.Entity<RepoRow>().HasIndex(x => x.RemoteUrl).IsUnique();
        // Q2: same clone root cannot host two repos (worktrees would race).
        mb.Entity<RepoRow>().HasIndex(x => x.CloneRoot).IsUnique();
        // FK on product_id; cascade delete when a product goes away.
        mb.Entity<RepoRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 9.6 — identified users.
        mb.Entity<UserRow>().ToTable("users").HasKey(x => x.Id);
        mb.Entity<UserRow>().HasIndex(x => x.Email).IsUnique();

        // Phase 10.1 — per-user preferences.
        mb.Entity<UserSettingsRow>().ToTable("user_settings").HasKey(x => x.UserId);

        // Phase 10.6 — crew rows.
        mb.Entity<CrewRow>().ToTable("crews").HasKey(x => x.Id);
        // SQLite stores decimal as TEXT; explicit conversion keeps EF happy.
        mb.Entity<CrewRow>().Property(x => x.ModelInputPricePerMTok).HasConversion<double>();
        mb.Entity<CrewRow>().Property(x => x.ModelOutputPricePerMTok).HasConversion<double>();
    }
}
