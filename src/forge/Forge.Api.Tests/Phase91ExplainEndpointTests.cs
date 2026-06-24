// -----------------------------------------------------------------------
// <copyright file="Phase91ExplainEndpointTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 91 — the Questions-tab "Explain" surface. Covers the idempotent
/// explain endpoint (a row that already carries an explanation is returned
/// verbatim with <c>cached:true</c> and never reaches the LLM) and the
/// questions list projection exposing <c>explanation</c> +
/// answered/total counts.
/// </summary>
public class Phase91ExplainEndpointTests
{
    private const string WiId = "ws-phase91-explain-test";

    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        return f;
    }

    [Fact]
    public async Task Explain_Returns_Cached_AgentAuthored_Explanation_Without_Generating()
    {
        await using var f = await BootAsync();
        var stored = JsonSerializer.Serialize(new
        {
            explanation = "Picks the persistence backend.",
            choiceExplanations = new { A = "use asz store", B = "keep legacy" },
            recommendation = "A",
            recommendationRationale = "asz store is the migration target.",
            generated = false,
        });

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = WiId,
                ExternalId = "persistence",
                Iter = 2,
                Text = "Which persistence backend?",
                Kind = "choice",
                ChoicesJson = "[\"A\",\"B\"]",
                ExplanationJson = stored,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test-operator");
        var resp = await client.PostAsync($"/api/work-items/{WiId}/questions/persistence/explain", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("cached").GetBoolean());
        var expl = body.GetProperty("explanation");
        Assert.Equal("Picks the persistence backend.", expl.GetProperty("explanation").GetString());
        Assert.Equal("A", expl.GetProperty("recommendation").GetString());
        Assert.Equal("use asz store", expl.GetProperty("choiceExplanations").GetProperty("A").GetString());
    }

    [Fact]
    public async Task Explain_Unknown_Question_Returns_404()
    {
        await using var f = await BootAsync();
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test-operator");
        var resp = await client.PostAsync($"/api/work-items/{WiId}/questions/does-not-exist/explain", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ListQuestions_Surfaces_Explanation_And_Counts()
    {
        await using var f = await BootAsync();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = WiId, ExternalId = "answered-q", Iter = 1, Text = "Answered?",
                Kind = "open", Answer = "yes", AnsweredAt = DateTimeOffset.UtcNow, AnsweredBy = "op",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = WiId, ExternalId = "open-q", Iter = 1, Text = "Still open?",
                Kind = "choice", ChoicesJson = "[\"A\",\"B\"]",
                ExplanationJson = JsonSerializer.Serialize(new { explanation = "ctx", recommendation = "A", generated = false }),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = f.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/work-items/{WiId}/questions");

        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("unansweredCount").GetInt32());
        Assert.Equal(1, body.GetProperty("answeredCount").GetInt32());

        var items = body.GetProperty("items");
        JsonElement openQ = default;
        foreach (var it in items.EnumerateArray())
        {
            if (it.GetProperty("externalId").GetString() == "open-q") openQ = it;
        }
        Assert.Equal("A", openQ.GetProperty("explanation").GetProperty("recommendation").GetString());
    }
}
