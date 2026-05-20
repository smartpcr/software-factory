// -----------------------------------------------------------------------
// <copyright file="ImplGateDepSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// docs/forge/e2e.md §2.1 — when the operator e2e-gate rewrites an impl
/// stage's DepsJson from <c>D</c> → <c>D — E2E</c>, the WorkstreamDep FK
/// edge table (which the DAG + claim sweep read) must follow. Pins that
/// <see cref="StorySpawnOrchestrator.SyncWorkstreamDepsFromJsonAsync"/> is
/// AUTHORITATIVE: it removes a stale edge the row no longer names and adds
/// the new one — not merely additive (the bug that left a live spawn
/// depending on BOTH the prior stage's impl AND its e2e).
/// </summary>
[Collection("Phase28")]
public class ImplGateDepSyncTests
{
    [Fact]
    public async Task SyncWorkstreamDeps_Rewrites_Stale_Impl_Edge_To_Prior_Stage_E2e()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }

        const string spawnId = "spawn-depsync-regress";

        // Seed a spawn whose impl stage B was decomposed BEFORE the gate:
        // its DepsJson now names the gated title ("A — E2E") but the FK
        // table still carries the pre-gate edge B → A (impl).
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = "depsync-regress",
                StoryTitle = "dep-sync regression",
                Phase = StorySpawnPhases.Implement,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Workstreams.AddRange(
                new WorkstreamRow { Id = "ws-a", StorySpawnId = spawnId, Name = "A", Status = "planning", Category = WorkstreamCategories.Impl },
                new WorkstreamRow { Id = "ws-a-e2e", StorySpawnId = spawnId, Name = "A — E2E", Status = "planning", Category = WorkstreamCategories.QaE2e },
                new WorkstreamRow { Id = "ws-b", StorySpawnId = spawnId, Name = "B", Status = "planning", Category = WorkstreamCategories.Impl, DepsJson = "[\"A — E2E\"]" });
            // Stale pre-gate edge: B depended on A's IMPL.
            db.WorkstreamDeps.Add(new WorkstreamDepRow { WorkstreamId = "ws-b", DependsOnId = "ws-a", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.FirstAsync(s => s.Id == spawnId);
            await StorySpawnOrchestrator.SyncWorkstreamDepsFromJsonAsync(control, db, spawn, default);
        }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var edges = await db.WorkstreamDeps.AsNoTracking()
                .Where(d => d.WorkstreamId == "ws-b")
                .Select(d => d.DependsOnId)
                .OrderBy(x => x)
                .ToListAsync();

            // Stale impl edge dropped, e2e edge added — authoritative, not additive.
            Assert.Equal(new[] { "ws-a-e2e" }, edges);
        }
    }

    [Fact]
    public async Task SyncWorkstreamDeps_Is_Idempotent_On_Stable_DepsJson()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }

        const string spawnId = "spawn-depsync-idem";

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = "depsync-idem",
                StoryTitle = "dep-sync idempotency",
                Phase = StorySpawnPhases.Implement,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Workstreams.AddRange(
                new WorkstreamRow { Id = "ws-a", StorySpawnId = spawnId, Name = "A", Status = "planning", Category = WorkstreamCategories.Impl },
                new WorkstreamRow { Id = "ws-b", StorySpawnId = spawnId, Name = "B", Status = "planning", Category = WorkstreamCategories.Impl, DepsJson = "[\"A\"]" });
            await db.SaveChangesAsync();
        }

        async Task RunAsync()
        {
            using var scope = f.Services.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.FirstAsync(s => s.Id == spawnId);
            await StorySpawnOrchestrator.SyncWorkstreamDepsFromJsonAsync(control, db, spawn, default);
        }

        await RunAsync();
        await RunAsync(); // second pass must not duplicate or churn

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var edges = await db.WorkstreamDeps.AsNoTracking()
                .Where(d => d.WorkstreamId == "ws-b")
                .Select(d => d.DependsOnId)
                .ToListAsync();
            Assert.Equal(new[] { "ws-a" }, edges); // exactly one, no dup
        }
    }
}
