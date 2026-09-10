// -----------------------------------------------------------------------
// <copyright file="TelegramSliceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class TelegramSliceTests
{
    private const string Path = "/api/products/p/story-spawns/shared/plugins/chat/notification-setup";
    private static OpenQuestionsAlert Alert(string body = "Agent secret, source and answer choices")
        => new("p", "repo-p", "child-0", 1, "PRIVATE TITLE", body, [new("PRIVATE CHOICE", "oq|secret|1")], "shared");

    [Fact]
    public async Task Successful_Provider_Call_With_Failed_Checkpoint_Is_Recovered_Without_Resending()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync(); await app.EnableAsync();
        app.Checkpoint.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.TickAsync());
        Assert.Equal("sending", Assert.Single(await app.OutboxAsync()).Status);
        Assert.Equal(0, await app.TickAsync());
        Assert.Equal("uncertain", Assert.Single(await app.OutboxAsync()).Status);
        Assert.Single(app.Http.Calls, c => c.Method == "sendMessage");
    }

    [Fact]
    public async Task Spawn_List_Carries_Explicit_Product_Ownership_And_Future_Children_Use_The_Parent_Subscription()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync(); await app.EnableAsync();
        await app.EnrollAsync("owner-p", productId: "q");
        using var client = await app.PairedClientAsync("owner-p");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/stories/spawns?productId=all");
        Assert.Equal(new[] { "p", "q" }, list.EnumerateArray().Select(s => s.GetProperty("productId").GetString()).Order().ToArray());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.Workstreams.Add(new() { Id = "future-child", RepoId = "repo-p", StorySpawnId = "shared" }); await db.SaveChangesAsync();
        }
        // Refresh the actor after enrollment changed its revision.
        app.Link("owner-p", sender: "owner-p");
        Assert.True(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert() with { WorkItemId = "future-child" }, CancellationToken.None)));
        Assert.Equal(2, await app.TickAsync());
        using var check = app.Services.CreateScope(); Assert.Single(await check.ServiceProvider.GetRequiredService<ControlDbContext>().SpawnPlugins.ToListAsync());
    }

    [Fact]
    public async Task Http_Save_Queues_Introduction_And_Verified_Child_Metadata_Refresh_Preserves_Health_And_Other_Product_Is_Off()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync();
        using var owner = await app.PairedClientAsync("owner-p");
        var initial = await owner.GetFromJsonAsync<ChatNotificationSetupView>(Path);
        Assert.Equal("off", initial!.State); Assert.Empty(await app.OutboxAsync()); Assert.Empty(app.Http.Calls);
        var response = await owner.PutAsJsonAsync(Path, new ChatNotificationSetupWrite(0, true, "test-channel"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("setup-pending", (await response.Content.ReadFromJsonAsync<ChatNotificationSetupView>())!.Reason);
        Assert.DoesNotContain(app.Http.Calls, c => c.Method == "sendMessage");
        Assert.Equal(1, await app.TickAsync());
        Assert.Equal("ready", (await owner.GetFromJsonAsync<ChatNotificationSetupView>(Path))!.State);
        Assert.True(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert(TelegramSliceTestFactory.Token + " E:\\private\\code"), CancellationToken.None)));
        Assert.False(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert(TelegramSliceTestFactory.Token + " E:\\private\\code"), CancellationToken.None)));
        Assert.Equal(1, await app.TickAsync());
        using var refreshed = await app.PairedClientAsync("owner-p");
        var status = await refreshed.GetFromJsonAsync<ChatNotificationSetupView>(Path);
        Assert.Equal("ready", status!.State); Assert.NotNull(status.LastSentAt);
        Assert.Equal("ready", (await app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"))).State);
        Assert.Equal("off", (await app.SliceAsync("owner-q", (s, a) => s.GetAsync(a, "q", "shared"))).State);
        var outbox = await app.OutboxAsync(); Assert.Equal(2, outbox.Count);
        Assert.All(outbox, row => { Assert.Equal("sent", row.Status); Assert.NotNull(row.ChatMessageId); Assert.Equal("-1004242", row.ChatId); });
        var sends = app.Http.Calls.Where(c => c.Method == "sendMessage").ToList(); Assert.Equal(2, sends.Count);
        foreach (var send in sends)
        {
            var text = send.Body.GetProperty("text").GetString()!;
            Assert.Contains("Open on Forge host", text);
            Assert.DoesNotContain("PRIVATE", text); Assert.DoesNotContain(TelegramSliceTestFactory.Token, text); Assert.DoesNotContain("E:\\", text);
            Assert.False(send.Body.TryGetProperty("reply_markup", out _)); Assert.False(send.Body.TryGetProperty("message_thread_id", out _));
        }
        Assert.DoesNotContain(app.Http.Calls, c => c.Method is "getUpdates" or "editMessageText" or "createForumTopic");
        Assert.DoesNotContain(TelegramSliceTestFactory.Token, JsonSerializer.Serialize(outbox));
        Assert.False(app.Services.GetRequiredService<IOptions<ChatBridgeOptions>>().Value.Enabled);
        Assert.False(app.Services.GetRequiredService<IOptions<ChatTransportOptions>>().Value.Enabled);
        Assert.False(ChatPluginInteractiveGate.IsAvailable);
    }

    [Fact]
    public async Task Cancel_Is_Read_Only_Disable_Cancels_Queued_Alerts_And_Reenable_Does_Not_Replay()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync();
        await app.SliceAsync("owner-p", (s, a) => s.GetAsync(a, "p", "shared"));
        Assert.Empty(await app.OutboxAsync());
        using (var scope = app.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().SpawnPlugins.ToListAsync());
        var enabled = await app.EnableAsync();
        var disabled = await app.SliceAsync("owner-p", (s, a) => s.SaveAsync(a, "p", "shared", new(enabled.Revision, false, null)));
        Assert.Equal("off", disabled.State); Assert.Equal(0, await app.TickAsync());
        Assert.All(await app.OutboxAsync(), n => Assert.Equal("superseded", n.Status));
        Assert.False(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert(), CancellationToken.None)));
        await app.EnableAsync(disabled.Revision); Assert.Equal(1, await app.TickAsync());
        Assert.Single(app.Http.Calls, c => c.Method == "sendMessage");
    }

    [Fact]
    public async Task Authorization_Revisions_And_Parent_Verification_Reject_Cross_Product_Or_Forged_Routes()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync();
        using var reader = await app.PairedClientAsync("reader-p");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(Path, new ChatNotificationSetupWrite(0, true, "test-channel"))).StatusCode);
        using var other = await app.PairedClientAsync("owner-q");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Path)).StatusCode);
        await app.EnableAsync();
        var conflict = await Assert.ThrowsAsync<ChatConfigurationException>(() => app.EnableAsync()); Assert.Equal("revision-conflict", conflict.Code);
        Assert.False(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert() with { SpawnId = "forged" }, CancellationToken.None)));
        Assert.False(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert() with { ProductId = "q" }, CancellationToken.None)));
        Assert.False(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert() with { RepoId = "repo-q" }, CancellationToken.None)));
        Assert.False(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert() with { WorkItemId = "missing" }, CancellationToken.None)));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.StorySpawns.Add(new() { Id = "second", StoryId = "second", RepoId = "repo-p" }); await db.SaveChangesAsync();
        }
        var second = await Assert.ThrowsAsync<ChatConfigurationException>(() => app.SliceAsync("owner-p", (s, a) => s.SaveAsync(a, "p", "second", new(0, true, "test-channel"))));
        Assert.Equal("slice-requires-one-route", second.Code); Assert.Single(await app.OutboxAsync());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("connection")]
    [InlineData("child")]
    [InlineData("run")]
    public async Task Send_Rechecks_Revocation_Connection_Revision_And_Persisted_Parent(string change)
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync(); await app.EnableAsync(); await app.TickAsync();
        Assert.True(await app.SliceAsync("owner-p", (s, _) => s.EnqueueAsync(Alert(), CancellationToken.None)));
        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            if (change == "grant") control.ChatConnectionProducts.Remove(await control.ChatConnectionProducts.SingleAsync());
            if (change == "connection") (await control.ChatConnections.SingleAsync()).Revision++;
            if (change == "child") (await db.Workstreams.SingleAsync(w => w.Id == "child-0")).StorySpawnId = null;
            if (change == "run") (await db.StorySpawns.SingleAsync()).CreatedAt = DateTimeOffset.UtcNow.AddDays(1);
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }
        Assert.Equal(0, await app.TickAsync());
        Assert.Equal("expired", (await app.OutboxAsync()).Single(n => n.Kind == "open-questions").Status);
        Assert.Single(app.Http.Calls, c => c.Method == "sendMessage");
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("new-bot")]
    [InlineData("profile")]
    [InlineData("queue")]
    [InlineData("bot-identity")]
    [InlineData("destination")]
    [InlineData("plugin-registry")]
    public async Task Invalid_Setup_Fails_Without_Persisting_Or_Sending(string failure)
    {
        await using var app = new TelegramSliceTestFactory { DuplicateChatPlugin = failure == "plugin-registry" }; await app.SetupAsync();
        var options = app.Services.GetRequiredService<IOptions<ChatPluginOptions>>().Value;
        if (failure == "legacy") options.Engine = "legacy";
        if (failure == "new-bot") options.TelegramSlice.NewTestBotConfirmed = false;
        if (failure == "profile") options.TelegramSlice.IsolatedProfileRoot = ForgeDbPaths.DefaultFolder;
        if (failure == "bot-identity") app.Http.RejectIdentity = true;
        if (failure == "destination") app.Http.RejectDestination = true;
        if (failure == "queue")
        {
            using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            db.ChatNotifications.Add(new() { Id = "legacy", Platform = "telegram", DedupeKey = "legacy", CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.EnableAsync());
        Assert.DoesNotContain(app.Http.Calls, c => c.Method == "sendMessage");
        using var check = app.Services.CreateScope(); var control = check.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.Empty(await control.SpawnPlugins.ToListAsync()); Assert.Empty(await control.ChatSubscriptions.ToListAsync());
        Assert.DoesNotContain(await app.OutboxAsync(), n => n.Platform == ChatTelegramSliceService.Platform);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_Send_Is_Visible_Sanitized_And_Never_Automatically_Repeated(bool throws)
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync(); await app.EnableAsync();
        app.Http.ThrowOnSend = throws; app.Http.OmitMessageReceipt = !throws;
        Assert.Equal(0, await app.TickAsync()); Assert.Equal(0, await app.TickAsync());
        Assert.Single(app.Http.Calls, c => c.Method == "sendMessage");
        var row = Assert.Single(await app.OutboxAsync()); Assert.Equal("uncertain", row.Status); Assert.Equal("telegram-send-unconfirmed", row.LastError);
        var state = await app.SliceAsync("owner-p", (s, a) => s.GetAsync(a, "p", "shared"));
        Assert.Equal("needs-setup", state.State); Assert.Equal("telegram-send-unconfirmed", state.Reason);
        Assert.DoesNotContain(TelegramSliceTestFactory.Token, JsonSerializer.Serialize(row));
    }

    [Fact]
    public async Task Plugin_Engine_Drops_Unsupported_Events_And_Legacy_Pump_Cannot_Send_Slice_Rows()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync(); await app.EnableAsync();
        using var scope = app.Services.CreateScope(); var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();
        Assert.IsType<ChatEngineNotifier>(notifier);
        Assert.False(await notifier.EnqueueWorkstreamStalledAsync(new("p", null, "child-0", "private", "private", "private", null, "shared"), CancellationToken.None));
        Assert.False(await notifier.EnqueueQueueStrainAsync(new("p", null, "crew", null, "private", "private", null), CancellationToken.None));
        await app.Services.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(app.Http.Calls, c => c.Method == "sendMessage"); Assert.Single(await app.OutboxAsync());
        using var admin = await app.PairedClientAsync("admin");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/chat/test-send", new { chatId = "-1004242" })).StatusCode);
    }
}