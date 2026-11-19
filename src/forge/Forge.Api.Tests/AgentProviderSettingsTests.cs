// -----------------------------------------------------------------------
// <copyright file="AgentProviderSettingsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Settings → Agent providers: choosing which GitHub account Copilot runs as.</summary>
public sealed class AgentProviderSettingsTests
{
    [Fact]
    public void Gh_Account_List_Is_Parsed_Without_Tokens_And_Unsafe_Logins_Are_Dropped()
    {
        // Shape of `gh auth status --hostname github.com --json hosts` (gh 2.105).
        const string json = """
            {"hosts":{"github.com":[
              {"state":"success","active":true,"host":"github.com","login":"xiaodoli","tokenSource":"keyring","scopes":"gist, read:org, repo, workflow","gitProtocol":"https"},
              {"state":"success","active":false,"host":"github.com","login":"xiaodoli_microsoft","tokenSource":"keyring","scopes":"gist, read:org, repo","gitProtocol":"https"},
              {"state":"error","active":false,"host":"github.com","login":"bad --user x","tokenSource":"keyring"}
            ]}}
            """;

        var accounts = GitHubAuthService.ParseAccounts(json);

        Assert.Equal(new[] { "xiaodoli", "xiaodoli_microsoft" }, accounts.Select(a => a.Login));
        Assert.True(accounts[0].Active);
        Assert.False(accounts[1].Active);
        Assert.Equal("success", accounts[1].State);
        Assert.Empty(GitHubAuthService.ParseAccounts("not json"));
        Assert.False(GitHubAuthService.IsValidLogin("a b"));
        Assert.False(GitHubAuthService.IsValidLogin("-flag"));
        Assert.True(GitHubAuthService.IsValidLogin("xiaodoli_microsoft"));
    }

    [Fact]
    public void Store_Persists_The_Copilot_Account_And_Clearing_Restores_The_Default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forge-agent-providers-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, AgentProviderSettingsStore.FileName);
        try
        {
            var store = new AgentProviderSettingsStore(file);
            Assert.Null(store.CopilotAccount);

            store.SetCopilotAccount("xiaodoli_microsoft", "user-1");
            var reloaded = new AgentProviderSettingsStore(file);
            Assert.Equal("xiaodoli_microsoft", reloaded.CopilotAccount);
            Assert.Equal("user-1", reloaded.Copilot!.UpdatedBy);
            Assert.DoesNotContain("token", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);

            reloaded.SetCopilotAccount("  ", "user-1");
            Assert.Null(new AgentProviderSettingsStore(file).CopilotAccount);

            File.WriteAllText(file, "{ broken");
            Assert.Null(new AgentProviderSettingsStore(file).CopilotAccount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Agent_Sessions_Never_Fall_Back_To_The_GitHub_Provider_Token_Once_An_Account_Is_Chosen()
    {
        // The per-session token decides whose Copilot quota is billed. On
        // 2026-11-19 sessions kept sending the GitHub provider's token
        // (xiaodoli, 0% premium quota) after the operator chose
        // xiaodoli_microsoft, so every turn failed "exceeded your monthly quota".
        var dir = Path.Combine(Path.GetTempPath(), "forge-agent-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AgentProviderSettingsStore(Path.Combine(dir, AgentProviderSettingsStore.FileName));
            var provider = new FixedTokenAuth("provider-token");
            var manager = new CopilotServerManager(
                Microsoft.Extensions.Options.Options.Create(new AgentRunnerOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotServerManager>.Instance,
                new IAuthService[] { provider },
                null,
                store);

            Assert.Equal("provider-token", await manager.GetAgentGitHubTokenAsync(CancellationToken.None));

            store.SetCopilotAccount("xiaodoli_microsoft", "test");
            // Only GitHubAuthService can mint a per-account token; anything else
            // must yield no token rather than silently billing the provider account.
            Assert.Null(await manager.GetAgentGitHubTokenAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Only_A_Host_Admin_Can_Change_The_Copilot_Account()
    {
        await using var app = new ForgeWebApplicationFactory();
        await app.SeedProductAsync("p");
        await app.EnrollAsync("po", productId: "p");
        await app.EnrollAsync("admin", admin: true, productId: "p");
        using var po = await app.PairedClientAsync("po");
        using var admin = await app.PairedClientAsync("admin");
        po.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");
        admin.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");

        Assert.Equal(HttpStatusCode.Forbidden, (await po.PutAsJsonAsync("/api/settings/agent-providers/copilot", new { account = (string?)null })).StatusCode);

        var resp = await admin.PutAsJsonAsync("/api/settings/agent-providers/copilot", new { account = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("copilot").GetProperty("selectedAccount").ValueKind);
        Assert.True(body.GetProperty("claude").TryGetProperty("apiKeyConfigured", out _));
        Assert.Null(app.Services.GetRequiredService<AgentProviderSettingsStore>().CopilotAccount);
        Assert.Equal("admin", app.Services.GetRequiredService<AgentProviderSettingsStore>().Copilot!.UpdatedBy);

        var invalid = await admin.PutAsJsonAsync("/api/settings/agent-providers/copilot", new { account = "not a login" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class FixedTokenAuth(string token) : IAuthService
    {
        public string Kind => "github";

        public Task<AuthStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(new AuthStatus("github", true, "x", "x", null));

        public Task<AuthStatus> SignInAsync(CancellationToken ct = default) => this.StatusAsync(ct);

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(token);
    }
}