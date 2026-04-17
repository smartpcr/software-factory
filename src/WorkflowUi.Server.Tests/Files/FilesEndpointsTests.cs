// -----------------------------------------------------------------------
// <copyright file="FilesEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Files;

using System.Net;
using System.Net.Http.Json;
using WorkflowUi.Server.Endpoints;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Integration tests for /api/files workspace endpoints.
/// </summary>
public class FilesEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient client;

    public FilesEndpointsTests(TestWebApplicationFactory factory)
    {
        this.client = factory.CreateClient();
    }

    [Fact]
    public async Task GetTree_Returns200()
    {
        var response = await this.client.GetAsync("/api/files");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateWorkflow_Returns201()
    {
        var request = new CreateWorkflowRequest("new-wf-" + Guid.NewGuid().ToString("N")[..8]);
        var response = await this.client.PostAsJsonAsync("/api/files", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Rename_NonExistent_Returns404()
    {
        var request = new RenameWorkflowRequest("target-name");
        var response = await this.client.PatchAsJsonAsync("/api/files/nonexistent-rename-12345", request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_NonExistent_Returns404()
    {
        var response = await this.client.DeleteAsync("/api/files/nonexistent-delete-12345");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_NonExistent_Returns404()
    {
        var response = await this.client.PostAsync("/api/files/duplicate/nonexistent-dup-12345", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
