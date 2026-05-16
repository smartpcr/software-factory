// -----------------------------------------------------------------------
// <copyright file="GitHubAuthService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 9.11 — GitHub auth via the GitHub CLI (<c>gh</c>).
///
/// Why <c>gh</c> rather than a Forge-owned OAuth App: this lets the operator
/// sign in with whichever GitHub account is already configured on the
/// machine, with zero client-id / app-registration steps. The browser
/// sign-in flow is whatever <c>gh auth login --web</c> does (handing off to
/// github.com's device-less OAuth path), and the token Forge uses is the
/// same one <c>gh</c> stores in the OS credential keyring.
///
/// Per-user setting <c>PreferredGitHubAccount</c> selects which gh-known
/// account to make active. Empty → use whatever <c>gh</c> reports as active.
/// </summary>
public partial class GitHubAuthService : IAuthService
{
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<GitHubAuthService> logger;

    private readonly object cacheGate = new();
    private string? cachedToken;
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;

    public GitHubAuthService(IServiceScopeFactory scopeFactory, ILogger<GitHubAuthService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public string Kind => "github";

    public async Task<AuthStatus> StatusAsync(CancellationToken ct = default)
    {
        if (!IsGhInstalled())
        {
            return new AuthStatus(this.Kind, false, null, null, null,
                "GitHub CLI (gh) is not installed. Install: winget install GitHub.cli");
        }

        var preferred = await this.GetPreferredAccountAsync(ct);
        var login = await this.ResolveActiveLoginAsync(preferred, ct);
        if (login is null)
        {
            return new AuthStatus(this.Kind, false, null, null, null);
        }

        return new AuthStatus(this.Kind, true, login, login, null);
    }

    public async Task<AuthStatus> SignInAsync(CancellationToken ct = default)
    {
        if (!IsGhInstalled())
        {
            return new AuthStatus(this.Kind, false, null, null, null,
                "GitHub CLI (gh) is not installed. Run: winget install GitHub.cli");
        }

        var preferred = await this.GetPreferredAccountAsync(ct);

        // 0) Already logged in to the preferred account? Just activate it and return.
        var existingLogin = await this.ResolveActiveLoginAsync(preferred, ct);
        if (existingLogin is not null)
        {
            this.InvalidateCache();
            this.logger.LogInformation("GitHub sign-in: {Login} already authenticated via gh", existingLogin);
            return new AuthStatus(this.Kind, true, existingLogin, existingLogin, null);
        }

        // 1) Spawn an interactive console so the user sees the gh CLI prompts
        // and can paste the one-time code into the browser. We do not capture
        // its stdout — gh's UX is what completes the flow. The window stays
        // open after gh exits so the user can read any final messages.
        try
        {
            LaunchInteractiveGhLogin();
        }
        catch (Exception ex)
        {
            return new AuthStatus(this.Kind, false, null, null, null, $"Could not launch gh CLI: {ex.Message}");
        }

        // 2) Poll for completion. 5-minute cap matches the AuthEndpoints upper bound.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var login = await this.ResolveActiveLoginAsync(preferred, ct);
            if (login is not null)
            {
                this.InvalidateCache();
                this.logger.LogInformation("GitHub sign-in succeeded as {Login}", login);
                return new AuthStatus(this.Kind, true, login, login, null);
            }
        }

        return new AuthStatus(this.Kind, false, null, null, null,
            "GitHub sign-in didn't complete in time. Re-run when ready.");
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (!IsGhInstalled())
        {
            return;
        }

        var preferred = await this.GetPreferredAccountAsync(ct);
        // gh auth logout requires --user when multiple accounts exist; fall
        // back to no-user if a preferred isn't set.
        var args = string.IsNullOrEmpty(preferred)
            ? "auth logout --hostname github.com"
            : $"auth logout --hostname github.com --user {preferred}";
        var (_, _, exit) = await RunGhAsync(args, captureOutput: true, ct);
        if (exit != 0)
        {
            this.logger.LogDebug("gh auth logout exited {Exit}", exit);
        }
        this.InvalidateCache();
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (!IsGhInstalled()) return null;

        // Memory cache — gh shell-out is ~100ms.
        lock (this.cacheGate)
        {
            if (this.cachedToken is not null && DateTimeOffset.UtcNow - this.cachedAt < TokenCacheTtl)
            {
                return this.cachedToken;
            }
        }

        var preferred = await this.GetPreferredAccountAsync(ct);
        var args = string.IsNullOrEmpty(preferred)
            ? "auth token --hostname github.com"
            : $"auth token --hostname github.com --user {preferred}";
        var (stdout, _, exit) = await RunGhAsync(args, captureOutput: true, ct);
        if (exit != 0)
        {
            return null;
        }

        var token = stdout.Trim();
        if (string.IsNullOrEmpty(token)) return null;

        // Resolve login lazily so callers that only need the token don't pay
        // for a second shell-out.
        lock (this.cacheGate)
        {
            this.cachedToken = token;
            this.cachedAt = DateTimeOffset.UtcNow;
        }
        return token;
    }

    // -- helpers --------------------------------------------------------------

    private async Task<string?> GetPreferredAccountAsync(CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = await db.UserSettings.AsNoTracking()
                .OrderBy(x => x.UserId)
                .FirstOrDefaultAsync(ct);
            var preferred = row?.PreferredGitHubAccount?.Trim();
            return string.IsNullOrEmpty(preferred) ? null : preferred;
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Could not read PreferredGitHubAccount; falling back to gh active account");
            return null;
        }
    }

    /// <summary>
    /// Determines the currently-active gh login for github.com, optionally
    /// switching to <paramref name="preferred"/> first. Returns null if no
    /// account is logged in.
    /// </summary>
    private async Task<string?> ResolveActiveLoginAsync(string? preferred, CancellationToken ct)
    {
        // If a preferred account is configured, try to make it active. Switch
        // is a no-op if it's already active; it errors if the account isn't
        // in the keyring at all (in which case we fall back to whatever's
        // active so the user still sees something useful).
        if (!string.IsNullOrEmpty(preferred))
        {
            var (_, _, switchExit) = await RunGhAsync(
                $"auth switch --hostname github.com --user {preferred}", captureOutput: true, ct);
            if (switchExit == 0)
            {
                return preferred;
            }
            // Switch failed — preferred account is not yet logged in. Surface
            // whatever is active (if any) so the caller can still operate.
        }

        var (statusOut, statusErr, statusExit) = await RunGhAsync(
            "auth status --hostname github.com", captureOutput: true, ct);
        if (statusExit != 0 && string.IsNullOrEmpty(statusOut) && string.IsNullOrEmpty(statusErr))
        {
            return null;
        }

        // gh writes status to stderr; older versions wrote to stdout. Search both.
        return ParseActiveLogin(statusOut + "\n" + statusErr);
    }

    /// <summary>
    /// Parses the output of <c>gh auth status</c> for the "Active account"
    /// login. The format we match (gh 2.x):
    /// <code>
    /// github.com
    ///   ✓ Logged in to github.com account &lt;login&gt; (keyring)
    ///   - Active account: true
    /// </code>
    /// We pick the login on the most recent "Logged in" line whose immediate
    /// "Active account: true" follows.
    /// </summary>
    internal static string? ParseActiveLogin(string text)
    {
        // Strip ANSI escape sequences gh writes when stderr is redirected.
        text = AnsiEscape().Replace(text, string.Empty);

        // Walk lines; remember the most recent Logged-in login, commit it as
        // active when we hit "Active account: true".
        string? pendingLogin = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var loggedIn = LoggedInLine().Match(line);
            if (loggedIn.Success)
            {
                pendingLogin = loggedIn.Groups[1].Value;
                continue;
            }
            if (line.StartsWith("- Active account: true", StringComparison.OrdinalIgnoreCase) && pendingLogin is not null)
            {
                return pendingLogin;
            }
            if (line.StartsWith("- Active account: false", StringComparison.OrdinalIgnoreCase))
            {
                pendingLogin = null;
            }
        }
        return null;
    }

    private static bool IsGhInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunGhAsync(
        string arguments, bool captureOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (string.Empty, string.Empty, -1);
        }

        var stdoutTask = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var stderrTask = captureOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (stdout, stderr, process.ExitCode);
    }

    /// <summary>
    /// Spawns <c>gh auth login -h github.com --git-protocol https --web</c>
    /// in a separate visible console window so the operator sees the device
    /// code prompt and can complete the flow. We don't pipe stdio because gh
    /// expects an interactive TTY for the prompts.
    /// </summary>
    private static void LaunchInteractiveGhLogin()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // cmd /c start … opens a new console window; cmd /k keeps it open
            // after gh exits so the user can read the final "Logged in as …" line.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start \"Forge — GitHub sign-in\" cmd /k gh auth login --hostname github.com --git-protocol https --web",
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            using var _ = Process.Start(psi);
            return;
        }

        // Non-Windows: best effort — run gh inline. The host process must
        // have a TTY for this to actually work.
        var inline = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = "auth login --hostname github.com --git-protocol https --web",
            UseShellExecute = true,
        };
        using var __ = Process.Start(inline);
    }

    private void InvalidateCache()
    {
        lock (this.cacheGate)
        {
            this.cachedToken = null;
            this.cachedAt = DateTimeOffset.MinValue;
        }
    }

    [GeneratedRegex(@"Logged in to github\.com account ([\w.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LoggedInLine();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();
}
