// -----------------------------------------------------------------------
// <copyright file="PrsEndpointsMergeConflictTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Regression tests for the merge endpoint conflict signalling fixed when
/// a real GitHub merge conflict was leaking out as a generic 502 + raw
/// "GitHub PUT pulls/3/merge → 405 Method Not Allowed. Body: ..." string.
/// The endpoint now consistently emits 409 with
/// <c>{conflict: true, resolution: "force-available" | "manual-required"}</c>
/// so the UI can branch deterministically.
/// </summary>
public class PrsEndpointsMergeConflictTests
{
    private const string GitHubConflictBody =
        "GitHub PUT pulls/3/merge → 405 Method Not Allowed. " +
        "Body: {\"message\":\"Pull Request has merge conflicts\"," +
        "\"documentation_url\":\"https://docs.github.com/rest/pulls/pulls#merge-a-pull-request\"," +
        "\"status\":\"405\"}";

    [Fact]
    public async Task Merge_With_AutoMerge_Off_And_Provider_Conflict_Returns_409_Force_Available()
    {
        // Arrange — PR row with AutoMerge=false and no parent story so
        // effectiveForce resolves to false. A fake provider throws a
        // GitHub-style HttpRequestException whose message contains
        // "merge conflicts" so PrMergeUtilities.LooksLikeConflict matches.
        await using var factory = new MergeStubFactory();
        const string prId = "PR smartpcr/forge-mc#3";
        await SeedPrAsync(factory, prId, autoMerge: false, branch: null, baseBranch: null);

        using var client = WriterClient(factory);

        // Act
        var resp = await client.PostAsync(
            "/api/prs/" + Uri.EscapeDataString(prId) + "/merge", content: null);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("conflict").GetBoolean());
        Assert.Equal("force-available", doc.RootElement.GetProperty("resolution").GetString());
        // The endpoint surfaces the provider's error in `detail` so the UI
        // can show the operator the underlying GitHub message; `hint` is
        // the static next-step guidance ("retry with ?force=true").
        var detail = doc.RootElement.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        Assert.Contains("merge conflicts", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Merge_With_Force_True_And_Persistent_Provider_Conflict_Returns_409_Manual_Required()
    {
        // Arrange — explicit ?force=true so effectiveForce=true. The fake
        // provider throws on every MergePullRequestAsync call so the retry
        // path also fails. The endpoint must convert the retry conflict
        // exception into 409 + resolution=manual-required (NOT 502).
        await using var factory = new MergeStubFactory();
        const string prId = "PR smartpcr/forge-mc#4";
        await SeedPrAsync(factory, prId, autoMerge: false, branch: null, baseBranch: null);

        using var client = WriterClient(factory);

        // Act
        var resp = await client.PostAsync(
            "/api/prs/" + Uri.EscapeDataString(prId) + "/merge?force=true", content: null);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("conflict").GetBoolean());
        Assert.Equal("manual-required", doc.RootElement.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task Merge_With_Force_True_And_Provider_Non_Conflict_Error_Returns_502()
    {
        // Arrange — make the provider throw a non-conflict exception
        // (e.g., network error). The endpoint should still return 502 in
        // that case so the conflict-specific 409 path doesn't swallow
        // unrelated provider failures.
        await using var factory = new MergeStubFactory(throwNonConflict: true);
        const string prId = "PR smartpcr/forge-mc#5";
        await SeedPrAsync(factory, prId, autoMerge: false, branch: null, baseBranch: null);

        using var client = WriterClient(factory);

        // Act
        var resp = await client.PostAsync(
            "/api/prs/" + Uri.EscapeDataString(prId) + "/merge?force=true", content: null);

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    private static HttpClient WriterClient(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return c;
    }

    private static async Task SeedPrAsync(
        ForgeWebApplicationFactory factory,
        string prId,
        bool autoMerge,
        string? branch,
        string? baseBranch)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (await db.Prs.AnyAsync(p => p.Id == prId)) return;
        db.Prs.Add(new PrRow
        {
            Id = prId,
            Col = "review",
            Provider = "github",
            ProviderRef = "3",
            Epic = string.Empty,
            EpicColor = string.Empty,
            Workstream = "ws-merge-conflict-fixture",
            Base = baseBranch ?? string.Empty,
            Branch = branch ?? string.Empty,
            Title = "merge conflict fixture",
            Additions = 1,
            Deletions = 0,
            Files = 1,
            Commits = 1,
            Age = "1m",
            AutoMerge = autoMerge,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Custom factory that swaps the real Ado/GitHub providers for a fake
    /// that throws conflict-shaped <see cref="HttpRequestException"/> on
    /// MergePullRequestAsync. Mirrors the GitHubProvider's actual error
    /// formatting so PrMergeUtilities.LooksLikeConflict matches.
    /// </summary>
    private sealed class MergeStubFactory : ForgeWebApplicationFactory
    {
        private readonly bool throwNonConflict;

        public MergeStubFactory(bool throwNonConflict = false)
        {
            this.throwNonConflict = throwNonConflict;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IForgeProvider>();
                s.AddSingleton<IForgeProvider>(_ => new FakeMergeProvider(this.throwNonConflict));
            });
        }
    }

    private sealed class FakeMergeProvider : IForgeProvider
    {
        private readonly bool throwNonConflict;

        public FakeMergeProvider(bool throwNonConflict)
        {
            this.throwNonConflict = throwNonConflict;
        }

        public string Kind => "github";

        public bool IsConfigured => true;

        public Task<Pr> MergePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default)
        {
            if (this.throwNonConflict)
            {
                throw new HttpRequestException(
                    "GitHub PUT pulls/3/merge → 500 Internal Server Error. Body: server fell over",
                    inner: null,
                    statusCode: HttpStatusCode.InternalServerError);
            }
            throw new HttpRequestException(
                GitHubConflictBody,
                inner: null,
                statusCode: HttpStatusCode.MethodNotAllowed);
        }

        // The merge endpoint kicks off PrSyncJob best-effort; failures are
        // swallowed. Stubbing every method to a benign default keeps the
        // sync path silent so the test focuses on the merge contract.
        public Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Card>>(Array.Empty<Card>());

        public Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<Card?>(null);

        public Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Pr>>(Array.Empty<Pr>());

        public Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<Pr?>(null);

        public Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default) =>
            Task.FromResult<BuildStatus?>(null);

        public Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProviderHealth(this.Kind, true, "fake", null, 0, null, null));

        public Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ClosePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.FromResult<BuildStatus?>(null);

        public Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.FromResult<BuildStatus?>(null);

        public Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoverableRepo>>(Array.Empty<DiscoverableRepo>());

        public Task<IReadOnlyList<WorkItem>> ListWorkItemsForSyncAsync(WorkItemSyncFilters filters, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());

        public Task<WorkItem?> GetWorkItemForSyncAsync(string providerRef, CancellationToken ct = default) =>
            Task.FromResult<WorkItem?>(null);
    }
}
