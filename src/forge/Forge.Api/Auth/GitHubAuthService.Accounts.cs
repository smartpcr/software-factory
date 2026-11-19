// -----------------------------------------------------------------------
// <copyright file="GitHubAuthService.Accounts.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Every github.com account gh is signed in to, and a token for one named
/// account. Used by the agent-provider settings so Copilot can run as a
/// different account than the one the GitHub repository provider uses.
/// Neither call changes gh's active account.
/// </summary>
public partial class GitHubAuthService
{
    /// <summary>One gh-known github.com account. Never carries a token.</summary>
    public sealed record GitHubAccount(string Login, bool Active, string State, string? Scopes);

    /// <summary>GitHub logins: letters, digits, '-' and, for managed (EMU) accounts, '_'. Also guards the gh argument string.</summary>
    public static bool IsValidLogin(string? login) => login is not null && LoginPattern().IsMatch(login);

    public async Task<IReadOnlyList<GitHubAccount>> ListAccountsAsync(CancellationToken ct = default)
    {
        if (!IsGhInstalled()) return Array.Empty<GitHubAccount>();
        var (stdout, _, _) = await RunGhAsync("auth status --hostname github.com --json hosts", captureOutput: true, ct);
        return ParseAccounts(stdout);
    }

    /// <summary>The account Forge uses when no explicit choice is made: the GitHub provider's preferred account, else gh's active one.</summary>
    public async Task<string?> DefaultAccountAsync(IReadOnlyList<GitHubAccount> accounts, CancellationToken ct = default)
    {
        var preferred = await this.GetPreferredAccountAsync(ct);
        if (preferred is not null && accounts.Any(a => a.Login == preferred && a.State == "success")) return preferred;
        return accounts.FirstOrDefault(a => a.Active)?.Login;
    }

    /// <summary>Token for <paramref name="login"/> from gh's keyring, or null. Does not switch the active account.</summary>
    public async Task<string?> GetAccessTokenForAccountAsync(string login, CancellationToken ct = default)
    {
        if (!IsValidLogin(login) || !IsGhInstalled()) return null;
        var (stdout, _, exit) = await RunGhAsync($"auth token --hostname github.com --user {login}", captureOutput: true, ct);
        var token = stdout.Trim();
        return exit == 0 && token.Length > 0 ? token : null;
    }

    internal static IReadOnlyList<GitHubAccount> ParseAccounts(string json)
    {
        var accounts = new List<GitHubAccount>();
        if (string.IsNullOrWhiteSpace(json)) return accounts;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hosts", out var hosts)
                || !hosts.TryGetProperty("github.com", out var list)
                || list.ValueKind != JsonValueKind.Array) return accounts;
            foreach (var a in list.EnumerateArray())
            {
                var login = a.TryGetProperty("login", out var l) ? l.GetString() : null;
                if (!IsValidLogin(login)) continue;
                accounts.Add(new GitHubAccount(
                    login!,
                    a.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True,
                    a.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown",
                    a.TryGetProperty("scopes", out var sc) ? sc.GetString() : null));
            }
        }
        catch (JsonException)
        {
            // Older gh without --json: report no accounts rather than guess.
        }
        return accounts;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,38}$")]
    private static partial Regex LoginPattern();
}