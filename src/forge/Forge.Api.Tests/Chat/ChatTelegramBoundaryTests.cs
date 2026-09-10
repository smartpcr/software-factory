// -----------------------------------------------------------------------
// <copyright file="ChatTelegramBoundaryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Text;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatTelegramBoundaryTests
{
    [Theory]
    [InlineData("pause")]
    [InlineData("link")]
    public async Task Completed_command_recovery_preserves_success_after_inbox_checkpoint_failure(string command)
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        var text = "/pause";
        if (command == "link") text = await f.ReadAsync(async sp =>
        {
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p");
            var result = JsonSerializer.SerializeToElement(await sp.GetRequiredService<ChatLinkService>().BeginAsync(actor, "bot-one", "12345", "42", CancellationToken.None));
            return "/start " + result.GetProperty("Token").GetString();
        });
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, text)); f.Fault.Armed = "dispatch";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.DispatchAsync()); await f.DispatchAsync();
        Assert.Equal(command == "link" ? "linked" : "parent-paused", (await f.InboxAsync()).Single().Result);
        if (command == "pause") Assert.Single(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.Where(r => r.Action == "spawn.pause").ToListAsync()));
    }

    [Fact]
    public async Task Truncated_question_scan_never_infers_a_unique_answer_target()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync(); await f.TickAsync();
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var original = await db.ChatDeliveryMessages.SingleAsync();
            for (var i = 0; i < 201; i++) db.ChatDeliveryMessages.Add(new() { DeliveryId = original.DeliveryId, PartKey = "historical:" + i,
                QuestionId = original.QuestionId, ExpectedRevision = original.ExpectedRevision, MessageId = "old-" + i, AddressJson = original.AddressJson, State = "sent" });
            await db.SaveChangesAsync();
        });
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, "Do not guess this target")); await f.DispatchAsync();
        Assert.Equal("too-many-questions", (await f.InboxAsync()).Single().Result);
        Assert.Empty(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.ToListAsync()));
    }

    [Fact]
    public async Task Webhook_conflict_is_exposed_in_host_setup_health_without_deletion()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync();
        f.Http.Override = (_, method, _, _) => Task.FromResult<HttpResponseMessage?>(method == "getWebhookInfo" ? TelegramProviderFixture.Ok(new { url = "https://example.test/owned" }) : null);
        await f.WithAsync(async sp =>
        {
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "admin");
            var result = await sp.GetRequiredService<ChatProviderSetupService>().DiscoverAsync(actor, "bot-one", null, CancellationToken.None);
            Assert.Equal("telegram-webhook-conflict", result.Failure!.Code);
        });
        Assert.Equal("telegram-webhook-conflict", f.Services.GetRequiredService<ChatRuntimeState>().Health["bot-one"].Reason);
        Assert.DoesNotContain(f.Http.Calls, c => c.Method is "deleteWebhook" or "getUpdates");
    }

    [Fact]
    public async Task Migration104_is_control_only_idempotent_and_upgrades_existing_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(connection).Options);
        await using var product = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(connection).Options);
        await control.Database.ExecuteSqlRawAsync("CREATE TABLE chat_inbox (Id TEXT PRIMARY KEY); CREATE TABLE chat_action_tokens (Hash TEXT PRIMARY KEY); CREATE TABLE chat_conversation_bindings (Id TEXT PRIMARY KEY); INSERT INTO chat_inbox VALUES ('existing');");
        await Phase104Migration.ApplyAsync(control, product); await Phase104Migration.ApplyAsync(control, product);
        Assert.Equal("none", await control.Database.SqlQueryRaw<string>("SELECT ResponseState AS Value FROM chat_inbox WHERE Id='existing'").SingleAsync());
        Assert.Equal(0, await control.Database.SqlQueryRaw<int>("SELECT ResponseAttempts AS Value FROM chat_inbox WHERE Id='existing'").SingleAsync());
        Assert.Equal(PhaseMigrationKind.Control, PhaseMigrationCatalog.ByPhase[104].Kind); Assert.Contains(104, PhaseMigrationRunner.ExecutionOrder);
        foreach (var table in PhaseMigrationCatalog.ByPhase[104].ControlTables)
        {
            Assert.Contains(control.Model.GetEntityTypes(), e => e.GetTableName() == table);
            Assert.DoesNotContain(product.Model.GetEntityTypes(), e => e.GetTableName() == table);
            Assert.Equal(PartitionStrategy.Global, DbBoundary.TryGet(table)!.Strategy);
        }
    }

    [Fact]
    public async Task DPAPI_credentials_are_encrypted_referenced_and_isolated_from_legacy_blob()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "chat-secret-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ChatDpapiSecretStore(root); var reference = await store.WriteAsync("bot-one", "12345:secret-test-value");
            Assert.Equal("dpapi", reference.Store); Assert.Equal("12345:secret-test-value", await store.ReadAsync(reference));
            var file = Assert.Single(Directory.GetFiles(root)); Assert.DoesNotContain("secret-test-value", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            Assert.Null(await store.ReadAsync(new("another-store", "bot-one")));
            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("../outside", "test").AsTask());
            await File.WriteAllTextAsync(file, "not-a-valid-dpapi-blob"); Assert.Null(await store.ReadAsync(reference));
            await store.DeleteAsync(reference); Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: false); }
    }

    [Fact]
    public async Task Setup_discovery_reuses_receiver_and_validation_is_host_authorized()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.ReceiveAsync(TelegramProviderFixture.Message(1));
        await f.WithAsync(async sp =>
        {
            var actors = sp.GetRequiredService<ForgeActorResolver>(); var service = sp.GetRequiredService<ChatProviderSetupService>();
            var admin = await actors.FromProviderAsync("telegram", "123", "admin"); var owner = await actors.FromProviderAsync("telegram", "123", "owner-p");
            Assert.Single((await service.DiscoverAsync(admin, "bot-one", null, CancellationToken.None)).Value!.Items);
            Assert.True((await service.ValidateAsync(admin, "bot-one", "destination", CancellationToken.None)).Succeeded);
            await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => service.DiscoverAsync(owner, "bot-one", null, CancellationToken.None));
            Assert.Equal("ready", await sp.GetRequiredService<ControlDbContext>().ChatDestinations.Select(d => d.AccessState).SingleAsync());
        });
        Assert.Single(f.Http.Calls, c => c.Method == "getUpdates");
    }

    [Fact]
    public async Task Existing_subscriber_and_pending_preference_challenge_survive_plugin_relink()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync();
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>();
            control.ChatSubscribers.Add(new() { Id = "existing", Platform = "telegram", ChatId = "42", ForgeUserId = "owner-p", LinkToken = "legacy-pending", Muted = true });
            await control.SaveChangesAsync();
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p");
            var challenge = JsonSerializer.SerializeToElement(await sp.GetRequiredService<ChatLinkService>().BeginAsync(actor, "bot-one", "12345", "42", CancellationToken.None));
            await f.ReceiveAsync(TelegramProviderFixture.Message(1, "/start " + challenge.GetProperty("Token").GetString()));
        });
        await f.DispatchAsync();
        Assert.Equal("linked", (await f.InboxAsync()).Single().Result);
        var subscriber = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatSubscribers.SingleAsync(s => s.ChatId == "42"));
        Assert.Equal("existing", subscriber.Id); Assert.True(subscriber.Muted); Assert.Equal("legacy-pending", subscriber.LinkToken);
    }

    [Fact]
    public async Task Original_message_alias_survives_question_splitting_and_unknown_legacy_target_is_never_guessed()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync(); await f.TickAsync();
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>(); var delivery = await control.ChatDeliveries.SingleAsync();
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "admin");
            var revision = await ChatDomainRevision.ReadAsync(sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p"), new(WorkstreamId: "child-0"), CancellationToken.None);
            var service = sp.GetRequiredService<ChatLegacyAliasService>();
            var id = await service.PreserveAsync(actor, delivery.Id, new(TelegramProviderFixture.Address(), "77"), revision);
            Assert.Equal(id, await service.PreserveAsync(actor, delivery.Id, new(TelegramProviderFixture.Address(), "77"), revision));
        });
        var messages = await f.MessagesAsync(); Assert.Equal(3, messages.Count);
        Assert.Equal("77", messages.Single(m => m.PartKey.StartsWith("legacy:", StringComparison.Ordinal)).MessageId);
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, "act|retry", "77")); await f.DispatchAsync();
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(2, "act|retry", "unknown")); await f.DispatchAsync();
        Assert.Equal("select-one-question", (await f.InboxAsync()).Single(i => i.DeliveryKey == "1").Result);
        Assert.Equal("legacy-alias-repair-required", (await f.InboxAsync()).Single(i => i.DeliveryKey == "2").Result);
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync()));
    }

    [Fact]
    public async Task Legacy_targetless_callback_uses_original_alias_and_shared_action_authorization()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(questions: 0);
        await f.WithAsync(async sp =>
        {
            var product = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            var child = await product.Workstreams.SingleAsync(w => w.Id == "child-0"); child.Status = "stalled";
            product.Deliverables.Add(new() { WorkItemId = child.Id, Iter = 1, Verdict = "fail", CreatedAt = DateTimeOffset.UtcNow });
            await product.SaveChangesAsync();
            var revision = await ChatDomainRevision.ReadAsync(product, new(WorkstreamId: "child-0"), CancellationToken.None);
            await sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("workstream-stalled", "stalled", "p", "shared", "child-0", "child-0", Revision: revision));
        });
        await f.TickAsync();
        await f.WithAsync(async sp =>
        {
            var delivery = await sp.GetRequiredService<ControlDbContext>().ChatDeliveries.SingleAsync();
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "admin");
            var revision = await ChatDomainRevision.ReadAsync(sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p"), new(WorkstreamId: "child-0"), CancellationToken.None);
            await sp.GetRequiredService<ChatLegacyAliasService>().PreserveAsync(actor, delivery.Id, new(TelegramProviderFixture.Address(), "77"), revision);
        });
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, "act|retry", "77")); await f.DispatchAsync();
        Assert.Equal("accepted", (await f.InboxAsync()).Single().Result);
        Assert.Single(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.Where(r => r.Action == "workstream.retry").ToListAsync()));
    }

    [Fact]
    public async Task Split_send_keeps_canonical_button_reference_and_partial_send_keeps_known_aliases()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync();
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>(); var n = await control.ChatNotifications.SingleAsync();
            var payload = ChatPayloadConverter.Read(n)!; n.PayloadJson = JsonSerializer.Serialize(payload with { Questions = [payload.Questions[0] with { Text = new string('x', 6000) }] });
            await control.SaveChangesAsync();
        });
        await f.TickAsync(); var messages = await f.MessagesAsync(); Assert.Equal(2, messages.Count);
        var canonical = messages.Single(m => m.PartKey.Count(c => c == ':') == 1);
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, f.Button(0, 0), canonical.MessageId!)); await f.DispatchAsync();
        Assert.Equal("answered-all", (await f.InboxAsync()).Single().Result);
    }

    [Fact]
    public async Task Command_content_is_scoped_for_purge_and_raw_unsupported_arguments_are_not_retained()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, "/status")); await f.DispatchAsync();
        await f.ReceiveAsync(TelegramProviderFixture.Message(2, "/status password=DO_NOT_KEEP")); await f.DispatchAsync();
        Assert.DoesNotContain("DO_NOT_KEEP", JsonSerializer.Serialize(await f.InboxAsync()));
        await f.WithAsync(async sp =>
        {
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p");
            await sp.GetRequiredService<ChatRetentionService>().PurgeSpawnContentAsync(actor, "p", "shared", CancellationToken.None);
        });
        var row = (await f.InboxAsync()).Single(i => i.DeliveryKey == "1");
        Assert.Null(row.ResponseText); Assert.Null(row.EnvelopeJson); Assert.Equal("expired", row.ResponseState);
    }

    [Fact]
    public async Task Partial_provider_receipt_is_durable_and_never_blindly_resent()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync();
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var n = await db.ChatNotifications.SingleAsync();
            var p = ChatPayloadConverter.Read(n)!; n.PayloadJson = JsonSerializer.Serialize(p with { Questions = [p.Questions[0] with { Text = new string('x', 6000) }] });
            await db.SaveChangesAsync();
        });
        var calls = 0;
        f.Http.Override = (_, method, _, _) => method == "sendMessage" && ++calls == 2 ? throw new HttpRequestException("unknown") : Task.FromResult<HttpResponseMessage?>(null);
        await f.TickAsync(); await f.TickAsync();
        Assert.Equal(2, calls);
        var delivery = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.SingleAsync());
        Assert.Equal("uncertain", delivery.State); Assert.Equal("partial-send-outcome-unknown", delivery.LastError);
        var message = Assert.Single(await f.MessagesAsync()); Assert.NotNull(message.MessageId); Assert.True(message.ControlsRetired);
    }

    [Fact]
    public async Task Render_failure_is_visible_before_any_provider_send()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync();
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var n = await db.ChatNotifications.SingleAsync();
            var p = ChatPayloadConverter.Read(n)!; n.PayloadJson = JsonSerializer.Serialize(p with { Questions = [p.Questions[0] with { Text = new string('x', 70000) }] });
            await db.SaveChangesAsync();
        });
        await f.TickAsync();
        var delivery = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.SingleAsync());
        Assert.Equal("failed", delivery.State); Assert.Equal("render-payload-limit:text:65536", delivery.LastError);
        Assert.DoesNotContain(f.Http.Calls, c => c.Method == "sendMessage");
    }
}