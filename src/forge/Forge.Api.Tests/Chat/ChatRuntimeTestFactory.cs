// -----------------------------------------------------------------------
// <copyright file="ChatRuntimeTestFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Collections.Concurrent;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

internal sealed class ChatRuntimeTestFactory : ChatPluginTestFactory
{
    public RuntimeClock Clock { get; } = new();
    public RuntimeProvider Alpha { get; } = new("FakeAlpha");
    public RuntimeProvider Beta { get; } = new("FakeBeta");
    public RuntimeProvider Slack { get; } = new("slack") { SharedReceptionKey = "app-test" };
    public RuntimeCheckpointFailure Fault { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatProviderPlugin>(); s.AddSingleton<IChatProviderPlugin>(this.Alpha); s.AddSingleton<IChatProviderPlugin>(this.Beta); s.AddSingleton<IChatProviderPlugin>(this.Slack);
            s.AddSingleton<TimeProvider>(this.Clock);
            s.PostConfigure<ChatPluginOptions>(o => o.ProductContentModes["p"] = "redacted");
            s.AddScoped(sp => new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>(
                sp.GetRequiredService<DbContextOptions<ControlDbContext>>()).AddInterceptors(this.Fault).Options));
        });
    }

    public async Task SetupAsync(bool interactive = false, string conversation = "flat", int questions = 1)
    {
        await this.SeedAsync(); await this.ConnectionsAsync();
        var parent = await this.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", Plugin()));
        foreach (var connection in new[] { "alpha-one", "beta" })
        {
            await this.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "sub-" + connection,
                new(0, parent.Revision, "dest-" + connection, conversation, ContentMode: "redacted")));
            parent = await this.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        }
        await this.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>();
            if (interactive) foreach (var sub in await control.ChatSubscriptions.ToListAsync()) sub.Interactive = true;
            await control.SaveChangesAsync();
            var product = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            for (var i = 1; i <= questions; i++) product.DeliverableQuestions.Add(new() { WorkItemId = "child-0", ExternalId = "q" + i, Iter = 1, Text = "Choose a safe value " + i });
            await product.SaveChangesAsync();
        });
        var identities = this.Services.GetRequiredService<HostIdentityStore>();
        foreach (var provider in new[] { "FakeAlpha", "FakeBeta" })
        {
            var token = identities.IssueLinkChallenge("owner-p", provider, "tenant", "operator");
            Assert.NotNull(identities.RedeemLinkChallenge(token, provider, "tenant", "operator"));
        }
    }

    public async Task WithAsync(Func<IServiceProvider, Task> action)
    { using var scope = this.Services.CreateScope(); await action(scope.ServiceProvider); }
    public async Task<T> ReadAsync<T>(Func<IServiceProvider, Task<T>> action)
    { using var scope = this.Services.CreateScope(); return await action(scope.ServiceProvider); }
    public Task<bool> EnqueueAsync(string dedupe = "event-1", long revision = 1) => this.ReadAsync(sp => sp.GetRequiredService<ChatEventService>()
        .EnqueueAsync(new("open-questions", dedupe, "p", "shared", "child-0", Revision: revision)));
    public Task TickAsync() => this.WithAsync(async sp =>
    {
        await sp.GetRequiredService<ChatResultReconciler>().RunOnceAsync();
        await sp.GetRequiredService<ChatDeliveryService>().RunOnceAsync();
    });
    public Task<List<ChatDeliveryRow>> DeliveriesAsync() => this.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.AsNoTracking().OrderBy(d => d.ConnectionId).ToListAsync());
    public Task<List<ChatDeliveryMessageRow>> MessagesAsync() => this.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveryMessages.AsNoTracking().ToListAsync());
    public Task<ChatInboundAcceptance> ReceiveAsync(ChatInboundEnvelope envelope, string? cursor = null) => this.ReadAsync(async sp =>
    {
        var revision = await sp.GetRequiredService<ControlDbContext>().ChatConnections.Where(c => c.Id == envelope.ConnectionId).Select(c => c.Revision).SingleAsync();
        return await sp.GetRequiredService<ChatInboxService>().AcceptBatchAsync([envelope], cursor is null ? [] : [new(envelope.ConnectionId, cursor)], new Dictionary<string, long> { [envelope.ConnectionId] = revision });
    });
    public ChatInboundEnvelope Answer(RuntimeProvider provider, string connection, string deliveryKey, int question = 0)
    {
        var sent = provider.Sent.Where(s => s.Address.ConnectionId == connection).ElementAt(question);
        return new(provider.Descriptor.Id, connection, deliveryKey, sent.Address, "operator", "action", "incoming-" + deliveryKey,
            sent.MessageId, "safe answer", sent.Notification.Actions.Single().Token, this.Clock.GetUtcNow());
    }
}

internal sealed class RuntimeClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => this.now;
    public void Advance(TimeSpan duration) => this.now += duration;
}

internal sealed class RuntimeCheckpointFailure : SaveChangesInterceptor
{
    public string? Armed { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var db = eventData.Context!;
        var fail = this.Armed switch
        {
            "send" => db.ChangeTracker.Entries<ChatDeliveryMessageRow>().Any(e => e.State == EntityState.Modified && e.Entity.State == "sent"
                && db.Set<ChatDeliveryRow>().Local.Any(d => d.Id == e.Entity.DeliveryId && d.ConnectionId == "alpha-one")),
            "claim" => db.ChangeTracker.Entries<ChatDeliveryMessageRow>().Any(e => e.Entity.State == "sending"
                && db.Set<ChatDeliveryRow>().Local.Any(d => d.Id == e.Entity.DeliveryId && d.ConnectionId == "alpha-one")),
            "inbox" => db.ChangeTracker.Entries<ChatInboxRow>().Any(e => e.State == EntityState.Added),
            "dispatch" => db.ChangeTracker.Entries<ChatInboxRow>().Any(e => e.Entity.State == "processed"),
            "event" => db.ChangeTracker.Entries<ChatNotificationRow>().Any(e => e.State == EntityState.Added && e.Entity.Platform == "plugins"),
            _ => false,
        };
        if (fail) { this.Armed = null; throw new InvalidOperationException("Injected storage checkpoint failure"); }
        return ValueTask.FromResult(result);
    }
}

internal sealed class RuntimeProvider(string id) : IChatProviderPlugin
{
    public ChatProviderDescriptor Descriptor { get; } = new(id, id, 1, [1], new(id == "slack" ? ChatReceptionMode.Socket : ChatReceptionMode.LongPolling, true, true, true, true, true, true,
        ChatConversationRetirement.RemoteClose, 4000, 16000, 64), []);
    public ConcurrentQueue<(ChatAddress Address, ChatSemanticNotification Notification, string MessageId)> Sent { get; } = new();
    public ConcurrentQueue<ChatSemanticNotification> Edits { get; } = new();
    public ConcurrentDictionary<string, ChatProviderFailure> Failures { get; } = new();
    public ConcurrentDictionary<string, RuntimeSession> Sessions { get; } = new();
    public int SendCalls;
    public int ProvisionCalls;
    public bool UncertainProvision;
    public string? SharedReceptionKey { get; set; }
    public ChatSettingsValidation ValidateSettings(int version, JsonElement settings) => ChatSettingsValidation.Success;
    public ValueTask<ChatProviderResult<IChatConnectionSession>> OpenSessionAsync(ChatConnectionConfiguration connection, IChatSecretStore secrets, CancellationToken ct = default)
    {
        var session = new RuntimeSession(connection, this); this.Sessions[connection.Id] = session;
        return ValueTask.FromResult(ChatProviderResult<IChatConnectionSession>.Success(session));
    }
    internal sealed class RuntimeSession(ChatConnectionConfiguration connection, RuntimeProvider provider) : IChatConnectionSession, IChatResumableSession
    {
        public string ConnectionId => connection.Id;
        public string ReceptionKey => provider.SharedReceptionKey ?? connection.InstallationId;
        public IChatInboundSink? Sink { get; private set; }
        public string? Cursor { get; private set; }
        public int ReceiveCalls { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask RestoreCursorAsync(string? cursor, CancellationToken ct = default) { this.Cursor = cursor; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { this.Disposed = true; return ValueTask.CompletedTask; }
        public ValueTask<ChatProviderResult<ChatDestination>> ValidateDestinationAsync(ChatAddress address, CancellationToken ct = default)
            => ValueTask.FromResult(ChatProviderResult<ChatDestination>.Success(new(address, "safe", "group")));
        public ValueTask<ChatProviderResult<ChatDestinationPage>> DiscoverAsync(string? cursor, CancellationToken ct = default)
            => ValueTask.FromResult(ChatProviderResult<ChatDestinationPage>.Success(new([], null)));
        public ValueTask<ChatProviderResult<ChatConversation>> EnsureConversationAsync(ChatDestination destination, ChatConversationPolicy policy, string operationKey, string title, CancellationToken ct = default)
        {
            Interlocked.Increment(ref provider.ProvisionCalls);
            return ValueTask.FromResult(provider.UncertainProvision ? ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.UnknownOutcome, "uncertain")
                : ChatProviderResult<ChatConversation>.Success(new(destination.Address with { ConversationId = operationKey }, "root")));
        }
        public ValueTask<ChatProviderResult<bool>> RetireConversationAsync(ChatConversation conversation, CancellationToken ct = default)
            => ValueTask.FromResult(ChatProviderResult<bool>.Success(true));
        public ValueTask<ChatProviderResult<ChatSendReceipt>> SendAsync(ChatAddress address, ChatSemanticNotification notification, CancellationToken ct = default)
        {
            Interlocked.Increment(ref provider.SendCalls);
            if (provider.Failures.TryRemove(connection.Id, out var failure)) return ValueTask.FromResult(new ChatProviderResult<ChatSendReceipt>(null, failure));
            var id = "message-" + Guid.NewGuid().ToString("N"); provider.Sent.Enqueue((address, notification, id));
            return ValueTask.FromResult(ChatProviderResult<ChatSendReceipt>.Success(new([new(address, id)])));
        }
        public ValueTask<ChatProviderResult<bool>> EditAsync(ChatMessageReference message, ChatSemanticNotification notification, CancellationToken ct = default)
        { provider.Edits.Enqueue(notification); return ValueTask.FromResult(ChatProviderResult<bool>.Success(true)); }
        public async Task ReceiveAsync(IChatInboundSink sink, CancellationToken ct)
        { this.Sink = sink; this.ReceiveCalls++; await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
    }
}