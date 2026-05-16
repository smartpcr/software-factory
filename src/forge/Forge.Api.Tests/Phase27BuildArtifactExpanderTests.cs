// -----------------------------------------------------------------------
// <copyright file="Phase27BuildArtifactExpanderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.IO.Compression;
using FluentAssertions;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 27 Q27.5 — verifies the artifact expander's
/// zip-slip guard, log-extension classification, and persistence behavior.
/// The HTTP fetch path is exercised via a stub artifact server hosted in
/// the test process so we don't depend on a real ADO / GitHub backend.
/// </summary>
public class Phase27BuildArtifactExpanderTests : IDisposable
{
    private readonly string testRoot;
    private readonly string previousEnv;

    public Phase27BuildArtifactExpanderTests()
    {
        this.testRoot = Path.Combine(Path.GetTempPath(), "forge-artifact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testRoot);
        this.previousEnv = Environment.GetEnvironmentVariable("FORGE_ARTIFACT_ROOT") ?? string.Empty;
        Environment.SetEnvironmentVariable("FORGE_ARTIFACT_ROOT", this.testRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FORGE_ARTIFACT_ROOT",
            string.IsNullOrEmpty(this.previousEnv) ? null : this.previousEnv);
        try { Directory.Delete(this.testRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ArtifactRoot_honours_env_override()
    {
        BuildArtifactExpander.ResolveArtifactRoot().Should().StartWith(this.testRoot);
    }

    [Fact]
    public async Task ExtractAsync_zip_slip_traversal_is_rejected_wholesale()
    {
        // Build a malicious ZIP with one safe entry and one ../../../etc/passwd entry.
        var zipPath = Path.Combine(this.testRoot, "malicious.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("safe.txt").Open()))
                w.Write("ok");
            using (var w = new StreamWriter(archive.CreateEntry("../../../etc/passwd").Open()))
                w.Write("root:x:0:0::/:/bin/sh");
        }

        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var expander = new BuildArtifactExpander(
            f.Services.GetRequiredService<IServiceScopeFactory>(),
            f.Services.GetRequiredService<ForgeMetrics>(),
            NullLogger<BuildArtifactExpander>.Instance,
            new TestHttpClientFactory(zipPath));

        var result = await expander.ExtractAsync(buildId: 99, provider: "ado",
            artifactName: "malicious", downloadUrl: "http://test/artifact.zip", default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith("zip-slip");
        // The extracted directory should NOT contain the safe file either —
        // one bad entry rejects the whole artifact.
        var extractDir = Path.Combine(this.testRoot, "ado", "99", "malicious");
        Directory.Exists(extractDir).Should().BeFalse();
    }

    [Fact]
    public async Task ExtractAsync_clean_zip_indexes_files_and_classifies_logs()
    {
        var zipPath = Path.Combine(this.testRoot, "clean.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("output.log").Open()))
                w.Write("test log");
            using (var w = new StreamWriter(archive.CreateEntry("results.trx").Open()))
                w.Write("<test/>");
            using (var w = new StreamWriter(archive.CreateEntry("binary.dll").Open()))
                w.Write("\0\0\0");
        }

        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var expander = new BuildArtifactExpander(
            f.Services.GetRequiredService<IServiceScopeFactory>(),
            f.Services.GetRequiredService<ForgeMetrics>(),
            NullLogger<BuildArtifactExpander>.Instance,
            new TestHttpClientFactory(zipPath));

        var result = await expander.ExtractAsync(buildId: 100, provider: "github",
            artifactName: "clean", downloadUrl: "http://test/artifact.zip", default);

        result.Succeeded.Should().BeTrue();
        result.IndexedFiles.Should().HaveCount(3);

        var files = await db.BuildArtifactFiles.AsNoTracking()
            .Where(x => x.ArtifactId == result.ArtifactRowId).ToListAsync();
        files.Should().HaveCount(3);
        files.Single(f => f.RelPath == "output.log").IsLog.Should().BeTrue();
        files.Single(f => f.RelPath == "results.trx").IsLog.Should().BeTrue();
        files.Single(f => f.RelPath == "binary.dll").IsLog.Should().BeFalse();
    }

    /// <summary>HttpClient factory that returns a fixed local file via 200 + ContentLength.</summary>
    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly string filePath;

        public TestHttpClientFactory(string filePath) { this.filePath = filePath; }

        public HttpClient CreateClient(string name)
        {
            var handler = new TestHandler(this.filePath);
            return new HttpClient(handler);
        }
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly string filePath;

        public TestHandler(string filePath) { this.filePath = filePath; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            resp.Content = new ByteArrayContent(File.ReadAllBytes(this.filePath));
            resp.Content.Headers.ContentLength = new FileInfo(this.filePath).Length;
            return resp;
        }
    }
}
