// -----------------------------------------------------------------------
// <copyright file="AdoWriteTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Net.Http;
using Forge.Api.Providers;
using Forge.Api.Providers.Ado;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>Phase 6.1/6.2/6.4/6.5 — ADO write paths against a stub HTTP server.</summary>
public class AdoWriteTests
{
    private static (AdoProvider Provider, StubHttpMessageHandler Stub) NewProvider()
    {
        var stub = new StubHttpMessageHandler();
        var factory = new SingleNamedHttpClientFactory(AdoProvider.ClientName, stub);
        var opts = Options.Create(new AdoOptions
        {
            OrgUrl = "https://dev.azure.test/contoso",
            Project = "Atlas",
            Repository = "atlas-platform",
            Pat = "pat",
        });
        return (new AdoProvider(factory, opts, NullLogger<AdoProvider>.Instance, Array.Empty<Forge.Api.Auth.IAuthService>()), stub);
    }

    [Fact]
    public async Task CreateWorkItem_Uses_JsonPatch_And_Returns_Mapped_Card()
    {
        var (provider, stub) = NewProvider();
        stub.When(req => req.RequestUri!.AbsolutePath.Contains("/_apis/wit/workitems/$Bug"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":501,\"fields\":{\"System.Title\":\"crash\",\"System.WorkItemType\":\"Bug\",\"System.State\":\"Active\"}}",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        var card = await provider.CreateWorkItemAsync(
            new NewWorkItemRequest("crash", "Bug", AreaPath: "Atlas\\Auth", Tags: new[] { "auth" }),
            ProviderCallContext.Anonymous);

        Assert.Equal("#501", card.Id);
        Assert.Equal("build", card.Col);
        var sent = stub.Requests.Last();
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/json-patch+json", sent.Content!.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CreatePullRequest_Posts_RefHeads_And_Returns_Mapped_Pr()
    {
        var (provider, stub) = NewProvider();
        stub.When(req => req.RequestUri!.AbsolutePath.Contains("/pullrequests") && req.Method == HttpMethod.Post,
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"pullRequestId\":77,\"title\":\"ship it\",\"sourceRefName\":\"refs/heads/ws/x\"," +
                    "\"targetRefName\":\"refs/heads/main\",\"status\":\"active\"}",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        var pr = await provider.CreatePullRequestAsync(
            new NewPullRequestRequest("ws/x", "main", "ship it", "body"),
            ProviderCallContext.Anonymous);

        Assert.Equal("PR #77", pr.Id);
        Assert.Equal("ws/x", pr.Branch);
        Assert.Equal("main", pr.Base);
    }

    [Fact]
    public async Task UserPat_Overrides_Server_Pat_On_Write()
    {
        var (provider, stub) = NewProvider();
        stub.When(req => req.RequestUri!.AbsolutePath.Contains("/_apis/wit/workitems/$Bug"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":1,\"fields\":{\"System.Title\":\"x\",\"System.WorkItemType\":\"Bug\",\"System.State\":\"Active\"}}",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        await provider.CreateWorkItemAsync(
            new NewWorkItemRequest("x", "Bug"),
            new ProviderCallContext("user1", "user-pat", null));

        // ADO basic auth: Authorization: Basic <base64(":<pat>")>
        var sent = stub.Requests.Last();
        var auth = sent.Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        var decoded = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal(":user-pat", decoded);
    }
}

/// <summary>Returns a single named HttpClient backed by the supplied handler.</summary>
public sealed class SingleNamedHttpClientFactory : IHttpClientFactory
{
    private readonly string name;
    private readonly HttpMessageHandler handler;
    public SingleNamedHttpClientFactory(string name, HttpMessageHandler handler) { this.name = name; this.handler = handler; }
    public HttpClient CreateClient(string name) => new(this.handler, disposeHandler: false);
}
