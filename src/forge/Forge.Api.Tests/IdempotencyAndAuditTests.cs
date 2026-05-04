// -----------------------------------------------------------------------
// <copyright file="IdempotencyAndAuditTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Phase 6.10 + 6.12 — audit + idempotency middleware behaviour.</summary>
public class IdempotencyAndAuditTests : IClassFixture<ForgeWebApplicationFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly ForgeWebApplicationFactory factory;

    public IdempotencyAndAuditTests(ForgeWebApplicationFactory factory) { this.factory = factory; }

    /// <summary>Seed PR #229 — the idempotency test SetAutoMerges against it.</summary>
    public async Task InitializeAsync()
    {
        using var scope = this.factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (!await db.Prs.AnyAsync(p => p.Id == "PR #229"))
        {
            db.Prs.Add(new PrRow
            {
                Id = "PR #229", Col = "review", Epic = "EPIC-024", EpicColor = "024",
                Workstream = "ws-idempotency", Base = "main", Branch = "ws/idempotency",
                Title = "Token store refactor", Age = "30m",
            });
            await db.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Idempotency_Replays_Cached_Response_For_Duplicate_Key()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var key = $"ph6-{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        // Reuse the existing SetAutoMerge endpoint as a representative write.
        var first = await client.PostAsJsonAsync("/api/prs/" + Uri.EscapeDataString("PR #229") + "/auto-merge",
            new { autoMerge = true }, JsonOpts);
        var second = await client.PostAsJsonAsync("/api/prs/" + Uri.EscapeDataString("PR #229") + "/auto-merge",
            new { autoMerge = false }, JsonOpts);

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replay"));

        var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        // Replay returns the original payload — the second call's body of `false` is ignored.
        Assert.Equal(firstDoc.RootElement.GetProperty("autoMerge").GetBoolean(),
                     secondDoc.RootElement.GetProperty("autoMerge").GetBoolean());
    }

    [Fact]
    public async Task Audit_Records_Writes_But_Skips_Reads()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "auditor@test");

        var beforeResp = await client.GetAsync("/api/audit");
        var before = JsonDocument.Parse(await beforeResp.Content.ReadAsStringAsync()).RootElement.GetArrayLength();

        // One read (GET) — should NOT be audited.
        await client.GetAsync("/api/cards/" + Uri.EscapeDataString("#234"));
        // One write (POST) — should be audited.
        await client.PostAsJsonAsync("/api/prs/" + Uri.EscapeDataString("PR #234") + "/auto-merge",
            new { autoMerge = true }, JsonOpts);

        var afterResp = await client.GetAsync("/api/audit");
        var afterDoc = JsonDocument.Parse(await afterResp.Content.ReadAsStringAsync());
        var after = afterDoc.RootElement.GetArrayLength();
        Assert.True(after >= before + 1, $"audit grew {before} → {after} but expected at least +1");

        var newest = afterDoc.RootElement[0];
        Assert.Equal("POST", newest.GetProperty("method").GetString());
        Assert.Contains("/api/prs/", newest.GetProperty("path").GetString());
        Assert.Equal("auditor@test", newest.GetProperty("user").GetString());
    }
}
