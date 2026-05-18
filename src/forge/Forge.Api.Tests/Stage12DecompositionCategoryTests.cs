// -----------------------------------------------------------------------
// <copyright file="Stage12DecompositionCategoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 1.2 — round-trip contract for
/// <c>DecompositionWorkstream.Category</c> (<see cref="WorkstreamCategories.Impl"/>
/// / <see cref="WorkstreamCategories.QaE2e"/>) through the YAML serializer,
/// the orchestrator's <c>PersistDecompositionAsync</c>, and the
/// <c>POST /api/stories/{id}/spawn/decompose</c> endpoint.
///
/// <para>
/// The first two Facts are pure-unit (no boot, no DB) — they test
/// <see cref="DecompositionYamlSerializer"/> in isolation. The third Fact
/// reaches into the orchestrator's private
/// <c>PersistDecompositionAsync</c> via reflection because the spec
/// scenario names that method explicitly and the orchestrator's public
/// surface only exposes it indirectly via the production strategy. The
/// fourth Fact is the headline integration scenario: a 4-workstream
/// story posted through the manual decompose endpoint with mixed
/// categories, asserted via EF + via the serialized
/// <c>StorySpawnRow.DecompositionJson</c> blob.
/// </para>
/// </summary>
[Collection("Phase28")]
public class Stage12DecompositionCategoryTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    // ── 1. [unit] YAML round-trip preserves qa-e2e ───────────────────────

    [Fact]
    public void Yaml_RoundTrips_QaE2e_Category()
    {
        var rows = new[]
        {
            new DecompositionYamlRow
            {
                Title = "auth",
                Description = "Auth service",
                AcceptanceCriteria = new List<string> { "Token issued" },
                Deps = new List<string>(),
                Deliverables = new List<string> { "REST endpoints" },
                Timeline = "2d",
                EstimatedEffort = "5pts",
                Category = DecompositionYamlSerializer.CategoryToYaml(WorkstreamCategories.QaE2e),
            },
            new DecompositionYamlRow
            {
                Title = "auth-e2e",
                Description = "E2E for auth",
                AcceptanceCriteria = new List<string> { "Happy path passes" },
                Deps = new List<string> { "auth" },
                Deliverables = new List<string>(),
                Timeline = "1d",
                EstimatedEffort = "2pts",
                Category = DecompositionYamlSerializer.CategoryToYaml(WorkstreamCategories.QaE2e),
            },
        };

        var yaml = DecompositionYamlSerializer.Serialize(rows);

        // The wire shape contains the literal `category: qa-e2e` for the
        // qa-e2e row. The serializer's camel-case naming means the key is
        // exactly "category" (lowercase).
        Assert.Contains("category: qa-e2e", yaml);

        // Round-trip back through the deserializer.
        var read = DecompositionYamlSerializer.Deserialize(yaml);
        Assert.NotNull(read);
        Assert.Equal(2, read!.Count);
        Assert.All(read, r => Assert.Equal(WorkstreamCategories.QaE2e,
            DecompositionYamlSerializer.CategoryFromYaml(r.Category)));

        // And the canonical record path round-trips lossless too:
        // YAML → record → YAML preserves the qa-e2e tagging via the helper.
        var canonical = read.Select(r => new DecompositionWorkstream(
            Title: r.Title ?? string.Empty,
            Description: r.Description ?? string.Empty,
            AcceptanceCriteria: (IReadOnlyList<string>)(r.AcceptanceCriteria ?? new List<string>()),
            Deps: (IReadOnlyList<string>)(r.Deps ?? new List<string>()),
            Deliverables: (IReadOnlyList<string>)(r.Deliverables ?? new List<string>()),
            Timeline: r.Timeline ?? string.Empty,
            EstimatedEffort: r.EstimatedEffort ?? string.Empty,
            Category: DecompositionYamlSerializer.CategoryFromYaml(r.Category))).ToList();
        Assert.All(canonical, w => Assert.Equal(WorkstreamCategories.QaE2e, w.Category));
    }

    // ── 2. [unit] Legacy YAML (no category) → 'impl' ─────────────────────

    [Fact]
    public void Yaml_Deserializes_Legacy_Shape_To_Impl_Category()
    {
        // Pre-Stage-1.2 architect YAML — `category` key entirely absent.
        var legacyYaml = """
            - title: auth
              description: Auth service
              acceptanceCriteria:
                - Token issued
              deps: []
              deliverables:
                - REST endpoints
              timeline: 2d
              estimatedEffort: 5pts
            - title: api
              description: API gateway
              acceptanceCriteria:
                - Routes wired
              deps:
                - auth
              deliverables:
                - Gateway code
              timeline: 1d
              estimatedEffort: 3pts
            """;

        var rows = DecompositionYamlSerializer.Deserialize(legacyYaml);
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);
        Assert.All(rows, r => Assert.Null(r.Category));

        // The canonical helper collapses null → 'impl' so downstream code
        // (orchestrator, persist path) never sees a missing default.
        Assert.All(rows, r => Assert.Equal(WorkstreamCategories.Impl,
            DecompositionYamlSerializer.CategoryFromYaml(r.Category)));

        // Symmetric helper: 'impl' on the way out collapses to null so
        // the YAML diff stays quiet for the common case.
        Assert.Null(DecompositionYamlSerializer.CategoryToYaml(WorkstreamCategories.Impl));

        // Re-serialise the impl rows and confirm the `category:` key
        // never appears in the emitted YAML.
        var roundTripped = rows
            .Select(r => new DecompositionYamlRow
            {
                Title = r.Title,
                Description = r.Description,
                AcceptanceCriteria = r.AcceptanceCriteria,
                Deps = r.Deps,
                Deliverables = r.Deliverables,
                Timeline = r.Timeline,
                EstimatedEffort = r.EstimatedEffort,
                Category = DecompositionYamlSerializer.CategoryToYaml(WorkstreamCategories.Impl),
            })
            .ToList();
        var yamlOut = DecompositionYamlSerializer.Serialize(roundTripped);
        Assert.DoesNotContain("category", yamlOut, StringComparison.OrdinalIgnoreCase);
    }

    // ── 3. [unit] PersistDecompositionAsync copies Category to WorkstreamRow ──

    [Fact]
    public async Task PersistDecompositionAsync_Copies_Category_To_WorkstreamRow()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot, apply migrations */ }

        // Seed a story + spawn directly (skip the orchestrator's plan flow
        // because this Fact targets PersistDecompositionAsync in isolation).
        const string storyId = "WI-stage12-persist";
        const string spawnId = "spawn-" + storyId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = storyId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 1.2 persist coverage",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "Stage 1.2 persist coverage",
                Phase = StorySpawnPhases.Decompose,
                PlanBranch = $"story/{storyId}/plan",
                PlanPrId = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 3,
            });
            await db.SaveChangesAsync();
        }

        // Hand-built decomposition: an impl row + a qa-e2e trailing row.
        // Mirrors what Stage 3.1 will emit but exercised here in isolation.
        var ws = new List<DecompositionWorkstream>
        {
            new(
                Title: "auth-impl",
                Description: "Auth implementation",
                AcceptanceCriteria: new[] { "Endpoints live" },
                Deps: Array.Empty<string>(),
                Deliverables: new[] { "REST endpoints" },
                Timeline: "2d",
                EstimatedEffort: "5pts",
                Category: WorkstreamCategories.Impl),
            new(
                Title: "auth-impl — E2E",
                Description: "Trailing e2e for auth",
                AcceptanceCriteria: new[] { "Happy path passes" },
                Deps: new[] { "auth-impl" },
                Deliverables: Array.Empty<string>(),
                Timeline: "1d",
                EstimatedEffort: "2pts",
                Category: WorkstreamCategories.QaE2e),
        };

        // Invoke PersistDecompositionAsync via reflection — the method is
        // private but the spec scenario names it explicitly, so we test it
        // directly rather than through a higher-level surface that also
        // tests other concerns.
        using (var scope = f.Services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.FirstAsync(s => s.Id == spawnId);

            var method = typeof(StorySpawnOrchestrator).GetMethod(
                "PersistDecompositionAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    "StorySpawnOrchestrator.PersistDecompositionAsync not found via reflection — " +
                    "Stage 1.2 test rig is out of date with the orchestrator.");
            var task = (Task?)method.Invoke(orchestrator, new object?[]
            {
                control, db, spawn, (IReadOnlyList<DecompositionWorkstream>)ws, CancellationToken.None,
            }) ?? throw new InvalidOperationException("PersistDecompositionAsync returned null.");
            await task;
        }

        // Verify Category landed on each row in a fresh tracker scope.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await db.Workstreams
                .AsNoTracking()
                .Where(w => w.StorySpawnId == spawnId)
                .OrderBy(w => w.Sort)
                .ToListAsync();

            Assert.Equal(2, rows.Count);

            var implRow = Assert.Single(rows.Where(r => r.Name == "auth-impl"));
            Assert.Equal(WorkstreamCategories.Impl, implRow.Category);

            var qaRow = Assert.Single(rows.Where(r => r.Name == "auth-impl — E2E"));
            Assert.Equal(WorkstreamCategories.QaE2e, qaRow.Category);

            // Dep edge was also written (the impl→qa-e2e edge inferred from
            // the decomp's Deps[]).
            var deps = await db.WorkstreamDeps
                .AsNoTracking()
                .Where(d => d.WorkstreamId == qaRow.Id)
                .ToListAsync();
            Assert.Single(deps);
            Assert.Equal(implRow.Id, deps[0].DependsOnId);
        }
    }

    // ── 4. [integration] Decompose endpoint + serializer + persist preserve Category ──

    [Fact]
    public async Task Decompose_Endpoint_RoundTrips_Category_For_FourWorkstream_Story()
    {
        using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string storyId = "WI-stage12-endpoint";
        const string spawnId = "spawn-" + storyId;

        // Seed story.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = storyId,
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "Stage 1.2 endpoint coverage",
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Spawn + force-advance to decompose phase so SubmitDecomposition
        // accepts the POST.
        var spawnResp = await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn", new { repoId = (string?)null }, JsonOpts);
        Assert.True(spawnResp.IsSuccessStatusCode,
            $"Spawn failed: {(int)spawnResp.StatusCode} {await spawnResp.Content.ReadAsStringAsync()}");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var s = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
            s.Phase = StorySpawnPhases.Decompose;
            await db.SaveChangesAsync();
        }

        // 4-workstream decomposition: 2 impl + 2 qa-e2e, mixed shape.
        var body = new
        {
            workstreams = new object[]
            {
                new
                {
                    title = "Auth impl",
                    description = "Implement auth",
                    deps = Array.Empty<string>(),
                    deliverables = new[] { "REST endpoints" },
                    timeline = "2d",
                    estimatedEffort = "5pts",
                    category = WorkstreamCategories.Impl,
                },
                new
                {
                    title = "Auth impl E2E",
                    description = "E2E for auth impl",
                    deps = new[] { "Auth impl" },
                    deliverables = new[] { "Gherkin + step defs" },
                    timeline = "1d",
                    estimatedEffort = "2pts",
                    category = WorkstreamCategories.QaE2e,
                },
                new
                {
                    // Legacy-shaped row: omits category entirely. Must
                    // default to impl on the way out (additive default in
                    // DecomposeWorkstream record).
                    title = "API gateway",
                    description = "Wire gateway",
                    deps = new[] { "Auth impl" },
                    deliverables = new[] { "Routes" },
                    timeline = "1d",
                    estimatedEffort = "3pts",
                },
                new
                {
                    title = "API gateway E2E",
                    description = "E2E for gateway",
                    deps = new[] { "API gateway" },
                    deliverables = new[] { "Gherkin + step defs" },
                    timeline = "1d",
                    estimatedEffort = "2pts",
                    category = WorkstreamCategories.QaE2e,
                },
            },
        };

        var resp = await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn/decompose", body, JsonOpts);
        Assert.True(resp.IsSuccessStatusCode,
            $"Decompose POST failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        // Read back rows + assert Category matches the request.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await db.Workstreams
                .AsNoTracking()
                .Where(w => w.StorySpawnId == spawnId)
                .OrderBy(w => w.Sort)
                .ToListAsync();

            Assert.Equal(4, rows.Count);

            var expected = new (string Title, string Category)[]
            {
                ("Auth impl",        WorkstreamCategories.Impl),
                ("Auth impl E2E",    WorkstreamCategories.QaE2e),
                ("API gateway",      WorkstreamCategories.Impl),    // legacy shape coerced
                ("API gateway E2E",  WorkstreamCategories.QaE2e),
            };
            foreach (var (title, expectedCat) in expected)
            {
                var row = Assert.Single(rows.Where(r => r.Name == title));
                Assert.Equal(expectedCat, row.Category);
            }

            // The StorySpawnRow.DecompositionJson blob ALSO preserves the
            // category — serialized via System.Text.Json from
            // DecomposeRequest.Workstreams, which now carries the field.
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
            Assert.False(string.IsNullOrEmpty(spawn.DecompositionJson));
            using var doc = JsonDocument.Parse(spawn.DecompositionJson!);
            var json = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, json.ValueKind);
            Assert.Equal(4, json.GetArrayLength());

            // Spot-check: the second + fourth entries carry category=qa-e2e
            // in the persisted JSON (no need to enumerate every row).
            var second = json[1];
            Assert.Equal(WorkstreamCategories.QaE2e,
                second.GetProperty("category").GetString());
            var fourth = json[3];
            Assert.Equal(WorkstreamCategories.QaE2e,
                fourth.GetProperty("category").GetString());

            // The 3rd entry (legacy shape, no category in the source body)
            // serialises as JSON null, which is fine — the persistence
            // layer already coerced it to 'impl' on the WorkstreamRow.
            var third = json[2];
            if (third.TryGetProperty("category", out var thirdCat))
            {
                Assert.True(thirdCat.ValueKind == JsonValueKind.Null
                    || string.IsNullOrEmpty(thirdCat.GetString()),
                    $"Expected null/empty category in JSON for legacy-shaped row; got '{thirdCat}'.");
            }
        }
    }
}
