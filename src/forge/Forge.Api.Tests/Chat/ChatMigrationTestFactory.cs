// -----------------------------------------------------------------------
// <copyright file="ChatMigrationTestFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal sealed class ChatMigrationTestFactory(string? blobPath = null) : ChatPluginTestFactory
{
    public const string Token = "12345:ABCDEFGHIJKLMNOPQRSTUVWXYZ_123456789";
    public TelegramProviderFixture Http { get; } = new();
    public RuntimeClock Clock { get; } = new();
    public MigrationFailure Fault { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Chat:Engine"] = "legacy", ["Forge:ChatBridge:Enabled"] = "true", ["Forge:ChatBridge:AllowRemoteActions"] = "true",
            ["Forge:ChatBridge:Telegram:BotToken"] = blobPath is null ? Token : "",
            ["Forge:ChatBridge:TokenBlobPath"] = blobPath ?? "",
            ["Forge:ChatBridge:Telegram:AllowedChatIds:0"] = "-200", ["Forge:ChatBridge:Telegram:AllowedChatIds:1"] = "-100",
            ["Forge:ChatBridge:Telegram:SupergroupChatId"] = "-100", ["Forge:ChatBridge:Telegram:LongPollTimeoutSec"] = "1",
        }));
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatProviderPlugin>(); s.AddSingleton<IChatProviderPlugin>(this.Http.Provider);
            s.AddSingleton<IHttpClientFactory>(this.Http); s.AddSingleton<TimeProvider>(this.Clock);
            s.PostConfigure<ChatPluginOptions>(o => { o.Engine = "legacy"; o.ProductContentModes["p"] = "redacted"; });
            s.AddScoped(sp => new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>(
                sp.GetRequiredService<DbContextOptions<ControlDbContext>>()).AddInterceptors(this.Fault).Options));
        });
    }
    public async Task SetupAsync(params ChatNotificationRow[] notifications)
    {
        await this.SeedAsync();
        await this.WithAsync(async sp =>
        {
            foreach (var product in new[] { "p", "q" })
            {
                var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor(product);
                (await db.StorySpawns.SingleAsync()).CreatedAt = DateTimeOffset.UtcNow.AddDays(-7); await db.SaveChangesAsync();
            }
        });
        var identities = this.Services.GetRequiredService<HostIdentityStore>();
        identities.RedeemLinkChallenge(identities.IssueLinkChallenge("owner-p", "telegram", "-100", "42"), "telegram", "-100", "42");
        await this.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            db.UserProductRoles.Add(new() { UserId = "admin", ProductId = "p", Role = "product-owner" });
            db.ChatSpawnTopics.Add(new() { ProductId = "p", SpawnId = "shared", ChatId = "-100", ThreadId = "7", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) });
            db.ChatBridgeState.Add(new() { Id = ChatBridgeStateRow.SingletonId, LastUpdateId = 41 });
            db.ChatNotifications.AddRange(notifications);
            await db.SaveChangesAsync();
        });
        this.Clock.Advance(TimeSpan.FromMinutes(1));
    }
    public static ChatNotificationRow Notification(string id, string status = "sent", string kind = "queue-strain", string? product = null) => new()
    {
        Id = id, Platform = "telegram", Status = status, Kind = kind, DedupeKey = "old:" + id, ChatId = "-100",
        ChatMessageId = status is "pending" or "failed" or "sending" ? null : "77-" + id, ProductId = product ?? "", SpawnId = product is null ? null : "shared",
        WorkstreamId = kind == "workstream-stalled" ? "child-0" : null, WorkItemId = kind == "open-questions" ? "child-0" : null,
        Title = "Old alert", Body = "Choose a safe value 1\nChoose a safe value 2", CreatedAt = DateTimeOffset.UtcNow,
        SentAt = status == "pending" ? null : DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(2),
        RespondedAt = status is "resolving" or "answered" or "acted" or "expired" or "superseded" ? DateTimeOffset.UtcNow : null,
    };
    public async Task WithAsync(Func<IServiceProvider, Task> run) { using var scope = this.Services.CreateScope(); await run(scope.ServiceProvider); }
    public async Task<T> ReadAsync<T>(Func<IServiceProvider, Task<T>> run) { using var scope = this.Services.CreateScope(); return await run(scope.ServiceProvider); }
    public Task<T> MigrateAsync<T>(Func<ChatMigrationService, ForgeActor, Task<T>> run, string user = "admin") => this.ReadAsync(async sp =>
        await run(sp.GetRequiredService<ChatMigrationService>(), (await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", user))!));
    public Task<ChatMigrationPreview> PreviewAsync() => this.MigrateAsync((s, a) => s.PreviewAsync(a));
    public async Task<ChatMigrationPreview> ImportAsync()
    {
        var p = await this.PreviewAsync(); return await this.MigrateAsync((s, a) => s.ImportAsync(a, new(p.Revision, p.SourceHash, "12345")));
    }
    public async Task<ChatMigrationPreview> CutoverAsync(params ChatMigrationSelection[] selected)
    {
        var p = await this.PreviewAsync(); return await this.MigrateAsync((s, a) => s.CutoverAsync(a, new(p.Revision, selected)));
    }
    public async Task<ChatMigrationPreview> RollbackAsync(bool pauseUnsupported = false)
    {
        var p = await this.PreviewAsync(); return await this.MigrateAsync((s, a) => s.RollbackAsync(a, new(p.Revision, pauseUnsupported)));
    }
    public Task TickAsync() => this.WithAsync(async sp =>
    { await sp.GetRequiredService<ChatResultReconciler>().RunOnceAsync(); await sp.GetRequiredService<ChatDeliveryService>().RunOnceAsync(); });
    public Task ReceiveAsync(string key, string message, string? callback = null, string? text = null, string channel = "-100", string? topic = "7") => this.WithAsync(async sp =>
    {
        var db = sp.GetRequiredService<ControlDbContext>(); var connection = await db.ChatConnections.SingleAsync(c => c.Id == "legacy-telegram");
        await sp.GetRequiredService<ChatInboxService>().AcceptBatchAsync([new("telegram", connection.Id, key,
            new(connection.Id, "12345", channel, topic), "42", callback is null ? "message" : "action", "incoming-" + key, message,
            text, callback, this.Clock.GetUtcNow())], [new(connection.Id, key)], new Dictionary<string, long> { [connection.Id] = connection.Revision });
        await sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync();
    });
}

internal sealed class MigrationFailure : SaveChangesInterceptor
{
    public string? Armed { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var db = data.Context!;
        var fail = this.Armed switch
        {
            "import" => db.ChangeTracker.Entries<ChatDeliveryRow>().Any(e => e.State == EntityState.Added),
            "cutover" => db.ChangeTracker.Entries<ChatMigrationStateRow>().Any(e => e.Entity.Owner == "plugins"),
            "rollback" => db.ChangeTracker.Entries<ChatMigrationStateRow>().Any(e => e.Entity.Phase == "legacy"),
            _ => false,
        };
        if (fail) { this.Armed = null; throw new InvalidOperationException("Injected migration checkpoint failure"); }
        return ValueTask.FromResult(result);
    }
}