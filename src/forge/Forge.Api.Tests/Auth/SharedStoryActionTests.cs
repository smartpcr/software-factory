namespace Forge.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class SharedStoryActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_And_Resume_Preserve_Phase_Child_Status_And_Release_Claims(bool viaChat)
    {
        await using var app = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("po", productId: "p");
        app.Link("po");
        using var client = await app.PairedClientAsync("po");
        client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");
        using (var seed = app.Services.CreateScope())
        {
            var control = seed.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.AgentClaims.Add(new AgentClaimRow { AgentId = "E1", WorkItemId = "ws-p" });
            db.TaskQueue.Add(new TaskQueueRow { WorkItemId = "ws-p", TargetId = "E1", State = "active" });
            db.WorkItems.Add(new WorkItemRow { Id = "ws-p", RepoId = "repo-p", ExecutionStatus = ExecutionStatuses.Live });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }
        foreach (var operation in new[] { "pause", "resume" })
        {
            if (viaChat)
            {
                using var actor = ForgeActorScope.Push(await app.ProviderActorAsync(), "p");
                using var product = ProductDbContextFactory.PushAsyncLocalSlug("alpha");
                using var scope = app.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<StoryOperatorActionService>();
                var result = operation == "pause" ? await service.PauseSpawnAsync("story-p", default) : await service.ResumeSpawnAsync("story-p", default);
                Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
            }
            else Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/stories/story-p/spawn/{operation}", new { })).StatusCode);
            using var verify = app.Services.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.SingleAsync(s => s.Id == "spawn-p");
            var ws = await db.Workstreams.SingleAsync(w => w.Id == "ws-p");
            Assert.Equal(operation == "pause" ? "paused" : "implement", spawn.Phase);
            Assert.Equal(operation == "pause" ? "paused" : "stalled", ws.Status);
            Assert.Equal("cancelled", (await db.TaskQueue.SingleAsync()).State);
            Assert.NotNull((await verify.ServiceProvider.GetRequiredService<ControlDbContext>().AgentClaims.SingleAsync()).ReleasedAt);
            Assert.Equal("implement", (await db.StorySpawns.SingleAsync(s => s.Id == "spawn-q")).Phase);
        }
    }

    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "accept")]
    [InlineData(true, "accept")]
    [InlineData(false, "retry")]
    [InlineData(true, "retry")]
    public async Task Artifact_Actions_Have_Equivalent_Domain_Results(bool viaChat, string operation)
    {
        await using var app = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("po", productId: "p");
        app.Link("po");
        const string doc = "plan-doc:spawn-p:architecture.md";
        using (var seed = app.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Deliverables.Add(new DeliverableRow { WorkItemId = doc, Iter = 3, Verdict = operation == "cancel" ? "iterate" : "stalled", Score = 72 });
            db.WorkItems.Add(new WorkItemRow { Id = doc, RepoId = "repo-p", ExecutionStatus = ExecutionStatuses.Live });
            var spawn = await db.StorySpawns.SingleAsync(s => s.Id == "spawn-p");
            spawn.ResumePendingJson = "[\"architecture\"]";
            await db.SaveChangesAsync();
        }
        if (viaChat)
        {
            using var actor = ForgeActorScope.Push(await app.ProviderActorAsync(), "p");
            using var product = ProductDbContextFactory.PushAsyncLocalSlug("alpha");
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<StoryOperatorActionService>();
            var result = operation switch
            {
                "cancel" => await service.CancelArtifactAsync("story-p", "architecture", default),
                "accept" => await service.AcceptArtifactAsync("story-p", "architecture", default),
                _ => await service.RetryArtifactAsync("story-p", "architecture", default),
            };
            Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        }
        else
        {
            using var client = await app.PairedClientAsync("po");
            client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");
            var response = await client.PostAsJsonAsync($"/api/stories/story-p/spawn/artifacts/architecture/{operation}", new { });
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        using var verify = app.Services.CreateScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.Equal(operation == "cancel" ? "stalled" : operation == "accept" ? "pass" : "iterate", (await dbVerify.Deliverables.SingleAsync()).Verdict);
        Assert.Equal(operation == "retry" ? ExecutionStatuses.Queued : ExecutionStatuses.Idle,
            (await dbVerify.WorkItems.SingleAsync(w => w.Id == doc)).ExecutionStatus);
    }
}
