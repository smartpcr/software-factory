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
        var response = await this.client.PostAsJsonAsync(
            "/api/files/duplicate",
            new DuplicateRequest("nonexistent-dup-12345", null));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_ExistingWorkflow_Returns201()
    {
        var srcId = "dup-src-" + Guid.NewGuid().ToString("N")[..8];
        await this.client.PostAsJsonAsync("/api/files", new CreateWorkflowRequest(srcId));

        var response = await this.client.PostAsJsonAsync(
            "/api/files/duplicate",
            new DuplicateRequest(srcId, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_WithExplicitTargetId_Uses_TargetId()
    {
        var srcId = "dup-src2-" + Guid.NewGuid().ToString("N")[..8];
        var targetId = "explicit-target-" + Guid.NewGuid().ToString("N")[..8];
        await this.client.PostAsJsonAsync("/api/files", new CreateWorkflowRequest(srcId));

        var response = await this.client.PostAsJsonAsync(
            "/api/files/duplicate",
            new DuplicateRequest(srcId, targetId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var getResp = await this.client.GetAsync($"/api/workflows/{targetId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
    }

    [Fact]
    public async Task ExportImport_RoundTrip()
    {
        var wfId = "exp-" + Guid.NewGuid().ToString("N")[..8];
        await this.client.PostAsJsonAsync("/api/files", new CreateWorkflowRequest(wfId));

        // Export
        var exportResp = await this.client.PostAsJsonAsync(
            "/api/files/export",
            new ExportRequest(new[] { wfId }));
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);

        var zipBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.True(zipBytes.Length > 0);

        // Import
        var importContent = new ByteArrayContent(zipBytes);
        importContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var importResp = await this.client.PostAsync("/api/files/import", importContent);
        Assert.Equal(HttpStatusCode.OK, importResp.StatusCode);
    }

    [Fact]
    public async Task NestedId_CreateAndDelete_Works()
    {
        var nestedId = "folder/sub/wf-" + Guid.NewGuid().ToString("N")[..8];
        var createResp = await this.client.PostAsJsonAsync("/api/files", new CreateWorkflowRequest(nestedId));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        // Verify via GET on workflow endpoint (also catch-all)
        var getResp = await this.client.GetAsync($"/api/workflows/{nestedId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        // Delete via files endpoint
        var deleteResp = await this.client.DeleteAsync($"/api/files/{nestedId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        // Confirm deleted
        var getAfter = await this.client.GetAsync($"/api/workflows/{nestedId}");
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    [Fact]
    public async Task NestedId_Rename_Works()
    {
        var srcId = "folder/rename-src-" + Guid.NewGuid().ToString("N")[..8];
        await this.client.PostAsJsonAsync("/api/files", new CreateWorkflowRequest(srcId));

        var newId = "folder/rename-tgt-" + Guid.NewGuid().ToString("N")[..8];
        var renameResp = await this.client.PatchAsJsonAsync(
            $"/api/files/{srcId}",
            new RenameWorkflowRequest(newId));
        Assert.Equal(HttpStatusCode.OK, renameResp.StatusCode);

        // Old id should be gone
        var oldGet = await this.client.GetAsync($"/api/workflows/{srcId}");
        Assert.Equal(HttpStatusCode.NotFound, oldGet.StatusCode);

        // New id should exist
        var newGet = await this.client.GetAsync($"/api/workflows/{newId}");
        Assert.Equal(HttpStatusCode.OK, newGet.StatusCode);
    }

    [Fact]
    public async Task NestedId_Duplicate_Works()
    {
        var srcId = "folder/dup-nested-" + Guid.NewGuid().ToString("N")[..8];
        await this.client.PostAsJsonAsync("/api/files", new CreateWorkflowRequest(srcId));

        var response = await this.client.PostAsJsonAsync(
            "/api/files/duplicate",
            new DuplicateRequest(srcId, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
