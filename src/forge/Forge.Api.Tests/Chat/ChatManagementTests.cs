// -----------------------------------------------------------------------
// <copyright file="ChatManagementTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatManagementTests
{
    private const string SetupPath = "/api/products/p/story-spawns/shared/plugins/chat/setup";

    [Fact]
    public async Task Drawer_save_is_atomic_revision_guarded_and_product_scoped()
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync(); await f.ConnectionsAsync();
        using var owner = await f.PairedClientAsync("owner-p");
        var invalid = await owner.PutAsJsonAsync(SetupPath, new ChatSetupWrite(0, true, [new("dest-alpha-one", "topic", false), new("missing", "flat", false)]));
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        Assert.Equal(0, (await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"))).Revision);
        var response = await owner.PutAsJsonAsync(SetupPath, new ChatSetupWrite(0, true, [new("dest-alpha-one", "topic", false), new("dest-beta", "flat", false)]));
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<ChatSetupView>())!;
        Assert.True(saved.Plugin.Enabled); Assert.Equal(2, saved.Plugin.Subscriptions.Count);
        var same = await owner.PutAsJsonAsync(SetupPath, new ChatSetupWrite(saved.Plugin.Revision, true, [new("dest-alpha-one", "topic", false), new("dest-beta", "flat", false)]));
        same.EnsureSuccessStatusCode();
        var repeated = (await same.Content.ReadFromJsonAsync<ChatSetupView>())!;
        Assert.Equal(saved.Plugin.Subscriptions.Select(s => (s.Id, s.Revision)).Order(), repeated.Plugin.Subscriptions.Select(s => (s.Id, s.Revision)).Order());
        var conflict = await owner.PutAsJsonAsync(SetupPath, new ChatSetupWrite(0, false, []));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.False((await f.RunAsync("owner-q", (s, a) => s.GetPluginAsync(a, "q", "shared"))).Enabled);
        using var reader = await f.PairedClientAsync("reader-p");
        Assert.False((await reader.GetFromJsonAsync<ChatSetupView>(SetupPath))!.CanManage);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(SetupPath, new ChatSetupWrite(saved.Plugin.Revision, false, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/chat/host/status")).StatusCode);
    }

    [Fact]
    public async Task Completed_parent_and_current_future_children_keep_their_work_state()
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync(); await f.ConnectionsAsync();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            (await db.StorySpawns.SingleAsync()).Phase = "done"; await db.SaveChangesAsync();
        }
        var saved = await f.RunAsync("owner-p", (s, a) => s.SaveSetupAsync(a, "p", "shared", new(0, true, [new("dest-alpha-one", "topic", false)]), default));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            Assert.Equal("done", (await db.StorySpawns.SingleAsync()).Phase);
            Assert.All(await db.Workstreams.ToListAsync(), w => Assert.Equal("running", w.Status));
            db.Workstreams.Add(new() { Id = "new-child", RepoId = "repo-p", StorySpawnId = "shared", Status = "running" }); await db.SaveChangesAsync();
            Assert.All(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatConversationBindings.ToListAsync(), b => Assert.Equal("pending", b.ProvisioningState));
        }
        foreach (var child in new[] { "child-0", "child-31", "new-child" })
        {
            var inherited = await f.RunAsync("owner-p", (s, a) => s.GetForChildAsync(a, "p", child));
            Assert.Equal(saved.Plugin.Subscriptions.Single().Id, inherited.Subscriptions.Single().Id);
        }
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(100, 1)]
    [InlineData(201, 3)]
    public async Task Summary_reads_exactly_one_control_query_per_chunk(int rowCount, int expectedQueries)
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync();
        using var scope = f.Services.CreateScope();
        var counter = new QueryCounter();
        var options = new DbContextOptionsBuilder<ControlDbContext>(scope.ServiceProvider.GetRequiredService<DbContextOptions<ControlDbContext>>()).AddInterceptors(counter).Options;
        await using var control = new ControlDbContext(options);
        var rows = Enumerable.Range(0, rowCount).Select(i => (new StorySpawnRow { Id = "row-" + i }, "p")).ToList();
        var result = await ChatSummaryService.ReadAsync(control, rows);
        Assert.Equal(expectedQueries, counter.Reads); Assert.Equal(rowCount, result.Count);
        Assert.All(result.Values, r => Assert.Equal("off", r.State));
    }

    [Fact]
    public async Task Management_credentials_and_preview_are_authorized_and_redacted()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync();
        using var admin = await f.PairedClientAsync("admin"); using var owner = await f.PairedClientAsync("owner-p");
        var management = (await owner.GetFromJsonAsync<ChatManagementView>("/api/chat/management"))!;
        Assert.False(management.CanManageConnections); Assert.False(management.CanManageHostAlerts); Assert.Single(management.Products);
        Assert.Contains(management.Links, l => l.ProviderId == "slack");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/chat/connections/slack-one/health")).StatusCode);
        var before = (await admin.GetFromJsonAsync<ChatConnectionDetails>("/api/chat/connections/slack-one"))!;
        var secret = "xoxb-new-secret-for-storage-test";
        var stored = await admin.PutAsJsonAsync("/api/chat/connections/slack-one/credentials/bot-token", new ChatCredentialWrite(before.Revision, secret));
        stored.EnsureSuccessStatusCode(); Assert.DoesNotContain(secret, await stored.Content.ReadAsStringAsync());
        var after = (await admin.GetFromJsonAsync<ChatConnectionDetails>("/api/chat/connections/slack-one"))!;
        Assert.Equal(before.Revision + 1, after.Revision); Assert.Equal("dpapi", after.SecretReferences["bot-token"].Store);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(after));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/chat/connections/slack-one/credentials/bot-token", new ChatCredentialWrite(before.Revision, secret))).StatusCode);
        var preview = await owner.PostAsJsonAsync("/api/chat/preview", new ChatPreviewRequest("redacted", "p", secret + " C:\\private\\file"));
        preview.EnsureSuccessStatusCode(); Assert.DoesNotContain(secret, await preview.Content.ReadAsStringAsync()); Assert.DoesNotContain("private", await preview.Content.ReadAsStringAsync());
        Assert.DoesNotContain(f.Slack.Calls, c => c.Method == "chat.postMessage");
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("telegram")]
    public async Task Validation_never_sends_and_test_send_is_idempotent_with_durable_reply_receipt(string provider)
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(telegram: provider == "telegram");
        using var admin = await f.PairedClientAsync("admin");
        var channel = provider == "slack" ? "/api/chat/connections/slack-one/destinations/dest-slack-one" : "/api/chat/connections/bot-one/destinations/dest-bot";
        (await admin.PostAsync(channel + "/validate", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(f.Slack.Calls, c => c.Method == "chat.postMessage");
        var request = new ChatCheckRequest(Guid.NewGuid().ToString(), 1, 1);
        var response = await admin.PostAsJsonAsync(channel + "/checks", request); response.EnsureSuccessStatusCode();
        var check = (await response.Content.ReadFromJsonAsync<ChatCheckView>())!; Assert.Equal("sent", check.State);
        (await admin.PostAsJsonAsync(channel + "/checks", request)).EnsureSuccessStatusCode();
        Assert.Equal(1, provider == "slack" ? f.Slack.Calls.Count(c => c.Method == "chat.postMessage") : f.Telegram.Calls.Count(c => c.Method == "sendMessage"));
        var row = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == check.Id));
        if (provider == "slack") await f.ReceiveAsync(SlackProviderFixture.Event("check-reply", thread: row.ChatMessageId, text: "check"));
        else await f.ReceiveTelegramAsync(JsonSerializer.SerializeToElement(new { update_id = 100, message = new { message_id = 101,
            chat = new { id = -100, type = "supergroup" }, @from = new { id = 42, is_bot = false },
            reply_to_message = new { message_id = int.Parse(row.ChatMessageId!) }, text = "check" } }));
        Assert.Equal("connection-check", Assert.Single(await f.InboxAsync()).Result);
        var checks = (await admin.GetFromJsonAsync<ChatCheckView[]>(channel + "/checks"))!; Assert.NotNull(Assert.Single(checks).ReplyAt);
        Assert.Empty(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.ToListAsync()));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(channel + "/checks", request with { RequestId = Guid.NewGuid().ToString(), ConnectionRevision = 0 })).StatusCode);
    }

    [Fact]
    public async Task Cancelled_or_incomplete_setup_creates_no_topic_and_summaries_include_saved_destinations()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync();
        using var owner = await f.PairedClientAsync("owner-p");
        var setup = (await owner.GetFromJsonAsync<ChatSetupView>(SetupPath))!;
        Assert.True(setup.CanManage); Assert.Empty(f.Slack.Calls);
        var response = await owner.PutAsJsonAsync(SetupPath, new ChatSetupWrite(setup.Plugin.Revision, false, [new("dest-slack-one", "thread", false)]));
        response.EnsureSuccessStatusCode(); Assert.DoesNotContain(f.Slack.Calls, c => c.Method == "chat.postMessage");
        var summaries = (await owner.GetFromJsonAsync<ChatSummaryItem[]>("/api/products/p/chat/summaries"))!;
        var summary = Assert.Single(summaries).Summary; Assert.Equal("off", summary.State); Assert.Equal("slack", Assert.Single(summary.Destinations!).ProviderId);
        Assert.Equal((await response.Content.ReadFromJsonAsync<ChatSetupView>())!.Plugin.Revision, summary.Revision);
    }

    [Fact]
    public async Task Newly_added_child_uses_the_saved_parent_conversation_on_both_providers()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1, telegram: true);
        var setup = await f.RunAsync("owner-p", (s, a) => s.SetupAsync(a, "p", "shared", default));
        await f.RunAsync("owner-p", (s, a) => s.SaveSetupAsync(a, "p", "shared", new(setup.Plugin.Revision, true,
            [new("dest-slack-one", "thread", true, "redacted"), new("dest-bot", "topic", true, "redacted")]), default));
        await f.DeliverAsync(); // Save alone provisions and introduces both destinations, without a work event.
        Assert.Single(f.Telegram.Calls.Where(c => c.Method == "createForumTopic"));
        Assert.Single(f.Slack.Calls.Where(c => c.Method == "chat.postMessage" && !c.Body.TryGetProperty("thread_ts", out _)));
        await f.EnqueueAsync(); await f.DeliverAsync();
        var before = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.AsNoTracking().Where(b => b.RetiredAt == null).ToListAsync());
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.Workstreams.Add(new() { Id = "later-child", RepoId = "repo-p", StorySpawnId = "shared", Status = "running" });
            db.DeliverableQuestions.Add(new() { WorkItemId = "later-child", ExternalId = "new-question", Iter = 1, Text = "A new child question" });
            await db.SaveChangesAsync();
        });
        await f.EnqueueAsync("new-child-event", "later-child"); await f.DeliverAsync(5);
        var after = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.AsNoTracking().Where(b => b.RetiredAt == null).ToListAsync());
        Assert.Equal(2, after.Count); Assert.Equal(before.Select(b => b.Id).Order(), after.Select(b => b.Id).Order());
        Assert.Single(f.Telegram.Calls.Where(c => c.Method == "createForumTopic"));
        Assert.Single(f.Slack.Calls.Where(c => c.Method == "chat.postMessage" && !c.Body.TryGetProperty("thread_ts", out _)));
        Assert.All(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").Workstreams.ToListAsync()), w => Assert.Equal("running", w.Status));
    }

    [Fact]
    public async Task Row_detail_reads_omit_chat_aggregation_while_detail_and_product_summary_reads_include_it()
    {
        await using var f = new SummaryHttpFactory(); await f.SeedAsync();
        using var client = await f.PairedClientAsync("owner-p"); client.DefaultRequestHeaders.Add("X-Forge-Product-Id", "p");
        var before = f.Counter.SummaryReads;
        (await client.GetAsync("/api/stories/story/spawn?includeChatSummary=false")).EnsureSuccessStatusCode();
        Assert.Equal(before, f.Counter.SummaryReads);
        (await client.GetAsync("/api/stories/story/spawn")).EnsureSuccessStatusCode();
        Assert.Equal(before + 1, f.Counter.SummaryReads);
        (await client.GetAsync("/api/products/p/chat/summaries")).EnsureSuccessStatusCode();
        Assert.Equal(before + 2, f.Counter.SummaryReads);
    }

    private sealed class SummaryHttpFactory : ChatPluginTestFactory
    {
        public QueryCounter Counter { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddScoped(sp => new ControlDbContext(
                new DbContextOptionsBuilder<ControlDbContext>(sp.GetRequiredService<DbContextOptions<ControlDbContext>>()).AddInterceptors(this.Counter).Options)));
        }
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public int SummaryReads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { this.Reads++; if (command.CommandText.Contains("WITH requested(", StringComparison.Ordinal)) this.SummaryReads++; return ValueTask.FromResult(result); }
    }
}