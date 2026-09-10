// -----------------------------------------------------------------------
// <copyright file="OpenQuestionServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Forge.Api.Persistence;
using Forge.Api.Auth;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 95 stage 3.1 — direct tests of the extracted <see cref="IOpenQuestionService"/>.
/// The pre-existing HTTP question/resume endpoint tests (StalledQuestionsRecovery,
/// Phase91Explain, …) are the behaviour-preserving safety net; these assert the
/// service the chat inbound path will call drives the same resume shape.
/// </summary>
public class OpenQuestionServiceTests
{
    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        await f.SeedProductAsync();
        await f.EnrollAsync("operator", productId: "p");
        f.Link("operator");
        return f;
    }

    private static async Task SeedWorkstreamWithQuestionsAsync(
        ForgeWebApplicationFactory f, string id, string stallReason, params string[] externalIds)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.WorkItems.Add(new WorkItemRow { Id = id, RepoId = "repo-p", ExecutionStatus = ExecutionStatuses.AwaitingAnswers });
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = id,
            RepoId = "repo-p",
            Status = "stalled",
            StallReason = stallReason,
            StorySpawnId = null,
        });
        var iter = 1;
        foreach (var ext in externalIds)
        {
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = id,
                ExternalId = ext,
                Iter = iter++,
                Text = "Question " + ext,
                Kind = "open",
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnswerAsync_Last_Answer_Resumes_Workstream_And_Clears_Bogus_Stall()
    {
        await using var f = await BootAsync();
        const string Id = "ws-oq-svc-resume";
        await SeedWorkstreamWithQuestionsAsync(f, Id, "Sloppy-3-attempts: mis-counted", "q1");

        using var actor = ForgeActorScope.Push(await f.ProviderActorAsync(), "p");
        using var productScope = ProductDbContextFactory.PushAsyncLocalSlug("p");
        OpenQuestionAnswerResult result;
        using (var scope = f.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IOpenQuestionService>();
            result = await svc.AnswerAsync(Id, new[] { new OpenQuestionAnswer("q1", "go with A") }, "operator", default);
        }

        Assert.Equal(0, result.UnansweredCount);
        Assert.True(result.ResumeScheduled);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == Id);
            Assert.Equal("active", ws.Status);
            Assert.Null(ws.StallReason);
            var q = await db.DeliverableQuestions.AsNoTracking().SingleAsync(x => x.WorkItemId == Id);
            Assert.Equal("go with A", q.Answer);
            Assert.Equal("operator", q.AnsweredBy);
        }
    }

    [Fact]
    public async Task AnswerAsync_Partial_Answer_Leaves_Unanswered_No_Resume()
    {
        await using var f = await BootAsync();
        const string Id = "ws-oq-svc-partial";
        await SeedWorkstreamWithQuestionsAsync(f, Id, "Sloppy-3-attempts: mis-counted", "q1", "q2");

        using var actor = ForgeActorScope.Push(await f.ProviderActorAsync(), "p");
        using var productScope = ProductDbContextFactory.PushAsyncLocalSlug("p");
        OpenQuestionAnswerResult result;
        using (var scope = f.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IOpenQuestionService>();
            result = await svc.AnswerAsync(Id, new[] { new OpenQuestionAnswer("q1", "answered") }, "operator", default);
        }

        Assert.Equal(1, result.UnansweredCount);
        Assert.False(result.ResumeScheduled);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == Id);
            // Not all answered → stall left intact, no resume.
            Assert.Equal("stalled", ws.Status);
        }
    }

    [Fact]
    public async Task DismissAsync_All_Resumes_With_Sentinel_Answer()
    {
        await using var f = await BootAsync();
        const string Id = "ws-oq-svc-dismiss";
        await SeedWorkstreamWithQuestionsAsync(f, Id, "Sloppy-3-attempts: mis-counted", "q1");

        using var actor = ForgeActorScope.Push(await f.ProviderActorAsync(), "p");
        using var productScope = ProductDbContextFactory.PushAsyncLocalSlug("p");
        OpenQuestionAnswerResult result;
        using (var scope = f.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IOpenQuestionService>();
            result = await svc.DismissAsync(Id, new[] { "q1" }, "operator", default);
        }

        Assert.Equal(0, result.UnansweredCount);
        Assert.True(result.ResumeScheduled);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var q = await db.DeliverableQuestions.AsNoTracking().SingleAsync(x => x.WorkItemId == Id);
            Assert.Equal("(dismissed by operator)", q.Answer);
        }
    }
}
