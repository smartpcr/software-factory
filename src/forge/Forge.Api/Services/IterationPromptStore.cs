// -----------------------------------------------------------------------
// <copyright file="IterationPromptStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 82 — durable persistence for per-iteration generator + evaluator
/// prompts. Sits next to the existing
/// <c>taskChannel.EmitAsync("task.prompt", …)</c> calls in
/// <see cref="Stories.CrewStoryArtifactStrategy"/> so the transcript path
/// keeps its live broadcast but the audit trail survives transcript
/// retention pruning (10-iter open / 1-iter closed) and the 10 000-row
/// per-work-item cap.
///
/// <para><b>Append-only.</b> <see cref="SaveAsync"/> reads
/// <c>COALESCE(MAX(Attempt), 0) + 1</c> inside an EF SaveChanges
/// transaction. A retry / replay for the same (WorkItemId, Iter, Role)
/// inserts attempt 2 (not overwrite); the unique index
/// <c>IX_iteration_prompt_wi_iter_role_attempt</c> blocks duplicates if
/// two writers somehow race for the same attempt number.</para>
///
/// <para><b>Scoped.</b> Shares the caller's <see cref="ProductDbContext"/>
/// so the save runs against the correct per-product DB (post-Phase-73
/// split).</para>
/// </summary>
public sealed class IterationPromptStore
{
    private readonly ProductDbContext db;
    private readonly ILogger<IterationPromptStore>? logger;

    public IterationPromptStore(ProductDbContext db, ILogger<IterationPromptStore>? logger = null)
    {
        this.db = db;
        this.logger = logger;
    }

    /// <summary>
    /// Persist one iteration prompt. Returns the assigned
    /// <see cref="IterationPromptRow.Attempt"/> (1 for the first save
    /// per (workItemId, iter, role); 2, 3, … for replays).
    /// </summary>
    /// <param name="workItemId">Workstream id or synthetic plan-doc id.</param>
    /// <param name="iter">1-based iteration number.</param>
    /// <param name="role">Either <c>generator</c> or <c>evaluator</c>.</param>
    /// <param name="agentId">Originating agent id.</param>
    /// <param name="crewId">Originating crew id.</param>
    /// <param name="systemPrompt">Optional soul.md (system prompt).</param>
    /// <param name="userPrompt">The full composed user prompt the agent saw.</param>
    /// <param name="model">Model name string.</param>
    /// <param name="backend">Backend kind string (<c>claude</c> | <c>copilot</c> | …).</param>
    /// <param name="invokeMode">Invoke mode string (<c>cli</c> | <c>sdk</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<int> SaveAsync(
        string workItemId,
        int iter,
        string role,
        string agentId,
        string crewId,
        string? systemPrompt,
        string userPrompt,
        string? model,
        string? backend,
        string? invokeMode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) throw new ArgumentException("workItemId required", nameof(workItemId));
        if (iter < 1) throw new ArgumentOutOfRangeException(nameof(iter), iter, "iter must be >= 1");
        if (string.IsNullOrEmpty(role)) throw new ArgumentException("role required", nameof(role));
        if (string.IsNullOrEmpty(userPrompt)) throw new ArgumentException("userPrompt required", nameof(userPrompt));

        var normalisedRole = role.ToLowerInvariant();

        // Read the current max attempt for this tuple. Single-writer per
        // workstream (the gen-eval loop is sequential within an iter), so
        // the read-then-write is safe in practice. The unique index acts
        // as a backstop if two writers ever race.
        var currentMax = await this.db.IterationPrompts.AsNoTracking()
            .Where(p => p.WorkItemId == workItemId && p.Iter == iter && p.Role == normalisedRole)
            .Select(p => (int?)p.Attempt)
            .MaxAsync(ct);

        var attempt = (currentMax ?? 0) + 1;

        var row = new IterationPromptRow
        {
            WorkItemId = workItemId,
            Iter = iter,
            Role = normalisedRole,
            Attempt = attempt,
            AgentId = agentId ?? string.Empty,
            CrewId = crewId ?? string.Empty,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Model = model,
            Backend = backend,
            InvokeMode = invokeMode,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this.db.IterationPrompts.Add(row);
        await this.db.SaveChangesAsync(ct);
        return attempt;
    }

    /// <summary>
    /// Returns the sorted list of distinct iteration numbers that have at
    /// least one prompt row for this work item. May be sparse (e.g. iters
    /// 3, 4, 7 if the run started writing after iter 3 for any reason).
    /// </summary>
    public async Task<IReadOnlyList<int>> ListItersAsync(string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return Array.Empty<int>();

        return await this.db.IterationPrompts.AsNoTracking()
            .Where(p => p.WorkItemId == workItemId)
            .Select(p => p.Iter)
            .Distinct()
            .OrderBy(i => i)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the latest (or pinned) generator + evaluator prompt rows
    /// for a single iteration, plus the per-role attempt counts so the UI
    /// can render a "Attempt N / M" selector. Either or both roles may be
    /// null when the loop never reached that role for this iter.
    /// </summary>
    /// <param name="workItemId">Workstream id or synthetic plan-doc id.</param>
    /// <param name="iter">1-based iteration number.</param>
    /// <param name="generatorAttempt">Optional pin to a specific generator attempt; null = latest.</param>
    /// <param name="evaluatorAttempt">Optional pin to a specific evaluator attempt; null = latest.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IterationPromptPair?> GetForIterAsync(
        string workItemId,
        int iter,
        int? generatorAttempt,
        int? evaluatorAttempt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return null;
        if (iter < 1) return null;

        var rows = await this.db.IterationPrompts.AsNoTracking()
            .Where(p => p.WorkItemId == workItemId && p.Iter == iter)
            .ToListAsync(ct);

        if (rows.Count == 0) return null;

        var generators = rows
            .Where(r => string.Equals(r.Role, "generator", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Attempt)
            .ToList();
        var evaluators = rows
            .Where(r => string.Equals(r.Role, "evaluator", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Attempt)
            .ToList();

        var gen = generators.Count == 0
            ? null
            : (generatorAttempt is int ga
                ? generators.FirstOrDefault(r => r.Attempt == ga)
                : generators[^1]);
        var eval = evaluators.Count == 0
            ? null
            : (evaluatorAttempt is int ea
                ? evaluators.FirstOrDefault(r => r.Attempt == ea)
                : evaluators[^1]);

        return new IterationPromptPair(
            Iter: iter,
            Generator: gen,
            Evaluator: eval,
            GeneratorAttempts: generators.Count,
            EvaluatorAttempts: evaluators.Count);
    }

    /// <summary>
    /// Returns the latest generator + evaluator rows for every iter that
    /// has at least one row. Used by the list endpoint to populate the
    /// PromptView pager + first render.
    /// </summary>
    public async Task<IReadOnlyList<IterationPromptPair>> ListForWorkItemAsync(
        string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return Array.Empty<IterationPromptPair>();

        var rows = await this.db.IterationPrompts.AsNoTracking()
            .Where(p => p.WorkItemId == workItemId)
            .ToListAsync(ct);

        if (rows.Count == 0) return Array.Empty<IterationPromptPair>();

        return rows
            .GroupBy(r => r.Iter)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var gens = g.Where(r => string.Equals(r.Role, "generator", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(r => r.Attempt).ToList();
                var evals = g.Where(r => string.Equals(r.Role, "evaluator", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(r => r.Attempt).ToList();
                return new IterationPromptPair(
                    Iter: g.Key,
                    Generator: gens.LastOrDefault(),
                    Evaluator: evals.LastOrDefault(),
                    GeneratorAttempts: gens.Count,
                    EvaluatorAttempts: evals.Count);
            })
            .ToList();
    }

    /// <summary>
    /// Purge every iteration_prompt row for one work item. Called from
    /// workstream delete / restart / ClearTranscript so a restarted
    /// workstream begins with an empty audit. Returns the number of
    /// rows deleted (0 when nothing to purge).
    /// </summary>
    public async Task<int> DeleteForWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return 0;

        var rows = await this.db.IterationPrompts
            .Where(p => p.WorkItemId == workItemId)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        this.db.IterationPrompts.RemoveRange(rows);
        await this.db.SaveChangesAsync(ct);
        return rows.Count;
    }
}

/// <summary>
/// In-memory pair representing one iteration's prompts. Either role may
/// be null when the loop never reached it for this iter. Attempt counts
/// drive the UI's per-role attempt selector.
/// </summary>
public sealed record IterationPromptPair(
    int Iter,
    IterationPromptRow? Generator,
    IterationPromptRow? Evaluator,
    int GeneratorAttempts,
    int EvaluatorAttempts);
