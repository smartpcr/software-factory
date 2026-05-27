// -----------------------------------------------------------------------
// <copyright file="WorkstreamRestartCleanupTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Regression coverage for Restart's clean-slate contract: operator-visible
/// iteration, prompt, worktree, and gate-log state must not survive a restart.
/// </summary>
public class WorkstreamRestartCleanupTests
{
    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    [Fact]
    public async Task Restart_DeletesIterationPrompts_ForTargetWorkstreamOnly()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string targetWsId = "ws-restart-prompts-target";
        const string siblingWsId = "ws-restart-prompts-sibling";
        await SeedWorkstreamsAsync(f, targetWsId, siblingWsId);
        await SeedPromptAsync(f, targetWsId, iter: 1, role: "generator", userPrompt: "target-generator");
        await SeedPromptAsync(f, targetWsId, iter: 1, role: "evaluator", userPrompt: "target-evaluator");
        await SeedPromptAsync(f, siblingWsId, iter: 1, role: "generator", userPrompt: "sibling-generator");

        var resp = await client.PostAsync($"/api/workstreams/{targetWsId}/restart", content: null);
        Assert.True(resp.IsSuccessStatusCode,
            $"restart failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal(2, body.GetProperty("iterationPromptsCleaned").GetInt32());

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.Equal(0, await db.IterationPrompts.CountAsync(p => p.WorkItemId == targetWsId));
        var siblingRows = await db.IterationPrompts.AsNoTracking()
            .Where(p => p.WorkItemId == siblingWsId)
            .ToListAsync();
        Assert.Single(siblingRows);
        Assert.Equal("sibling-generator", siblingRows[0].UserPrompt);
    }

    [Fact]
    public async Task Restart_PrunesGateLogs_AndClearsWorktreePointer()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string wsId = "ws-restart-gate-logs";
        var cloneRoot = Path.Combine(f.Folder, "repo-gate-logs");
        var worktreePath = Path.Combine(cloneRoot, ".worktree", $"ws-{wsId}");
        var logsDir = Path.Combine(worktreePath, ".forge", "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "build-1.log"), "old build failure");
        await File.WriteAllTextAsync(Path.Combine(logsDir, "test-1.log"), "old test failure");

        await SeedWorkstreamsAsync(f, wsId, cloneRoot: cloneRoot);

        var resp = await client.PostAsync($"/api/workstreams/{wsId}/restart", content: null);
        Assert.True(resp.IsSuccessStatusCode,
            $"restart failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(body.GetProperty("gateLogsPruned").GetBoolean());
        Assert.False(Directory.Exists(logsDir));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
        var worktree = JsonSerializer.Deserialize<string[]>(ws.WorktreeJson);
        Assert.Equal(new[] { "(not yet created)" }, worktree);
    }

    [Fact]
    public async Task Restart_MergedPr_ReturnsConflictAndPreservesRows()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string wsId = "ws-restart-merged-pr";
        const string prId = "pr-restart-merged";
        await SeedWorkstreamsAsync(f, wsId);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.SingleAsync(w => w.Id == wsId);
            ws.Status = "complete";
            ws.PrsJson = JsonSerializer.Serialize(new[] { prId });
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Sort = 0,
                Col = "merged",
                Epic = "story-restart-cleanup",
                Workstream = wsId,
                Base = "main",
                Branch = "ws/restart-merged",
                Title = "Merged implementation",
                IsActive = false,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = wsId,
                CrewId = "crew-eng",
                GeneratorAgentId = "E9",
                EvaluatorAgentId = "R3",
                Iter = 1,
                Score = 92,
                Verdict = "pass",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync($"/api/workstreams/{wsId}/restart", content: null);

        Assert.Equal(409, (int)resp.StatusCode);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
            Assert.Equal("complete", ws.Status);
            Assert.Equal(JsonSerializer.Serialize(new[] { prId }), ws.PrsJson);
            var pr = await db.Prs.AsNoTracking().SingleAsync(p => p.Id == prId);
            Assert.Equal("merged", pr.Col);
            Assert.False(pr.IsActive);
            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == wsId));
        }
    }

    [Fact]
    public async Task Restart_MergedPrLinkedByWorkstream_ReturnsConflict_WhenPrsJsonWasCleared()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string wsId = "ws-restart-merged-pr-row-link";
        const string prId = "pr-restart-merged-row-link";
        await SeedWorkstreamsAsync(f, wsId);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.SingleAsync(w => w.Id == wsId);
            ws.Status = "active";
            ws.PrsJson = "[]";
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Sort = 0,
                Col = "merged",
                Epic = "story-restart-cleanup",
                Workstream = wsId,
                Base = "main",
                Branch = "ws/restart-merged-row-link",
                Title = "Merged implementation",
                IsActive = false,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = wsId,
                CrewId = "crew-eng",
                GeneratorAgentId = "E9",
                EvaluatorAgentId = "R3",
                Iter = 1,
                Score = 92,
                Verdict = "pass",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync($"/api/workstreams/{wsId}/restart", content: null);

        Assert.Equal(409, (int)resp.StatusCode);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == wsId);
            Assert.Equal("active", ws.Status);
            Assert.Equal("[]", ws.PrsJson);
            var pr = await db.Prs.AsNoTracking().SingleAsync(p => p.Id == prId);
            Assert.Equal("merged", pr.Col);
            Assert.False(pr.IsActive);
            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == wsId));
        }
    }

    private static async Task SeedPromptAsync(
        ForgeWebApplicationFactory f,
        string workItemId,
        int iter,
        string role,
        string userPrompt)
    {
        using var scope = f.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IterationPromptStore>();
        await store.SaveAsync(
            workItemId,
            iter,
            role,
            $"agent-{role}",
            $"crew-{role}",
            systemPrompt: $"system-{role}",
            userPrompt,
            model: "model-x",
            backend: "copilot",
            invokeMode: "sdk");
    }

    private static async Task SeedWorkstreamsAsync(
        ForgeWebApplicationFactory f,
        params string[] wsIds)
    {
        await SeedWorkstreamsAsync(f, wsIds, cloneRoot: null);
    }

    private static async Task SeedWorkstreamsAsync(
        ForgeWebApplicationFactory f,
        string wsId,
        string? cloneRoot)
    {
        await SeedWorkstreamsAsync(f, new[] { wsId }, cloneRoot);
    }

    private static async Task SeedWorkstreamsAsync(
        ForgeWebApplicationFactory f,
        IReadOnlyCollection<string> wsIds,
        string? cloneRoot)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var repoId = cloneRoot is null ? null : "repo-restart-cleanup";
        if (cloneRoot is not null)
        {
            Directory.CreateDirectory(cloneRoot);
            db.Repos.Add(new RepoRow
            {
                Id = repoId!,
                ProductId = "prod-x",
                Kind = "github",
                RemoteUrl = "https://example.invalid/restart-cleanup.git",
                DisplayName = "restart-cleanup",
                DefaultBranch = "main",
                CloneRoot = cloneRoot,
            });
        }

        foreach (var wsId in wsIds)
        {
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                Sort = 0,
                RepoId = repoId,
                Name = wsId,
                Branch = string.Empty,
                Base = "main",
                Epic = "story-restart-cleanup",
                Status = "stalled",
                WorktreeJson = JsonSerializer.Serialize(new[] { "old-root", "old-worktree" }),
                AgentsJson = "[]",
                PrsJson = "[]",
                CardsJson = "[]",
                Summary = wsId,
                Category = WorkstreamCategories.Impl,
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wsId,
                Provider = "local",
                Track = "dev",
                Type = "task",
                State = "active",
                Title = wsId,
                DescriptionMd = "restart cleanup regression",
                Points = 3,
                Priority = "med",
                ExecutionStatus = ExecutionStatuses.Idle,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }
}
