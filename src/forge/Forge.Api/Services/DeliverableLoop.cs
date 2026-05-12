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
            //
            // Phase 50 fix (2026-05-11) — agents re-emit the same
            // `## Open Questions` block every iter (their prompt template
            // requires it) even after the operator answered the question.
            // Without forwarding the answer onto the fresh row, the dedupe
            // (latest-per-id wins) flips the question back to Answer=null
            // and the loop blocks pass forever → score plateaus 82-86 with
            // iter count climbing meaninglessly. Carry forward the latest
            // non-empty answer for the same ExternalId so the regenerated
            // row stays "answered" until the operator explicitly clears or
            // updates it via the wizard.
            var newQuestions = OpenQuestionsExtractor.Extract(gen.OutputText);
            if (newQuestions.Count > 0)
            {
                var priorAnswers = await this.db.DeliverableQuestions.AsNoTracking()
                    .Where(q => q.WorkItemId == request.WorkItemId
                        && q.Answer != null && q.Answer != string.Empty)
                    .ToListAsync(ct);
                var priorByExtId = priorAnswers
                    .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First(),
                        StringComparer.OrdinalIgnoreCase);

                foreach (var q in newQuestions)
                {
                    var inheritedAnswer = priorByExtId.TryGetValue(q.ExternalId, out var prev) ? prev : null;
                    this.db.DeliverableQuestions.Add(new DeliverableQuestionRow
                    {
                        WorkItemId = request.WorkItemId,
                        ExternalId = q.ExternalId,
                        Iter = iter,
                        Text = q.Text,
                        Kind = q.Kind,
                        ChoicesJson = q.Choices is { Count: > 0 } ? JsonSerializer.Serialize(q.Choices) : null,
                        Answer = inheritedAnswer?.Answer,
                        AnsweredAt = inheritedAnswer?.AnsweredAt,
                        AnsweredBy = inheritedAnswer?.AnsweredBy,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
                await this.db.SaveChangesAsync(ct);
            }

            // Compute "currently unanswered for this work item" as a hard
            // gate on the pass verdict. An externalId is "unanswered" iff
            // NO row for that id has an Answer set — this means a
            // re-emitted question (same id, fresh row with Answer=null
            // because the carry-forward inherited the prior answer onto
            // the new row) won't flip the gate back to blocking. See
            // CountUnansweredQuestionsAsync for the full rationale.
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

            // Phase 50 — checkbox-attention hard gate. The evaluator now
            // emits "What still needs work" as `- [ ] N. <item>` checkboxes,
            // and the generator prompt requires those checkboxes to be
            // echoed back into the next iter's `### Prior feedback
            // resolution` section with each one marked `- [x]` FIXED or
            // `- [x]` DEFERRED. Silently skipping a checkbox is the
            // dominant cause of convergence loops (DefaultAction appeared
            // unaddressed in 41/51 architecture.md iters on
            // qq:TELEGRAM-MESSENGER-S). When the prior iter listed N
            // unchecked items and this iter's generator output has fewer
            // than N `- [x]` markings, demote pass→iterate so the
            // evaluator's next round catches and re-lists the skipped
            // items.
            //
            // priorChain[Count-1] holds iter N-1's eval notes (the chain
            // hasn't been extended with iter N's row yet at this point).
            // For iter 1 there is no prior, so the gate is a no-op.
            if (verdict == "pass" && priorChain.Count > 0)
            {
                var priorEval = priorChain[priorChain.Count - 1].Notes ?? string.Empty;
                var priorUnchecked = CountCheckboxMarkers(priorEval, lookForUnchecked: true);
                if (priorUnchecked > 0)
                {
                    var genChecked = CountCheckboxMarkers(gen.OutputText ?? string.Empty, lookForUnchecked: false);
                    if (genChecked < priorUnchecked)
                    {
                        verdict = "iterate";
                        row.Verdict = verdict;
                        var skipNote = $"BLOCKED: prior iteration's evaluator listed {priorUnchecked} `- [ ]` checkbox item(s); the generator's reply only marked {genChecked} as `- [x]`. Every prior checkbox must be marked FIXED or DEFERRED-with-rationale in the `### Prior feedback resolution` block before pass is allowed. Silently skipping items is the dominant cause of convergence loops — the next iteration must address the remaining {priorUnchecked - genChecked} item(s).";
                        row.Notes = string.IsNullOrEmpty(row.Notes) ? skipNote : row.Notes + "\n\n" + skipNote;
                        lastScore = lastScore with { Verdict = verdict, Notes = row.Notes };
                        await this.db.SaveChangesAsync(ct);
                    }
                }
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

            // Phase 50 — convergence detector. When the same topics show
            // up in the evaluator's "What still needs work" list across N
            // consecutive iters, the loop is going in circles (observed
            // on qq:TELEGRAM-MESSENGER-S architecture.md: DefaultAction
            // re-litigated in 41/51 iters; P95-latency in 46/51).
            // Continuing burns budget chasing a sibling-doc moving target
            // the agent can't resolve unilaterally. Exit with
            // `stalled-no-convergence` so the operator can decision-pin
            // or restart with smaller scope.
            //
            // We require at least 4 prior iters worth of chain before
            // running the check — fewer than that and the topic-overlap
            // signal is too noisy (a single repeated section ref between
            // iter 1 and iter 2 is normal).
            if (priorChain.Count >= 4)
            {
                var (looping, recurringTopics) = DetectConvergenceLoop(priorChain);
                if (looping)
                {
                    verdict = "stalled-no-convergence";
                    row.Verdict = verdict;
                    var loopNote = "STALLED: convergence loop detected — the same topics ("
                        + string.Join(", ", recurringTopics.Take(8))
                        + ") have appeared in the evaluator's `What still needs work` list across the last "
                        + Math.Min(priorChain.Count, 5)
                        + " iterations. The agent cannot resolve these unilaterally — operator should either pin a decision via the spawn's decision-pins endpoint, restart the loop with smaller scope, or accept the doc at its current score.";
                    row.Notes = string.IsNullOrEmpty(row.Notes) ? loopNote : row.Notes + "\n\n" + loopNote;
                    await this.db.SaveChangesAsync(ct);
                    await this.EmitAsync(request.WorkItemId, "task.iteration.stalled", new
                    {
                        iter,
                        score = lastScore.Score,
                        reason = "no-convergence",
                        recurringTopics,
                        priorIters = priorChain.Count,
                    }, ct);
                    return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
                }
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
    /// that are still unanswered for a work item.
    ///
    /// <para>Phase 50 fix (2026-05-11) — semantic change: an externalId is
    /// "unanswered" iff <em>no</em> row for that id has a non-empty
    /// <see cref="DeliverableQuestionRow.Answer"/>. Previously the count
    /// took the latest-per-id and treated re-emissions as fresh unanswered
    /// questions, but agents re-emit the same `## Open Questions` block
    /// every iter (their prompt template requires it) even after the
    /// operator answered. The old logic flipped answered questions back
    /// to unanswered on every iter, hard-demoting `verdict=pass` →
    /// `iterate` and causing the loop to plateau (observed
    /// qq:TELEGRAM-MESSENGER-S iter 17 score 86, never reaching the 90
    /// pass threshold). The new behaviour: once a question is answered,
    /// it stays answered until the operator explicitly clears the answer
    /// via the wizard.</para>
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
        // An externalId is "answered" if ANY row for that id has a
        // non-empty Answer. Re-emitted rows in later iters with Answer=null
        // do NOT flip the id back to unanswered.
        var unanswered = rows
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(q => string.IsNullOrEmpty(q.Answer)))
            .Count();
        return unanswered;
    }

    /// <summary>
    /// Phase 50 — count markdown checkbox markers in a free-form text
    /// block. <paramref name="lookForUnchecked"/>=true counts <c>- [ ]</c> /
    /// <c>* [ ]</c> entries (unaddressed evaluator items); false counts
    /// <c>- [x]</c> / <c>* [x]</c> / <c>- [X]</c> / <c>* [X]</c>
    /// (generator-marked FIXED or DEFERRED items).
    ///
    /// <para>Used by the checkbox-attention hard gate
    /// (<c>RunAsync</c>) to demote pass→iterate when the generator's
    /// output marks fewer items as resolved than the prior evaluator
    /// listed as unresolved.</para>
    /// </summary>
    internal static int CountCheckboxMarkers(string text, bool lookForUnchecked)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var pattern = lookForUnchecked
            ? @"(?:^|\n)\s*[-*]\s+\[ \]"
            : @"(?:^|\n)\s*[-*]\s+\[[xX]\]";
        return System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;
    }

    /// <summary>
    /// Phase 50 — token-based convergence detector.Returns
    /// <c>(looping=true, recurringTopics)</c> when the same set of
    /// significant tokens (section refs, camelCase identifiers, dotted
    /// symbols) appears in the evaluator's `What still needs work` block
    /// across the last 5 iterations.
    ///
    /// <para>Token extraction picks symbols that look like
    /// engineer-meaningful identifiers: <c>§N.M</c> section markers,
    /// dotted names (<c>telegram.send.latency_ms</c>), camelCase
    /// (<c>DefaultAction</c>), slash names (<c>/handoff</c>). English
    /// stopwords and short words are dropped. The "topic" is the set
    /// of these tokens after dedup.</para>
    ///
    /// <para>Threshold: a topic is "recurring" when it appears in at
    /// least 3 of the last 4 iterations (75 %). The loop is "looping"
    /// when ≥ 2 distinct recurring topics exist — a single recurring
    /// topic might be a genuinely-stuck dimension that one more iter
    /// could resolve; two or more is a pattern that hasn't been
    /// converging.</para>
    ///
    /// <para>The function only consumes the trailing 4 chain entries
    /// (or fewer when chain is shorter). The 4-iter window is the
    /// minimum that catches "same critique in 3 of last 4" — observed
    /// on qq:TELEGRAM-MESSENGER-S architecture.md as a stable pattern
    /// from iter 5 onward. Earlier triggering means earlier operator
    /// intervention (pin a decision, restart, or accept) instead of
    /// burning 40+ iters in a loop.</para>
    /// </summary>
    internal static (bool looping, IReadOnlyList<string> recurringTopics) DetectConvergenceLoop(
        IReadOnlyList<(int Iter, int Score, string Verdict, string Notes)> chain)
    {
        const int WindowSize = 4;
        const int RecurrenceThreshold = 3;
        const int MinDistinctTopics = 2;

        if (chain.Count < 4) return (false, Array.Empty<string>());

        var window = chain.Skip(Math.Max(0, chain.Count - WindowSize)).ToList();

        // Build token sets per iter.
        var perIterTokens = window
            .Select(c => ExtractTopicTokens(c.Notes))
            .ToList();

        // Count token recurrence across iters.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tokenSet in perIterTokens)
        {
            foreach (var token in tokenSet)
            {
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }
        }

        var recurring = counts
            .Where(kv => kv.Value >= RecurrenceThreshold)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToList();

        var looping = recurring.Count >= MinDistinctTopics;
        return (looping, recurring);
    }

    /// <summary>
    /// Extract "topic tokens" from an evaluator notes string. Targets:
    /// <list type="bullet">
    ///   <item>Section refs: <c>§3.1</c>, <c>§10.4</c>, <c>Section 5.5</c>,
    ///         <c>5.5</c> bare (when preceded by section/§).</item>
    ///   <item>Dotted symbols: <c>telegram.send.latency_ms</c>,
    ///         <c>AgentQuestion.DefaultAction</c>.</item>
    ///   <item>CamelCase / PascalCase identifiers: <c>DefaultAction</c>,
    ///         <c>OperatorBinding</c>.</item>
    ///   <item>Slash commands / paths: <c>/handoff</c>, <c>/approve</c>.</item>
    /// </list>
    /// Short tokens (&lt; 4 chars) and stopwords are dropped.
    /// </summary>
    private static HashSet<string> ExtractTopicTokens(string? notes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(notes)) return result;

        // Section markers — capture the number for cross-iter equality.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"§\s*([0-9]+(?:\.[0-9]+)*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            result.Add($"§{m.Groups[1].Value}");
        }
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"\bSection\s+([0-9]+(?:\.[0-9]+)*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            result.Add($"§{m.Groups[1].Value}");
        }

        // Slash commands.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"(?<![A-Za-z])/[a-z][a-z0-9_-]{2,30}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            result.Add(m.Value.ToLowerInvariant());
        }

        // Dotted symbols — at least one dot, leading letter, terms
        // separated by dots, underscores, or dashes.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"\b[A-Za-z][A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+){1,5}\b"))
        {
            if (m.Value.Length >= 6) result.Add(m.Value);
        }

        // CamelCase / PascalCase identifiers: at least one inner
        // uppercase after a lowercase, length ≥ 5 to drop noise like
        // "API" or "Bar".
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"\b[A-Z][a-z]+(?:[A-Z][a-z]+){1,5}\b"))
        {
            if (m.Value.Length >= 5) result.Add(m.Value);
        }

        return result;
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

        // Phase 50 (2026-05-11) — explicit instruction to treat the
        // `## What still needs work` numbered items as a checklist.
        // Score-regression observed on qq:TELEGRAM-MESSENGER-S iter 43
        // (88 → 85): the agent fixed the symptom of iter 42's comments
        // locally but introduced new structural bugs (MessengerMessage
        // inserted before OperatorBinding table; AlertType renamed in
        // §4.6 but missed in §5.6's example). The fix-one-place-break-
        // another pattern needs an explicit acknowledgement step in the
        // next iter so the agent can't drift away from prior critique.
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("**REQUIRED — Prior-feedback resolution checklist.** In your next iteration's `## Iteration Summary` / `## Change Summary`, add a `### Prior feedback resolution` subsection that mirrors EACH numbered item from the `## What still needs work` list above and marks it:");
        sb.AppendLine();
        sb.AppendLine("- `1. ADDRESSED — <one-line description of the fix + section/file ref>`");
        sb.AppendLine("- `2. DEFERRED — <reason + which iter/workstream will handle it>`");
        sb.AppendLine();
        sb.AppendLine("Silently skipping items is the most common cause of score regression. If you fixed three of five points, the remaining two MUST appear as `DEFERRED` with a rationale.");
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
