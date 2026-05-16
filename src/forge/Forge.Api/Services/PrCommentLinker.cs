// -----------------------------------------------------------------------
// <copyright file="PrCommentLinker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Forge.Api.Persistence;
using Forge.Api.Services.Git;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 24 — links PR comments to the commits that address them and to the
/// agent's in-thread responses. Two complementary heuristics:
/// <list type="bullet">
///   <item>Body parse — <c>Addresses comment N</c>, <c>Fixes comment N</c>, <c>CR-N</c>.</item>
///   <item>Position-and-time — agent commit modifies the same file/line as
///     an inline comment, lands after the comment's <c>CreatedAt</c>.</item>
/// </list>
/// </summary>
public sealed class PrCommentLinker
{
    private static readonly Regex BodyTagRegex = new(
        @"(?:Addresses|Fixes|fix|fixes|address|addresses)\s+comment\s+(?<id>\d+)|CR-(?<id2>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ProductDbContext db;
    private readonly ILogger<PrCommentLinker> logger;
    private readonly ForgeMetrics? metrics;

    public PrCommentLinker(ProductDbContext db, ILogger<PrCommentLinker> logger, ForgeMetrics? metrics = null)
    {
        this.db = db;
        this.logger = logger;
        this.metrics = metrics;
    }

    /// <summary>
    /// Run the linker for one PR. Scans all unlinked comments and attempts
    /// the two heuristics. Each successful link writes a
    /// <c>pr_comment_history(kind='linked')</c> row + recomputes
    /// <c>RequiresAttention</c>.
    /// </summary>
    public async Task<int> LinkAsync(string prId, string? worktreeRoot, CancellationToken ct)
    {
        var pr = await this.db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
        if (pr is null) return 0;

        var unlinked = await this.db.PrComments
            .Where(c => c.PrId == prId
                && c.LinkedCommitSha == null
                && c.LinkedAgentResponseId == null
                && c.AuthorKind != "agent")
            .ToListAsync(ct);
        if (unlinked.Count == 0) return 0;

        var allComments = await this.db.PrComments
            .Where(c => c.PrId == prId)
            .ToListAsync(ct);
        var allReplies = allComments
            .Where(c => c.AuthorKind == "agent" && c.ParentCommentId.HasValue)
            .ToList();

        var commits = await this.LoadRecentCommitsAsync(prId, pr, worktreeRoot, ct);
        var matched = 0;

        foreach (var comment in unlinked)
        {
            // -- 1) Body-parse — agent reply on the thread --
            foreach (var reply in allReplies)
            {
                if (reply.ParentCommentId != comment.Id) continue;
                if (this.MatchesId(reply.Body, comment.Id)
                    || reply.ParentCommentId == comment.Id) // direct reply counts as link
                {
                    comment.LinkedAgentResponseId = reply.Id;
                    this.AppendLinked(comment, $"reply:{reply.Id}");
                    matched++;
                    this.metrics?.LinkerMatched("body-parse-reply");
                    break;
                }
            }

            // -- 2) Body-parse on commit messages --
            if (comment.LinkedCommitSha is null)
            {
                foreach (var commit in commits)
                {
                    if (commit.At <= comment.CreatedAt) continue;
                    if (this.MatchesId(commit.Message, comment.Id))
                    {
                        comment.LinkedCommitSha = commit.Sha;
                        this.AppendLinked(comment, $"commit:{commit.Sha}");
                        matched++;
                        this.metrics?.LinkerMatched("body-parse-commit");
                        break;
                    }
                }
            }

            // -- 3) Position-and-time heuristic for inline comments --
            if (comment.LinkedCommitSha is null
                && !string.IsNullOrEmpty(comment.FilePath)
                && comment.Line.HasValue)
            {
                foreach (var commit in commits)
                {
                    if (commit.At <= comment.CreatedAt) continue;
                    if (commit.ModifiesFile(comment.FilePath!) && commit.ModifiesLine(comment.FilePath!, comment.Line.Value))
                    {
                        comment.LinkedCommitSha = commit.Sha;
                        this.AppendLinked(comment, $"commit:{commit.Sha}");
                        matched++;
                        this.metrics?.LinkerMatched("position-time");
                        break;
                    }
                }
            }

            // Recompute RequiresAttention after attempting link
            if (comment.LinkedAgentResponseId.HasValue || !string.IsNullOrEmpty(comment.LinkedCommitSha))
            {
                comment.RequiresAttention = false;
            }
        }

        if (commits.Count > 0)
        {
            pr.LastLinkerCommitSha = commits[0].Sha;
        }

        await this.db.SaveChangesAsync(ct);
        return matched;
    }

    private bool MatchesId(string text, long commentId)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (Match m in BodyTagRegex.Matches(text))
        {
            var id = m.Groups["id"].Success ? m.Groups["id"].Value : m.Groups["id2"].Value;
            if (long.TryParse(id, out var idn) && idn == commentId) return true;
        }
        return false;
    }

    private void AppendLinked(PrCommentRow comment, string toValue)
    {
        this.db.PrCommentHistory.Add(new PrCommentHistoryRow
        {
            CommentId = comment.Id,
            Kind = "linked",
            ToValue = toValue,
            ByUserId = "system:pr-comment-linker",
            At = DateTimeOffset.UtcNow,
        });
    }

    private async Task<List<CommitInfo>> LoadRecentCommitsAsync(string prId, PrRow pr, string? worktreeRoot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(worktreeRoot) || !Directory.Exists(worktreeRoot))
            return new List<CommitInfo>();

        var commits = new List<CommitInfo>();
        try
        {
            var psi = new ProcessStartInfo("git", $"log {pr.LastLinkerCommitSha ?? string.Empty}..HEAD --pretty=format:%H%x1f%cI%x1f%s%n%b%x1e --name-only")
            {
                WorkingDirectory = worktreeRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
            using var proc = Process.Start(psi);
            if (proc is null) return commits;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            // Parse: each commit is sha\1cdate\1subject\nbody\1e\nfile1\nfile2…
            foreach (var rec in stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = rec.Split('\n');
                if (lines.Length == 0) continue;
                var header = lines[0].Split('\x1f');
                if (header.Length < 3) continue;
                var sha = header[0];
                if (!DateTimeOffset.TryParse(header[1], out var at)) continue;
                var subject = header[2];
                var body = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.Contains('\x1e')));
                var files = lines.SkipWhile(l => !string.IsNullOrEmpty(l)).Skip(1).Where(l => !string.IsNullOrEmpty(l)).ToList();

                commits.Add(new CommitInfo(sha, at, subject + "\n" + body, files));
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "PrCommentLinker: git log probe failed for {PrId}", prId);
        }
        return commits;
    }

    private sealed record CommitInfo(string Sha, DateTimeOffset At, string Message, IReadOnlyList<string> Files)
    {
        public bool ModifiesFile(string path) =>
            this.Files.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase));

        // Without a unified diff we can't be 100% sure a specific line was
        // touched. Conservative approach: if the commit modifies the file at
        // all, treat the line as touched. The spec calls out the false-
        // positive risk; a more precise check requires `git blame -L` per
        // commit which is heavy for the tick.
        public bool ModifiesLine(string path, int line) => this.ModifiesFile(path);
    }
}
