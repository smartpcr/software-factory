// -----------------------------------------------------------------------
// <copyright file="StepBudgetChecker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 16.6 — coding-pipeline file-budget check. When a coding deliverable
/// reaches PR-emit, the runner calls
/// <see cref="CheckAsync(string, int, ProductDbContext, TaskChannel, CancellationToken)"/>
/// to compare the actual <c>git diff --name-only</c> count against the step's
/// <see cref="WorkItemRow.ExpectedFileChanges"/> and emit a soft-fail
/// <c>step-budget-met</c> check on the PR (Phase 6.9 human-gate path).
///
/// The check is **soft-fail**: the lead can still merge with a recorded
/// reason. v0.1 surfaces the breach via:
/// <list type="bullet">
///   <item>SignalR <c>task.budget-warning</c> on the per-work-item channel.</item>
///   <item><c>forge.coding.over_budget</c> counter.</item>
///   <item>An <c>AuditRow</c> tagged with the breach so <c>/audit</c> can filter it.</item>
/// </list>
/// </summary>
public static class StepBudgetChecker
{
    private static readonly Meter Meter = LeadAutoDispatcher.Meter;
    private static readonly Counter<long> OverBudgetCounter =
        Meter.CreateCounter<long>("forge.coding.over_budget");

    /// <summary>Outcome of a single check.</summary>
    public sealed record Result(bool WithinBudget, int? Expected, int Actual, string? Detail);

    /// <summary>
    /// Compare the actual file count against the step's declared budget.
    /// Returns <c>WithinBudget=true</c> when the row isn't a step, has no
    /// declared budget, or is within cap. Always emits the audit row + the
    /// counter on a breach so the operator can see it after-the-fact even
    /// if the SignalR fan-out missed.
    /// </summary>
    public static async Task<Result> CheckAsync(
        string workItemId,
        int actualFileCount,
        ProductDbContext db,
        TaskChannel channel,
        CancellationToken ct)
    {
        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (row is null) return new Result(WithinBudget: true, Expected: null, Actual: actualFileCount, Detail: "work item not found");
        if (row.Type != "step") return new Result(WithinBudget: true, Expected: null, Actual: actualFileCount, Detail: "non-step row");
        if (row.ExpectedFileChanges is not int expected) return new Result(WithinBudget: true, Expected: null, Actual: actualFileCount, Detail: "no budget declared");

        if (actualFileCount <= expected)
        {
            return new Result(WithinBudget: true, Expected: expected, Actual: actualFileCount, Detail: null);
        }

        // Over budget — record + emit.
        OverBudgetCounter.Add(1, new KeyValuePair<string, object?>("stepId", workItemId));
        db.Audit.Add(new AuditRow
        {
            At = DateTimeOffset.UtcNow,
            User = string.Empty,
            Method = "AUTO",
            Path = $"/api/work-items/{workItemId}/coding-budget",
            Status = 200,
            IdempotencyKey = null,
            RequestBodySnippet = System.Text.Json.JsonSerializer.Serialize(new
            {
                stepId = workItemId,
                expected,
                actual = actualFileCount,
                breach = actualFileCount - expected,
            }),
            Supervising = false,
        });
        await db.SaveChangesAsync(ct);

        await channel.EmitAsync(
            workItemId,
            "task.budget-warning",
            new
            {
                kind = "step-budget-met",
                stepId = workItemId,
                expected,
                actual = actualFileCount,
                breach = actualFileCount - expected,
                softFail = true,
            },
            ct: ct);

        return new Result(
            WithinBudget: false,
            Expected: expected,
            Actual: actualFileCount,
            Detail: $"Step budget breach: {actualFileCount} files vs {expected} expected.");
    }
}
