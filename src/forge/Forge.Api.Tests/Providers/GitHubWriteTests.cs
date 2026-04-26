// -----------------------------------------------------------------------
// <copyright file="GitHubWriteTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Net.Http;
using Forge.Api.Providers;
using Forge.Api.Providers.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>Phase 6 — GitHub write paths.</summary>
public class GitHubWriteTests
{
    private static (GitHubProvider Provider, StubHttpMessageHandler Stub) NewProvider()
    {
        var stub = new StubHttpMessageHandler();
        var factory = new SingleNamedHttpClientFactory(GitHubProvider.ClientName, stub);
        var opts = Options.Create(new GitHubOptions { Repo = "ms/forge", Pat = "server-pat" });
        return (new GitHubProvider(factory, opts, NullLogger<GitHubProvider>.Instance), stub);
    }

    [Fact]
    public async Task CreateWorkItem_Posts_Issue_With_Type_Label()
    {
        var (provider, stub) = NewProvider();
        stub.When(req => req.RequestUri!.AbsolutePath.EndsWith("/repos/ms/forge/issues") && req.Method == HttpMethod.Post,
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"number\":777,\"title\":\"crash\",\"state\":\"open\",\"labels\":[{\"name\":\"bug\"}]}",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        var card = await provider.CreateWorkItemAsync(
            new NewWorkItemRequest("crash", "bug"),
            ProviderCallContext.Anonymous);

        Assert.Equal("#777", card.Id);
        Assert.Equal("bug", card.Type);
    }

    [Fact]
    public async Task ApprovePullRequest_Posts_Reviews_With_APPROVE_Event()
    {
        var (provider, stub) = NewProvider();
        var seen = "";
        stub.When(req => req.RequestUri!.AbsolutePath.Contains("/pulls/41/reviews") && req.Method == HttpMethod.Post,
            req =>
            {
                seen = req.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                };
            });

        await provider.ApprovePullRequestAsync("PR #41", ProviderCallContext.Anonymous);
        Assert.Contains("\"event\":\"APPROVE\"", seen, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserPat_Overrides_Authorization_Header()
    {
        var (provider, stub) = NewProvider();
        stub.When(req => req.RequestUri!.AbsolutePath.EndsWith("/comments") && req.Method == HttpMethod.Post,
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent("{}"),
            });

        await provider.CommentOnPullRequestAsync("PR #1", "lgtm",
            new ProviderCallContext("user2", "user-pat-xyz", null));

        var sent = stub.Requests.Last();
        var auth = sent.Headers.Authorization!;
        Assert.Equal("token", auth.Scheme);
        Assert.Equal("user-pat-xyz", auth.Parameter);
    }
}
