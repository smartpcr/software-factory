// -----------------------------------------------------------------------
// <copyright file="ExternalBlockerReviewTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The evaluator treats work blocked outside the stage as deferred. On
/// harness:DSH-M2 DH-S-16 (2026-11-27) the evaluator never saw the operator's
/// answers ("merge the partial record now; finish closure later") and listed
/// the same four externally blocked items for five iterations until the loop
/// stalled "no-convergence". The evaluator now gets the pins, the answers and
/// <see cref="CrewStoryArtifactStrategy.ExternalBlockerReviewRule"/>, and the
/// convergence detector and checkbox gate ignore its deferred list.
/// </summary>
public sealed class ExternalBlockerReviewTests : IAsyncDisposable
{
    private const string WorkItemId = "spawn-defer-test";

    private readonly List<SqliteConnection> openConns = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var c in this.openConns) await c.DisposeAsync();
    }

    [Fact]
    public async Task EvaluatorBlock_WithNoPinsOrAnswers_CarriesTheRuleAlone()
    {
        await using var sp = await this.BuildServicesAsync();

        var block = await CrewStoryArtifactStrategy.BuildEvaluatorOperatorContextBlockAsync(
            sp.GetRequiredService<IServiceScopeFactory>(), WorkItemId, default);

        Assert.Contains("Blockers outside this stage are deferred, not defects", block);
        Assert.DoesNotContain("OPERATOR DECISION PINS", block);
        Assert.DoesNotContain(CrewStoryArtifactStrategy.EvaluatorAnswersHeading, block);
    }

    [Fact]
    public async Task EvaluatorBlock_CarriesPinsAndTheLatestAnswers()
    {
        await using var sp = await this.BuildServicesAsync();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // With no workstream row the pins lookup treats the work item id as the spawn id.
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = WorkItemId,
                StoryId = "story-defer",
                Phase = "implement",
                DecisionPinsJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new { workItemId = (string?)null, text = "Lane records are the gate evidence." },
                }),
            });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                ExternalId = "closure-wait", WorkItemId = WorkItemId, Iter = 4, Kind = "choice",
                Text = "What should this workstream do?", Answer = "Pause until M1 closes",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                ExternalId = "closure-wait", WorkItemId = WorkItemId, Iter = 5, Kind = "choice",
                Text = "What should this workstream do?", Answer = "Merge the partial record now",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                ExternalId = "unanswered", WorkItemId = WorkItemId, Iter = 5, Kind = "choice",
                Text = "Still open?", Answer = null, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var block = await CrewStoryArtifactStrategy.BuildEvaluatorOperatorContextBlockAsync(scopeFactory, WorkItemId, default);

        Assert.Contains("Lane records are the gate evidence.", block);
        Assert.Contains(CrewStoryArtifactStrategy.EvaluatorAnswersHeading, block);
        Assert.Contains("Merge the partial record now", block);
        Assert.DoesNotContain("Pause until M1 closes", block);
        Assert.DoesNotContain("Still open?", block);
        Assert.True(
            block.IndexOf("Merge the partial record now", StringComparison.Ordinal)
                < block.IndexOf("Blockers outside this stage", StringComparison.Ordinal),
            "the rule must follow the answers it refers to as 'above'");
    }

    [Fact]
    public void Convergence_IgnoresTheDeferredList()
    {
        var chain = Enumerable.Range(1, 5)
            .Select(i => (i, 79, "iterate", Notes(DistinctItem(i), deferredHeading: true)))
            .ToList();

        var (looping, recurring) = DeliverableLoop.DetectConvergenceLoop(chain);

        Assert.False(looping, "deferred items recur by design; they are not a convergence loop");
        Assert.Empty(recurring);
    }

    [Fact]
    public void Convergence_StillFiresWhenTheSameItemsSitUnderStillNeedsImprovement()
    {
        var chain = Enumerable.Range(1, 5)
            .Select(i => (i, 79, "iterate", Notes(DistinctItem(i), deferredHeading: false)))
            .ToList();

        var (looping, _) = DeliverableLoop.DetectConvergenceLoop(chain);

        Assert.True(looping);
    }

    [Fact]
    public void CheckboxGate_CountsOnlyTheStillNeedsCheckboxes()
    {
        Assert.Equal(1, DeliverableLoop.CountCheckboxMarkers(Notes(DistinctItem(1), deferredHeading: true), lookForUnchecked: true));
    }

    private static string DistinctItem(int i) => i switch
    {
        1 => "Synchronize the run README acceptance wording with the record's quoted operator answer.",
        2 => "Explain why the second-launch narrative omits the listener port reported by the log.",
        3 => "Correct the secret-set transcript path that points at a deleted scratch directory.",
        4 => "Replace the paraphrased owner confirmation with the verbatim answer and its date.",
        _ => "Add the missing commit hash beside the WSL lane evidence table heading row.",
    };

    private static string Notes(string stillNeeds, bool deferredHeading)
    {
        var deferred =
            "- Manual checks 16.3 and 16.4 wait for a stage that adds the Claude Code route to the release build.\n"
            + "- Advancing built-through to DH-S-16 waits for harness DSH-M1 closure and the seven guarded rows.\n";
        return "Improvements this iteration:\n- Read all changed files.\n\n"
            + "Still needs improvement:\n"
            + $"- [ ] 1. {stillNeeds}\n"
            + (deferredHeading ? "\nDeferred (outside this stage):\n" : string.Empty)
            + deferred
            + "\nWhy this score: the record is honest about what is open.";
    }

    private async Task<ServiceProvider> BuildServicesAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        this.openConns.Add(conn);

        var services = new ServiceCollection();
        services.AddDbContext<ControlDbContext>(opt => opt.UseSqlite(conn));
        services.AddDbContext<ProductDbContext>(opt => opt.UseSqlite(conn));
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Database.EnsureCreatedAsync();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        foreach (var stmt in db.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length == 0) continue;
            try { await db.Database.ExecuteSqlRawAsync(trimmed); }
            catch { /* tables Control already created */ }
        }

        return sp;
    }
}