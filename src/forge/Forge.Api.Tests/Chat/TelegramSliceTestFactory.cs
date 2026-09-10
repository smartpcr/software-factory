// -----------------------------------------------------------------------
// <copyright file="TelegramSliceTestFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Plugins;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal sealed class TelegramSliceTestFactory : ChatPluginTestFactory
{
    public const string Token = "123456:STAGE1S_FAKE_TOKEN_ONLY_1234567890";
    public string TokenKey { get; } = "FORGE_CHAT_TEST_" + Guid.NewGuid().ToString("N");
    public FakeTelegramHttp Http { get; } = new();
    public SliceCheckpointFailure Checkpoint { get; } = new();
    public bool DuplicateChatPlugin { get; set; }
    public TelegramSliceTestFactory() => Environment.SetEnvironmentVariable(this.TokenKey, Token);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            if (this.DuplicateChatPlugin) services.AddSingleton<IWorkstreamPlugin, ChatWorkstreamPlugin>();
            services.AddOptions<ChatPluginOptions>().PostConfigure<IForgeDbPaths>((options, paths) =>
            {
                options.TelegramSlice.Enabled = true; options.TelegramSlice.NewTestBotConfirmed = true;
                options.TelegramSlice.IsolatedProfileRoot = paths.Folder;
            });
            services.AddHttpClient(TelegramNotificationProvider.ClientName).ConfigurePrimaryHttpMessageHandler(() => this.Http);
            services.AddScoped(sp => new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>(
                sp.GetRequiredService<DbContextOptions<ControlDbContext>>()).AddInterceptors(this.Checkpoint).Options));
        });
    }

    public async Task SetupAsync()
    {
        await this.SeedAsync();
        await this.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "test-bot", this.Connection()));
        await this.RunAsync("admin", (s, a) => s.PutDestinationAsync(a, "test-bot", "test-channel", new(0, "123456", "-1004242", "Empty test channel", "supergroup")));
    }

    public ChatConnectionWrite Connection(long revision = 0, params string[] grants) => new(revision, "telegram", "123456", "Fresh test bot",
        true, 1, EmptySettings, new Dictionary<string, ChatSecretReference> { ["bot-token"] = new("environment", this.TokenKey) }, grants.Length == 0 ? ["p"] : grants);

    public async Task<T> SliceAsync<T>(string user, Func<ChatTelegramSliceService, ForgeActor, Task<T>> run)
    {
        using var scope = this.Services.CreateScope();
        var actor = await scope.ServiceProvider.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", user);
        return await run(scope.ServiceProvider.GetRequiredService<ChatTelegramSliceService>(), actor!);
    }
    public Task<ChatNotificationSetupView> EnableAsync(long revision = 0)
        => this.SliceAsync("owner-p", (s, a) => s.SaveAsync(a, "p", "shared", new(revision, true, "test-channel")));
    public Task<int> TickAsync() => this.SliceAsync("owner-p", (s, _) => s.RunOutboundOnceAsync(CancellationToken.None));
    public async Task<List<ChatNotificationRow>> OutboxAsync()
    {
        using var scope = this.Services.CreateScope();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatNotifications);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Environment.SetEnvironmentVariable(this.TokenKey, null);
    }
}

internal sealed class SliceCheckpointFailure : SaveChangesInterceptor
{
    public bool Armed { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (this.Armed && eventData.Context!.ChangeTracker.Entries<ChatNotificationRow>()
            .Any(e => e.Entity.Platform == ChatTelegramSliceService.Platform && e.Entity.Status == "sent" && e.State == EntityState.Modified))
        {
            this.Armed = false;
            throw new InvalidOperationException("Simulated local checkpoint failure after the provider accepted the send.");
        }
        return ValueTask.FromResult(result);
    }
}

internal sealed class FakeTelegramHttp : HttpMessageHandler
{
    public ConcurrentQueue<(string Method, JsonElement Body)> Calls { get; } = new();
    public bool RejectIdentity { get; set; }
    public bool RejectDestination { get; set; }
    public bool OmitMessageReceipt { get; set; }
    public bool ThrowOnSend { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.Segments.Last();
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
        this.Calls.Enqueue((method, body));
        object response = method switch
        {
            "getMe" when !this.RejectIdentity => new { ok = true, result = new { id = 123456, is_bot = true } },
            "getChat" when !this.RejectDestination => new { ok = true, result = new { id = -1004242L, type = "supergroup" } },
            "sendMessage" when this.OmitMessageReceipt => new { ok = true, result = new { } },
            "sendMessage" when this.ThrowOnSend => throw new HttpRequestException("Bearer " + TelegramSliceTestFactory.Token + " E:\\private\\log"),
            "sendMessage" => new { ok = true, result = new { message_id = this.Calls.Count + 100 } },
            _ => new { ok = false, description = "Bearer " + TelegramSliceTestFactory.Token },
        };
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
    }
}