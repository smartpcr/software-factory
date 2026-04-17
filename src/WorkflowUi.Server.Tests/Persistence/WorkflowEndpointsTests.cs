// -----------------------------------------------------------------------
// <copyright file="WorkflowEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Persistence;

using System.Net;
using System.Net.Http.Json;
using WorkflowUi.Server.Endpoints;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Integration tests for /api/workflows CRUD endpoints.
/// </summary>
public class WorkflowEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient client;

    public WorkflowEndpointsTests(TestWebApplicationFactory factory)
    {
        this.client = factory.CreateClient();
    }

    [Fact]
    public async Task List_Returns200()
    {
        var response = await this.client.GetAsync("/api/workflows");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_NonExistent_Returns404()
    {
        var response = await this.client.GetAsync("/api/workflows/nonexistent-wf-id-12345");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_CreatesWorkflow_Returns201()
    {
        var uniqueId = "test-put-" + Guid.NewGuid().ToString("N")[..8];
        var request = new SaveWorkflowRequest($"id: {uniqueId}\nname: Test\nversion: '1.0'\nnodes: []\nconnections: []\n", "{}");
        var response = await this.client.PutAsJsonAsync($"/api/workflows/{uniqueId}", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Delete_NonExistent_Returns404()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/workflows/nonexistent-delete-12345");
        request.Headers.TryAddWithoutValidation("If-Match", "\"fake-etag\"");
        var response = await this.client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutIfMatch_Returns400()
    {
        var response = await this.client.DeleteAsync("/api/workflows/some-id");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
