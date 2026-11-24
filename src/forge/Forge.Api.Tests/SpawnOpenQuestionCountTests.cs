// -----------------------------------------------------------------------
// <copyright file="SpawnOpenQuestionCountTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The story index shows a per-story count of unanswered questions. It must
/// cover the plan docs and the workstreams, count each question once (latest
/// iteration wins) and skip workstreams nothing will resume.
/// </summary>
public class SpawnOpenQuestionCountTests
{
    [Fact]
    public async Task Counts_unanswered_plan_doc_and_workstream_questions_per_spawn()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot host + migrations */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            foreach (var id in new[] { "spawn-oq-a", "spawn-oq-b" })
            {
                db.StorySpawns.Add(new StorySpawnRow
                {
                    Id = id, StoryId = "WI-" + id, StoryTitle = id, Phase = StorySpawnPhases.Implement, BaseBranch = "main",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
            }

            db.Workstreams.Add(new WorkstreamRow { Id = "ws-oq-a", Name = "a", Branch = "ws/a", Base = "main", Epic = "a", Status = "active", StorySpawnId = "spawn-oq-a" });
            db.Workstreams.Add(new WorkstreamRow { Id = "ws-oq-a-gone", Name = "gone", Branch = "ws/g", Base = "main", Epic = "a", Status = "cancelled", StorySpawnId = "spawn-oq-a" });
            db.DeliverableQuestions.AddRange(
                Q("plan-doc:spawn-oq-a:architecture.md", "q1", 1, null),
                Q("plan-doc:spawn-oq-a:architecture.md", "q2", 1, "yes"),
                // Asked again and answered on iteration 2: no longer open.
                Q("plan-doc:spawn-oq-a:tech-spec.md", "q1", 1, null),
                Q("plan-doc:spawn-oq-a:tech-spec.md", "q1", 2, "yes"),
                Q("ws-oq-a", "q1", 1, null),
                Q("ws-oq-a-gone", "q1", 1, null),
                Q("plan-doc:spawn-oq-b:architecture.md", "q1", 1, null),
                Q("plan-doc:spawn-oq-other:architecture.md", "q1", 1, null));
            await db.SaveChangesAsync();
        }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var counts = await StoriesEndpoints.CountUnansweredQuestionsBySpawnAsync(db, new[] { "spawn-oq-a", "spawn-oq-b" }, default);

            Assert.Equal(2, counts["spawn-oq-a"]);
            Assert.Equal(1, counts["spawn-oq-b"]);
            Assert.False(counts.ContainsKey("spawn-oq-other"));
        }
    }

    private static DeliverableQuestionRow Q(string workItemId, string externalId, int iter, string? answer) => new()
    {
        WorkItemId = workItemId,
        ExternalId = externalId,
        Iter = iter,
        Text = externalId,
        Kind = "choice",
        Answer = answer,
    };
}