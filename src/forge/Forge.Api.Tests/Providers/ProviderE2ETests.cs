// -----------------------------------------------------------------------
// <copyright file="ProviderE2ETests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Providers;
using Forge.Api.Providers.Ado;
using Forge.Api.Providers.GitHub;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Phase 5.12 — boots the API with the GitHub provider configured against
/// a stub HttpMessageHandler, runs one sync, and asserts cards + PRs land
/// in the cache + GET /api/cards exposes them.
/// </summary>
public class ProviderE2ETests : IClassFixture<ProviderE2ETests.Factory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Factory factory;

    public ProviderE2ETests(Factory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GitHub_Sync_Imports_Cards_And_Prs()
    {
        // Force the host to wire up so all services are available.
        using var bootClient = this.factory.CreateClient();
        _ = await bootClient.GetAsync("/api/health");

        // Sanity: provider should be configured (PAT + repo set in the factory).
        using (var scope = this.factory.Services.CreateScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<IForgeProvider>>();
            var gh = providers.First(p => p.Kind == "github");
            Assert.True(gh.IsConfigured, "github provider should be configured by the test factory");

            // Diagnostic: probe the named HttpClient directly to confirm the stub is wired.
            var hcf = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            using var probe = hcf.CreateClient(GitHubProvider.ClientName);
            probe.BaseAddress = new Uri("https://api.github.com/");
            // Sanity: provider can list issues against the stub handler.
            var issues = await gh.ListWorkItemsAsync();
            Assert.NotEmpty(issues);

            var prs = await gh.ListPullRequestsAsync();
            Assert.NotEmpty(prs);

            // Run one sync round so ProviderCache upserts into the DB.
            var sync = scope.ServiceProvider.GetRequiredService<ProviderSyncService>();
            await sync.RunOneAsync(gh, default);
        }

        var cardsResp = await bootClient.GetAsync("/api/cards");
        var prsResp = await bootClient.GetAsync("/api/prs");
        var cards = JsonDocument.Parse(await cardsResp.Content.ReadAsStringAsync());
        var prsJson = JsonDocument.Parse(await prsResp.Content.ReadAsStringAsync());

        var cardIds = new List<string>();
        foreach (var c in cards.RootElement.EnumerateArray())
        {
            cardIds.Add(c.GetProperty("id").GetString()!);
        }
        var prIds = new List<string>();
        foreach (var p in prsJson.RootElement.EnumerateArray())
        {
            prIds.Add(p.GetProperty("id").GetString()!);
        }

        Assert.Contains("#42", cardIds);
        Assert.Contains("PR ms/forge#99", prIds);
    }

    [Fact]
    public async Task TestProvider_Endpoint_Returns_Health()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/providers/github/test");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("github", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(),
            "stub server returns 200 for /repos/{owner}/{repo} so the probe should be ok");
    }

    [Fact]
    public async Task GitHub_Webhook_Bad_Signature_Returns_401()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));
        client.DefaultRequestHeaders.Add("X-GitHub-Event", "pull_request");
        var resp = await client.PostAsJsonAsync("/api/webhooks/github", new { action = "opened" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    /// <summary>
    /// Custom factory that replaces the GitHub HttpClient handler with a
    /// stub serving canned responses, so the e2e test runs offline.
    /// </summary>
    public sealed class Factory : ForgeWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Forge:GitHub:Repo"] = "ms/forge",
                    ["Forge:GitHub:Pat"] = "test-pat",
                    ["Forge:GitHub:WebhookSecret"] = "shhh",
                    ["Forge:Sync:Enabled"] = "false", // we drive the sync from the test
                });
            });
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(GitHubProvider.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(_ =>
                    {
                        Console.Error.WriteLine("[ProviderE2ETests] building stub primary handler");
                        return CreateStub();
                    });
                services.AddHttpClient(AdoProvider.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(_ => new StubHttpMessageHandler());
            });
        }

        private static StubHttpMessageHandler CreateStub()
        {
            var stub = new StubHttpMessageHandler();
            // Order matters — most-specific routes first.
            stub.When("/repos/ms/forge/issues",
                "[" +
                "{\"number\":42,\"title\":\"Local cache hit ratio\",\"state\":\"open\",\"labels\":[{\"name\":\"task\"}]}," +
                "{\"number\":99,\"title\":\"WIP\",\"state\":\"open\",\"labels\":[],\"pull_request\":{}}" +
                "]");
            stub.When("/repos/ms/forge/pulls",
                "[" +
                "{\"number\":99,\"title\":\"Add cache layer\",\"state\":\"open\",\"draft\":false," +
                "\"head\":{\"ref\":\"ws/cache\"},\"base\":{\"ref\":\"main\"}," +
                "\"user\":{\"login\":\"xyz\"}," +
                "\"additions\":12,\"deletions\":3,\"changed_files\":2,\"commits\":1,\"labels\":[]}" +
                "]");
            // Health probe — repo root. Use exact-match predicate so it doesn't shadow /issues or /pulls.
            stub.When(
                req => req.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith("/repos/ms/forge", StringComparison.Ordinal),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{ \"name\": \"forge\" }", System.Text.Encoding.UTF8, "application/json"),
                });
            return stub;
        }
    }
}
