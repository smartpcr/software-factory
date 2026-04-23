// -----------------------------------------------------------------------
// <copyright file="FileWorkflowStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Persistence;

using System;
using System.IO;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Auth;
using WorkflowUi.Server.Persistence;
using Xunit;

public class FileWorkflowStoreTests : IDisposable
{
    private readonly string tempDir;
    private readonly FileWorkflowStore store;
    private readonly InMemoryAuditLog auditLog;

    public FileWorkflowStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "wf-store-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);

        this.auditLog = new InMemoryAuditLog();
        var options = Options.Create(new PersistenceOptions { WorkflowsRoot = this.tempDir });
        this.store = new FileWorkflowStore(
            options,
            this.auditLog,
            NullLogger<FileWorkflowStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private static ClaimsPrincipal Editor()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user"),
            new Claim(ClaimTypes.Role, AuthPolicies.EditorRole),
            new Claim(ClaimTypes.Role, AuthPolicies.ViewerRole),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Viewer()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "view-user"),
            new Claim(ClaimTypes.Role, AuthPolicies.ViewerRole),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task Save_NewId_Creates_BothFiles()
    {
        var result = await this.store.SaveAsync("wf1", "yaml: true", "{}", null, Editor(), default);

        Assert.Equal(SaveResult.Created, result);
        Assert.True(File.Exists(Path.Combine(this.tempDir, "wf1.json")));
        Assert.True(File.Exists(Path.Combine(this.tempDir, "wf1.ui.json")));
    }

    [Fact]
    public async Task Save_ExistingId_WithMatchingEtag_Updates()
    {
        await this.store.SaveAsync("wf1", "yaml: v1", "{}", null, Editor(), default);
        var record = await this.store.GetAsync("wf1", Editor(), default);

        var result = await this.store.SaveAsync("wf1", "yaml: v2", "{}", record!.ETag, Editor(), default);

        Assert.Equal(SaveResult.Updated, result);
        var updated = await this.store.GetAsync("wf1", Editor(), default);
        Assert.Equal("yaml: v2", updated!.Definition);
    }

    [Fact]
    public async Task Save_ExistingId_WithStaleEtag_ReturnsPreconditionFailed()
    {
        await this.store.SaveAsync("wf1", "yaml: v1", "{}", null, Editor(), default);

        var result = await this.store.SaveAsync("wf1", "yaml: v2", "{}", "stale-etag", Editor(), default);

        Assert.Equal(SaveResult.PreconditionFailed, result);
    }

    [Fact]
    public async Task Get_MissingId_ReturnsNull()
    {
        var result = await this.store.GetAsync("nonexistent", Editor(), default);
        Assert.Null(result);
    }

    [Fact]
    public async Task Get_YamlPresent_SidecarMissing_ReturnsRecordWithEmptySidecar()
    {
        File.WriteAllText(Path.Combine(this.tempDir, "wf1.json"), "yaml: true");

        var result = await this.store.GetAsync("wf1", Editor(), default);

        Assert.NotNull(result);
        Assert.Equal("{}", result!.SidecarJson);
    }

    [Fact]
    public async Task Delete_WithMatchingEtag_RemovesBothFiles()
    {
        await this.store.SaveAsync("wf1", "yaml: true", "{}", null, Editor(), default);
        var record = await this.store.GetAsync("wf1", Editor(), default);

        var result = await this.store.DeleteAsync("wf1", record!.ETag, Editor(), default);

        Assert.True(result);
        Assert.False(File.Exists(Path.Combine(this.tempDir, "wf1.yaml")));
    }

    [Fact]
    public async Task Delete_WithStaleEtag_ReturnsFalse()
    {
        await this.store.SaveAsync("wf1", "yaml: true", "{}", null, Editor(), default);

        var result = await this.store.DeleteAsync("wf1", "stale-etag", Editor(), default);

        Assert.False(result);
    }

    [Fact]
    public async Task List_ReturnsOnlyYamlFiles()
    {
        await this.store.SaveAsync("wf1", "yaml: true", "{}", null, Editor(), default);
        await this.store.SaveAsync("wf2", "yaml: true", "{}", null, Editor(), default);
        File.WriteAllText(Path.Combine(this.tempDir, "readme.txt"), "ignore");

        var summaries = await this.store.ListAsync(Editor(), default);

        Assert.Equal(2, summaries.Count);
    }

    [Fact]
    public async Task PathTraversal_Rejected()
    {
        var result = await this.store.GetAsync("../etc/passwd", Editor(), default);
        Assert.Null(result);
    }

    [Fact]
    public async Task Save_RecordsAuditEntry()
    {
        await this.store.SaveAsync("wf1", "yaml: true", "{}", null, Editor(), default);

        var entries = this.auditLog.GetEntries();
        Assert.Single(entries);
        Assert.Equal("Save", entries[0].Action);
        Assert.Equal("wf1", entries[0].TargetPath);
    }

    [Fact]
    public async Task Viewer_Cannot_Save()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => this.store.SaveAsync("wf1", "yaml: true", "{}", null, Viewer(), default));
    }

    [Fact]
    public async Task Viewer_Can_List()
    {
        await this.store.SaveAsync("wf1", "yaml: true", "{}", null, Editor(), default);
        var summaries = await this.store.ListAsync(Viewer(), default);
        Assert.Single(summaries);
    }
}
