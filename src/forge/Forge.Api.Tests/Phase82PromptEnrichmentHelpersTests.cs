// -----------------------------------------------------------------------
// <copyright file="Phase82PromptEnrichmentHelpersTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 82.2 — verifies the two static prompt-enrichment helpers
/// promoted out of <see cref="CrewStoryArtifactStrategy"/> so
/// <c>CrewQueueWorker.GeneratorDelegate</c> (impl-path) can apply the
/// same operator-decision-pins + prior-iter-notes banners that
/// plan-doc + qa-e2e prompts already get.
///
/// <para>Without this audit goal #2 (prior-iter context across sessions)
/// and goal #4 (global rules / pins) succeed only on plan-doc + qa-e2e
/// prompts; impl-path generators silently drop the operator's pinned
/// decisions and re-discover the codebase every iter.</para>
/// </summary>
public sealed class Phase82PromptEnrichmentHelpersTests : IDisposable
{
    private readonly string workspace;
    private readonly List<SqliteConnection> openConns = new();

    public Phase82PromptEnrichmentHelpersTests()
    {
        this.workspace = Path.Combine(
            Path.GetTempPath(),
            "forge-phase82-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workspace);
    }

    public void Dispose()
    {
        foreach (var c in this.openConns)
        {
            try { c.Close(); c.Dispose(); }
            catch { /* best effort */ }
        }
        try { Directory.Delete(this.workspace, recursive: true); }
        catch { /* best effort */ }
    }

    // ── prior-iter notes wrapper ─────────────────────────────────────────

    [Fact]
    public async Task BuildPriorIterNotesPromptBlockAsync_EmptyWorkspace_ReturnsEmpty()
    {
        var result = await CrewStoryArtifactStrategy.BuildPriorIterNotesPromptBlockAsync(
            this.workspace, default);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task BuildPriorIterNotesPromptBlockAsync_NullWorkspace_ReturnsEmpty()
    {
        var result = await CrewStoryArtifactStrategy.BuildPriorIterNotesPromptBlockAsync(
            string.Empty, default);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task BuildPriorIterNotesPromptBlockAsync_WithNotes_WrapsContentInBanner()
    {
        var notesDir = Path.Combine(this.workspace, ".forge", "notes");
        Directory.CreateDirectory(notesDir);
        await File.WriteAllTextAsync(
            Path.Combine(notesDir, "iter-1.md"),
            "I discovered the API requires base64 padding.");

        var result = await CrewStoryArtifactStrategy.BuildPriorIterNotesPromptBlockAsync(
            this.workspace, default);

        // Banner present
        Assert.Contains("NOTES FROM PRIOR ITERATIONS", result);
        // Raw note body included
        Assert.Contains("I discovered the API requires base64 padding.", result);
        // Header for the iter section (from ReadPriorIterNotesAsync)
        Assert.Contains("### Iteration 1", result);
        // Overwrite-instruction footer present
        Assert.Contains("Overwrite `.forge/iter-notes.md`", result);
    }

    // ── decision-pins helper (static refactor) ───────────────────────────

    [Fact]
    public async Task BuildDecisionPinsBlockAsync_NullWorkItemId_ReturnsEmpty()
    {
        // Short-circuits before touching the DI scope when id is empty.
        await using var sp = await this.BuildScopeFactoryAsync();
        var result = await CrewStoryArtifactStrategy.BuildDecisionPinsBlockAsync(
            sp.GetRequiredService<IServiceScopeFactory>(),
            string.Empty,
            default);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task BuildDecisionPinsBlockAsync_NoSpawn_ReturnsEmpty()
    {
        // Real ProductDbContext, but no StorySpawnRow seeded -> the
        // helper finds no pins, returns empty.
        await using var sp = await this.BuildScopeFactoryAsync();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var result = await CrewStoryArtifactStrategy.BuildDecisionPinsBlockAsync(
            scopeFactory,
            "wi-no-spawn-anywhere",
            default);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task BuildDecisionPinsBlockAsync_SpawnWithPins_RendersBoxedBanner()
    {
        await using var sp = await this.BuildScopeFactoryAsync();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // The helper resolves spawnId by querying Workstreams; with no
        // Workstream row, spawnId falls back to workItemId itself. So
        // seeding a StorySpawn with Id == workItemId is the simplest path.
        const string spawnId = "spawn-phase82-test";

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = "story-phase82",
                Phase = "implement",
                DecisionPinsJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new { workItemId = (string?)null, text = "All public APIs must be async." },
                    new { workItemId = (string?)spawnId, text = "Use Newtonsoft.Json, NOT System.Text.Json." },
                }),
            });
            await db.SaveChangesAsync();
        }

        // Pass spawnId as workItemId so spawnId-fallback path hits.
        var result = await CrewStoryArtifactStrategy.BuildDecisionPinsBlockAsync(
            scopeFactory, spawnId, default);

        Assert.Contains("OPERATOR DECISION PINS", result);
        Assert.Contains("Newtonsoft.Json", result);
        Assert.Contains("All public APIs must be async", result);
    }

    /// <summary>
    /// Builds a ServiceProvider with empty SQLite-backed Control + Product
    /// contexts. Each test gets its own in-memory connection that lives
    /// for the test's duration (kept open via <see cref="openConns"/>).
    /// </summary>
    private async Task<ServiceProvider> BuildScopeFactoryAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        this.openConns.Add(conn);

        var services = new ServiceCollection();
        services.AddDbContext<ControlDbContext>(opt => opt.UseSqlite(conn));
        services.AddDbContext<ProductDbContext>(opt => opt.UseSqlite(conn));
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctl = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            await ctl.Database.EnsureCreatedAsync();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // ProductDbContext shares the connection; generate-create-script
            // emits CREATE TABLE for every model entity (mirrors
            // Phase50EvaluatorDistributionTests.NewDbAsync).
            var script = db.Database.GenerateCreateScript();
            foreach (var stmt in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = stmt.Trim();
                if (trimmed.Length > 0)
                {
                    try { await db.Database.ExecuteSqlRawAsync(trimmed); }
                    catch { /* duplicate-create from Control's EnsureCreated is fine */ }
                }
            }
        }

        return sp;
    }
}
