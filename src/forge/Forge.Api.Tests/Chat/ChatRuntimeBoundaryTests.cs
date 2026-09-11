// -----------------------------------------------------------------------
// <copyright file="ChatRuntimeBoundaryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Forge.Api.Services;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatRuntimeBoundaryTests
{
    [Fact]
    public async Task Every_stage2_table_has_migration_model_runner_boundary_and_split_registration()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>(); var product = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            for (var repeat = 0; repeat < 2; repeat++)
            {
                await Phase100Migration.ApplyAsync(control, product); await Phase101Migration.ApplyAsync(control, product);
                await Phase102Migration.ApplyAsync(control, product); await Phase103Migration.ApplyAsync(control, product);
                await Phase106Migration.ApplyAsync(control, product);
            }
            var report = await sp.GetRequiredService<SplitDryRunService>().AnalyzeAsync();
            foreach (var phase in new[] { 100, 102, 103, 106 })
            {
                var spec = PhaseMigrationCatalog.ByPhase[phase]; Assert.Equal(PhaseMigrationKind.Control, spec.Kind);
                Assert.Contains(phase, PhaseMigrationRunner.ExecutionOrder);
                foreach (var name in spec.ControlTables)
                {
                    Assert.Contains(control.Model.GetEntityTypes(), e => e.GetTableName() == name);
                    Assert.DoesNotContain(product.Model.GetEntityTypes(), e => e.GetTableName() == name);
                    Assert.Equal(PartitionStrategy.Global, DbBoundary.TryGet(name)!.Strategy);
                    Assert.Equal("global", Assert.Single(report.Tables, t => t.TableName == name).Strategy);
                    Assert.DoesNotContain(report.UnknownTables, t => t.TableName == name);
                    Assert.DoesNotContain(report.Orphans, t => t.TableName == name);
                }
            }
            Assert.Equal(PhaseMigrationKind.Product, PhaseMigrationCatalog.ByPhase[101].Kind);
            Assert.Contains(product.Model.GetEntityTypes(), e => e.GetTableName() == "chat_action_receipts");
            Assert.DoesNotContain(control.Model.GetEntityTypes(), e => e.GetTableName() == "chat_action_receipts");
            Assert.Equal(PartitionStrategy.DirectProductId, DbBoundary.TryGet("chat_action_receipts")!.Strategy);
            Assert.Equal("ProductId", DbBoundary.TryGet("chat_action_receipts")!.PartitionColumn);
        });
    }

    [Fact]
    public async Task Legacy_upgrade_preserves_text_and_does_not_import_or_route_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(connection).Options);
        await using var product = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(connection).Options);
        await Phase95Migration.ApplyAsync(control, product); await Phase96Migration.ApplyAsync(control, product); await Phase97Migration.ApplyAsync(control, product);
        await Phase98Migration.ApplyAsync(control, product); await Phase99Migration.ApplyAsync(control, product);
        await control.Database.ExecuteSqlRawAsync("INSERT INTO chat_notifications (Id,Kind,ProductId,DedupeKey,Title,Body,Status,Platform,Attempts,CreatedAt) VALUES ('legacy','open-questions','p','old','Legacy title','Legacy body','pending','telegram',0,'2026-09-01T00:00:00+00:00')");
        await Phase100Migration.ApplyAsync(control, product); await Phase102Migration.ApplyAsync(control, product); await Phase103Migration.ApplyAsync(control, product);
        await Phase100Migration.ApplyAsync(control, product);
        var row = await control.ChatNotifications.SingleAsync(); Assert.Equal("Legacy body", row.Body); Assert.Equal("legacy", row.ScopeKind); Assert.Equal(0, row.PayloadVersion);
        Assert.Empty(await control.ChatDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Slack_app_has_one_receiver_and_routes_verified_workspaces_to_separate_inboxes()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        foreach (var workspace in new[] { "T1", "T2" })
            await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, workspace, ChatPluginTestFactory.Connection("slack", workspace)));
        var supervisor = f.Services.GetRequiredService<ChatConnectionSupervisor>(); await supervisor.RefreshAsync(default); await Task.Delay(30);
        Assert.Equal(1, f.Slack.Sessions.Values.Sum(s => s.ReceiveCalls));
        var receiver = f.Slack.Sessions.Values.Single(s => s.Sink is not null);
        var envelope = new ChatInboundEnvelope("slack", receiver.ConnectionId, "socket-envelope", new(receiver.ConnectionId, "T2", "channel"), "U1", "text", "1.0001", null, "safe", null, f.Clock.GetUtcNow());
        Assert.Equal(ChatInboundAcceptance.Accepted, await receiver.Sink!.AcceptAsync(envelope));
        Assert.Equal("T2", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Select(i => i.ConnectionId).SingleAsync()));
        Assert.Equal(ChatInboundAcceptance.Retry, await receiver.Sink.AcceptAsync(envelope with { Address = envelope.Address with { ExternalTenantId = "unknown" } }));
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task Empty_polls_do_not_write_and_duplicate_cursors_do_not_rewind()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        var envelope = new ChatInboundEnvelope("FakeAlpha", "alpha-one", "first", new("alpha-one", "tenant", "channel"), "operator", "text", "incoming", null, "safe", null, f.Clock.GetUtcNow());
        await f.ReceiveAsync(envelope, "cursor-1"); await f.ReceiveAsync(envelope with { DeliveryKey = "second" }, "cursor-2");
        await f.ReceiveAsync(envelope, "cursor-1");
        Assert.Equal("cursor-2", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConnectionStates.Where(s => s.ConnectionId == "alpha-one").Select(s => s.Cursor).SingleAsync()));
        var runtime = f.Services.GetRequiredService<ChatRuntimeState>(); var writes = runtime.CompletedWrites;
        await f.WithAsync(async sp => Assert.Equal(ChatInboundAcceptance.Accepted, await sp.GetRequiredService<ChatInboxService>().AcceptBatchAsync([], [], new Dictionary<string, long>())));
        Assert.Equal(writes, runtime.CompletedWrites);
    }

    [Fact]
    public async Task Inbox_sqlite_busy_budget_refuses_ack_within_two_seconds()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        var path = f.Services.GetRequiredService<IForgeDbPaths>().ControlDbPath();
        await using var blocker = new SqliteConnection("Data Source=" + path); await blocker.OpenAsync();
        await using var held = blocker.BeginTransaction();
        await using var command = blocker.CreateCommand(); command.Transaction = held; command.CommandText = "UPDATE chat_connections SET DisplayName=DisplayName WHERE Id='alpha-one'"; await command.ExecuteNonQueryAsync();
        var envelope = new ChatInboundEnvelope("FakeAlpha", "alpha-one", "busy", new("alpha-one", "tenant", "channel"), "operator", "text", "incoming", null, "safe", null, f.Clock.GetUtcNow());
        var watch = Stopwatch.StartNew(); var accepted = await f.ReceiveAsync(envelope, "never-advance");
        Assert.Equal(ChatInboundAcceptance.Retry, accepted); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), watch.Elapsed.ToString());
        await held.RollbackAsync();
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.CountAsync()));
    }

    [Fact]
    public async Task Unavailable_product_file_is_degraded_without_retiring_its_scope()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        var paths = f.Services.GetRequiredService<IForgeDbPaths>(); var path = Path.GetFullPath(paths.ProductDbPath("p"));
        Assert.StartsWith(Path.GetFullPath(paths.Folder) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        SqliteConnection.ClearAllPools(); File.Move(path, path + ".offline");
        try
        {
            await f.WithAsync(sp => sp.GetRequiredService<ChatScopeReconciler>().RunOnceAsync()); await f.TickAsync();
            Assert.All(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.ToListAsync()), b => { Assert.Equal("degraded", b.ProvisioningState); Assert.Null(b.RetiredAt); });
            Assert.Equal(0, f.Alpha.SendCalls); Assert.Equal(0, f.Beta.SendCalls);
        }
        finally { File.Move(path + ".offline", path); }
    }

    [Fact]
    public async Task Link_challenge_is_connection_bound_single_use_and_revocation_uses_existing_authority()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        var actor = await f.ProviderActorAsync("123", "owner-p");
        var begin = await f.ReadAsync(sp => sp.GetRequiredService<ChatLinkService>().BeginAsync(actor, "alpha-one", "tenant", "new-operator", default));
        var token = JsonSerializer.SerializeToElement(begin).GetProperty("Token").GetString()!;
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<ChatLinkService>().RedeemAsync(token, "beta", "tenant", "new-operator", default)));
        Assert.NotNull(await f.ReadAsync(sp => sp.GetRequiredService<ChatLinkService>().RedeemAsync(token, "alpha-one", "tenant", "new-operator", default)));
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<ChatLinkService>().RedeemAsync(token, "alpha-one", "tenant", "new-operator", default)));
        await f.WithAsync(sp => sp.GetRequiredService<ChatLinkService>().RevokeAsync(actor, default));
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("FakeAlpha", "tenant", "new-operator")));
        Assert.All(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatOperatorLinks.ToListAsync()), l => Assert.NotNull(l.RevokedAt));
    }

    [Fact]
    public async Task Answer_edit_requires_the_current_browser_revision_and_does_not_repeat_resume()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(questions: 2);
        using var client = await f.PairedClientAsync("owner-p"); client.DefaultRequestHeaders.Add("X-Forge-ProductId", "p");
        var endpoint = "/api/work-items/child-0/questions/answer?productId=p";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(endpoint, new { answers = new[] { new { externalId = "q1", answer = "first" } } })).StatusCode);
        var question = await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.SingleAsync(q => q.ExternalId == "q1"));
        var edit = new { answers = new[] { new { externalId = "q1", answer = "edited", expectedRevision = question.Id, editExisting = true, expectedAnswerRevision = question.AnsweredAt!.Value.ToString("O") } } };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(endpoint, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(endpoint, edit)).StatusCode);
        Assert.Equal("edited", await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.Where(q => q.ExternalId == "q1").Select(q => q.Answer).SingleAsync()));
    }

    [Fact]
    public async Task Nontransactional_operation_is_recorded_before_effect_and_unknown_effect_is_not_repeated()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        using var actor = ForgeActorScope.Push(await f.ProviderActorAsync("123", "owner-p"), "p");
        using var product = ProductDbContextFactory.PushAsyncLocalSlug("p"); var effects = 0;
        await Assert.ThrowsAsync<IOException>(() => f.WithAsync(sp => sp.GetRequiredService<ChatDomainActionRunner>().RunAsync<int>("spawn.pause", new(SpawnId: "shared"),
            async () => { effects++; Assert.True(await sp.GetRequiredService<ProductDbContext>().ChatActionReceipts.AnyAsync(r => r.State == "executing" && r.OperationId != null)); throw new IOException("simulated external outcome"); },
            _ => -1, value => value == 1, default)));
        var repeated = await f.ReadAsync(sp => sp.GetRequiredService<ChatDomainActionRunner>().RunAsync("spawn.pause", new(SpawnId: "shared"),
            () => { effects++; return Task.FromResult(1); }, _ => -1, value => value == 1, default));
        Assert.Equal(-1, repeated); Assert.Equal(1, effects);
    }
}