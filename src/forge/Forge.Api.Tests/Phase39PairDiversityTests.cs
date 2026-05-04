// -----------------------------------------------------------------------
// <copyright file="Phase39PairDiversityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Threading.Channels;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 13e — pair-diversity rule. Generator and
/// evaluator MUST run on different models so the evaluator catches blind
/// spots the generator's training distribution can't see. Same-model pairs
/// were producing pass-stamped subpar work that no second eye ever caught
/// (operator-reported 2026-05-03), so the gate is now enforced server-side
/// in <see cref="CrewStoryArtifactStrategy"/> and <see cref="CrewQueueWorker"/>
/// before the agent runner is invoked.
/// </summary>
public class Phase39PairDiversityTests
{
    /// <summary>
    /// Architect plan-doc path: when a paired crew has the same Model on
    /// both sides (and no EvaluatorModel override), <see cref="CrewStoryArtifactStrategy.ProducePlanArtifactsAsync"/>
    /// must throw <see cref="InvalidOperationException"/> before any
    /// invocation reaches the agent runner.
    /// </summary>
    [Fact]
    public async Task ArchitectPair_RefusesDispatch_WhenGeneratorAndEvaluatorShareModel()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Two crews, paired, same Model, no EvaluatorModel.
        var matchingModel = "claude-opus-4.6-1m";
        db.Crews.Add(new CrewRow
        {
            Id = "crew-pd-day", Name = "PD-Day",
            Model = matchingModel, EvaluatorModel = null,
            DefaultGeneratorAgentId = "PD-G", DefaultEvaluatorAgentId = "PD-E",
            PairedWithCrewId = "crew-pd-night",
            Backend = "copilot", InvokeMode = "sdk",
        });
        db.Crews.Add(new CrewRow
        {
            Id = "crew-pd-night", Name = "PD-Night",
            Model = matchingModel, EvaluatorModel = null,
            DefaultGeneratorAgentId = "PD-G2", DefaultEvaluatorAgentId = "PD-E2",
            PairedWithCrewId = "crew-pd-day",
            Backend = "copilot", InvokeMode = "sdk",
        });
        db.Agents.AddRange(
            new AgentRow { Id = "PD-G", Name = "Gen", Role = "arch", CrewId = "crew-pd-day" },
            new AgentRow { Id = "PD-E", Name = "Eval", Role = "arch", CrewId = "crew-pd-day" },
            new AgentRow { Id = "PD-G2", Name = "Gen2", Role = "arch", CrewId = "crew-pd-night" },
            new AgentRow { Id = "PD-E2", Name = "Eval2", Role = "arch", CrewId = "crew-pd-night" });
        await db.SaveChangesAsync();

        var spawn = new StorySpawnRow
        {
            Id = "spawn-pd-arch",
            StoryId = "story-pd-arch",
            StoryTitle = "Pair-diversity test",
            StoryDescription = "Test that pair diversity is enforced.",
            StoryPoints = 3,
            Phase = StorySpawnPhases.Plan,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.StorySpawns.Add(spawn);
        var story = new WorkItemRow
        {
            Id = "story-pd-arch", Title = "Pair-diversity test", Type = "feature",
            State = "active", Track = "dev", Points = 3,
            DescriptionMd = "Test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkItems.Add(story);
        await db.SaveChangesAsync();

        // Override the architect crew lookup — CrewIdPrefix("Architect") = "crew-arch"
        // by convention, but the strategy looks up by id-equality first then
        // prefix. We seed crew-arch + crew-arch-night with the matching model
        // so the strategy picks our test crews.
        // Easier: just rename our crews to crew-arch / crew-arch-night.
        // Re-seed under the production names so the resolution chain hits them.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM crews WHERE Id IN ('crew-pd-day', 'crew-pd-night')");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM agents WHERE Id IN ('PD-G','PD-E','PD-G2','PD-E2')");
        db.Crews.Add(new CrewRow
        {
            Id = "crew-arch", Name = "Architecture",
            Model = matchingModel, EvaluatorModel = null,
            DefaultGeneratorAgentId = "PD-AG", DefaultEvaluatorAgentId = "PD-AE",
            PairedWithCrewId = "crew-arch-night",
            Backend = "copilot", InvokeMode = "sdk",
        });
        db.Crews.Add(new CrewRow
        {
            Id = "crew-arch-night", Name = "Architecture (Night)",
            Model = matchingModel, EvaluatorModel = null,
            DefaultGeneratorAgentId = "PD-AG2", DefaultEvaluatorAgentId = "PD-AE2",
            PairedWithCrewId = "crew-arch",
            Backend = "copilot", InvokeMode = "sdk",
        });
        db.Agents.AddRange(
            new AgentRow { Id = "PD-AG",  Name = "Gen",  Role = "arch", CrewId = "crew-arch" },
            new AgentRow { Id = "PD-AE",  Name = "Eval", Role = "arch", CrewId = "crew-arch" },
            new AgentRow { Id = "PD-AG2", Name = "Gen2", Role = "arch", CrewId = "crew-arch-night" },
            new AgentRow { Id = "PD-AE2", Name = "Eval2",Role = "arch", CrewId = "crew-arch-night" });
        await db.SaveChangesAsync();

        var strategy = scope.ServiceProvider.GetRequiredService<IStoryArtifactStrategy>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await strategy.ProducePlanArtifactsAsync(spawn, story, forceResume: false, CancellationToken.None));

        // The strategy fans out four parallel architect invocations; the
        // first to hit the gate throws. Task.WhenAll surfaces it as either
        // the InvalidOperationException directly or wrapped in an
        // AggregateException — accept either.
        var inner = ex is AggregateException agg ? agg.Flatten().InnerException ?? agg : ex;
        Assert.Contains("Pair-diversity rule violated", inner.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(matchingModel, inner.Message);

        f.Dispose();
    }

    /// <summary>
    /// When the evaluator crew has a different EvaluatorModel set, the gate
    /// passes. Smoke-test that a correctly-configured pair doesn't trip the
    /// guard. We don't run the full architect loop (no real agent runner),
    /// just verify the strategy gets past the diversity check by injecting
    /// a runner stub that throws on first invocation — that throw must NOT
    /// be the diversity error.
    /// </summary>
    [Fact]
    public async Task ArchitectPair_AllowsDispatch_WhenEvaluatorModelDiffers()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Re-seed so crew-arch-night uses gpt-5.5 as evaluator model — the
        // production pattern (opus generator, sonnet/gpt evaluator).
        await db.Database.ExecuteSqlRawAsync("DELETE FROM crews WHERE Id IN ('crew-arch', 'crew-arch-night')");
        db.Crews.Add(new CrewRow
        {
            Id = "crew-arch", Name = "Architecture",
            Model = "claude-opus-4.6-1m", EvaluatorModel = null,
            DefaultGeneratorAgentId = "PD-OK-AG", DefaultEvaluatorAgentId = "PD-OK-AE",
            PairedWithCrewId = "crew-arch-night",
            Backend = "copilot", InvokeMode = "sdk",
        });
        db.Crews.Add(new CrewRow
        {
            Id = "crew-arch-night", Name = "Architecture (Night)",
            Model = "claude-opus-4.6-1m", EvaluatorModel = "gpt-5.5",
            DefaultGeneratorAgentId = "PD-OK-AG2", DefaultEvaluatorAgentId = "PD-OK-AE2",
            PairedWithCrewId = "crew-arch",
            Backend = "copilot", InvokeMode = "sdk",
        });
        db.Agents.AddRange(
            new AgentRow { Id = "PD-OK-AG",  Name = "Gen",  Role = "arch", CrewId = "crew-arch" },
            new AgentRow { Id = "PD-OK-AE",  Name = "Eval", Role = "arch", CrewId = "crew-arch" },
            new AgentRow { Id = "PD-OK-AG2", Name = "Gen2", Role = "arch", CrewId = "crew-arch-night" },
            new AgentRow { Id = "PD-OK-AE2", Name = "Eval2",Role = "arch", CrewId = "crew-arch-night" });
        var spawn = new StorySpawnRow
        {
            Id = "spawn-pd-ok",
            StoryId = "story-pd-ok",
            StoryTitle = "Pair-diversity OK",
            StoryDescription = "Configured correctly.",
            StoryPoints = 3,
            Phase = StorySpawnPhases.Plan,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.StorySpawns.Add(spawn);
        var story = new WorkItemRow
        {
            Id = "story-pd-ok", Title = "Pair-diversity OK", Type = "feature",
            State = "active", Track = "dev", Points = 3,
            DescriptionMd = "OK", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkItems.Add(story);
        await db.SaveChangesAsync();

        var strategy = scope.ServiceProvider.GetRequiredService<IStoryArtifactStrategy>();

        // The runner stub will throw with a marker message if invoked;
        // the diversity gate must pass first so the failure is the runner
        // (not the gate).
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await strategy.ProducePlanArtifactsAsync(spawn, story, forceResume: false, CancellationToken.None));

        var inner = ex is AggregateException agg ? agg.Flatten().InnerException ?? agg : ex;
        Assert.DoesNotContain("Pair-diversity rule violated", inner.Message,
            StringComparison.OrdinalIgnoreCase);

        f.Dispose();
    }
}
