// -----------------------------------------------------------------------
// <copyright file="AgentProviderSettingsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Agents;
using Forge.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
/// Settings → Agent providers: which credentials each agent runtime uses.
/// Copilot can run as any github.com account gh is signed in to, independent
/// of the GitHub repository provider's account. Writes are host-wide and sit
/// under <c>/api/settings/</c>, which <c>OperatorRequestMiddleware</c>
/// restricts to host administrators.
/// </summary>
public static class AgentProviderSettingsEndpoints
{
    public static WebApplication MapAgentProviderSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings/agent-providers").WithTags("Settings");
        group.MapGet("/", Get).WithName("GetAgentProviders");
        group.MapPut("/copilot", PutCopilot).WithName("SetCopilotAccount");
        return app;
    }

    public sealed record CopilotAccountRequest(string? Account);

    private static async Task<IResult> Get(
        AgentProviderSettingsStore store,
        CopilotServerManager copilot,
        IEnumerable<IAuthService> auth,
        IOptions<AgentRunnerOptions> runner,
        CancellationToken ct)
        => Results.Ok(await BuildAsync(store, copilot, auth, runner, ct));

    private static async Task<IResult> PutCopilot(
        HttpContext ctx,
        [FromBody] CopilotAccountRequest req,
        AgentProviderSettingsStore store,
        CopilotServerManager copilot,
        IEnumerable<IAuthService> auth,
        IOptions<AgentRunnerOptions> runner,
        CancellationToken ct)
    {
        var writer = ForgeUser.RequireWriter(ctx);
        if (writer is not null) return writer;

        var account = string.IsNullOrWhiteSpace(req?.Account) ? null : req!.Account!.Trim();
        if (account is not null)
        {
            if (!GitHubAuthService.IsValidLogin(account))
                return Results.Problem(title: $"'{account}' is not a GitHub login", statusCode: 400);
            if (Gh(auth) is not { } gh) return Results.Problem(title: "GitHub CLI (gh) is not available on this host", statusCode: 409);
            var accounts = await gh.ListAccountsAsync(ct);
            var match = accounts.FirstOrDefault(a => a.Login == account);
            if (match is null)
                return Results.Problem(title: $"'{account}' is not signed in to gh on this host", detail: "Run `gh auth login` for it first.", statusCode: 400);
            if (match.State != "success")
                return Results.Problem(title: $"gh reports '{account}' as {match.State}", detail: "Re-authenticate it with `gh auth login`.", statusCode: 409);
        }

        var changed = store.CopilotAccount != account;
        store.SetCopilotAccount(account, ForgeUser.UserId(ctx) ?? "unknown");
        // The shared server authenticates once at spawn, so a new account
        // only takes effect on a fresh server.
        if (changed) await copilot.RestartForAccountChangeAsync();
        return Results.Ok(await BuildAsync(store, copilot, auth, runner, ct));
    }

    private static GitHubAuthService? Gh(IEnumerable<IAuthService> auth) => auth.OfType<GitHubAuthService>().FirstOrDefault();

    private static async Task<object> BuildAsync(
        AgentProviderSettingsStore store,
        CopilotServerManager copilot,
        IEnumerable<IAuthService> auth,
        IOptions<AgentRunnerOptions> runner,
        CancellationToken ct)
    {
        var gh = Gh(auth);
        var accounts = gh is null ? Array.Empty<GitHubAuthService.GitHubAccount>() : await gh.ListAccountsAsync(ct);
        var defaultAccount = gh is null ? null : await gh.DefaultAccountAsync(accounts, ct);
        var choice = store.Copilot;
        var state = copilot.State;
        return new
        {
            copilot = new
            {
                selectedAccount = choice?.Account,
                defaultAccount,
                effectiveAccount = choice?.Account ?? defaultAccount,
                accounts = accounts.Select(a => new { login = a.Login, activeInGh = a.Active, state = a.State, scopes = a.Scopes }),
                updatedAt = choice?.UpdatedAt,
                updatedBy = choice?.UpdatedBy,
                server = new
                {
                    running = state.Running,
                    // null while running = the GitHub provider's account.
                    account = state.Running ? state.Account ?? defaultAccount : null,
                    lastStartError = state.LastStartError,
                    lastStartErrorAt = state.LastStartErrorAt,
                },
            },
            claude = new
            {
                apiKeyConfigured = !string.IsNullOrWhiteSpace(runner.Value.AnthropicApiKey),
                cliPath = string.IsNullOrWhiteSpace(runner.Value.ClaudeCliPath) ? null : runner.Value.ClaudeCliPath,
            },
        };
    }
}