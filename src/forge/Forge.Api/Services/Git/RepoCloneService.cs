// -----------------------------------------------------------------------
// <copyright file="RepoCloneService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

using System.Diagnostics;
using System.Text;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 8.10 — shells out to the local <c>git</c> binary to clone a repo
/// into its <c>cloneRoot</c>. Auth is HTTPS-only with the PAT injected into
/// the URL; SSH is not supported (Q6 resolution). Progress is streamed via
/// SignalR <c>forge.repo.clone.{started,progress,finished,failed}</c>.
/// </summary>
public class RepoCloneService : IRepoCloneService
{
    private readonly IHubContext<ForgeHub> hub;
    private readonly ILogger<RepoCloneService> logger;
    private readonly AdoOptions adoOptions;
    private readonly GitHubOptions ghOptions;
    private readonly RepoCloneOptions options;

    public RepoCloneService(
        IHubContext<ForgeHub> hub,
        IOptions<AdoOptions> adoOptions,
        IOptions<GitHubOptions> ghOptions,
        IOptions<RepoCloneOptions> options,
        ILogger<RepoCloneService> logger)
    {
        this.hub = hub;
        this.adoOptions = adoOptions.Value;
        this.ghOptions = ghOptions.Value;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<RepoCloneResult> EnsureClonedAsync(RepoRow repo, CancellationToken ct = default)
    {
        if (!this.options.AutoClone)
        {
            return new RepoCloneResult(RepoCloneOutcome.Skipped, "auto-clone disabled");
        }

        // Already a valid checkout? Verify the remote points at the expected URL.
        if (Directory.Exists(repo.CloneRoot) && Directory.Exists(Path.Combine(repo.CloneRoot, ".git")))
        {
            var actual = await ReadOriginAsync(repo.CloneRoot, ct);
            if (string.IsNullOrWhiteSpace(actual))
            {
                return new RepoCloneResult(RepoCloneOutcome.Skipped, "existing checkout, no origin remote");
            }
            if (!UrlsMatch(actual, repo.RemoteUrl))
            {
                return new RepoCloneResult(
                    RepoCloneOutcome.Mismatched,
                    $"cloneRoot already contains '{actual}' — expected '{repo.RemoteUrl}'");
            }
            return new RepoCloneResult(RepoCloneOutcome.Skipped, "already cloned");
        }

        // Refuse to clone over a non-empty directory that isn't a git repo —
        // the operator picked a path with content unrelated to this repo.
        if (Directory.Exists(repo.CloneRoot) && Directory.EnumerateFileSystemEntries(repo.CloneRoot).Any())
        {
            return new RepoCloneResult(
                RepoCloneOutcome.Mismatched,
                $"cloneRoot '{repo.CloneRoot}' exists and is not empty");
        }

        var parent = Path.GetDirectoryName(repo.CloneRoot);
        if (string.IsNullOrEmpty(parent))
        {
            return new RepoCloneResult(RepoCloneOutcome.Failed, "cloneRoot has no parent directory");
        }
        Directory.CreateDirectory(parent);

        var authedUrl = InjectPat(repo.RemoteUrl, repo.Kind, this.adoOptions.Pat, this.ghOptions.Pat);

        await this.hub.Clients.All.SendAsync(
            "forge.repo.clone.started",
            new { repoId = repo.Id, remote = repo.RemoteUrl, cloneRoot = repo.CloneRoot },
            ct);

        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("clone");
            psi.ArgumentList.Add("--progress");
            psi.ArgumentList.Add(authedUrl);
            psi.ArgumentList.Add(repo.CloneRoot);

            using var proc = Process.Start(psi)!;
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { /* git puts progress on stderr */ };
            proc.ErrorDataReceived += async (_, e) =>
            {
                if (e.Data is null) return;
                stderr.AppendLine(e.Data);
                // Sanitize: strip any PAT that leaked into messages.
                var safe = SanitizeProgressLine(e.Data, this.adoOptions.Pat, this.ghOptions.Pat);
                await this.hub.Clients.All.SendAsync(
                    "forge.repo.clone.progress",
                    new { repoId = repo.Id, line = safe });
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0)
            {
                await this.hub.Clients.All.SendAsync(
                    "forge.repo.clone.finished",
                    new { repoId = repo.Id, cloneRoot = repo.CloneRoot },
                    ct);
                // Phase 8.10 — write `.worktree/` into the repo's `.git/info/exclude`
                // so sibling worktrees never report as untracked content.
                await WriteWorktreeExcludeAsync(repo.CloneRoot, ct);
                return new RepoCloneResult(RepoCloneOutcome.Cloned, "ok");
            }

            // Best-effort cleanup of partial clone.
            TryCleanup(repo.CloneRoot);
            var safeErr = SanitizeProgressLine(stderr.ToString(), this.adoOptions.Pat, this.ghOptions.Pat).Trim();
            await this.hub.Clients.All.SendAsync(
                "forge.repo.clone.failed",
                new { repoId = repo.Id, error = safeErr, exitCode = proc.ExitCode },
                ct);
            return new RepoCloneResult(RepoCloneOutcome.Failed, $"git clone exit {proc.ExitCode}: {safeErr}");
        }
        catch (Exception ex)
        {
            TryCleanup(repo.CloneRoot);
            this.logger.LogWarning(ex, "RepoCloneService failed for {RepoId}", repo.Id);
            await this.hub.Clients.All.SendAsync(
                "forge.repo.clone.failed",
                new { repoId = repo.Id, error = ex.Message },
                ct);
            return new RepoCloneResult(RepoCloneOutcome.Failed, ex.Message);
        }
    }

    private static async Task<string?> ReadOriginAsync(string cloneRoot, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cloneRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("--get");
            psi.ArgumentList.Add("remote.origin.url");
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool UrlsMatch(string a, string b)
    {
        // Strip any embedded credentials before comparing.
        return Strip(a).Equals(Strip(b), StringComparison.OrdinalIgnoreCase);

        static string Strip(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var b = new UriBuilder(u) { UserName = string.Empty, Password = string.Empty };
                return b.Uri.ToString().TrimEnd('/');
            }
            return url.TrimEnd('/');
        }
    }

    /// <summary>
    /// Injects a PAT into an HTTPS clone URL. Returns the original URL when
    /// no PAT is configured for the provider — git will then prompt or fail,
    /// which is the right behavior in dev when the operator hasn't signed in yet.
    /// </summary>
    internal static string InjectPat(string remoteUrl, string kind, string? adoPat, string? ghPat)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) return remoteUrl;
        if (uri.Scheme != "https") return remoteUrl;

        var pat = kind switch
        {
            "ado" => adoPat,
            "github" => ghPat,
            _ => null,
        };
        if (string.IsNullOrEmpty(pat)) return remoteUrl;

        var (user, pass) = kind switch
        {
            // ADO: empty username, PAT in password slot.
            "ado" => (string.Empty, pat!),
            // GitHub: PAT-as-username with x-oauth-basic password is the historical form,
            // but token-as-password also works. Keep token in password slot for
            // consistency with ADO and to avoid leaking the PAT into log messages
            // that print the URL with the username visible.
            "github" => ("x-access-token", pat!),
            _ => (string.Empty, pat!),
        };
        var builder = new UriBuilder(uri)
        {
            UserName = Uri.EscapeDataString(user),
            Password = Uri.EscapeDataString(pass),
        };
        return builder.Uri.ToString();
    }

    private static string SanitizeProgressLine(string line, params string?[] secrets)
    {
        var s = line;
        foreach (var sec in secrets)
        {
            if (!string.IsNullOrEmpty(sec))
            {
                s = s.Replace(sec, "***", StringComparison.Ordinal);
            }
        }
        return s;
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static async Task WriteWorktreeExcludeAsync(string cloneRoot, CancellationToken ct)
    {
        try
        {
            var excludePath = Path.Combine(cloneRoot, ".git", "info", "exclude");
            if (!File.Exists(excludePath)) return;
            var existing = await File.ReadAllLinesAsync(excludePath, ct);
            if (existing.Any(l => l.Trim() == ".worktree/")) return;
            using var sw = new StreamWriter(excludePath, append: true);
            await sw.WriteLineAsync(string.Empty);
            await sw.WriteLineAsync("# Forge — sibling worktrees live here. Phase 8.10.");
            await sw.WriteLineAsync(".worktree/");
        }
        catch
        {
            // Non-fatal — operator can run `git status` and add the entry by hand.
        }
    }
}

public class RepoCloneOptions
{
    public const string SectionName = "Forge:Repo";

    public bool AutoClone { get; set; } = true;
}
