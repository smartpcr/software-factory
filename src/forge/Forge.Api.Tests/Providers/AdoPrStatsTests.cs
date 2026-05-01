// -----------------------------------------------------------------------
// <copyright file="AdoPrStatsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Providers.Ado;
using Xunit;

/// <summary>
/// Phase 28 — verifies that <see cref="AdoPrStats.ApplyStats"/> correctly
/// extracts commit count, file count, iteration ("updates") count, and the
/// latest <c>updatedDate</c> from canned ADO REST envelopes. The fixture
/// shape mirrors what
/// <c>https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/pullRequests/{id}/iterations</c>
/// (and friends) actually return in production.
/// </summary>
public class AdoPrStatsTests
{
    private static Pr BuildBasePr() => new(
        Id: "PR #15567121",
        Col: "review",
        Epic: "atlas",
        EpicColor: "atlas",
        Workstream: "ws/WI-37738500",
        Base: "main",
        Branch: "ws/WI-37738500",
        Title: "Implement circuit breaker policy in update service",
        Author: new Agent("XL", "human", "Xiaodong Li"),
        Additions: 0,
        Deletions: 0,
        Files: 0,
        Commits: 0,
        Age: "1d",
        Build: null,
        Review: null,
        Conflicts: false,
        HumanGate: false,
        CreatedAt: "2026-04-29T10:00:00Z",
        UpdatedAt: "2026-04-29T10:00:00Z");

    [Fact]
    public void ApplyStats_Populates_Updates_Commits_Files_And_LatestUpdatedAt()
    {
        // Iterations: 4 entries. The latest updatedDate is on iteration 4.
        const string iterationsJson = """
        {
          "value": [
            { "id": 1, "createdDate": "2026-04-29T10:00:00Z", "updatedDate": "2026-04-29T10:00:00Z", "description": "initial push" },
            { "id": 2, "createdDate": "2026-04-29T18:32:00Z", "updatedDate": "2026-04-29T18:32:00Z", "description": "second push" },
            { "id": 3, "createdDate": "2026-04-30T08:14:00Z", "updatedDate": "2026-04-30T08:14:00Z", "description": "review edits" },
            { "id": 4, "createdDate": "2026-04-30T19:01:00Z", "updatedDate": "2026-04-30T19:01:00Z", "description": "final fixup" }
          ],
          "count": 4
        }
        """;

        // Commits: 6 commits across the source branch.
        const string commitsJson = """
        {
          "value": [
            { "commitId": "aaaa1111", "comment": "feat: scaffold circuit breaker" },
            { "commitId": "aaaa2222", "comment": "feat: wire breaker into update path" },
            { "commitId": "aaaa3333", "comment": "test: add breaker unit tests" },
            { "commitId": "aaaa4444", "comment": "fix: race in close-after-cooldown" },
            { "commitId": "aaaa5555", "comment": "review: rename for clarity" },
            { "commitId": "aaaa6666", "comment": "fix: tweak telemetry verbosity" }
          ],
          "count": 6
        }
        """;

        // Iteration 4 changes: 13 distinct files (one duplicate path for good measure).
        const string changesJson = """
        {
          "changeEntries": [
            { "item": { "path": "/src/UpdateService/CircuitBreaker.cs" }, "changeType": "edit" },
            { "item": { "path": "/src/UpdateService/CircuitBreakerOptions.cs" }, "changeType": "add" },
            { "item": { "path": "/src/UpdateService/Policies/RetryPolicy.cs" }, "changeType": "edit" },
            { "item": { "path": "/src/UpdateService/Telemetry/BreakerEvents.cs" }, "changeType": "add" },
            { "item": { "path": "/src/UpdateService/UpdateController.cs" }, "changeType": "edit" },
            { "item": { "path": "/src/UpdateService/UpdateService.csproj" }, "changeType": "edit" },
            { "item": { "path": "/tests/UpdateService.Tests/CircuitBreakerTests.cs" }, "changeType": "add" },
            { "item": { "path": "/tests/UpdateService.Tests/Helpers/BreakerHarness.cs" }, "changeType": "add" },
            { "item": { "path": "/tests/UpdateService.Tests/UpdateService.Tests.csproj" }, "changeType": "edit" },
            { "item": { "path": "/docs/update-service/circuit-breaker.md" }, "changeType": "add" },
            { "item": { "path": "/docs/update-service/circuit-breaker.md" }, "changeType": "edit" },
            { "item": { "path": "/eng/build/UpdateService.targets" }, "changeType": "edit" },
            { "item": { "path": "/eng/release/notes-2026-04.md" }, "changeType": "edit" },
            { "item": { "path": "/global.json" }, "changeType": "edit" }
          ]
        }
        """;

        using var iterations = JsonDocument.Parse(iterationsJson);
        using var commits = JsonDocument.Parse(commitsJson);
        using var changes = JsonDocument.Parse(changesJson);

        var basePr = BuildBasePr();
        var enriched = AdoPrStats.ApplyStats(
            basePr,
            iterations.RootElement,
            commits.RootElement,
            changes.RootElement);

        Assert.Equal(4, enriched.Updates);
        Assert.Equal(6, enriched.Commits);
        Assert.Equal(13, enriched.Files);

        // The latest updatedDate from iteration 4 should win, normalised to UTC ISO.
        Assert.NotNull(enriched.UpdatedAt);
        var parsed = DateTimeOffset.Parse(enriched.UpdatedAt!);
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 19, 1, 0, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void ApplyStats_Falls_Back_To_Value_Length_When_Commits_Has_No_Count()
    {
        const string commitsJson = """
        {
          "value": [
            { "commitId": "1" },
            { "commitId": "2" },
            { "commitId": "3" }
          ]
        }
        """;
        using var commits = JsonDocument.Parse(commitsJson);

        var enriched = AdoPrStats.ApplyStats(BuildBasePr(), default, commits.RootElement, default);

        Assert.Equal(3, enriched.Commits);
    }

    [Fact]
    public void ApplyStats_Tolerates_Missing_Envelopes()
    {
        // All three envelopes default → base PR returned unchanged.
        var basePr = BuildBasePr();
        var enriched = AdoPrStats.ApplyStats(basePr, default, default, default);

        Assert.Equal(0, enriched.Updates);
        Assert.Equal(0, enriched.Commits);
        Assert.Equal(0, enriched.Files);
        Assert.Equal(basePr.UpdatedAt, enriched.UpdatedAt);
    }

    [Fact]
    public void ApplyStats_Counts_Distinct_File_Paths_Case_Insensitive()
    {
        // Same path with different casing should collapse to one — Windows-friendly.
        const string changesJson = """
        {
          "changeEntries": [
            { "item": { "path": "/src/Foo.cs" } },
            { "item": { "path": "/src/foo.cs" } },
            { "item": { "path": "/src/Bar.cs" } }
          ]
        }
        """;
        using var changes = JsonDocument.Parse(changesJson);

        var enriched = AdoPrStats.ApplyStats(BuildBasePr(), default, default, changes.RootElement);

        Assert.Equal(2, enriched.Files);
    }
}
