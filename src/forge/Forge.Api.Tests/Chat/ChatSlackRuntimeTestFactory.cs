// -----------------------------------------------------------------------
// <copyright file="ChatSlackRuntimeTestFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Common.Chat;
using Common.Chat.Telegram;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

internal sealed class ChatSlackRuntimeTestFactory : ChatPluginTestFactory, IChatDurableBatchSink
{
    public SlackProviderFixture Slack { get; } = new();
    public TelegramProviderFixture Telegram { get; } = new();
    public RuntimeClock Clock { get; } = new();
    public RuntimeCheckpointFailure Fault { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatProviderPlugin>(); s.AddSingleton<IChatProviderPlugin>(this.Slack.Provider); s.AddSingleton<IChatProviderPlugin>(this.Telegram.Provider);
            s.AddSingleton<IChatSecretStore>(this.Slack); s.AddSingleton<IChatSecretStore>(this.Telegram); s.AddSingleton<TimeProvider>(this.Clock);
            s.PostConfigure<ChatPluginOptions>(o => o.ProductContentModes["p"] = "redacted");
            s.AddScoped(sp => new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>(sp.GetRequiredService<DbContextOptions<ControlDbContext>>()).AddInterceptors(this.Fault).Options));
        });
        // Empty fixture polls must yield just as a real long poll would.
        this.Telegram.Override = async (_, method, _, ct) => { if (method == "getUpdates") await Task.Delay(50, ct); return null; };
    }
    public async Task SetupAsync(int questions = 2, bool linked = true, bool telegram = false, bool secondWorkspace = false)
    {
        await this.SeedAsync();
        var parent = await this.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", Plugin()));
        foreach (var (id, workspace, channel) in secondWorkspace ? new[] { ("slack-one", "T1", "C1"), ("slack-two", "T2", "G1") } : new[] { ("slack-one", "T1", "C1") })
        {
            parent = await this.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
            var config = this.Slack.Configuration(id, workspace);
            await this.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, id, new(0, "slack", workspace, id, true, 1, config.Settings, config.Secrets, ["p"])));
            await this.RunAsync("admin", (s, a) => s.PutDestinationAsync(a, id, "dest-" + id, new(0, workspace, channel, channel, channel == "G1" ? "private-channel" : "public-channel")));
            await this.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "sub-" + id,
                new(0, parent.Revision, "dest-" + id, "thread", Interactive: true, ContentMode: "redacted")));
        }
        if (telegram)
        {
            parent = await this.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
            var config = this.Telegram.Configuration();
            await this.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "bot-one", new(0, "telegram", "12345", "Telegram", true, 1, config.Settings, config.Secrets, ["p"])));
            await this.RunAsync("admin", (s, a) => s.PutDestinationAsync(a, "bot-one", "dest-bot", new(0, "12345", "-100", "Forum", "forum")));
            await this.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "sub-bot",
                new(0, parent.Revision, "dest-bot", "topic", Interactive: true, ContentMode: "redacted")));
        }
        await this.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            for (var i = 1; i <= questions; i++) db.DeliverableQuestions.Add(new() { WorkItemId = "child-0", ExternalId = "q" + i, Iter = 1,
                Text = "Choose a safe value " + i, ChoicesJson = "[\"First\",\"Second\"]" });
            await db.SaveChangesAsync();
        });
        if (linked)
        {
            var ids = this.Services.GetRequiredService<HostIdentityStore>();
            foreach (var workspace in secondWorkspace ? new[] { "T1", "T2" } : new[] { "T1" })
                Assert.NotNull(ids.RedeemLinkChallenge(ids.IssueLinkChallenge("owner-p", "slack", workspace, "U1"), "slack", workspace, "U1"));
            if (telegram) Assert.NotNull(ids.RedeemLinkChallenge(ids.IssueLinkChallenge("owner-p", "telegram", "12345", "42"), "telegram", "12345", "42"));
        }
    }
    public async Task WithAsync(Func<IServiceProvider, Task> action) { using var scope = this.Services.CreateScope(); await action(scope.ServiceProvider); }
    public async Task<T> ReadAsync<T>(Func<IServiceProvider, Task<T>> action) { using var scope = this.Services.CreateScope(); return await action(scope.ServiceProvider); }
    public Task<bool> EnqueueAsync(string id = "event", string child = "child-0") => this.ReadAsync(sp => sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("open-questions", id, "p", "shared", child)));
    public Task TickAsync() => this.WithAsync(async sp => { await sp.GetRequiredService<ChatResultReconciler>().RunOnceAsync(); await sp.GetRequiredService<ChatDeliveryService>().RunOnceAsync(); });
    public async Task DeliverAsync(int ticks = 3) { for (var i = 0; i < ticks; i++) await this.TickAsync(); }
    public Task DispatchAsync() => this.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
    public Task RespondAsync() => this.WithAsync(sp => sp.GetRequiredService<ChatInboxResponseService>().RunOnceAsync());
    public Task StartAsync() => this.Services.GetRequiredService<ChatConnectionSupervisor>().RefreshAsync(default);
    public async Task ReceiveAsync(JsonElement frame)
    {
        await this.StartAsync(); var socket = this.Slack.Sockets.Last(); var before = socket.Acknowledged.Count;
        socket.Push(frame); await SlackProviderFixture.UntilAsync(() => socket.Acknowledged.Count > before);
    }
    public async Task ReceiveTelegramAsync(JsonElement update)
    {
        this.Telegram.Updates.Enqueue(update);
        await this.WithAsync(async sp =>
        {
            var row = await sp.GetRequiredService<ControlDbContext>().ChatConnections.AsNoTracking().SingleAsync(c => c.Id == "bot-one");
            var session = (await sp.GetRequiredService<ChatConnectionSupervisor>().GetAsync(row, default))!;
            Assert.True((await ((TelegramSession)session.Provider).PollOnceAsync(this)).Succeeded);
        });
    }
    public ValueTask<ChatInboundAcceptance> AcceptAsync(ChatInboundEnvelope envelope, CancellationToken ct = default) => throw new NotSupportedException();
    public async ValueTask<ChatInboundAcceptance> AcceptBatchAsync(IReadOnlyList<ChatInboundEnvelope> envelopes, IReadOnlyList<ChatAcceptedCursor> cursors, CancellationToken ct = default)
        => await this.ReadAsync(async sp =>
        {
            var revision = await sp.GetRequiredService<ControlDbContext>().ChatConnections.Where(c => c.Id == "bot-one").Select(c => c.Revision).SingleAsync(ct);
            return await sp.GetRequiredService<ChatInboxService>().AcceptBatchAsync(envelopes, cursors, new Dictionary<string, long> { ["bot-one"] = revision }, ct);
        });
    public Task<List<ChatDeliveryMessageRow>> MessagesAsync() => this.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveryMessages.AsNoTracking().ToListAsync());
    public Task<List<ChatInboxRow>> InboxAsync() => this.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.AsNoTracking().ToListAsync());
    public Task<List<DeliverableQuestionRow>> QuestionsAsync() => this.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.AsNoTracking().ToListAsync());
    public async Task<ChatAddress> AddressAsync(string connection = "slack-one") => JsonSerializer.Deserialize<ChatAddress>((await this.MessagesAsync()).First(m => JsonSerializer.Deserialize<ChatAddress>(m.AddressJson)?.ConnectionId == connection).AddressJson)!;
    public string Button(string question = "q1", int index = 0, string workspace = "T1") => this.Slack.Calls.First(c => c.Workspace == workspace && c.Method == "chat.postMessage"
        && c.Body.GetProperty("text").GetString()!.Contains("Choose a safe value " + question[1..], StringComparison.Ordinal))
        .Body.GetProperty("blocks")[1].GetProperty("elements")[index].GetProperty("value").GetString()!;
    public async Task<ChatDeliveryMessageRow> MessageAsync(string question = "q1", string connection = "slack-one") => (await this.MessagesAsync())
        .Single(m => m.QuestionId == question && m.PartKey.StartsWith("question:", StringComparison.Ordinal) && JsonSerializer.Deserialize<ChatAddress>(m.AddressJson)?.ConnectionId == connection);
}