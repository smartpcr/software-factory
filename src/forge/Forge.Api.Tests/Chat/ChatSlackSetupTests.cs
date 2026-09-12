// -----------------------------------------------------------------------
// <copyright file="ChatSlackSetupTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatSlackSetupTests
{
    private const string Root = "/api/chat/authorization";

    [Fact]
    public async Task Slack_internal_error_is_reported_as_unavailable_without_automatic_retry()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); f.Http.Failure = "setup-internal";
        var response = await client.PostAsJsonAsync(Root + "/slack/setup", new ChatAuthorizationStart("slack"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("authorization-provider-unavailable", await response.Content.ReadAsStringAsync());
        Assert.Equal(["apps.hosted.generateAuthTicket"], f.Http.Calls);
        f.Http.Failure = "";
        Assert.Equal("waiting", (await Begin(client)).Status); // A user-initiated retry may request a fresh ticket.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unregistered_host_provisions_private_app_and_saves_both_credentials_without_messages(bool includePrivate)
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin");
        var availability = (await client.GetFromJsonAsync<ChatAuthorizationAvailability[]>(Root))!.Single(a => a.ProviderId == "slack");
        Assert.False(availability.Available); Assert.True(availability.CanConfigure);
        var waiting = await Begin(client, includePrivate);
        Assert.Equal("slack-cli", waiting.SetupMode); Assert.StartsWith("/slackauthticket ", waiting.ApprovalRequest);
        Assert.Equal(["apps.hosted.generateAuthTicket"], f.Http.Calls);
        var approved = await Approve(client, waiting.Id);
        Assert.Equal("approved", approved.Status); Assert.Null(approved.ApprovalRequest);
        Assert.Equal("T123", approved.InstallationId); Assert.Equal("Fixture workspace", approved.AccountName);
        var manifest = Assert.Single(f.Http.SetupManifests);
        var settings = manifest.GetProperty("settings");
        Assert.True(settings.GetProperty("socket_mode_enabled").GetBoolean());
        Assert.False(settings.GetProperty("token_rotation_enabled").GetBoolean());
        Assert.False(settings.GetProperty("org_deploy_enabled").GetBoolean());
        var expectedScopes = new[] { "chat:write", "channels:read", "channels:history" }
            .Concat(includePrivate ? ["groups:read", "groups:history"] : Array.Empty<string>());
        Assert.Equal(expectedScopes, manifest.GetProperty("oauth_config").GetProperty("scopes").GetProperty("bot").EnumerateArray().Select(scope => scope.GetString()));
        Assert.Equal(includePrivate ? new[] { "message.channels", "message.groups" } : ["message.channels"],
            settings.GetProperty("event_subscriptions").GetProperty("bot_events").EnumerateArray().Select(value => value.GetString()));
        Assert.True(settings.GetProperty("interactivity").GetProperty("is_enabled").GetBoolean());
        Assert.DoesNotContain("redirect", manifest.ToString());
        Assert.Equal(approved, await Approve(client, waiting.Id)); // No duplicate app on response retry.
        Assert.Single(f.Http.Calls, c => c == "apps.manifest.create");
        var savedResponse = await client.PostAsJsonAsync(Root + "/" + waiting.Id + "/save", new ChatAuthorizationSave("Automatic Slack", ["p"]));
        savedResponse.EnsureSuccessStatusCode();
        var saved = (await savedResponse.Content.ReadFromJsonAsync<ChatAuthorizationView>())!;
        var details = await f.RunAsync("admin", (service, actor) => service.GetConnectionAsync(actor, saved.ConnectionId!));
        Assert.Equal("A123", details.Settings.GetProperty("appId").GetString());
        Assert.Equal(["p"], details.ProductIds);
        Assert.All(details.SecretReferences.Values, reference => Assert.Equal("dpapi", reference.Store));
        using var scope = f.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ChatSecretResolver>();
        Assert.Equal(AuthorizationHttp.BotToken, await secrets.ReadAsync(details.SecretReferences["bot-token"]));
        Assert.Equal("xapp-fixture-created-token", await secrets.ReadAsync(details.SecretReferences["app-token"]));
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.Single(await db.ChatConnections.ToListAsync());
        var audit = await db.Audit.Where(a => a.Path.EndsWith("/slack/approve")).ToListAsync();
        Assert.Equal(2, audit.Count); // Initial approval and the idempotent retry are both audited.
        Assert.All(audit, entry => Assert.Equal("[redacted]", entry.RequestBodySnippet));
        Assert.DoesNotContain(f.Http.Calls, call => call.Contains("postMessage", StringComparison.Ordinal) || call.Contains("approvals.requests", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(approved) + await savedResponse.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "xox", "xapp-", "refresh-fixture", "unused-client-fixture", "fixture-private", "code-fixture" }) Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public async Task Setup_requires_current_host_role_origin_csrf_and_owns_the_ticket()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var anon = f.AnonymousClient(); using var owner = await f.PairedClientAsync("owner-p");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(Root + "/slack/setup", new ChatAuthorizationStart("slack"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(Root + "/slack/setup", new ChatAuthorizationStart("slack"))).StatusCode);
        using var admin = await f.PairedClientAsync("admin");
        var waiting = await Begin(admin);
        await f.EnrollAsync("other-admin", admin: true); using var other = await f.PairedClientAsync("other-admin");
        var path = Root + "/" + waiting.Id + "/slack/approve";
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(path, new ChatSlackSetupApproval("code-fixture"))).StatusCode);
        admin.DefaultRequestHeaders.Remove("X-Forge-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(path, new ChatSlackSetupApproval("code-fixture"))).StatusCode);
        Assert.Equal(["apps.hosted.generateAuthTicket"], f.Http.Calls);
    }

    [Theory]
    [InlineData("setup-enterprise")]
    [InlineData("setup-team")]
    [InlineData("setup-app-token")]
    [InlineData("setup-admin")]
    [InlineData("setup-uncertain")]
    public async Task Failed_setup_never_saves_or_replays_and_preserves_known_created_app(string failure)
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin");
        var waiting = await Begin(client); f.Http.Failure = failure;
        var failed = await Approve(client, waiting.Id);
        Assert.Equal("failed", failed.Status); Assert.Null(failed.ApprovalRequest);
        if (failure is "setup-team" or "setup-app-token" or "setup-admin") Assert.Equal("A123", failed.AppId);
        if (failure == "setup-admin") Assert.Equal("authorization-slack-admin-approval-required", failed.Reason);
        if (failure == "setup-enterprise") Assert.DoesNotContain("apps.manifest.create", f.Http.Calls);
        var calls = f.Http.Calls.Count;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Root + "/" + waiting.Id + "/slack/approve", new ChatSlackSetupApproval("code-fixture"))).StatusCode);
        Assert.Equal(calls, f.Http.Calls.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Root + "/" + waiting.Id + "/save", new ChatAuthorizationSave("Incomplete", ["p"]))).StatusCode);
        using var scope = f.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatConnections.ToListAsync());
    }

    [Fact]
    public async Task Cancel_and_expiry_discard_the_approval_before_provider_access()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin");
        var cancelled = await Begin(client); (await client.DeleteAsync(Root + "/" + cancelled.Id)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Root + "/" + cancelled.Id + "/slack/approve", new ChatSlackSetupApproval("code-fixture"))).StatusCode);
        var expired = await Begin(client); f.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Root + "/" + expired.Id + "/slack/approve", new ChatSlackSetupApproval("code-fixture"))).StatusCode);
        Assert.All(f.Http.Calls, method => Assert.Equal("apps.hosted.generateAuthTicket", method));
    }

    [Fact]
    public async Task Administrator_revocation_during_creation_stops_installation()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); var waiting = await Begin(client);
        f.Http.BeforeReply = async method => { if (method == "apps.manifest.create") await f.EnrollAsync("admin", admin: false); };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Root + "/" + waiting.Id + "/slack/approve", new ChatSlackSetupApproval("code-fixture"))).StatusCode);
        Assert.DoesNotContain("apps.developerInstall", f.Http.Calls);
    }

    [Fact]
    public async Task Existing_host_registration_is_never_overridden()
    {
        await using var f = new AuthorizationFactory(); await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Root + "/slack/setup", new ChatAuthorizationStart("slack"))).StatusCode);
        Assert.Empty(f.Http.Calls);
    }

    [Fact]
    public async Task Invalid_code_can_be_corrected_and_existing_workspace_is_rejected_before_app_creation()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); var waiting = await Begin(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Root + "/" + waiting.Id + "/slack/approve", new ChatSlackSetupApproval("invalid code"))).StatusCode);
        Assert.Equal(["apps.hosted.generateAuthTicket"], f.Http.Calls);
        Assert.Equal("approved", (await Approve(client, waiting.Id)).Status);
        (await client.PostAsJsonAsync(Root + "/" + waiting.Id + "/save", new ChatAuthorizationSave("Slack", ["p"]))).EnsureSuccessStatusCode();
        var duplicate = await Begin(client);
        var failed = await Approve(client, duplicate.Id);
        Assert.Equal("failed", failed.Status); Assert.Equal("configuration-conflict", failed.Reason);
        Assert.Single(f.Http.Calls, method => method == "apps.manifest.create");
    }

    [Fact]
    public async Task Expiry_after_creation_keeps_app_reference_and_prevents_installation()
    {
        await using var f = new AuthorizationFactory { Configured = false }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); var waiting = await Begin(client);
        f.Http.BeforeReply = method => { if (method == "apps.manifest.create") f.Clock.Advance(TimeSpan.FromMinutes(11)); return Task.CompletedTask; };
        var failed = await Approve(client, waiting.Id);
        Assert.Equal("failed", failed.Status); Assert.Equal("authorization-expired", failed.Reason);
        Assert.Equal("A123", failed.AppId); Assert.Null(failed.ApprovalRequest);
        Assert.DoesNotContain("apps.developerInstall", f.Http.Calls);
    }

    [Fact]
    public async Task Failed_second_secret_write_cleans_up_the_first_without_a_connection()
    {
        var store = new SetupStore();
        await using var f = new AuthorizationFactory { Configured = false, ProtectedStore = store }; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); var waiting = await Begin(client); await Approve(client, waiting.Id);
        var response = await client.PostAsJsonAsync(Root + "/" + waiting.Id + "/save", new ChatAuthorizationSave("Slack", ["p"]));
        Assert.False(response.IsSuccessStatusCode); Assert.Empty(store.Values);
        using var scope = f.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatConnections.ToListAsync());
    }

    private static async Task<ChatAuthorizationView> Begin(HttpClient client, bool includePrivate = false)
    {
        var response = await client.PostAsJsonAsync(Root + "/slack/setup", new ChatAuthorizationStart("slack", includePrivate));
        response.EnsureSuccessStatusCode(); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadFromJsonAsync<ChatAuthorizationView>())!;
    }
    private static async Task<ChatAuthorizationView> Approve(HttpClient client, string id)
    {
        var response = await client.PostAsJsonAsync(Root + "/" + id + "/slack/approve", new ChatSlackSetupApproval("code-fixture"));
        response.EnsureSuccessStatusCode(); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadFromJsonAsync<ChatAuthorizationView>())!;
    }
    private sealed class SetupStore : IChatSecretStore
    {
        public string Id => "dpapi";
        public ChatSecretStoreStatus Status => new(true, false);
        public Dictionary<string, string> Values { get; } = [];
        private int writes;
        public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default) => ValueTask.FromResult(this.Values.GetValueOrDefault(reference.Key));
        public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default)
        {
            if (++this.writes == 2) throw new IOException("Fixture write failure");
            this.Values[key] = value; return ValueTask.FromResult(new ChatSecretReference(this.Id, key));
        }
        public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) { this.Values.Remove(reference.Key); return ValueTask.CompletedTask; }
    }
}