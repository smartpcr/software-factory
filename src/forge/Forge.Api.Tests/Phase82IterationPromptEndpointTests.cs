// -----------------------------------------------------------------------
// <copyright file="Phase82IterationPromptEndpointTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 82.1 — integration contract for
/// <c>GET /api/work-items/{id}/iteration-prompts</c>. This is the
/// endpoint the in-tab Prompt pager fetches on mount + on every new
/// persisted iter. Two invariants are exercised:
/// <list type="number">
///   <item><b>Untruncated:</b> persisted user prompts can be hundreds
///         of KB. The /transcript stream caps them at 320 bytes per
///         field but THIS endpoint must return the original bytes
///         verbatim — the operator needs the full audit copy.</item>
///   <item><b>Ordering:</b> iters are returned ascending by iter, and
///         within each iter the latest attempt wins.</item>
/// </list>
/// </summary>
public class Phase82IterationPromptEndpointTests
{
    private const string WorkItemId = "wi-phase82-ep-1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetIterationPrompts_ReturnsFullUntruncatedPayload()
    {
        using var f = new ForgeWebApplicationFactory();

        // Seed: 3 iters × 2 roles = 6 rows. The user prompt for iter 2
        // is intentionally 200 KB to verify the bulk-truncation guard
        // that lives on /transcript does NOT apply here.
        var bigPrompt = new string('X', 200_000);

        await SeedAsync(f, iter: 1, role: "generator", userPrompt: "iter1-gen-prompt");
        await SeedAsync(f, iter: 1, role: "evaluator", userPrompt: "iter1-eval-prompt");
        await SeedAsync(f, iter: 2, role: "generator", userPrompt: bigPrompt);
        await SeedAsync(f, iter: 2, role: "evaluator", userPrompt: "iter2-eval-prompt");
        await SeedAsync(f, iter: 3, role: "generator", userPrompt: "iter3-gen-prompt");
        // iter 3 has NO evaluator row — exercises the null-Evaluator branch.

        using var client = f.CreateClient();
        var resp = await client.GetAsync($"/api/work-items/{WorkItemId}/iteration-prompts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var payload = await resp.Content.ReadFromJsonAsync<IterationPromptListDto>(Json);
        Assert.NotNull(payload);
        Assert.Equal(WorkItemId, payload!.WorkItemId);
        Assert.Equal(3, payload.Iters.Count);

        // Ascending order.
        Assert.Equal(1, payload.Iters[0].Iter);
        Assert.Equal(2, payload.Iters[1].Iter);
        Assert.Equal(3, payload.Iters[2].Iter);

        // Pair completeness.
        Assert.NotNull(payload.Iters[0].Generator);
        Assert.NotNull(payload.Iters[0].Evaluator);
        Assert.NotNull(payload.Iters[1].Generator);
        Assert.NotNull(payload.Iters[1].Evaluator);
        Assert.NotNull(payload.Iters[2].Generator);
        Assert.Null(payload.Iters[2].Evaluator);

        // Critical invariant: the 200 KB prompt is returned BYTE-FOR-BYTE,
        // not truncated. This is the entire point of the new endpoint.
        Assert.Equal(bigPrompt.Length, payload.Iters[1].Generator!.UserPrompt.Length);
        Assert.Equal(bigPrompt, payload.Iters[1].Generator!.UserPrompt);

        // Generator + evaluator role labels round-trip via JSON.
        Assert.Equal("generator", payload.Iters[0].Generator!.Role);
        Assert.Equal("evaluator", payload.Iters[0].Evaluator!.Role);
    }

    [Fact]
    public async Task GetIterationPrompts_LatestAttemptWinsPerIter()
    {
        using var f = new ForgeWebApplicationFactory();

        // Seed two attempts for the same (iter=1, role=generator) so
        // the SaveAsync increment-on-replay logic kicks in.
        await SeedAsync(f, iter: 1, role: "generator", userPrompt: "attempt-1");
        await SeedAsync(f, iter: 1, role: "generator", userPrompt: "attempt-2");

        using var client = f.CreateClient();
        var resp = await client.GetAsync($"/api/work-items/{WorkItemId}/iteration-prompts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var payload = await resp.Content.ReadFromJsonAsync<IterationPromptListDto>(Json);
        Assert.NotNull(payload);
        Assert.Single(payload!.Iters);
        var pair = payload.Iters[0];
        Assert.Equal(1, pair.Iter);
        Assert.NotNull(pair.Generator);
        Assert.Equal(2, pair.Generator!.Attempt);
        Assert.Equal("attempt-2", pair.Generator.UserPrompt);
        Assert.Equal(2, pair.GeneratorAttempts);
    }

    [Fact]
    public async Task GetIterationPrompts_NoRows_ReturnsEmptyList()
    {
        using var f = new ForgeWebApplicationFactory();

        using var client = f.CreateClient();
        var resp = await client.GetAsync($"/api/work-items/{WorkItemId}/iteration-prompts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var payload = await resp.Content.ReadFromJsonAsync<IterationPromptListDto>(Json);
        Assert.NotNull(payload);
        Assert.Equal(WorkItemId, payload!.WorkItemId);
        Assert.Empty(payload.Iters);
    }

    [Fact]
    public async Task ClearTranscript_AlsoDeletesIterationPrompts()
    {
        using var f = new ForgeWebApplicationFactory();

        // No WorkItemRow needed — ClearTranscript no-ops on absent
        // work item (the `wi` lookup at the end is a "if not null"
        // branch), and the prompt-delete branch runs before that.
        // Two prompt rows: one each for the generator + evaluator
        // of iter 1.
        await SeedAsync(f, iter: 1, role: "generator", userPrompt: "to-be-deleted-gen");
        await SeedAsync(f, iter: 1, role: "evaluator", userPrompt: "to-be-deleted-eval");

        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "writer");

        var clearResp = await client.DeleteAsync($"/api/work-items/{WorkItemId}/transcript");
        Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);

        // After clear, the iteration-prompts list must be empty.
        var listResp = await client.GetAsync($"/api/work-items/{WorkItemId}/iteration-prompts");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var payload = await listResp.Content.ReadFromJsonAsync<IterationPromptListDto>(Json);
        Assert.NotNull(payload);
        Assert.Empty(payload!.Iters);
    }

    // ── helpers ───────────────────────────────────────────────────

    private static async Task SeedAsync(
        ForgeWebApplicationFactory f,
        int iter,
        string role,
        string userPrompt)
    {
        using var scope = f.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IterationPromptStore>();
        await store.SaveAsync(
            workItemId: WorkItemId,
            iter: iter,
            role: role,
            agentId: $"agent-{role}",
            crewId: $"crew-{role}",
            systemPrompt: $"soul-{role}",
            userPrompt: userPrompt,
            model: "model-x",
            backend: "claude",
            invokeMode: "sdk");
    }
}
