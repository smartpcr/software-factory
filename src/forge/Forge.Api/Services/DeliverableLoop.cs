// -----------------------------------------------------------------------
// <copyright file="DeliverableLoop.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 14.4 — generator ↔ evaluator iteration orchestrator. Runs a
/// deliverable through up to <see cref="ScoringConstants.MaxIterations"/>
/// rounds, persisting one <see cref="DeliverableRow"/> per attempt and
/// flipping the linked work-item state on terminal verdicts.
/// <para>
/// The loop is deliberately decoupled from <c>AgentRunnerDispatcher</c>:
/// callers pass in two delegates (one for the generator run, one for the
/// evaluator) so the orchestrator is fully unit-testable without a live
/// runner. Production wiring (Phase 13.4 / 13.5 stubs become real) will
/// supply delegates that invoke the dispatcher.
/// </para>
/// <para>
/// Verdict semantics (locked by <see cref="ScoringConstants"/>):
/// <list type="bullet">
///   <item>score ≥ 90 → <c>pass</c>; work item → <c>closed</c>; loop ends.</item>
///   <item>70 ≤ score &lt; 90 → <c>iterate</c>; loop re-enters with the
///         evaluator's notes appended to the next generator run.</item>
///   <item>score &lt; 70 → <c>reject</c>; work item → <c>proposed</c>;
///         loop ends. The lead reopens manually.</item>
///   <item>5 iterations exhausted with no pass → <c>stalled</c>; the work
///         item is left at its current state for the lead to triage.</item>
///   <item>budget cap hit → <c>stalled-budget</c>; same triage flow.</item>
/// </list>
/// </para>
/// </summary>
public interface IDeliverableLoop
{
    Task<DeliverableLoopResult> RunAsync(DeliverableLoopRequest request, CancellationToken ct);
}

/// <summary>
/// Inputs to one full deliverable run.
/// <para><c>Generator</c> — per-iteration delegate; receives the iter
/// number (1-based) and the evaluator's previous notes (empty on iter 1).</para>
/// <para><c>Evaluator</c> — per-iteration delegate; receives the
/// generator's raw output text and returns the evaluator's raw assistant
/// text, which the loop parses via <see cref="EvaluatorScoreParser"/>.</para>
/// <para><c>MaxTaskBudgetUsd</c> — optional hard cap on cumulative spend
/// across all iterations.</para>
/// </summary>
public sealed record DeliverableLoopRequest(
    string WorkItemId,
    string CrewId,
    string GeneratorAgentId,
    string EvaluatorAgentId,
    Func<int, string, CancellationToken, Task<GeneratorRunOutput>> Generator,
    Func<int, string, CancellationToken, Task<EvaluatorRunOutput>> Evaluator,
    decimal? MaxTaskBudgetUsd = null,
    /// <summary>
    /// Phase 16.4 — when set, evaluator-side cost ledger writes are tagged
    /// to this crew (the paired evaluator crew). When null, the existing
    /// Phase 14 in-crew evaluator path is used and both costs land on
    /// <see cref="CrewId"/>.
    /// </summary>
    string? EvaluatorCrewId = null,
    /// <summary>
    /// Phase 20 — optional override for <see cref="ScoringConstants.MaxIterations"/>.
    /// When null, the compile-time constant is used.
    /// </summary>
    int? MaxIterations = null,
    /// <summary>
    /// Phase 24 Q24.12 — task kind discriminator. Defaults to <c>'coding'</c>
    /// for backward compatibility; <c>'comment-response'</c> selects the
    /// scope-is-union-of-comments rubric note for the evaluator (Q24.13). The
    /// loop's flow control is identical across kinds — only the evaluator
    /// preamble differs, and that's the caller's responsibility (the loop
    /// surfaces this string on the deliverable row + transcript events so the
    /// downstream evaluator delegate can branch).
    /// </summary>
    string Kind = DeliverableKinds.Coding,
    /// <summary>
    /// Work-item story points — used to adjust the reject/iterate threshold.
    /// Higher-point items get a lower reject bar so more iterations are attempted.
    /// </summary>
    int WorkItemPoints = 1);

/// <summary>
/// Phase 24 Q24.12 — well-known values for <see cref="DeliverableLoopRequest.Kind"/>.
/// </summary>
public static class DeliverableKinds
{
    public const string Coding = "coding";
    public const string CommentResponse = "comment-response";
    public const string BuildFix = "build-fix";
    public const string Plan = "plan";
}

/// <summary>One generator turn's raw output + cost.</summary>
public sealed record GeneratorRunOutput(string OutputText, decimal CostUsd, long InputTokens = 0, long OutputTokens = 0);

/// <summary>One evaluator turn's raw output + cost.</summary>
public sealed record EvaluatorRunOutput(string AssistantText, decimal CostUsd, long InputTokens = 0, long OutputTokens = 0);

/// <summary>Final outcome of <see cref="IDeliverableLoop.RunAsync"/>.</summary>
public sealed record DeliverableLoopResult(
    string FinalVerdict,
    int Iterations,
    int FinalScore,
    IReadOnlyList<long> DeliverableRowIds,
    decimal TotalCostUsd);

/// <inheritdoc cref="IDeliverableLoop"/>
public sealed class DeliverableLoop : IDeliverableLoop
{
    private readonly ForgeDbContext db;
    private readonly TaskChannel? channel;

    /// <summary>
    /// Phase 38 (Layer A.3) — map a work item's story points to a per-loop
    /// iteration cap. Bigger work earns more attempts before the loop
    /// stalls; tiny work still gets a 5-iter floor so the gen↔eval pair has
    /// breathing room to converge on subtle docs. Falls back to
    /// <see cref="ScoringConstants.MaxIterations"/> for points &lt;= 0 (e.g.
    /// legacy synthetic workItemIds with no work-item row). Exposed as
    /// <c>public static</c> so call sites + tests can reference the same
    /// mapping without duplicating the table.
    /// </summary>
    /// <summary>5 iterations per story point. 1pt=5, 8pt=40, 13pt=65.</summary>
    public const int IterationsPerPoint = 5;

    public static int PointsToMaxIter(int points) =>
        points <= 0 ? ScoringConstants.MaxIterations : points * IterationsPerPoint;

    public DeliverableLoop(ForgeDbContext db) : this(db, channel: null) { }

    /// <summary>
    /// Phase 16.7 — when <paramref name="channel"/> is non-null, the loop
    /// emits <c>task.iteration.started | scored | iterated | passed |
    /// rejected | stalled</c> events on the per-work-item live channel.
    /// </summary>
    public DeliverableLoop(ForgeDbContext db, TaskChannel? channel)
    {
        this.db = db;
        this.channel = channel;
    }

    public async Task<DeliverableLoopResult> RunAsync(DeliverableLoopRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Generator);
        ArgumentNullException.ThrowIfNull(request.Evaluator);

        var rowIds = new List<long>();
        var totalCost = 0m;
        // Running chain of (iter, score, verdict, notes) from every prior
        // attempt — fed to the next generator turn so it sees the full
        // feedback trajectory, not just the last evaluator's note. Without
        // this the generator forgets what it's already been told and can
        // ship the same flawed diff three rounds in a row (WI-1035 stalled
        // at score 10 by ignoring the same root issue across iters 3-5).
        var priorChain = new List<(int Iter, int Score, string Verdict, string Notes)>();
        var notesForNext = string.Empty;
        EvaluatorScore? lastScore = null;
        var verdict = "iterate";
        var iter = 0;

        // Phase 14.3 / 16.4 — pin the evaluator agent on iter 1 so subsequent
        // rounds reuse the same grader (rubric reproducibility). Generator
        // agent is also pinned for symmetry.
        var pinnedEvaluator = request.EvaluatorAgentId;
        var evalCrewId = request.EvaluatorCrewId ?? request.CrewId;

        // Phase 38 (Layer A.3): when the caller didn't pin MaxIterations,
        // derive the cap from the work item's points so a 1-point doc gets a
        // tight 3-iter budget while a 13-point delivery gets up to 15. This
        // matches the operator intuition that bigger work earns more
        // attempts before stalling. PATCH /api/work-items/{id}/execution
        // (Q20.2) still wins via MaxIterOverride on the queue row, which
        // the loop reads each iteration below.
        var maxIter = request.MaxIterations ?? PointsToMaxIter(request.WorkItemPoints);

        // Phase 47 — apply the queue's MaxIterOverride BEFORE the terminal
        // short-circuit below so an operator who bumped the cap (Resume on
        // a stalled WI) gets a chance to run additional iters instead of
        // being short-circuited on the OLD cap. The original per-iter
        // override read inside the while loop still wins for mid-loop
        // updates; this is just to seed the right baseline for the
        // short-circuit check.
        var initialQueueRows = await this.db.TaskQueue.AsNoTracking()
            .Where(q => q.WorkItemId == request.WorkItemId)
            .ToListAsync(ct);
        var initialOverride = initialQueueRows
            .Where(q => q.State != "completed" && q.MaxIterOverride != null)
            .OrderByDescending(q => q.Id)
            .Select(q => q.MaxIterOverride)
            .FirstOrDefault()
            ?? initialQueueRows
                .Where(q => q.MaxIterOverride != null)
                .OrderByDescending(q => q.Id)
                .Select(q => q.MaxIterOverride)
                .FirstOrDefault();
        if (initialOverride is int iov && iov > 0)
        {
            maxIter = iov;
        }

        // Resume support — when boot-recovery puts a half-finished work item
        // back on the queue, the agent invocation re-enters this loop. Seed
        // `iter` and `notesForNext` from the most recent persisted deliverable
        // so the next round picks up at iter+1 with the prior evaluator's
        // feedback, instead of starting iteration 1 over from scratch.
        // Skip the seeding for terminal verdicts ("pass" / "reject") — those
        // mean the loop reached its end naturally; re-running would either
        // duplicate completed work or fight a verdict the lead must triage.
        var existingDeliverables = await this.db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == request.WorkItemId)
            .ToListAsync(ct);
        var lastPersisted = existingDeliverables.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter).FirstOrDefault();

        // Phase 47 — terminal-verdict short-circuit. Before this, a workstream
        // that hit `iter == maxIter` with `stalled` verdict would re-enter the
        // loop with `iter` reset to 0 (the resume guard below requires
        // `lastPersisted.Iter < maxIter`) and run a full fresh cycle of
        // iterations 1→maxIter. Confirmed in production 2026-05-09: a single
        // workstream had 139 deliverable rows for maxIter=50 — the iter
        // histogram showed each iter 1-36 occurring 3 times, costing $9,825
        // across 4 days. Now we return the persisted verdict immediately on
        // any of these terminal cases:
        //   pass            — terminal regardless of iter (orchestrator-side
        //                     short-circuits this too, but defense-in-depth
        //                     in case a caller bypasses the orchestrator).
        //   reject          — terminal regardless of iter (lead must triage).
        //   stalled-budget  — terminal regardless of iter (operator must
        //                     raise budget via Restart; just re-running with
        //                     the same cap burns more cost).
        //   stalled         — terminal only when at-or-past cap. If the
        //                     operator bumped MaxIterOverride (handled
        //                     above), the new cap exceeds the persisted
        //                     iter and the resume path takes over instead.
        // Mirror the original side effects (TrySetWorkItemStateAsync) so a
        // replay of pass/reject still leaves WorkItem state consistent if a
        // crash had skipped them last round.
        if (lastPersisted is not null)
        {
            var persistedTerminal =
                lastPersisted.Verdict is "pass" or "reject" or "stalled-budget"
                || (lastPersisted.Verdict == "stalled" && lastPersisted.Iter >= maxIter);
            if (persistedTerminal)
            {
                if (lastPersisted.Verdict == "pass")
                {
                    await TrySetWorkItemStateAsync(this.db, request.WorkItemId, "active", ct);
                }
                else if (lastPersisted.Verdict == "reject")
                {
                    await TrySetWorkItemStateAsync(this.db, request.WorkItemId, "proposed", ct);
                }

                await this.EmitAsync(request.WorkItemId, "task.iteration.terminal-replay", new
                {
                    iter = lastPersisted.Iter,
                    verdict = lastPersisted.Verdict,
                    score = lastPersisted.Score,
                    maxIter,
                }, ct);

                return new DeliverableLoopResult(
                    lastPersisted.Verdict,
                    lastPersisted.Iter,
                    lastPersisted.Score,
                    new List<long>(),
                    0m);
            }
        }

        if (lastPersisted is not null
            && lastPersisted.Verdict is "iterate" or "stalled" or "stalled-budget"
            && lastPersisted.Iter < maxIter)
        {
            iter = lastPersisted.Iter;
            // Seed the chain from every prior non-terminal row so the
            // resumed generator turn gets the same history a fresh run
            // would have built up in-memory.
            var seedRows = existingDeliverables
                .Where(d => d.Verdict is "iterate" or "stalled" or "stalled-budget")
                .OrderBy(d => d.Iter)
                .ThenBy(d => d.CreatedAt);
            foreach (var d in seedRows)
            {
                priorChain.Add((d.Iter, d.Score, d.Verdict, d.Notes ?? string.Empty));
            }
            notesForNext = FormatPriorChain(priorChain);
            await this.EmitAsync(request.WorkItemId, "task.iteration.resumed", new
            {
                resumeFromIter = iter,
                nextIter = iter + 1,
                priorVerdict = lastPersisted.Verdict,
                priorScore = lastPersisted.Score,
                priorAgent = lastPersisted.GeneratorAgentId,
            }, ct);
        }

        while (iter < maxIter)
        {
            // Phase 20 — re-read per-work-item override each iteration so
            // the lead can raise/lower the cap from the Live Execution view.
            // Prefer the active (non-completed) row, fall back to any row with an override.
            var queueRows = await this.db.TaskQueue.AsNoTracking()
                .Where(q => q.WorkItemId == request.WorkItemId)
                .ToListAsync(ct);
            var queueOverride = queueRows
                .Where(q => q.State != "completed" && q.MaxIterOverride != null)
                .OrderByDescending(q => q.Id)
                .Select(q => q.MaxIterOverride)
                .FirstOrDefault()
                ?? queueRows
                    .Where(q => q.MaxIterOverride != null)
                    .OrderByDescending(q => q.Id)
                    .Select(q => q.MaxIterOverride)
                    .FirstOrDefault();
            if (queueOverride is int ov && ov > 0)
            {
                maxIter = ov;
            }

            iter++;

            await this.EmitAsync(request.WorkItemId, "task.iteration.started", new
            {
                iter,
                generatorCrewId = request.CrewId,
                evaluatorCrewId = evalCrewId,
                generatorAgentId = request.GeneratorAgentId,
                evaluatorAgentId = pinnedEvaluator,
            }, ct);

            // Phase 39 (Layer C) — prepend operator-answered questions to
            // the notes-for-next so the generator sees the resolved answers
            // before any iteration-chain feedback. The block is appended as
            // a `## Operator answers` section the generator can quote
            // directly into the doc/code.
            var answersBlock = await BuildOperatorAnswersBlockAsync(this.db, request.WorkItemId, ct);
            var iterPrelude = string.IsNullOrEmpty(answersBlock)
                ? notesForNext
                : answersBlock + (string.IsNullOrEmpty(notesForNext) ? string.Empty : "\n\n" + notesForNext);

            // (a) generator
            var gen = await request.Generator(iter, iterPrelude, ct);
            await this.WriteUsageAsync(request.WorkItemId, request.GeneratorAgentId, request.CrewId, "generator", gen.CostUsd, gen.InputTokens, gen.OutputTokens, ct);

            // (b) evaluator
            var eval = await request.Evaluator(iter, gen.OutputText, ct);
            lastScore = EvaluatorScoreParser.Parse(eval.AssistantText, request.WorkItemPoints);
            await this.WriteUsageAsync(request.WorkItemId, pinnedEvaluator, evalCrewId, "evaluator", eval.CostUsd, eval.InputTokens, eval.OutputTokens, ct);

            // 2026-05-07 — when the parser falls back to score 0 / iterate,
            // emit a transcript event carrying the exact AssistantText that
            // was parsed (capped at 8 KB). The fallback Notes already names
            // the parse error, but the operator can't reproduce a fix from
            // the row alone — they need to see the literal bytes the parser
            // saw. This event closes the gap between "parse failed" and
            // "what did the model emit" without spamming the row.
            if (lastScore.ParseFailed)
            {
                const int CaptureLimit = 8192;
                var captured = eval.AssistantText ?? string.Empty;
                var truncated = captured.Length > CaptureLimit;
                if (truncated) captured = captured[..CaptureLimit];
                await this.EmitAsync(request.WorkItemId, "task.eval.parse-failed", new
                {
                    iter,
                    parseError = lastScore.ParseError,
                    assistantTextLength = eval.AssistantText?.Length ?? 0,
                    assistantText = captured,
                    truncated,
                    evaluatorAgentId = pinnedEvaluator,
                }, ct);
            }

            totalCost += gen.CostUsd + eval.CostUsd;

            // (c) persist one row per iteration. The summary is the
            // explicit `## Iteration Summary` / `## Change Summary` /
            // `## Plan Summary` section the generator appends per its
            // prompt instructions; falls back to the response tail if the
            // agent forgot the heading.
            var genSummary = GeneratorSummaryExtractor.Extract(gen.OutputText);
            var row = new DeliverableRow
            {
                WorkItemId = request.WorkItemId,
                CrewId = request.CrewId,
                GeneratorAgentId = request.GeneratorAgentId,
                EvaluatorAgentId = pinnedEvaluator,
                Iter = iter,
                Score = lastScore.Score,
                DimsJson = JsonSerializer.Serialize(lastScore.Dims),
                Verdict = lastScore.Verdict,
                Notes = lastScore.Notes,
                GeneratorSummary = genSummary,
                GenCostUsd = gen.CostUsd,
                EvalCostUsd = eval.CostUsd,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            this.db.Deliverables.Add(row);
            await this.db.SaveChangesAsync(ct);
            rowIds.Add(row.Id);

            // Phase 39 (Layer C) — extract structured open questions from the
            // generator's output and persist any new ones. We then read the
            // full unanswered set so the verdict gate below can override
            // pass→iterate. Re-emitted ids in a later iter become new rows so
            // the history is preserved; the wizard dedupes on externalId.
            var newQuestions = OpenQuestionsExtractor.Extract(gen.OutputText);
            if (newQuestions.Count > 0)
            {
                foreach (var q in newQuestions)
                {
                    this.db.DeliverableQuestions.Add(new DeliverableQuestionRow
                    {
                        WorkItemId = request.WorkItemId,
                        ExternalId = q.ExternalId,
                        Iter = iter,
                        Text = q.Text,
                        Kind = q.Kind,
                        ChoicesJson = q.Choices is { Count: > 0 } ? JsonSerializer.Serialize(q.Choices) : null,
                        Answer = null,
                        AnsweredAt = null,
                        AnsweredBy = null,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
                await this.db.SaveChangesAsync(ct);
            }

            // Compute "currently unanswered for this work item" as a hard
            // gate on the pass verdict. We dedupe by ExternalId taking the
            // most-recent row so an answered question that wasn't re-emitted
            // doesn't keep blocking. Any externalId whose latest row has
            // Answer=null counts as unanswered.
            var unansweredCount = await CountUnansweredQuestionsAsync(this.db, request.WorkItemId, ct);

            verdict = lastScore.Verdict;
            // Hard gate: pass with unanswered questions → demote to iterate
            // and tack a note onto the evaluator's notes so the next
            // iteration's chain includes the reason.
            if (verdict == "pass" && unansweredCount > 0)
            {
                verdict = "iterate";
                row.Verdict = verdict;
                var blockNote = $"BLOCKED: {unansweredCount} open question{(unansweredCount == 1 ? string.Empty : "s")} unanswered — operator must answer via the conversation-tab wizard before pass is allowed.";
                row.Notes = string.IsNullOrEmpty(row.Notes) ? blockNote : row.Notes + "\n\n" + blockNote;
                lastScore = lastScore with { Verdict = verdict, Notes = row.Notes };
                await this.db.SaveChangesAsync(ct);
            }
            priorChain.Add((iter, lastScore.Score, verdict, lastScore.Notes));
            notesForNext = FormatPriorChain(priorChain);

            await this.EmitAsync(request.WorkItemId, "task.iteration.scored", new
            {
                iter,
                score = lastScore.Score,
                verdict,
                dims = lastScore.Dims,
                generatorCostUsd = gen.CostUsd,
                evaluatorCostUsd = eval.CostUsd,
                totalCostUsd = totalCost,
                unansweredQuestions = unansweredCount,
            }, ct);

            // (e) terminal pass → mark active (delivery phase will set
            //     "resolved" once branch is pushed and PR is created).
            if (verdict == "pass")
            {
                await TrySetWorkItemStateAsync(this.db, request.WorkItemId, "active", ct);
                await this.EmitAsync(request.WorkItemId, "task.iteration.passed", new { iter, score = lastScore.Score }, ct);
                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
            }

            // (f) terminal reject → bounce back to lead, exit.
            if (verdict == "reject")
            {
                await TrySetWorkItemStateAsync(this.db, request.WorkItemId, "proposed", ct);
                await this.EmitAsync(request.WorkItemId, "task.iteration.rejected", new { iter, score = lastScore.Score }, ct);
                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
            }

            // budget cap (per-task) — stop early as 'stalled-budget'.
            if (request.MaxTaskBudgetUsd is decimal cap && totalCost >= cap)
            {
                verdict = "stalled-budget";
                row.Verdict = verdict;
                await this.db.SaveChangesAsync(ct);
                await this.EmitAsync(request.WorkItemId, "task.budget-warning", new { iter, totalCostUsd = totalCost, cap }, ct);
                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
            }

            // verdict == "iterate" → loop re-enters
            await this.EmitAsync(request.WorkItemId, "task.iteration.iterated", new { iter, score = lastScore.Score, notes = lastScore.Notes }, ct);
        }

        // Cap hit without pass.
        verdict = "stalled";
        // Rewrite the last row's verdict to 'stalled' so it's distinguishable
        // from a regular iterate row — the row is still iter==MaxIterations.
        var lastId = rowIds[rowIds.Count - 1];
        var lastRow = await this.db.Deliverables.FirstAsync(d => d.Id == lastId, ct);
        lastRow.Verdict = verdict;
        await this.db.SaveChangesAsync(ct);

        await this.EmitAsync(request.WorkItemId, "task.iteration.stalled", new { iter, score = lastScore?.Score ?? 0 }, ct);

        return new DeliverableLoopResult(verdict, iter, lastScore?.Score ?? 0, rowIds, totalCost);
    }

    /// <summary>
    /// Phase 39 (Layer C) — render every answered question for a work item
    /// as a `## Operator answers` block prepended to the next iteration's
    /// generator prompt. Dedupes by ExternalId taking the most-recent
    /// answered row so a re-answered question carries the latest value.
    /// Returns empty string when no answers exist (caller skips the
    /// prepend).
    /// </summary>
    public static async Task<string> BuildOperatorAnswersBlockAsync(
        ForgeDbContext db, string workItemId, CancellationToken ct)
    {
        var rows = await db.DeliverableQuestions.AsNoTracking()
            .Where(q => q.WorkItemId == workItemId && q.Answer != null && q.Answer != string.Empty)
            .ToListAsync(ct);
        if (rows.Count == 0) return string.Empty;
        var latestPerId = rows
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First())
            .OrderBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Operator answers (apply these in this iteration)");
        foreach (var q in latestPerId)
        {
            sb.AppendLine();
            sb.AppendLine($"- **{q.ExternalId}**: {q.Text}");
            sb.AppendLine($"  → {q.Answer}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 39 (Layer C) — count <see cref="DeliverableQuestionRow"/> ids
    /// that are still unanswered for a work item. Dedupe by ExternalId
    /// taking the most-recent row so an externalId whose latest emission
    /// has Answer=null counts as "still blocking" even if an older row for
    /// the same id was answered. Used to override pass→iterate verdicts.
    /// </summary>
    public static async Task<int> CountUnansweredQuestionsAsync(
        ForgeDbContext db, string workItemId, CancellationToken ct)
    {
        // SQLite can't ORDER BY DateTimeOffset; pull rows for this work
        // item, group + dedupe in memory.
        var rows = await db.DeliverableQuestions.AsNoTracking()
            .Where(q => q.WorkItemId == workItemId)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        var latestPerId = rows
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First())
            .ToList();
        return latestPerId.Count(q => string.IsNullOrEmpty(q.Answer));
    }

    /// <summary>
    /// Best-effort state flip on the linked work item. Tolerates missing
    /// rows (the loop should never crash because of a stale id); existing
    /// state-machine constraints (e.g. closed → closed is a no-op) are
    /// respected by the regular <c>WorkItemRow</c> writes.
    /// </summary>
    private static async Task TrySetWorkItemStateAsync(ForgeDbContext db, string workItemId, string newState, CancellationToken ct)
    {
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null) return;
        if (string.Equals(wi.State, newState, StringComparison.OrdinalIgnoreCase)) return;
        wi.State = newState;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Phase 16.4 — split-cost ledger writer. Records one
    /// <see cref="AgentUsageRow"/> per generator/evaluator turn so the
    /// per-pair salary is reportable separately. <c>category</c> is one
    /// of <c>generator | evaluator | judge</c>.
    /// </summary>
    private async Task WriteUsageAsync(string workItemId, string? agentId, string crewId, string category, decimal costUsd, long inputTokens, long outputTokens, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        var now = DateTimeOffset.UtcNow;
        this.db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = agentId,
            CrewId = crewId,
            TaskId = workItemId,
            Model = string.Empty,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CostUsd = costUsd,
            StartedAt = now,
            EndedAt = now,
            Status = "finished",
            CostCategory = category,
        });
        await this.db.SaveChangesAsync(ct);
    }

    private async Task EmitAsync(string workItemId, string kind, object payload, CancellationToken ct)
    {
        if (this.channel is null) return;
        await this.channel.EmitAsync(workItemId, kind, payload, ct: ct);
    }

    /// <summary>
    /// Render the running chain of prior (iter, score, verdict, notes) into
    /// the block the generator gets at the top of its next prompt. The
    /// generator delegate splices this in under "Evaluator feedback from
    /// iteration N-1" — the variable name there is now misleading (it's the
    /// whole chain, not just N-1) but the rendered output makes the
    /// trajectory explicit so the model can see "I've been told this same
    /// thing three rounds in a row" instead of treating each round as
    /// independent.
    /// </summary>
    private static string FormatPriorChain(IReadOnlyList<(int Iter, int Score, string Verdict, string Notes)> chain)
    {
        if (chain.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();

        // Show the LATEST iteration's feedback prominently at the top.
        var latest = chain[chain.Count - 1];
        sb.AppendLine($"## LATEST evaluator feedback (iteration {latest.Iter}, score {latest.Score}, verdict: {latest.Verdict})");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(latest.Notes) ? "(no notes)" : latest.Notes);
        sb.AppendLine();

        // If there's more than one prior iteration, show a condensed summary
        // of older ones so the generator sees the pattern without drowning
        // in repetitive text.
        if (chain.Count > 1)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"### Iteration history ({chain.Count} total attempts)");
            sb.AppendLine();

            // Show second-most-recent in full (useful for diff vs latest),
            // older ones condensed to one line.
            for (var idx = 0; idx < chain.Count - 1; idx++)
            {
                var (i, s, v, n) = chain[idx];
                if (idx == chain.Count - 2)
                {
                    sb.AppendLine($"**Iteration {i}** — score {s} ({v})");
                    sb.AppendLine(string.IsNullOrWhiteSpace(n) ? "(no notes)" : n);
                }
                else
                {
                    var oneLiner = string.IsNullOrWhiteSpace(n) ? "(no notes)" : n.Split('\n')[0];
                    if (oneLiner.Length > 120) oneLiner = oneLiner[..117] + "...";
                    sb.AppendLine($"- Iter {i}: score {s} ({v}) — {oneLiner}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("⚠ If you see the SAME critique repeated across iterations, your prior approach did NOT work. You MUST try a fundamentally different strategy — do not repeat the same edit.");
        return sb.ToString();
    }
}
