// -----------------------------------------------------------------------
// <copyright file="Phase92OperatorSteeringTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 92 — human operator steering. Covers the
/// <c>/api/workstreams/{id}/operator-prompts</c> add/list/dismiss endpoints
/// and the <see cref="CrewStoryArtifactStrategy.BuildOperatorSteeringBlockAsync"/>
/// injection helper that feeds BOTH the generator and the evaluator each iter.
/// </summary>
public class Phase92OperatorSteeringTests
{
    private const string Ws = "ws-phase92-op-1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PromptDto(
        string Id, string WorkstreamId, string Body,
        string CreatedAt, string? CreatedBy, string? DismissedAt);

    [Fact]
    public async Task Add_List_Dismiss_RoundTrips()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "operator-jane");

        // Add two prompts.
        var add1 = await client.PostAsJsonAsync($"/api/workstreams/{Ws}/operator-prompts", new { body = "Keep the public API backward compatible." });
        Assert.Equal(HttpStatusCode.Created, add1.StatusCode);
        var dto1 = await add1.Content.ReadFromJsonAsync<PromptDto>(Json);
        Assert.NotNull(dto1);
        Assert.Equal(Ws, dto1!.WorkstreamId);
        Assert.Equal("operator-jane", dto1.CreatedBy);
        Assert.Null(dto1.DismissedAt);

        var add2 = await client.PostAsJsonAsync($"/api/workstreams/{Ws}/operator-prompts", new { body = "Prioritise correctness over speed." });
        Assert.Equal(HttpStatusCode.Created, add2.StatusCode);

        // List: newest first, both active.
        var list = await client.GetFromJsonAsync<List<PromptDto>>($"/api/workstreams/{Ws}/operator-prompts", Json);
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
        Assert.Equal("Prioritise correctness over speed.", list[0].Body);
        Assert.All(list, p => Assert.Null(p.DismissedAt));

        // Dismiss the first one.
        var dismiss = await client.PostAsync($"/api/workstreams/{Ws}/operator-prompts/{dto1.Id}/dismiss", null);
        Assert.Equal(HttpStatusCode.OK, dismiss.StatusCode);
        var dismissed = await dismiss.Content.ReadFromJsonAsync<PromptDto>(Json);
        Assert.NotNull(dismissed);
        Assert.NotNull(dismissed!.DismissedAt);

        // List again: still 2 rows, one now dismissed.
        var list2 = await client.GetFromJsonAsync<List<PromptDto>>($"/api/workstreams/{Ws}/operator-prompts", Json);
        Assert.NotNull(list2);
        Assert.Equal(2, list2!.Count);
        Assert.Single(list2, p => p.DismissedAt != null);
    }

    [Fact]
    public async Task Add_RequiresWriterHeader()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        var resp = await client.PostAsJsonAsync($"/api/workstreams/{Ws}/operator-prompts", new { body = "no header" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Add_EmptyBody_Returns400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "operator");

        var resp = await client.PostAsJsonAsync($"/api/workstreams/{Ws}/operator-prompts", new { body = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SteeringBlock_IncludesActiveOnly_AndIsEmptyWhenNone()
    {
        using var f = new ForgeWebApplicationFactory();
        var scopeFactory = f.Services.GetRequiredService<IServiceScopeFactory>();

        // No prompts yet → empty block.
        var empty = await CrewStoryArtifactStrategy.BuildOperatorSteeringBlockAsync(scopeFactory, Ws, default);
        Assert.Equal(string.Empty, empty);

        // Seed one active + one dismissed.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.OperatorPrompts.Add(new OperatorPromptRow
            {
                Id = "p-active", WorkstreamId = Ws, Body = "ACTIVE-DIRECTIVE-TEXT",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5), DismissedAt = null,
            });
            db.OperatorPrompts.Add(new OperatorPromptRow
            {
                Id = "p-dismissed", WorkstreamId = Ws, Body = "DISMISSED-DIRECTIVE-TEXT",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10), DismissedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var block = await CrewStoryArtifactStrategy.BuildOperatorSteeringBlockAsync(scopeFactory, Ws, default);
        Assert.Contains("HUMAN OPERATOR STEERING", block);
        Assert.Contains("generator", block);
        Assert.Contains("evaluator", block);
        Assert.Contains("ACTIVE-DIRECTIVE-TEXT", block);
        Assert.DoesNotContain("DISMISSED-DIRECTIVE-TEXT", block);
    }

    [Fact]
    public async Task SteeringBlock_OtherWorkstream_NotLeaked()
    {
        using var f = new ForgeWebApplicationFactory();
        var scopeFactory = f.Services.GetRequiredService<IServiceScopeFactory>();

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.OperatorPrompts.Add(new OperatorPromptRow
            {
                Id = "p-other", WorkstreamId = "ws-other", Body = "OTHER-WS-DIRECTIVE",
                CreatedAt = DateTimeOffset.UtcNow, DismissedAt = null,
            });
            await db.SaveChangesAsync();
        }

        var block = await CrewStoryArtifactStrategy.BuildOperatorSteeringBlockAsync(scopeFactory, Ws, default);
        Assert.Equal(string.Empty, block);
    }
}
