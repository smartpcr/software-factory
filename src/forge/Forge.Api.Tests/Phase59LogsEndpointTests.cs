// -----------------------------------------------------------------------
// <copyright file="Phase59LogsEndpointTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 59 Stage B — integration contract for
/// <c>GET /api/workstreams/{wsId}/logs/{*relPath}</c> (T13-T17 in
/// docs/forge_implementation.md). Exercises the real endpoint
/// through <see cref="ForgeWebApplicationFactory"/> so the
/// <c>WorktreePathResolver</c> → file-stream → traversal-guard
/// pipeline is what's actually under test, not a hand-rolled mock.
///
/// <para>Each test sets up a temp clone root with a synthetic
/// worktree subdirectory + a real <c>.forge/logs/{name}.log</c>
/// file at the convention path
/// (<c>{cloneRoot}/.worktree/ws-{wsId}/.forge/logs/{name}</c>) so
/// the resolver's fallback "ws-{sanitizedId}" branch maps the
/// workstream id to the on-disk worktree. No StorySpawnId is set,
/// keeping the slug derivation simple + the test deterministic.</para>
/// </summary>
public class Phase59LogsEndpointTests
{
    // ── Fixture helpers ──────────────────────────────────────────────────

    private sealed record SeededWorkstream(string WsId, string CloneRoot, string WorktreePath, string LogsDir);

    private static async Task<SeededWorkstream> SeedWorkstreamWithLogsAsync(
        ForgeWebApplicationFactory f, string wsId, string? logFileName = null, string? logContent = null)
    {
        // Per-test temp clone root — every fixture gets its own
        // folder so the parallel xUnit runner doesn't see two
        // tests collide on the same cloneRoot (RepoRow has a
        // UNIQUE constraint on CloneRoot — Q2 invariant).
        var cloneRoot = Path.Combine(Path.GetTempPath(), $"phase59-logs-ep-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(cloneRoot, ".worktree", $"ws-{wsId}");
        var logsDir = Path.Combine(worktreePath, ".forge", "logs");
        Directory.CreateDirectory(logsDir);

        if (logFileName is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(logsDir, logFileName), logContent ?? string.Empty);
        }

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // The factory's first-boot seed creates a default product
        // (`atlas`) we can attach the repo to; no need to make a
        // new one per test.
        var product = await control.Products.FirstAsync();

        var repoId = $"repo-{wsId}";
        db.Repos.Add(new RepoRow
        {
            Id = repoId,
            ProductId = product.Id,
            Kind = "github",
            RemoteUrl = $"https://example.invalid/{wsId}.git",
            DisplayName = wsId,
            DefaultBranch = "main",
            CloneRoot = cloneRoot,
        });
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = wsId,
            Name = "phase59-ep-fixture",
            Epic = "EPIC-1",
            Branch = $"ws/{wsId}",
            Base = "main",
            Status = "active",
            RepoId = repoId,
            PrsJson = "[]",
            AgentsJson = "[]",
            CardsJson = "[]",
            DepsJson = "[]",
        });
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();

        return new SeededWorkstream(wsId, cloneRoot, worktreePath, logsDir);
    }

    // ── T13 — 200 + text/plain + exact bytes ─────────────────────────────

    [Fact]
    public async Task T13_GetLog_ReturnsOk_WithTextPlainAndExactBytes()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        const string body = "the quick brown fox\njumps over the lazy dog\n[error] something went wrong\n";
        var seed = await SeedWorkstreamWithLogsAsync(
            f, "ws-t13", logFileName: "test-1-12345.log", logContent: body);

        var resp = await client.GetAsync($"/api/workstreams/{seed.WsId}/logs/test-1-12345.log");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
        // UTF-8 charset is set in the endpoint; ASP.NET may rewrite
        // it to a normalised casing but the value is stable.
        Assert.Equal("utf-8", resp.Content.Headers.ContentType?.CharSet, ignoreCase: true);

        var fetched = await resp.Content.ReadAsStringAsync();
        Assert.Equal(body, fetched);
    }

    // ── T14 — Range: bytes=0-1023 returns 206 + first 1 KiB ──────────────

    [Fact]
    public async Task T14_GetLog_HonoursRangeRequest()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        // Build a deterministic 4 KiB body so a 1023-byte range
        // returns a known first slice.
        var body = string.Concat(Enumerable.Range(0, 4096).Select(i => ((char)('a' + (i % 26))).ToString()));
        var seed = await SeedWorkstreamWithLogsAsync(
            f, "ws-t14", logFileName: "test-1-12345.log", logContent: body);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/workstreams/{seed.WsId}/logs/test-1-12345.log");
        req.Headers.Range = new RangeHeaderValue(0, 1023);
        using var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);

        var fetched = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(1024, fetched.Length);
        // First 1 KiB of the body matches the slice.
        var expected = System.Text.Encoding.UTF8.GetBytes(body.AsSpan(0, 1024).ToString());
        Assert.Equal(expected, fetched);

        // Content-Range header reflects what was served. Bytes 0-1023 of a 4096-byte resource.
        Assert.Equal(0, resp.Content.Headers.ContentRange?.From);
        Assert.Equal(1023, resp.Content.Headers.ContentRange?.To);
        Assert.Equal(4096, resp.Content.Headers.ContentRange?.Length);
    }

    // ── T15 — path traversal → 400 + no body leak ───────────────────────

    [Theory]
    [InlineData("..%2F..%2Fsecret.txt")]
    [InlineData("..%2F..%2F..%2Fetc%2Fpasswd")]
    public async Task T15_GetLog_RefusesPathTraversal(string urlEncodedTraversal)
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        // Set up a real workstream so the resolver gets past the
        // "ws not found" branch — the traversal guard is what we
        // want to exercise. Plant a "secret.txt" outside the logs
        // dir so a successful traversal would actually leak it.
        var seed = await SeedWorkstreamWithLogsAsync(
            f, "ws-t15", logFileName: "test-1-12345.log", logContent: "harmless log content");
        await File.WriteAllTextAsync(Path.Combine(seed.CloneRoot, "secret.txt"), "TOP SECRET BANANAS");

        var resp = await client.GetAsync($"/api/workstreams/{seed.WsId}/logs/{urlEncodedTraversal}");

        // The guard refuses with 400 (treated as invalid path).
        // 404 would also be acceptable per the spec ("no body
        // returned"), but 400 is the explicit signal the endpoint
        // emits. Either way: the secret must NOT appear in the
        // body.
        Assert.True(
            resp.StatusCode == HttpStatusCode.BadRequest
            || resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected 400 or 404 for traversal '{urlEncodedTraversal}', got {(int)resp.StatusCode} {resp.StatusCode}.");

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("BANANAS", body);
        Assert.DoesNotContain("TOP SECRET", body);
    }

    // ── T16 — missing file → 404 ─────────────────────────────────────────

    [Fact]
    public async Task T16_GetLog_MissingFile_Returns404()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        // Workstream + logs directory exist but the named file
        // doesn't. The endpoint must NOT 500 + must NOT leak any
        // info about adjacent files — just 404.
        var seed = await SeedWorkstreamWithLogsAsync(f, "ws-t16", logFileName: null);
        await File.WriteAllTextAsync(Path.Combine(seed.LogsDir, "test-99-99999.log"), "adjacent log");

        var resp = await client.GetAsync($"/api/workstreams/{seed.WsId}/logs/test-1-nonexistent.log");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("test-99-99999", body); // no adjacent-file leak in error body
    }

    // ── T16b — unknown workstream → 404 ─────────────────────────────────

    [Fact]
    public async Task T16b_GetLog_UnknownWorkstream_Returns404()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        // Don't seed anything. Hitting /logs/anything for a
        // workstream id the DB doesn't know about should fall
        // through the resolver's null-WorktreePath branch and
        // 404 with a clear title — never crash.
        var resp = await client.GetAsync("/api/workstreams/ws-totally-unknown/logs/test-1-12345.log");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── T17 — Auth: endpoint is intentionally open today ────────────────

    /// <summary>
    /// Per Phase 59 Stage B spec note: "the endpoint currently
    /// doesn't gate sign-in because gate logs are operator-visible
    /// across the install; revisit when multi-tenant lands." This
    /// test PINS that intentional choice — unsigned-in callers get
    /// the log, not a 401. If a future change adds auth gating,
    /// this test will fail loudly and force the spec update.
    /// </summary>
    [Fact]
    public async Task T17_GetLog_NoAuthHeader_StillReturnsLog_PinIntentionalOpenAccess()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        // CreateClient adds no auth header by default. Confirms
        // the endpoint serves anyway (operator-visible logs across
        // the install). If we add auth gating in a future phase,
        // this assertion flips to Assert.Equal(401, ...) and the
        // spec note gets a [x].
        var seed = await SeedWorkstreamWithLogsAsync(
            f, "ws-t17", logFileName: "test-1-12345.log", logContent: "anonymous read");

        var resp = await client.GetAsync($"/api/workstreams/{seed.WsId}/logs/test-1-12345.log");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("anonymous read", body);
    }
}
