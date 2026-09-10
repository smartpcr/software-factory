// -----------------------------------------------------------------------
// <copyright file="ChatPluginSchemaTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Forge.Api.Services.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatPluginSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_And_Upgraded_Schemas_Are_Idempotent_And_Enforce_Scope_And_Unique_Constraints(bool ensureCreated)
    {
        await using var conn = new SqliteConnection("Data Source=:memory:"); await conn.OpenAsync();
        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options);
        await using var product = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);
        if (ensureCreated) await control.Database.EnsureCreatedAsync();
        await Phase98Migration.ApplyAsync(control, product);
        control.ChatConnections.Add(new ChatConnectionRow { Id = "c", ProviderId = "fake", InstallationId = "i", Enabled = true });
        control.ChatConnectionProducts.Add(new ChatConnectionProductRow { ConnectionId = "c", ProductId = "p:雪" });
        control.ChatDestinations.Add(new ChatDestinationRow { Id = "d", ConnectionId = "c", ExternalTenantId = "tenant", ChannelId = "channel" });
        control.SpawnPlugins.Add(new SpawnPluginRow { ProductId = "p:雪", SpawnId = "s/😀", RunIdentity = "run", ActivationGeneration = 1 });
        var scope = ChatConfigurationScope.Spawn("p:雪", "s/😀");
        control.ChatSubscriptions.Add(new ChatSubscriptionRow { Id = "s", ScopeKind = "spawn", ScopeKey = scope.Key,
            ProductId = scope.ProductId, SpawnId = scope.SpawnId, PluginId = "chat", DestinationId = "d" });
        control.ChatConversationBindings.Add(new ChatConversationBindingRow { Id = "b", SubscriptionId = "s", SubscriptionRevision = 1,
            ConnectionId = "c", ExternalTenantId = "tenant", ChannelId = "channel", ConversationId = "1700000000.001" });
        await control.SaveChangesAsync();
        await Phase98Migration.ApplyAsync(control, product);
        Assert.Equal("1700000000.001", (await control.ChatConversationBindings.SingleAsync()).ConversationId);
        Assert.Equal(1, await control.ChatConnections.CountAsync());

        foreach (var (column, value) in new[] { ("HostId", "mixed"), ("ProductId", ""), ("ProductId", (string?)null), ("PluginId", (string?)null),
            ("ScopeKey", "wrong-key"), ("ScopeKey", (string?)null), ("ScopeKind", "host"), ("ConversationPolicy", "invented") })
        {
            await using var command = conn.CreateCommand();
            // The column names are fixed test cases, never input.
            command.CommandText = $"UPDATE chat_subscriptions SET {column} = @value WHERE Id = 's'";
            command.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
            Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync())).SqliteErrorCode);
        }
        control.ChatSubscriptions.Add(new ChatSubscriptionRow { Id = "duplicate", ScopeKind = "spawn", ScopeKey = scope.Key,
            ProductId = scope.ProductId, SpawnId = scope.SpawnId, PluginId = "chat", DestinationId = "d" });
        await Assert.ThrowsAsync<DbUpdateException>(() => control.SaveChangesAsync());
        control.ChangeTracker.Clear();
        var existing = await control.ChatSubscriptions.SingleAsync(); existing.RetiredAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync();
        control.ChatSubscriptions.Add(new ChatSubscriptionRow { Id = "replacement", ScopeKind = "spawn", ScopeKey = scope.Key,
            ProductId = scope.ProductId, SpawnId = scope.SpawnId, PluginId = "chat", DestinationId = "d" });
        await control.SaveChangesAsync();
        Assert.Equal(2, await control.ChatSubscriptions.CountAsync());
        // Delimiters/Unicode cannot alias another scope or the permanent host scope.
        Assert.NotEqual(ChatConfigurationScope.Spawn("p:a", "b").Key, ChatConfigurationScope.Spawn("p", "a:b").Key);
        Assert.NotEqual(scope.Key, ChatConfigurationScope.Host(scope.Key).Key);
    }

    [Fact]
    public async Task Revision_Compare_And_Swap_Rejects_A_Concurrent_Writer()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:"); await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options;
        await using var first = new ControlDbContext(opts); await first.Database.EnsureCreatedAsync();
        first.SpawnPlugins.Add(new SpawnPluginRow { ProductId = "p", SpawnId = "s", RunIdentity = "run" }); await first.SaveChangesAsync();
        await using var second = new ControlDbContext(opts);
        var stale = await second.SpawnPlugins.SingleAsync();
        var winner = await first.SpawnPlugins.SingleAsync(); winner.Enabled = true; winner.Revision++; await first.SaveChangesAsync();
        stale.SettingsVersion = 99; stale.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        second.ChangeTracker.Clear(); Assert.Equal(1, (await second.SpawnPlugins.SingleAsync()).SettingsVersion);
    }

    [Fact]
    public async Task All_Six_Tables_Are_Control_Global_In_Catalog_Runner_Model_And_Actual_Split_Dry_Run()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var spec = PhaseMigrationCatalog.ByPhase[98];
        Assert.Equal(PhaseMigrationKind.Control, spec.Kind); Assert.Empty(spec.ProductTables); Assert.Equal(6, spec.ControlTables.Count);
        Assert.Equal(99, PhaseMigrationRunner.ExecutionOrder.Last());
        var report = await scope.ServiceProvider.GetRequiredService<SplitDryRunService>().AnalyzeAsync();
        foreach (var name in spec.ControlTables)
        {
            Assert.Contains(control.Model.GetEntityTypes(), t => t.GetTableName() == name);
            var boundary = Assert.Single(DbBoundary.AllTables, t => t.TableName == name);
            Assert.Equal(DbKind.Control, boundary.Kind); Assert.Equal(PartitionStrategy.Global, boundary.Strategy);
            var actual = Assert.Single(report.Tables, t => t.TableName == name);
            Assert.Equal("control", actual.Kind); Assert.Equal("global", actual.Strategy);
            Assert.DoesNotContain(report.UnknownTables, t => t.TableName == name);
            Assert.DoesNotContain(report.Orphans, t => t.TableName == name);
            foreach (var product in new[] { "p", "q" })
            {
                var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor(product);
                Assert.DoesNotContain(db.Model.GetEntityTypes(), t => t.GetTableName() == name);
                await db.Database.OpenConnectionAsync();
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = name; command.Parameters.Add(parameter);
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
        }
    }
}