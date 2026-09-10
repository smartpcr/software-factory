namespace Forge.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed record CapabilityProbe(string Capability, ForgeActionTarget Target);

/// <summary>A test-only consumer of the real common boundary, behind real cookies, origins and CSRF.</summary>
public sealed class CapabilityProbeStartup : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Map("/api/operator/capability-probe", branch =>
        {
            branch.UseMiddleware<OperatorOriginMiddleware>();
            branch.UseAuthentication();
            branch.UseMiddleware<OperatorRequestMiddleware>();
            branch.Run(async ctx =>
            {
                var request = (await ctx.Request.ReadFromJsonAsync<CapabilityProbe>())!;
                ctx.Items[ProductDbContextFactory.SlugItemKey] = "alpha";
                var action = ctx.RequestServices.GetRequiredService<ForgeActionContext>();
                await action.RequireAsync(request.Capability, request.Target);
                ctx.Response.StatusCode = 204;
            });
        });
        next(app);
    };
}

public sealed class CapabilityParityTests
{
    internal static async Task SeedTargetsAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        foreach (var (id, slug) in new[] { ("p", "alpha"), ("q", "beta") })
        {
            control.Products.Add(new ProductRow { Id = id, Slug = slug, Name = slug });
            control.RepoRegistry.Add(new RepoRegistryRow { Id = "repo-" + id, ProductId = id, ProductSlug = slug, CloneRoot = Path.Combine(Path.GetTempPath(), "forge-no-clone-" + id), RemoteUrl = "https://example.invalid/" + id });
            db.Repos.Add(new RepoRow { Id = "repo-" + id, ProductId = id, CloneRoot = Path.Combine(Path.GetTempPath(), "forge-no-clone-" + id), RemoteUrl = "https://example.invalid/" + id });
            db.StorySpawns.Add(new StorySpawnRow { Id = "spawn-" + id, StoryId = "story-" + id, RepoId = "repo-" + id, Phase = "implement" });
            db.Workstreams.Add(new WorkstreamRow { Id = "ws-" + id, StorySpawnId = "spawn-" + id, RepoId = "repo-" + id, Status = "stalled" });
            db.WorkItems.Add(new WorkItemRow { Id = "story-" + id, RepoId = "repo-" + id, Type = "story" });
        }
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
    }

    private static ForgeActionTarget Target(string capability) => capability switch
    {
        ForgeCapabilities.ConnectionManage or ForgeCapabilities.HostAlertManage or ForgeCapabilities.IdentityManage or ForgeCapabilities.LinkSelf => new(),
        ForgeCapabilities.SpawnRead or ForgeCapabilities.SpawnPause or ForgeCapabilities.SpawnResume or ForgeCapabilities.SubscriptionManage => new("p", SpawnId: "spawn-p"),
        ForgeCapabilities.QuestionAnswer => new("p", SpawnId: "spawn-p", WorkItemId: "ws-p"),
        _ when capability.StartsWith("artifact.") => new("p", SpawnId: "spawn-p", ArtifactKey: "architecture"),
        _ => new("p", SpawnId: "spawn-p", WorkstreamId: "ws-p"),
    };

    [Fact]
    public async Task Every_Capability_Has_Identical_Cookie_And_Provider_Authority_For_Every_Fixed_Role()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, CapabilityProbeStartup>()));
        await SeedTargetsAsync(app);
        var roles = ForgeCapabilities.Roles.Cast<string?>().Concat(new string?[] { null, "invented" }).ToArray();
        foreach (var role in roles)
        foreach (var admin in new[] { false, true })
        {
            var userId = (role ?? "missing") + admin;
            await app.EnrollAsync(userId, admin, role is null ? null : "p", role ?? "human");
            using var client = await app.PairedClientAsync(userId);
            app.Link(userId);
            using var scope = app.Services.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<ForgeActorResolver>();
            var actor = await resolver.FromProviderAsync("telegram", "123", "123");
            Assert.NotNull(actor);
            using var productScope = ProductDbContextFactory.PushAsyncLocalSlug("alpha");
            using var actorScope = ForgeActorScope.Push(actor, "p");
            var action = scope.ServiceProvider.GetRequiredService<ForgeActionContext>();
            foreach (var capability in ForgeCapabilities.All.Append("invented.capability"))
            {
                var target = Target(capability);
                var expected = capability switch
                {
                    ForgeCapabilities.ConnectionManage or ForgeCapabilities.HostAlertManage or ForgeCapabilities.IdentityManage => admin,
                    ForgeCapabilities.LinkSelf => true,
                    ForgeCapabilities.SpawnRead => role is not null && ForgeCapabilities.Roles.Contains(role),
                    "invented.capability" => false,
                    _ => role == "product-owner",
                };
                using var response = await client.PostAsJsonAsync("/api/operator/capability-probe", new CapabilityProbe(capability, target));
                Assert.True(response.StatusCode == (expected ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden),
                    $"HTTP {capability}, role={role}, admin={admin}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                var denied = await Record.ExceptionAsync(() => action.RequireAsync(capability, target));
                if (expected) Assert.Null(denied); else Assert.IsType<ForgeAccessDeniedException>(denied);
            }
        }
    }

    [Fact]
    public async Task Unresolved_Cross_Product_And_Conflicting_Targets_Fail_For_Web_And_Chat()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, CapabilityProbeStartup>()));
        await SeedTargetsAsync(app);
        await app.EnrollAsync("po", productId: "p");
        using var client = await app.PairedClientAsync("po");
        app.Link("po");
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ForgeActorResolver>();
        var actor = await resolver.FromProviderAsync("telegram", "123", "123");
        using var productScope = ProductDbContextFactory.PushAsyncLocalSlug("alpha");
        using var actorScope = ForgeActorScope.Push(actor!, "p");
        var action = scope.ServiceProvider.GetRequiredService<ForgeActionContext>();
        foreach (var capability in ForgeCapabilities.All)
        {
            var target = Target(capability);
            var targets = new[] { target with { ProductId = "all" }, target with { ProductId = "missing" }, target with { ProductId = "q" },
                target with { ProductId = "p", SpawnId = "missing" }, target with { ProductId = "p", SpawnId = "spawn-q" },
                target with { ProductId = "p", WorkstreamId = "ws-q" }, target with { ProductId = "p", WorkItemId = "ws-q" },
                target with { ProductId = "p", ArtifactKey = "unknown" } };
            foreach (var invalid in targets)
            {
                using var response = await client.PostAsJsonAsync("/api/operator/capability-probe", new CapabilityProbe(capability, invalid));
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => action.RequireAsync(capability, invalid));
            }
        }
        app.Services.GetRequiredService<HostIdentityStore>().RevokeLinks("po");
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => action.RequireAsync("workstream.cancel", Target("workstream.cancel")));
        Assert.Null(await resolver.FromProviderAsync("telegram", "123", "123"));
        // Product-role revocation takes effect even with the existing browser cookie.
        await scope.ServiceProvider.GetRequiredService<ControlDbContext>().UserProductRoles.ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/operator/capability-probe",
            new CapabilityProbe("workstream.cancel", Target("workstream.cancel")))).StatusCode);
    }

    [Fact]
    public async Task Every_Shared_Action_Denies_Without_An_Actor_Before_Domain_Side_Effects()
    {
        await using var app = new ForgeWebApplicationFactory();
        await SeedTargetsAsync(app);
        using var scope = app.Services.CreateScope();
        var ws = scope.ServiceProvider.GetRequiredService<IOperatorActionService>();
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => ws.CancelAsync("ws-p", default));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => ws.AcceptAsync("ws-p", "forged", default));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => ws.RetryAsync("ws-p", "forged", default));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => ws.RestartAsync("ws-p", default));
        var questions = scope.ServiceProvider.GetRequiredService<IOpenQuestionService>();
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => questions.AnswerAsync("ws-p", [], "forged", default));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => questions.DismissAsync("ws-p", [], "forged", default));
        var story = scope.ServiceProvider.GetRequiredService<StoryOperatorActionService>();
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => story.PauseSpawnAsync("story-p", default));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => story.ResumeSpawnAsync("story-p", default));
        foreach (var invoke in new Func<Task<IResult>>[] { () => story.ResumeArtifactAsync("story-p", "architecture", default),
            () => story.RetryArtifactAsync("story-p", "architecture", default), () => story.AcceptArtifactAsync("story-p", "architecture", default),
            () => story.CancelArtifactAsync("story-p", "architecture", default), () => story.RestartArtifactAsync("story-p", "architecture", default) })
            await Assert.ThrowsAsync<ForgeAccessDeniedException>(invoke);
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.All(await db.Workstreams.ToListAsync(), row => Assert.Equal("stalled", row.Status));
        Assert.False(await db.TaskQueue.AnyAsync());
    }
}
