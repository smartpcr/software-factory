// -----------------------------------------------------------------------
// <copyright file="ChatTelegramManagerSetupTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatTelegramManagerSetupTests
{
    private const string Path = "/api/chat/authorization/telegram/manager";
    private const string Token = "888:MANAGER_FIXTURE_ONLY";

    [Fact]
    public async Task First_manager_is_verified_encrypted_and_usable_immediately_and_from_fresh_runtime_state()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin");
        var before = await Availability(client);
        Assert.False(before.Available); Assert.True(before.CanConfigure);
        var response = await client.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token));
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("FixtureManagerBot", (await response.Content.ReadFromJsonAsync<ChatTelegramManagerView>())!.Username);
        Assert.DoesNotContain(Token, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Token, new ChatTelegramManagerSetup(Token).ToString());
        var ready = await Availability(client); Assert.True(ready.Available); Assert.False(ready.CanConfigure);

        using var scope = f.Services.CreateScope();
        var folder = System.IO.Path.Combine(scope.ServiceProvider.GetRequiredService<IForgeDbPaths>().Folder, "chat-secrets");
        var file = Assert.Single(Directory.GetFiles(folder, "*.bin"));
        Assert.DoesNotContain(Token, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        var reopenedStore = new ChatDpapiSecretStore(folder);
        Assert.Equal(Token, await reopenedStore.ReadAsync(ChatAuthorizationService.LocalTelegramManager));
        using var freshState = new ChatAuthorizationState(f.Clock);
        var fresh = ActivatorUtilities.CreateInstance<ChatAuthorizationService>(scope.ServiceProvider, freshState, new ChatSecretResolver([reopenedStore]));
        var actor = await scope.ServiceProvider.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "admin");
        Assert.True((await fresh.AvailabilityAsync(actor, default)).Single(p => p.ProviderId == "telegram").Available);
        var flow = await fresh.BeginAsync(actor, new("telegram"), default);
        Assert.StartsWith("https://t.me/newbot/FixtureManagerBot/", flow.AuthorizationUrl);
        Assert.DoesNotContain(f.Http.Calls, m => m is "getUpdates" or "getManagedBotToken" or "sendMessage" or "deleteWebhook");
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.Empty(await db.ChatConnections.ToListAsync());
        var audit = Assert.Single(await db.Audit.Where(a => a.Path == Path).ToListAsync());
        Assert.Equal("[redacted]", audit.RequestBodySnippet); Assert.Equal(200, audit.Status);
    }

    [Fact]
    public async Task Registration_requires_current_host_admin_origin_and_csrf_before_provider_access()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var anonymous = f.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token))).StatusCode);
        using var owner = await f.PairedClientAsync("owner-p");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token))).StatusCode);
        using var admin = await f.PairedClientAsync("admin");
        admin.DefaultRequestHeaders.Remove("X-Forge-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token))).StatusCode);
        using var untrusted = await f.PairedClientAsync("admin");
        untrusted.DefaultRequestHeaders.Remove("Origin"); untrusted.DefaultRequestHeaders.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await untrusted.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token))).StatusCode);
        Assert.Empty(f.Http.Calls);
        await AssertNotStored(f);
    }

    [Fact]
    public async Task Revocation_during_provider_verification_prevents_storage()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var admin = await f.PairedClientAsync("admin");
        f.Http.BeforeReply = async method => { if (method == "getMe") await f.EnrollAsync("admin", admin: false); };
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token))).StatusCode);
        await AssertNotStored(f);
    }

    [Theory]
    [InlineData("not-manager", "authorization-manager-required")]
    [InlineData("invalid-token", "authorization-telegram-token-rejected")]
    [InlineData("webhook", "authorization-manager-webhook")]
    [InlineData("delivery-bot", "authorization-manager-dedicated")]
    [InlineData("exception", "authorization-provider-unavailable")]
    [InlineData("malformed", "authorization-provider-unavailable")]
    public async Task Rejected_manager_is_not_stored_and_diagnostics_do_not_contain_credentials(string failure, string reason)
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync(); f.Http.Failure = failure;
        if (failure == "delivery-bot") await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "existing-bot", new(0, "telegram", "888", "Existing", false, 1,
            ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference>(), ["p"])));
        using var admin = await f.PairedClientAsync("admin");
        var response = await admin.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token));
        Assert.False(response.IsSuccessStatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains(reason, json); Assert.DoesNotContain(Token, json); Assert.DoesNotContain(AuthorizationHttp.BotToken, json);
        await AssertNotStored(f);
        using var scope = f.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Audit.Where(a => a.Path == Path).ToListAsync();
        Assert.Equal("[redacted]", Assert.Single(audits).RequestBodySnippet);
        Assert.DoesNotContain(f.Http.Calls, m => m is "getUpdates" or "sendMessage" or "deleteWebhook");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("888:")]
    [InlineData("https://example.invalid/token")]
    [InlineData("888:secret\nother")]
    public async Task Invalid_token_is_rejected_before_external_calls(string? token)
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var admin = await f.PairedClientAsync("admin");
        var response = await admin.PostAsJsonAsync(Path, new { token });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(f.Http.Calls); await AssertNotStored(f);
    }

    [Fact]
    public async Task Repeated_registration_is_idempotent_and_does_not_overwrite_another_manager_or_external_reference()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var admin = await f.PairedClientAsync("admin");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => admin.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token))));
        foreach (var response in results) response.EnsureSuccessStatusCode();
        var replacement = await admin.PostAsJsonAsync(Path, new ChatTelegramManagerSetup("999:OTHER_FIXTURE_ONLY"));
        Assert.Equal(HttpStatusCode.Conflict, replacement.StatusCode);
        Assert.Contains("authorization-manager-already-configured", await replacement.Content.ReadAsStringAsync());
        using var scope = f.Services.CreateScope();
        Assert.Equal(Token, await scope.ServiceProvider.GetRequiredService<ChatSecretResolver>().ReadAsync(ChatAuthorizationService.LocalTelegramManager));
        using var external = new AuthorizationFactory(); await external.SeedAsync();
        using var externalAdmin = await external.PairedClientAsync("admin");
        var blocked = await externalAdmin.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("authorization-manager-externally-configured", await blocked.Content.ReadAsStringAsync());
        Assert.Empty(external.Http.Calls); await AssertNotStored(external);
    }

    [Fact]
    public async Task Existing_manual_credential_writes_also_redact_audit_bodies()
    {
        await using var f = new AuthorizationFactory(); await f.SeedAsync();
        await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "manual", new(0, "telegram", "777", "Manual", false, 1,
            ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference>(), ["p"])));
        using var admin = await f.PairedClientAsync("admin");
        const string credentialPath = "/api/chat/connections/manual/credentials/bot-token";
        (await admin.PutAsJsonAsync(credentialPath, new ChatCredentialWrite(1, AuthorizationHttp.TelegramToken))).EnsureSuccessStatusCode();
        using var scope = f.Services.CreateScope();
        var audit = Assert.Single(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Audit.Where(a => a.Path == credentialPath).ToListAsync());
        Assert.Equal("[redacted]", audit.RequestBodySnippet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_failed_protected_storage_does_not_report_ready(bool available)
    {
        await using var f = new AuthorizationFactory { Configured = false, ProtectedStore = new FailingStore(available) }; await f.SeedAsync();
        using var admin = await f.PairedClientAsync("admin");
        var response = await admin.PostAsJsonAsync(Path, new ChatTelegramManagerSetup(Token));
        Assert.Equal(available ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(Token, await response.Content.ReadAsStringAsync());
        var status = await Availability(admin); Assert.False(status.Available); Assert.Equal(available, status.CanConfigure);
        if (!available) Assert.Empty(f.Http.Calls);
        await AssertNotStored(f);
    }

    private sealed class FailingStore(bool available) : IChatSecretStore
    {
        public string Id => "dpapi";
        public ChatSecretStoreStatus Status => new(available, false);
        public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
        public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default) => throw new IOException(Token);
        public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private static async Task<ChatAuthorizationAvailability> Availability(HttpClient client)
        => (await client.GetFromJsonAsync<List<ChatAuthorizationAvailability>>("/api/chat/authorization"))!.Single(p => p.ProviderId == "telegram");

    private static async Task AssertNotStored(AuthorizationFactory f)
    {
        using var scope = f.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<ChatSecretResolver>().ReadAsync(ChatAuthorizationService.LocalTelegramManager));
    }
}