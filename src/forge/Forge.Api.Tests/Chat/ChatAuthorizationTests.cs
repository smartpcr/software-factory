// -----------------------------------------------------------------------
// <copyright file="ChatAuthorizationTests.cs" company="Microsoft Corp.">
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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class ChatAuthorizationTests
{
    private const string Path = "/api/chat/authorization";

    [Fact]
    public async Task Slack_consent_saves_encrypted_credentials_and_exact_grants_once_without_sending()
    {
        await using var f = new AuthorizationFactory(); await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin");
        var flow = await Begin(client, "slack", true);
        var query = QueryHelpers.ParseQuery(new Uri(flow.AuthorizationUrl!).Query);
        Assert.Equal("https://slack.com/oauth/v2/authorize", flow.AuthorizationUrl!.Split('?')[0]);
        Assert.Contains("groups:history", query["scope"].ToString());
        Assert.Equal("https://localhost:5182/chat/authorize/slack", query["redirect_uri"]);
        using var anonymous = f.AnonymousClient(); anonymous.DefaultRequestHeaders.Remove("Origin");
        var callback = await anonymous.GetAsync("/chat/authorize/slack?state=" + query["state"] + "&code=fixture-code");
        callback.EnsureSuccessStatusCode();
        Assert.Equal("no-store", callback.Headers.CacheControl!.ToString());
        Assert.DoesNotContain("fixture-code", await callback.Content.ReadAsStringAsync());
        Assert.Empty(f.Http.Calls); // Callback does not exchange, write a connection, or need a browser cookie.
        var approved = await Poll(client, flow.Id);
        Assert.Equal("approved", approved.Status); Assert.Equal("Fixture workspace", approved.AccountName);
        Assert.DoesNotContain("xoxb-", JsonSerializer.Serialize(approved));
        var response = await client.PostAsJsonAsync(Path + "/" + flow.Id + "/save", new ChatAuthorizationSave("Team chat", ["p"]));
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<ChatAuthorizationView>())!;
        Assert.Equal("saved", saved.Status);
        var details = await f.RunAsync("admin", (s, a) => s.GetConnectionAsync(a, saved.ConnectionId!));
        Assert.True(details.Enabled); Assert.Equal("T123", details.InstallationId); Assert.Equal(["p"], details.ProductIds);
        Assert.Equal(new ChatSecretReference("auth-fixture", "app"), details.SecretReferences["app-token"]);
        Assert.Equal("dpapi", details.SecretReferences["bot-token"].Store);
        using var scope = f.Services.CreateScope();
        Assert.Equal(AuthorizationHttp.BotToken, await scope.ServiceProvider.GetRequiredService<ChatSecretResolver>().ReadAsync(details.SecretReferences["bot-token"]));
        Assert.DoesNotContain(AuthorizationHttp.BotToken, await response.Content.ReadAsStringAsync());
        var repeated = await client.PostAsJsonAsync(Path + "/" + flow.Id + "/save", new ChatAuthorizationSave("Changed retry", ["q"]));
        repeated.EnsureSuccessStatusCode();
        Assert.Equal(saved.ConnectionId, (await repeated.Content.ReadFromJsonAsync<ChatAuthorizationView>())!.ConnectionId);
        Assert.Single(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatConnections.ToListAsync());
        Assert.Single(f.Http.Calls); Assert.Equal("oauth.v2.access", f.Http.Calls[0]);
        var replay = await anonymous.GetAsync("/chat/authorize/slack?state=" + query["state"] + "&code=other-code");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Authorization_requires_host_role_csrf_and_same_current_operator()
    {
        await using var f = new AuthorizationFactory(); await f.SeedAsync();
        await f.EnrollAsync("second-admin", admin: true);
        using var anonymous = f.AnonymousClient(); using var owner = await f.PairedClientAsync("owner-p");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(Path, new ChatAuthorizationStart("slack"))).StatusCode);
        using var admin = await f.PairedClientAsync("admin");
        var flow = await Begin(admin, "slack");
        using var other = await f.PairedClientAsync("second-admin");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(Path + "/" + flow.Id + "/poll", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(Path + "/" + flow.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(Path + "/" + flow.Id + "/save", new ChatAuthorizationSave("stolen", ["q"]))).StatusCode);
        admin.DefaultRequestHeaders.Remove("X-Forge-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(Path, new ChatAuthorizationStart("slack"))).StatusCode);
        await f.EnrollAsync("admin", admin: false);
        Assert.False((await anonymous.GetAsync("/chat/authorize/slack?state=wrong&code=code")).IsSuccessStatusCode);
        using var revoked = await f.PairedClientAsync("admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await revoked.PostAsync(Path + "/" + flow.Id + "/poll", null)).StatusCode);
        Assert.Empty(f.Http.Calls);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("replay")]
    public async Task Invalid_consent_cannot_create_connections(string scenario)
    {
        await using var f = new AuthorizationFactory(); await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); var flow = await Begin(client, "slack");
        var nonce = QueryHelpers.ParseQuery(new Uri(flow.AuthorizationUrl!).Query)["state"].ToString();
        if (scenario == "cancelled") (await client.DeleteAsync(Path + "/" + flow.Id)).EnsureSuccessStatusCode();
        if (scenario == "expired") f.Clock.Advance(TimeSpan.FromMinutes(11));
        var callback = await client.GetAsync("/chat/authorize/slack?state=" + nonce + (scenario == "denied" ? "&error=access_denied" : "&code=code"));
        if (scenario is "cancelled" or "expired") Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        if (scenario == "replay") Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/chat/authorize/slack?state=" + nonce + "&code=different")).StatusCode);
        // Even a valid callback cannot save before the current operator exchanges and reviews it.
        Assert.False((await client.PostAsJsonAsync(Path + "/" + flow.Id + "/save", new ChatAuthorizationSave("Must not save", ["p"]))).IsSuccessStatusCode);
        using var scope = f.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatConnections.ToListAsync());
        Assert.Empty(f.Http.Calls);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("app")]
    [InlineData("rotation")]
    [InlineData("enterprise")]
    [InlineData("malformed")]
    [InlineData("exception")]
    [InlineData("oversize")]
    public async Task Unsupported_or_failed_exchange_is_redacted_and_never_retried(string scenario)
    {
        await using var f = new AuthorizationFactory(); f.Http.Failure = scenario; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); var flow = await Begin(client, "slack");
        var nonce = QueryHelpers.ParseQuery(new Uri(flow.AuthorizationUrl!).Query)["state"].ToString();
        (await client.GetAsync("/chat/authorize/slack?state=" + nonce + "&code=code")).EnsureSuccessStatusCode();
        var result = await Poll(client, flow.Id);
        Assert.Equal("failed", result.Status); Assert.Null(result.AuthorizationUrl);
        Assert.DoesNotContain(AuthorizationHttp.BotToken, JsonSerializer.Serialize(result));
        Assert.Equal("failed", (await Poll(client, flow.Id)).Status); Assert.Single(f.Http.Calls);
    }

    [Fact]
    public async Task Missing_registration_and_http_callback_report_actionable_status_without_opening_provider()
    {
        await using var f = new AuthorizationFactory(); f.Configured = false; await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin");
        var missing = (await client.GetFromJsonAsync<List<ChatAuthorizationAvailability>>(Path))!;
        Assert.All(missing, p => Assert.False(p.Available));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, new ChatAuthorizationStart("telegram"))).StatusCode);
        Assert.Empty(f.Http.Calls);
        using var configured = new AuthorizationFactory(); configured.RedirectUri = "http://localhost:5180/chat/authorize/slack"; await configured.SeedAsync();
        using var httpClient = await configured.PairedClientAsync("admin");
        var invalid = (await httpClient.GetFromJsonAsync<List<ChatAuthorizationAvailability>>(Path))!.Single(p => p.ProviderId == "slack");
        Assert.Equal("authorization-https-required", invalid.Reason); Assert.False(invalid.Available);
    }

    [Fact]
    public async Task Telegram_native_consent_matches_only_the_requested_bot_and_saves_without_token_entry()
    {
        await using var f = new AuthorizationFactory(); await f.SeedAsync();
        using var client = await f.PairedClientAsync("admin"); var flow = await Begin(client, "telegram");
        var uri = new Uri(flow.AuthorizationUrl!);
        Assert.Equal("t.me", uri.Host); Assert.StartsWith("/newbot/FixtureManagerBot/", uri.AbsolutePath);
        var username = uri.Segments.Last(); Assert.Equal(32, username.Length); Assert.EndsWith("bot", username);
        f.Http.BotUsername = "unrelated_bot";
        Assert.Equal("waiting", (await Poll(client, flow.Id)).Status);
        Assert.DoesNotContain("getManagedBotToken", f.Http.Calls);
        f.Http.BotUsername = username; f.Http.UpdateId++; f.Clock.Advance(TimeSpan.FromSeconds(4));
        var approved = await Poll(client, flow.Id);
        Assert.Equal("approved", approved.Status); Assert.Equal("@" + username, approved.AccountName); Assert.Equal("777", approved.InstallationId);
        Assert.DoesNotContain(AuthorizationHttp.TelegramToken, JsonSerializer.Serialize(approved));
        var save = await client.PostAsJsonAsync(Path + "/" + flow.Id + "/save", new ChatAuthorizationSave("Operations", ["p"])); save.EnsureSuccessStatusCode();
        var saved = (await save.Content.ReadFromJsonAsync<ChatAuthorizationView>())!;
        var details = await f.RunAsync("admin", (s, a) => s.GetConnectionAsync(a, saved.ConnectionId!));
        Assert.Equal("telegram", details.ProviderId); Assert.Equal("777", details.InstallationId);
        Assert.DoesNotContain(f.Http.Calls, m => m is "sendMessage" or "deleteWebhook");
    }

    [Theory]
    [InlineData("webhook")]
    [InlineData("not-manager")]
    [InlineData("delivery-bot")]
    public async Task Telegram_manager_must_be_dedicated_without_webhook(string scenario)
    {
        await using var f = new AuthorizationFactory(); f.Http.Failure = scenario; await f.SeedAsync();
        if (scenario == "delivery-bot") await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "already-delivering", new(0, "telegram", "888", "Existing", false, 1, ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference>(), ["p"])));
        using var client = await f.PairedClientAsync("admin");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, new ChatAuthorizationStart("telegram"))).StatusCode);
        Assert.DoesNotContain("getUpdates", f.Http.Calls);
    }

    private static async Task<ChatAuthorizationView> Begin(HttpClient client, string provider, bool includePrivate = false)
    {
        var response = await client.PostAsJsonAsync(Path, new ChatAuthorizationStart(provider, includePrivate)); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatAuthorizationView>())!;
    }
    private static async Task<ChatAuthorizationView> Poll(HttpClient client, string id)
    {
        var response = await client.PostAsync(Path + "/" + id + "/poll", null); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatAuthorizationView>())!;
    }
}

internal sealed class AuthorizationFactory : ChatPluginTestFactory
{
    public AuthorizationHttp Http { get; } = new();
    public RuntimeClock Clock { get; } = new();
    public bool Configured { get; set; } = true;
    public IChatSecretStore? ProtectedStore { get; set; }
    public string RedirectUri { get; set; } = "https://localhost:5182/chat/authorize/slack";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(this.Clock);
            if (this.ProtectedStore is { } store)
            {
                services.RemoveAll<IChatSecretStore>();
                services.AddSingleton(store);
            }
            services.AddSingleton<IChatSecretStore, AuthorizationSecrets>();
            services.Configure<ChatPluginOptions>(options =>
            {
                if (!this.Configured) return;
                options.Authorization.Slack = new() { ClientId = "123.456", AppId = "A123", RedirectUri = this.RedirectUri,
                    ClientSecret = new("auth-fixture", "client"), AppToken = new("auth-fixture", "app") };
                options.Authorization.Telegram.ManagerToken = new("auth-fixture", "manager");
            });
            services.AddHttpClient(ChatAuthorizationProvider.ClientName).ConfigurePrimaryHttpMessageHandler(() => this.Http);
        });
    }
}

internal sealed class AuthorizationSecrets : IChatSecretStore
{
    public string Id => "auth-fixture";
    public ChatSecretStoreStatus Status => new(true, true);
    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default)
        => ValueTask.FromResult<string?>(reference.Key switch { "manager" => "888:MANAGER_FIXTURE_ONLY", "client" => "client-fixture-secret", "app" => "xapp-fixture-token", _ => null });
    public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class AuthorizationHttp : HttpMessageHandler
{
    public const string BotToken = "xoxb-fixture-private-credential";
    public const string TelegramToken = "777:TELEGRAM_FIXTURE_ONLY";
    public List<string> Calls { get; } = [];
    public string? Failure { get; set; }
    public string BotUsername { get; set; } = "fixture_bot";
    public int UpdateId { get; set; } = 1;
    public Func<string, Task>? BeforeReply { get; set; }
    public List<JsonElement> SetupManifests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.Segments.Last(); this.Calls.Add(method);
        if (this.BeforeReply is not null) await this.BeforeReply(method);
        if (this.Failure == "invalid-token") return new(HttpStatusCode.Unauthorized) { Content = new StringContent(BotToken) };
        if (this.Failure == "exception") throw new HttpRequestException(BotToken);
        if (this.Failure is "malformed" or "oversize") return new(HttpStatusCode.OK) { Content = new StringContent(this.Failure == "oversize" ? new string('a', 270000) : BotToken) };
        object result;
        if (method == "oauth.v2.access")
        {
            Assert.Equal("slack.com", request.RequestUri.Host); Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Contains("code=", await request.Content!.ReadAsStringAsync(ct));
            var response = new Dictionary<string, object> { ["ok"] = true, ["app_id"] = this.Failure == "app" ? "AOTHER" : "A123", ["token_type"] = "bot", ["access_token"] = BotToken,
                ["scope"] = this.Failure == "scope" ? "chat:write" : "chat:write,channels:read,channels:history,groups:read,groups:history", ["team"] = new { id = "T123", name = "Fixture workspace" },
                ["is_enterprise_install"] = this.Failure == "enterprise" };
            if (this.Failure == "rotation") response["refresh_token"] = "fixture-refresh";
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
        if (request.RequestUri.Host == "slack.com")
        {
            Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "slack-cli" && value.Product.Version == "v4.7.0");
            Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "Forge");
            if (this.Failure == "setup-internal") return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = false, error = "internal_error" }) };
            var token = request.Headers.Authorization?.Parameter;
            var payload = await request.Content!.ReadAsStringAsync(ct);
            if (method.StartsWith("apps.hosted.", StringComparison.Ordinal))
            {
                Assert.Equal("application/x-www-form-urlencoded", request.Content.Headers.ContentType!.MediaType);
                Assert.Contains("slack_cli_version=v4.7", payload);
            }
            if (method == "apps.manifest.create")
            {
                this.SetupManifests.Add(JsonDocument.Parse(payload).RootElement.GetProperty("manifest").Clone());
                if (this.Failure == "setup-uncertain") throw new HttpRequestException("xoxp-do-not-log");
            }
            if (method == "apps.developerInstall" && this.Failure == "setup-admin")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = false, error = "app_approval_request_eligible" }) };
            object setup = method switch
            {
                "apps.hosted.generateAuthTicket" => new { ok = true, ticket = new string('F', 48) },
                "apps.hosted.exchangeAuthTicket" => new { ok = true, is_ready = true, token = "xoxe.xoxp-tooling-fixture", refresh_token = "refresh-fixture-private",
                    team_id = this.Failure == "setup-enterprise" ? "E123" : "T123", team_name = "Fixture workspace", user_id = "U123", is_enterprise_install = this.Failure == "setup-enterprise" },
                "auth.test" when token == "xoxe.xoxp-tooling-fixture" => new { ok = true, team_id = "T123", user_id = "U123" },
                "auth.test" => new { ok = true, team_id = this.Failure == "setup-team" ? "T999" : "T123", bot_id = "B123", user_id = "U456", app_id = "A123" },
                "apps.manifest.create" => new { ok = true, app_id = "A123", credentials = new { client_secret = "unused-client-fixture" } },
                "apps.developerInstall" => new { ok = true, app_id = "A123", api_access_tokens = new { bot = BotToken, app_level = this.Failure == "setup-app-token" ? "" : "xapp-fixture-created-token" } },
                "apps.connections.open" => new { ok = true, url = "wss://wss-primary.slack.com/link?ticket=fixture-private" },
                _ => throw new InvalidOperationException("Unexpected Slack setup operation"),
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(setup) };
        }
        Assert.Equal("api.telegram.org", request.RequestUri.Host);
        result = method switch
        {
            "getMe" when request.RequestUri.AbsolutePath.Contains("888:", StringComparison.Ordinal) => new { id = 888L, is_bot = true, username = "FixtureManagerBot", can_manage_bots = this.Failure != "not-manager" },
            "getMe" => new { id = 777L, is_bot = true, username = this.BotUsername },
            "getWebhookInfo" => new { url = this.Failure == "webhook" ? "https://example.invalid/webhook" : "" },
            "getUpdates" => new[] { new { update_id = this.UpdateId, managed_bot = new { user = new { id = 321L }, bot = new { id = 777L, is_bot = true, username = this.BotUsername } } } },
            "getManagedBotToken" => TelegramToken,
            _ => throw new InvalidOperationException("Unexpected provider operation"),
        };
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true, result }) };
    }
}