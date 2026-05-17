// -----------------------------------------------------------------------
// <copyright file="Phase27BuildSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using FluentAssertions;
using Forge.Api.Persistence;
using Forge.Api.Providers.Ado;
using Forge.Api.Providers.GitHub;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 27 — migration + state-mapping tests.
/// </summary>
public class Phase27BuildSyncTests
{
    [Fact]
    public async Task Migration_creates_all_tables_idempotently()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot triggers migrations */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Re-applying the migration is a no-op.
        await Phase31Migration.ApplyAsync(control, db);
        await Phase31Migration.ApplyAsync(control, db);

        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        async Task<bool> TableExists(string name)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
            cmd.Parameters.AddWithValue("$n", name);
            var r = await cmd.ExecuteScalarAsync();
            return r is not null;
        }

        (await TableExists("builds")).Should().BeTrue();
        (await TableExists("build_stages")).Should().BeTrue();
        (await TableExists("build_stage_logs")).Should().BeTrue();
        (await TableExists("build_artifacts")).Should().BeTrue();
        (await TableExists("build_artifact_files")).Should().BeTrue();
        (await TableExists("build_failure_points")).Should().BeTrue();
        (await TableExists("build_fix_pending")).Should().BeTrue();
    }

    [Fact]
    public async Task BuildFixPending_unique_workItemId_constraint()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.BuildFixPending.Add(new BuildFixPendingRow
        {
            WorkItemId = "wi-uniq-1", BuildId = 1, EnqueuedAt = DateTimeOffset.UtcNow.ToString("o"),
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // Re-insert with same WorkItemId must fail (UX_build_fix_pending_WorkItemId).
        db.BuildFixPending.Add(new BuildFixPendingRow
        {
            WorkItemId = "wi-uniq-1", BuildId = 2, EnqueuedAt = DateTimeOffset.UtcNow.ToString("o"),
        });
        Func<Task> dup = async () => { await control.SaveChangesAsync(); await db.SaveChangesAsync(); };
        await dup.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("inProgress", null, "running", true)]
    [InlineData("notStarted", null, "not-started", true)]
    [InlineData("completed", "succeeded", "succeeded", false)]
    [InlineData("completed", "failed", "failed", false)]
    [InlineData("completed", "partiallySucceeded", "partially-succeeded", false)]
    [InlineData("completed", "canceled", "canceled", false)]
    public void Ado_state_mapping_Q27_2(string status, string? result, string expectedState, bool expectedActive)
    {
        var (state, active) = AdoBuildSyncRunner.MapAdoState(status, result);
        state.Should().Be(expectedState);
        active.Should().Be(expectedActive);
    }

    [Theory]
    [InlineData("queued", null, "queued", true)]
    [InlineData("in_progress", null, "running", true)]
    [InlineData("completed", "success", "succeeded", false)]
    [InlineData("completed", "failure", "failed", false)]
    [InlineData("completed", "cancelled", "canceled", false)]
    [InlineData("completed", "timed_out", "failed", false)]
    public void GitHub_state_mapping_Q27_2(string status, string? conclusion, string expectedState, bool expectedActive)
    {
        var (state, active) = GitHubBuildSyncRunner.MapGitHubState(status, conclusion);
        state.Should().Be(expectedState);
        active.Should().Be(expectedActive);
    }

    [Fact]
    public void TailTruncate_trims_to_max_bytes_preserving_tail()
    {
        var input = string.Join("", Enumerable.Range(0, 1000).Select(_ => "abcdefghij"));   // 10_000 chars
        var truncated = AdoBuildSyncRunner.TailTruncate(input, maxBytes: 1000);
        truncated.Length.Should().BeLessThanOrEqualTo(1000);
        truncated.Should().EndWith(input[^100..]); // tail preserved
    }

    [Fact]
    public void TailTruncate_under_cap_is_no_op()
    {
        var input = "short content";
        AdoBuildSyncRunner.TailTruncate(input, maxBytes: 1024).Should().Be(input);
    }
}
