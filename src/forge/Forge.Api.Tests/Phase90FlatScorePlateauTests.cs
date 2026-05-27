// -----------------------------------------------------------------------
// <copyright file="Phase90FlatScorePlateauTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 90 — flat-score-plateau sloppy trigger. Re-enables a NARROW path
/// in <see cref="SloppyDetector.CheckAsync"/> that flips <c>IsSloppy=true</c>
/// with reason <see cref="SloppyReasons.FlatPlateau"/> when the trailing
/// <see cref="ScoringConstants.SloppyFlatPlateauWindow"/> deliverable scores
/// are ALL IDENTICAL and non-zero AND the chain length is at least
/// <see cref="ScoringConstants.SloppyFlatPlateauMinIters"/>.
///
/// <para>Reference incident: <c>local:HTTP-MEMORY:bounded-trace-primitives-
/// stage-extend-requesttraceexpressions-with-stream-evaluator-e2e</c>
/// (2026-05-27) — 34 iters at flat score 68 across attempt 3-of-3 burned
/// ~$200 per attempt × 3 reassignments = ~$1,080 before iter-cap finally
/// fired. Phase 90 would have caught each attempt at iter 8 for ~$50.</para>
/// </summary>
public class Phase90FlatScorePlateauTests
{
    // ── Pure helper: CheckFlatPlateau ───────────────────────────────────

    [Fact]
    public void Empty_Score_List_DoesNotFire()
    {
        var (fired, score, width) = SloppyDetector.CheckFlatPlateau(new List<int>());
        Assert.False(fired);
        Assert.Equal(0, score);
        Assert.Equal(0, width);
    }

    [Fact]
    public void Below_MinIters_DoesNotFire()
    {
        // MinIters default 8. A 7-element all-68 chain is below threshold.
        var (fired, _, _) = SloppyDetector.CheckFlatPlateau(new[] { 68, 68, 68, 68, 68, 68, 68 });
        Assert.False(fired);
    }

    [Fact]
    public void Exactly_MinIters_With_Flat_Window_Fires()
    {
        // Chain length 8 = min. Last 6 are identical at 68; first 2 are
        // setup (varied, then flat starts at iter 3). Trigger fires
        // because windowed equality holds AND length >= 8.
        var (fired, score, width) = SloppyDetector.CheckFlatPlateau(
            new[] { 60, 65, 68, 68, 68, 68, 68, 68 });
        Assert.True(fired);
        Assert.Equal(68, score);
        Assert.Equal(6, width);
    }

    [Fact]
    public void Bounded_Trace_Incident_Pattern_Fires()
    {
        // Exact pattern from the 2026-05-27 incident: 34 iters at flat 68.
        var scores = new int[34];
        for (var i = 0; i < scores.Length; i++) scores[i] = 68;
        var (fired, score, width) = SloppyDetector.CheckFlatPlateau(scores);
        Assert.True(fired);
        Assert.Equal(68, score);
        Assert.Equal(6, width);
    }

    [Fact]
    public void Recent_Improvement_Inside_Window_DoesNotFire()
    {
        // Last 6 are 68/68/68/68/68/72 — the final score moved.
        var (fired, _, _) = SloppyDetector.CheckFlatPlateau(
            new[] { 60, 65, 68, 68, 68, 68, 68, 68, 72 });
        Assert.False(fired);
    }

    [Fact]
    public void Recent_Downgrade_Inside_Window_DoesNotFire()
    {
        // Last 6 = 68/68/68/68/68/64 — final score dropped.
        var (fired, _, _) = SloppyDetector.CheckFlatPlateau(
            new[] { 60, 65, 68, 68, 68, 68, 68, 68, 64 });
        Assert.False(fired);
    }

    [Fact]
    public void Trailing_Zeros_DoesNotFire()
    {
        // Phase 61 handles the zero case with K=3. Phase 90 must not
        // poach those — anchor zero in the trailing window is excluded.
        var (fired, score, width) = SloppyDetector.CheckFlatPlateau(
            new[] { 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.False(fired);
        Assert.Equal(0, score);
        Assert.Equal(0, width);
    }

    [Fact]
    public void Old_Plateau_With_Recent_Recovery_DoesNotFire()
    {
        // 6-window of 68s at iters 1-6, then improvement at 7-8. Trigger
        // looks only at the trailing window (positions N-6..N-1), so this
        // must NOT fire even though there's an old flat region.
        var scores = new[] { 68, 68, 68, 68, 68, 68, 80, 90 };
        var (fired, _, _) = SloppyDetector.CheckFlatPlateau(scores);
        Assert.False(fired);
    }

    [Fact]
    public void Custom_Window_Override_Honored()
    {
        // Caller can tighten or widen the window. With windowSize=3 the
        // 8-element flat tail fires; with windowSize=10 it cannot (chain
        // shorter than window).
        var scores = new[] { 50, 60, 68, 68, 68, 68, 68, 68 };
        var (fired3, _, w3) = SloppyDetector.CheckFlatPlateau(scores, windowSize: 3);
        Assert.True(fired3);
        Assert.Equal(3, w3);

        var (fired10, _, _) = SloppyDetector.CheckFlatPlateau(scores, windowSize: 10);
        Assert.False(fired10);
    }

    [Fact]
    public void Custom_MinIters_Override_Honored()
    {
        // With minIters=4 a 6-element flat chain fires; with default 8 it
        // does not.
        var scores = new[] { 68, 68, 68, 68, 68, 68 };
        var (firedDefault, _, _) = SloppyDetector.CheckFlatPlateau(scores);
        Assert.False(firedDefault);

        var (firedRelaxed, score, _) = SloppyDetector.CheckFlatPlateau(scores, minIters: 4);
        Assert.True(firedRelaxed);
        Assert.Equal(68, score);
    }

    [Fact]
    public void WindowSize_One_DoesNotFire()
    {
        // Pathological config — a window of 1 would fire on every single
        // non-zero last score. Guard rejects.
        var (fired, _, _) = SloppyDetector.CheckFlatPlateau(
            new[] { 50, 60, 70, 80, 80, 80, 80, 80, 80, 80 }, windowSize: 1);
        Assert.False(fired);
    }

    // ── CheckAsync integration ──────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_Flat_Plateau_Fires_With_Reason()
    {
        var (db, control) = await CreateDbAsync();
        await using var _dbScope = db;
        await using var _ctlScope = control;

        // 8-iter chain: [60, 65, 68, 68, 68, 68, 68, 68].
        // Trailing 6 all = 68, chain length = 8.
        var scores = new[] { 60, 65, 68, 68, 68, 68, 68, 68 };

        var result = await SloppyDetector.CheckAsync(
            control, db,
            workItemId: "wi-phase90",
            generatorAgentId: "JS2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.True(result.IsSloppy);
        Assert.Equal(SloppyReasons.FlatPlateau, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_Zero_Trigger_Wins_Over_Flat_Plateau()
    {
        // Phase 61 (ConsecutiveScoreZero) runs first; trailing zeros
        // produce reason=consecutive-score-zero NOT flat-plateau.
        var (db, control) = await CreateDbAsync();
        await using var _dbScope = db;
        await using var _ctlScope = control;
        var scores = new[] { 60, 65, 68, 68, 68, 0, 0, 0 };

        var result = await SloppyDetector.CheckAsync(
            control, db,
            workItemId: "wi-phase90",
            generatorAgentId: "JS2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.True(result.IsSloppy);
        Assert.Equal(SloppyReasons.ConsecutiveScoreZero, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_Below_Min_Iters_DoesNotFire()
    {
        // Length 7 < minIters 8 — even though trailing 6 are flat 68.
        var (db, control) = await CreateDbAsync();
        await using var _dbScope = db;
        await using var _ctlScope = control;
        var scores = new[] { 60, 68, 68, 68, 68, 68, 68 };

        var result = await SloppyDetector.CheckAsync(
            control, db,
            workItemId: "wi-phase90",
            generatorAgentId: "JS2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.False(result.IsSloppy);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task CheckAsync_Improving_Chain_DoesNotFire()
    {
        // Long chain with steady improvement — must not poach legitimate
        // progress.
        var (db, control) = await CreateDbAsync();
        await using var _dbScope = db;
        await using var _ctlScope = control;
        var scores = new[] { 30, 40, 50, 60, 70, 75, 80, 85, 88, 90 };

        var result = await SloppyDetector.CheckAsync(
            control, db,
            workItemId: "wi-phase90",
            generatorAgentId: "JS2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.False(result.IsSloppy);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task CheckAsync_LegacyPlateau_Loose_StillDoesNotFire()
    {
        // The disabled Q52.1 plateau would fire on {90, 80, 70, 70} (peak
        // at iter 1, trailing-3 < peak). Phase 90 is strictly narrower —
        // length 4 below minIters=8, so this still must not fire.
        // Regression safety check matching Phase61's "Score_Plateau_Still_Disabled".
        var (db, control) = await CreateDbAsync();
        await using var _dbScope = db;
        await using var _ctlScope = control;
        var scores = new[] { 90, 80, 70, 70 };

        var result = await SloppyDetector.CheckAsync(
            control, db,
            workItemId: "wi-phase90",
            generatorAgentId: "JS2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.False(result.IsSloppy);
        Assert.Equal(90, result.BestScore);
        Assert.Equal(1, result.BestIter);
    }

    [Fact]
    public void SloppyReason_Constant_Has_Stable_String_Value()
    {
        // Persisted in agent_pair_assignment.SloppyReason — the literal
        // must not drift or operator dashboards will silently fail to
        // group flat-plateau alongside its siblings.
        Assert.Equal("flat-plateau", SloppyReasons.FlatPlateau);
        // Sanity-check the unchanged sibling values too.
        Assert.Equal("consecutive-score-zero", SloppyReasons.ConsecutiveScoreZero);
        Assert.Equal("score-plateau", SloppyReasons.ScorePlateau);
        Assert.Equal("iter-cap", SloppyReasons.IterCap);
    }

    [Fact]
    public void ScoringConstants_Defaults_Match_Spec()
    {
        // Lock the configured defaults so accidental drift is caught.
        Assert.Equal(8, ScoringConstants.SloppyFlatPlateauMinIters);
        Assert.Equal(6, ScoringConstants.SloppyFlatPlateauWindow);
    }

    // ── Shared SQLite-in-memory fixture (matches existing
    // Phase61ConsecutiveScoreZeroTests pattern). ────────────────────────

    private static async Task<(ProductDbContext db, ControlDbContext control)> CreateDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var ctlOpts = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(conn)
            .Options;
        var control = new ControlDbContext(ctlOpts);
        await control.Database.EnsureCreatedAsync();
        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ProductDbContext(opts);
        var _productSchema = db.Database.GenerateCreateScript();
        foreach (var _stmt in _productSchema.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var _trimmed = _stmt.Trim();
            if (_trimmed.Length > 0) await db.Database.ExecuteSqlRawAsync(_trimmed);
        }
        return (db, control);
    }
}
