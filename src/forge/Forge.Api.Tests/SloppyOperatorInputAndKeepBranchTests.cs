// -----------------------------------------------------------------------
// <copyright file="SloppyOperatorInputAndKeepBranchTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using System.Text.Json;
using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// harness:DSH stage 1.4 (2026-11-20): the score sat at 89 while the generator waited
/// on an operator question and then on a requested PR review. The flat-plateau rule
/// counted iterations from before and after the operator's answer as one stuck run,
/// marked the loop sloppy a minute after the answer, and the reassignment reset the
/// finished branch to base and force-pushed it (closing the open PR).
/// <list type="bullet">
///   <item>A plateau counts only iterations scored after the latest operator input.</item>
///   <item>A plateau while questions are unanswered parks for answers instead of reassigning.</item>
///   <item>A reassignment keeps a work branch that has commits and records a backup ref.</item>
/// </list>
/// </summary>
public sealed class SloppyOperatorInputAndKeepBranchTests
{
    private const string Fence = "```";
    private const int FlatScore = 85;

    [Fact]
    public async Task Flat_Plateau_Still_Fires_Without_Operator_Input()
    {
        var (f, db, loop) = await SetupLoopAsync("wi-flat-control");
        try
        {
            var result = await loop.RunAsync(FlatRequest("wi-flat-control", maxIterations: 9), CancellationToken.None);

            Assert.Equal("sloppy", result.FinalVerdict);
            Assert.Equal(SloppyReasons.FlatPlateau, result.SloppyReason);
            Assert.Equal(ScoringConstants.SloppyFlatPlateauMinIters, result.Iterations);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Flat_Plateau_With_Unanswered_Question_Parks_For_Answers_Instead_Of_Reassigning()
    {
        var (f, db, loop) = await SetupLoopAsync("wi-flat-question");
        try
        {
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = "wi-flat-question", ExternalId = "pr-review", Iter = 1, Kind = "choice",
                Text = "Who opens the PR for the required review?", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var result = await loop.RunAsync(FlatRequest("wi-flat-question", maxIterations: 9), CancellationToken.None);

            Assert.Equal("stalled-questions", result.FinalVerdict);
            Assert.Null(result.SloppyReason);
            Assert.Equal(ScoringConstants.SloppyFlatPlateauMinIters, result.Iterations);
            var last = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == "wi-flat-question").OrderByDescending(d => d.Iter).FirstAsync();
            Assert.Equal("stalled-questions", last.Verdict);
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-flat-question");
            Assert.Equal(ExecutionStatuses.AwaitingAnswers, wi.ExecutionStatus);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task An_Operator_Answer_Starts_A_New_Plateau_Window()
    {
        const string Wi = "wi-flat-answered";
        var (f, db, loop) = await SetupLoopAsync(Wi);
        try
        {
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = Wi, ExternalId = "pr-review", Iter = 1, Kind = "choice",
                Text = "Who opens the PR for the required review?", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // The operator answers between iterations 3 and 4, so iteration 8 has only
            // five post-answer scores: below the plateau minimum, the loop runs to its cap.
            var request = FlatRequest(Wi, maxIterations: 9) with
            {
                Generator = async (iter, _, ct) =>
                {
                    if (iter == 4)
                    {
                        using var scope = f.Services.CreateScope();
                        var answerDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                        var q = await answerDb.DeliverableQuestions.FirstAsync(x => x.WorkItemId == Wi, ct);
                        q.Answer = "Engineer opens a draft PR";
                        q.AnsweredAt = DateTimeOffset.UtcNow;
                        q.AnsweredBy = "operator";
                        await answerDb.SaveChangesAsync(ct);
                    }

                    return new GeneratorRunOutput("out", 0m);
                },
            };

            var result = await loop.RunAsync(request, CancellationToken.None);

            Assert.Equal(9, result.Iterations);
            Assert.NotEqual(SloppyReasons.FlatPlateau, result.SloppyReason);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Reassignment_Keeps_A_Branch_With_Commits_And_Records_A_Backup_Ref()
    {
        const string Wi = "wi-keep-branch";
        var (root, worktree, tip) = CreateRepoWithWorkBranch(Wi, commitWork: true);
        await using var f = await BootReconcilerAsync();
        try
        {
            await SeedPendingReassignAsync(f, Wi, Path.Combine(root, "clone"));

            using (var scope = f.Services.CreateScope())
            {
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>().RunOnceAsync(CancellationToken.None));
            }

            Assert.Equal(tip, Git(worktree, "rev-parse", "HEAD"));
            Assert.Equal(tip, Git(worktree, "rev-parse", "origin/ws/keep"));
            Assert.Contains(tip, Git(Path.Combine(root, "origin.git"), "ls-remote", ".", "refs/heads/ws/keep"));
            Assert.Equal(tip, Git(worktree, "rev-parse", $"refs/forge/backup/{Wi}/before-attempt-2"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Reassignment_Still_Resets_A_Branch_Without_Commits()
    {
        const string Wi = "wi-reset-branch";
        var (root, worktree, baseTip) = CreateRepoWithWorkBranch(Wi, commitWork: false);
        File.WriteAllText(Path.Combine(worktree, "scratch.txt"), "uncommitted");
        await using var f = await BootReconcilerAsync();
        try
        {
            await SeedPendingReassignAsync(f, Wi, Path.Combine(root, "clone"));

            using (var scope = f.Services.CreateScope())
            {
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>().RunOnceAsync(CancellationToken.None));
            }

            Assert.Equal(baseTip, Git(worktree, "rev-parse", "HEAD"));
            Assert.False(File.Exists(Path.Combine(worktree, "scratch.txt")));
        }
        finally { TryDelete(root); }
    }

    private static string EvalJson(int score) => Fence + "json\n{ \"score\": " + score + " }\n" + Fence;

    private static DeliverableLoopRequest FlatRequest(string workItemId, int maxIterations) => new(
        workItemId, "crew-x", "gen-1", "eval-1",
        Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
        Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(FlatScore), 0m)),
        MaxIterations: maxIterations);

    private static async Task<(ForgeWebApplicationFactory F, ProductDbContext Db, DeliverableLoop Loop)> SetupLoopAsync(string workItemId)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId, Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (f, db, new DeliverableLoop(control, db));
    }

    private static async Task<ForgeWebApplicationFactory> BootReconcilerAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }
        using var scope = f.Services.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SloppyReassignmentOptions>>();
        opts.CurrentValue.ResetWorktreeOnReassign = true;
        opts.CurrentValue.Enabled = false; // RunOnceAsync is driven by the test
        return f;
    }

    private static async Task SeedPendingReassignAsync(ForgeWebApplicationFactory f, string workItemId, string cloneRoot)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        control.Crews.Add(new CrewRow { Id = "crew-keep", Name = "crew-keep", Summary = string.Empty, Lead = "agent-lead", Model = "claude-sonnet-4-6" });
        foreach (var id in new[] { "agent-a", "agent-b", "agent-c", "agent-d" })
        {
            control.Agents.Add(new AgentRow { Id = id, Name = id, Role = "eng" });
            control.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-keep", AgentId = id, Role = "eng" });
        }

        control.Agents.Add(new AgentRow { Id = "agent-lead", Name = "agent-lead", Role = "tech-lead" });
        await control.SaveChangesAsync();

        db.Repos.Add(new RepoRow { Id = "repo-keep", ProductId = "keep", DisplayName = "keep", Kind = "local", RemoteUrl = "file://keep", DefaultBranch = "main", CloneRoot = cloneRoot });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId, Type = "step", State = "active", Title = "T", RepoId = "repo-keep",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.PendingReassign, PairAttemptCount = 1,
            PriorPairsJson = JsonSerializer.Serialize(new[] { new[] { "agent-a", "agent-b" } }),
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = workItemId, CrewId = "crew-keep", GeneratorAgentId = "agent-a", EvaluatorAgentId = "agent-b",
            Iter = 8, Score = 89, Verdict = "sloppy", DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>origin.git (bare) ← clone (main) with a worktree at .worktree/ws-{id} on ws/keep.</summary>
    private static (string Root, string Worktree, string Tip) CreateRepoWithWorkBranch(string workItemId, bool commitWork)
    {
        var root = Directory.CreateTempSubdirectory("forge-keep-branch-").FullName;
        Git(root, "init", "--bare", "-b", "main", "origin.git");
        Git(root, "clone", "origin.git", "clone");
        var clone = Path.Combine(root, "clone");
        File.WriteAllText(Path.Combine(clone, "README.md"), "base");
        Git(clone, "add", ".");
        Git(clone, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "base");
        Git(clone, "push", "origin", "HEAD:main");
        Git(clone, "fetch", "origin");

        var worktree = Path.Combine(clone, ".worktree", "ws-" + workItemId);
        Git(clone, "worktree", "add", "-b", "ws/keep", worktree, "origin/main");
        if (commitWork)
        {
            File.WriteAllText(Path.Combine(worktree, "work.txt"), "finished stage");
            Git(worktree, "add", ".");
            Git(worktree, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "work");
        }

        Git(worktree, "push", "-u", "origin", "ws/keep");
        Git(worktree, "fetch", "origin");
        return (root, worktree, Git(worktree, "rev-parse", "HEAD"));
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    private static void TryDelete(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}