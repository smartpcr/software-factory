// -----------------------------------------------------------------------
// <copyright file="ChatTelegramRuntimeTestFactory.cs" company="Microsoft Corp.">
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

internal sealed class ChatTelegramRuntimeTestFactory : ChatPluginTestFactory, IChatDurableBatchSink
{
    public TelegramProviderFixture Http { get; } = new();
    public RuntimeClock Clock { get; } = new();
    public RuntimeCheckpointFailure Fault { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatProviderPlugin>(); s.AddSingleton<IChatProviderPlugin>(this.Http.Provider);
            s.AddSingleton<IChatSecretStore>(this.Http); s.AddSingleton<TimeProvider>(this.Clock);
            s.PostConfigure<ChatPluginOptions>(o => o.ProductContentModes["p"] = "redacted");
            s.AddScoped(sp => new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>(
                sp.GetRequiredService<DbContextOptions<ControlDbContext>>()).AddInterceptors(this.Fault).Options));
        });
    }
    public async Task SetupAsync(int questions = 2, bool linked = true, string policy = "topic")
    {
        await this.SeedAsync();
        await this.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "bot-one", new(0, "telegram", "12345", "Bot", true, 1,
            ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference> { ["bot-token"] = new("fixture", "12345") }, ["p"])));
        await this.RunAsync("admin", (s, a) => s.PutDestinationAsync(a, "bot-one", "destination", new(0, "12345", "-100", "Forum", "forum")));
        var parent = await this.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", Plugin()));
        await this.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "subscription",
            new(0, parent.Revision, "destination", policy, Interactive: true, ContentMode: "redacted")));
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
            Assert.NotNull(ids.RedeemLinkChallenge(ids.IssueLinkChallenge("owner-p", "telegram", "12345", "42"), "telegram", "12345", "42"));
        }
    }
    public async Task WithAsync(Func<IServiceProvider, Task> action) { using var scope = this.Services.CreateScope(); await action(scope.ServiceProvider); }
    public async Task<T> ReadAsync<T>(Func<IServiceProvider, Task<T>> action) { using var scope = this.Services.CreateScope(); return await action(scope.ServiceProvider); }
    public Task<bool> EnqueueAsync(string dedupe = "event", string child = "child-0") => this.ReadAsync(sp => sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("open-questions", dedupe, "p", "shared", child)));
    public Task TickAsync() => this.WithAsync(async sp =>
    { await sp.GetRequiredService<ChatResultReconciler>().RunOnceAsync(); await sp.GetRequiredService<ChatDeliveryService>().RunOnceAsync(); });
    public Task DispatchAsync() => this.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
    public Task RespondAsync() => this.WithAsync(sp => sp.GetRequiredService<ChatInboxResponseService>().RunOnceAsync());
    public async Task ReceiveAsync(JsonElement update)
    {
        this.Http.Updates.Enqueue(update);
        await this.WithAsync(async sp =>
        {
            var connection = await sp.GetRequiredService<ControlDbContext>().ChatConnections.AsNoTracking().SingleAsync(c => c.Id == "bot-one");
            var session = (await sp.GetRequiredService<ChatConnectionSupervisor>().GetAsync(connection, CancellationToken.None))!;
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
    public Task<List<ChatInboxRow>> InboxAsync() => this.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.AsNoTracking().OrderBy(i => i.DeliveryKey).ToListAsync());
    public Task<List<ChatDeliveryMessageRow>> MessagesAsync() => this.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveryMessages.AsNoTracking().ToListAsync());
    public static JsonElement Callback(int update, string token, string message, string sender = "42", string topic = "7") => JsonSerializer.SerializeToElement(new
    {
        update_id = update, callback_query = new { id = "callback-" + update, data = token, from = new { id = sender, is_bot = false },
            message = new { message_id = message, message_thread_id = topic, chat = new { id = "-100", type = "supergroup", is_forum = true } } },
    });
    public static JsonElement Reply(int update, string message, string answer, string sender = "42", string topic = "7") => JsonSerializer.SerializeToElement(new
    {
        update_id = update, message = new { message_id = "reply-" + update, message_thread_id = topic, chat = new { id = "-100", type = "supergroup" },
            from = new { id = sender, is_bot = false }, text = answer, reply_to_message = new { message_id = message } },
    });
    public string Button(int question, int choice) => this.Http.Calls.Where(c => c.Method == "sendMessage" && c.Body.GetProperty("reply_markup").TryGetProperty("inline_keyboard", out var keys) && keys.GetArrayLength() > 0)
        .ElementAt(question).Body.GetProperty("reply_markup").GetProperty("inline_keyboard")[choice][0].GetProperty("callback_data").GetString()!;
    public string ButtonFor(string question, int choice) => this.Http.Calls.First(c => c.Method == "sendMessage" && c.Body.GetProperty("text").GetString()!.Contains("Choose a safe value " + question[1..], StringComparison.Ordinal))
        .Body.GetProperty("reply_markup").GetProperty("inline_keyboard")[choice][0].GetProperty("callback_data").GetString()!;
}