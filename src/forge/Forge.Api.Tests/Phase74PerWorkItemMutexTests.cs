// -----------------------------------------------------------------------
// <copyright file="Phase74PerWorkItemMutexTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 74 Fix 1 — verifies the per-<c>workItemId</c> fast-fail mutex
/// in <see cref="DeliverableLoop.RunAsync"/>. Two concurrent calls
/// against the same workItemId must produce one "real" run + one
/// <c>skipped-concurrent</c> result; concurrent calls against
/// different workItemIds must both proceed in parallel.
/// </summary>
[Collection("Phase74SerialTests")]
public class Phase74PerWorkItemMutexTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), $"forge-phase74mutex-{Guid.NewGuid():N}");

    public Phase74PerWorkItemMutexTests()
    {
        Directory.CreateDirectory(this.testRoot);
        DeliverableLoop.ResetWorkItemMutexesForTesting();
    }

    public void Dispose()
    {
        DeliverableLoop.ResetWorkItemMutexesForTesting();
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SkippedConcurrentVerdict_IsExposedAsPublicConst()
    {
        Assert.Equal("skipped-concurrent", DeliverableLoop.SkippedConcurrentVerdict);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TwoConcurrentRunsSameWorkItem_OneSkipped()
    {
        // Build a tiny gen/eval that yields after a 200 ms gate so the
        // SECOND RunAsync call can fast-fail the mutex while the first
        // is still in its delay.
        var firstGateRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dbPath = Path.Combine(this.testRoot, "forge.db");
        await BootstrapAsync(dbPath);

        var resultA = Task.Run(async () =>
        {
            await using var ctl = BuildControl(dbPath);
            await using var prod = BuildProduct(dbPath);
            var loop = new DeliverableLoop(ctl, prod, channel: null);
            return await loop.RunAsync(new DeliverableLoopRequest(
                WorkItemId: "ws-shared-id",
                CrewId: "crew-x",
                GeneratorAgentId: "agent-g",
                EvaluatorAgentId: "agent-e",
                Generator: async (iter, evalNotes, ct) =>
                {
                    firstGenStarted.TrySetResult(true);
                    await firstGateRelease.Task;
                    // Returning a failure short-circuits the loop after one iter.
                    return new GeneratorRunOutput(
                        OutputText: "first run",
                        CostUsd: 0m,
                        InputTokens: 1,
                        OutputTokens: 1,
                        FailureReason: "test short-circuit",
                        FailureKind: GeneratorFailureKinds.Other);
                },
                Evaluator: (iter, genText, ct) =>
                    throw new InvalidOperationException("evaluator should not run when gen failed"),
                WorkItemPoints: 1), CancellationToken.None);
        });

        // Wait for the first generator to be parked at the gate.
        await firstGenStarted.Task;

        // Second concurrent call against the SAME workItemId — must fast-fail.
        DeliverableLoopResult resultB;
        await using (var ctl = BuildControl(dbPath))
        await using (var prod = BuildProduct(dbPath))
        {
            var loop = new DeliverableLoop(ctl, prod, channel: null);
            resultB = await loop.RunAsync(new DeliverableLoopRequest(
                WorkItemId: "ws-shared-id",
                CrewId: "crew-x",
                GeneratorAgentId: "agent-g",
                EvaluatorAgentId: "agent-e",
                Generator: (iter, evalNotes, ct) =>
                    throw new InvalidOperationException("second generator should NOT run; mutex must have skipped it"),
                Evaluator: (iter, genText, ct) =>
                    throw new InvalidOperationException("second evaluator should NOT run"),
                WorkItemPoints: 1), CancellationToken.None);
        }

        // The second call must have returned skipped-concurrent.
        Assert.Equal(DeliverableLoop.SkippedConcurrentVerdict, resultB.FinalVerdict);
        Assert.Equal(0, resultB.Iterations);
        Assert.Empty(resultB.DeliverableRowIds);

        // Release the first call's gen so the first task can complete.
        firstGateRelease.SetResult(true);
        var resultAFinal = await resultA;
        Assert.NotEqual(DeliverableLoop.SkippedConcurrentVerdict, resultAFinal.FinalVerdict);
    }

    /// <summary>
    /// A drive reserves its workstream before touching the worktree. Another
    /// flow (the orchestrator's next tick) cannot reserve it, so it changes
    /// nothing; the reservation is free again once the drive disposes it.
    /// Seen on harness:DSH-M2 stage 5.9 2026-11-27: a concurrent tick merged
    /// the base branch into a held iteration's worktree and scrubbed .forge/.
    /// </summary>
    [Fact]
    public async Task Reservation_KeepsOtherFlowsOutUntilDisposed()
    {
        var drive = DeliverableLoop.TryReserveWorkItem("ws-held");
        Assert.NotNull(drive);
        Assert.True(DeliverableLoop.IsWorkItemInFlight("ws-held"));

        Assert.Null(await InSeparateFlow(() => DeliverableLoop.TryReserveWorkItem("ws-held")));
        var otherWorkItem = await InSeparateFlow(() => DeliverableLoop.TryReserveWorkItem("ws-other"));
        Assert.NotNull(otherWorkItem);
        otherWorkItem!.Dispose();

        drive!.Dispose();
        drive.Dispose(); // a second dispose must not over-release the mutex
        Assert.False(DeliverableLoop.IsWorkItemInFlight("ws-held"));

        var next = await InSeparateFlow(() => DeliverableLoop.TryReserveWorkItem("ws-held"));
        Assert.NotNull(next);
        next!.Dispose();
        Assert.False(DeliverableLoop.IsWorkItemInFlight("ws-held"));
    }

    /// <summary>
    /// The orchestrator's outer drive and the strategy's producer it calls both
    /// reserve; the inner reservation is a no-op and leaves the outer held.
    /// </summary>
    [Fact]
    public void Reservation_IsReentrantWithinOneFlow()
    {
        using var outer = DeliverableLoop.TryReserveWorkItem("ws-nested");
        Assert.NotNull(outer);

        var inner = DeliverableLoop.TryReserveWorkItem("ws-nested");
        Assert.NotNull(inner);
        inner!.Dispose();

        Assert.True(DeliverableLoop.IsWorkItemInFlight("ws-nested"));
    }

    /// <summary>
    /// RunAsync inside the reserving flow runs its loop (it must not refuse its
    /// own drive); RunAsync from another flow is skipped-concurrent.
    /// </summary>
    [Fact]
    public async Task RunAsync_RunsInTheReservingFlowAndSkipsOthers()
    {
        var dbPath = Path.Combine(this.testRoot, "forge.db");
        await BootstrapAsync(dbPath);

        using var drive = DeliverableLoop.TryReserveWorkItem("ws-drive");
        Assert.NotNull(drive);

        var ran = 0;
        var own = await RunOnceAsync(dbPath, "ws-drive", () => Interlocked.Increment(ref ran));
        Assert.NotEqual(DeliverableLoop.SkippedConcurrentVerdict, own.FinalVerdict);
        Assert.Equal(1, ran);
        Assert.True(DeliverableLoop.IsWorkItemInFlight("ws-drive")); // still the drive's

        var other = await InSeparateFlow(() => RunOnceAsync(dbPath, "ws-drive", () => Interlocked.Increment(ref ran)));
        Assert.Equal(DeliverableLoop.SkippedConcurrentVerdict, other.FinalVerdict);
        Assert.Equal(1, ran);
    }

    /// <summary>Runs <paramref name="work"/> in a flow that does not inherit this one's reservations.</summary>
    private static Task<T> InSeparateFlow<T>(Func<T> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    private static Task<T> InSeparateFlow<T>(Func<Task<T>> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    private static async Task<DeliverableLoopResult> RunOnceAsync(string dbPath, string workItemId, Action onGenerate)
    {
        await using var ctl = BuildControl(dbPath);
        await using var prod = BuildProduct(dbPath);
        var loop = new DeliverableLoop(ctl, prod, channel: null);
        return await loop.RunAsync(new DeliverableLoopRequest(
            WorkItemId: workItemId,
            CrewId: "crew-x",
            GeneratorAgentId: "agent-g",
            EvaluatorAgentId: "agent-e",
            Generator: (iter, evalNotes, ct) =>
            {
                onGenerate();
                return Task.FromResult(new GeneratorRunOutput(
                    OutputText: "one run",
                    CostUsd: 0m,
                    InputTokens: 1,
                    OutputTokens: 1,
                    FailureReason: "test short-circuit",
                    FailureKind: GeneratorFailureKinds.Other));
            },
            Evaluator: (iter, genText, ct) =>
                throw new InvalidOperationException("evaluator should not run when gen failed"),
            WorkItemPoints: 1), CancellationToken.None);
    }

    [Fact]
    public async Task TwoConcurrentRunsDifferentWorkItems_BothProceed()
    {
        var bGateRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aGenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bGenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dbPath = Path.Combine(this.testRoot, "forge.db");
        await BootstrapAsync(dbPath);

        async Task<DeliverableLoopResult> Run(string wsId, TaskCompletionSource<bool> started, Task release)
        {
            await using var ctl = BuildControl(dbPath);
            await using var prod = BuildProduct(dbPath);
            var loop = new DeliverableLoop(ctl, prod, channel: null);
            return await loop.RunAsync(new DeliverableLoopRequest(
                WorkItemId: wsId,
                CrewId: "crew-x",
                GeneratorAgentId: "agent-g",
                EvaluatorAgentId: "agent-e",
                Generator: async (iter, evalNotes, ct) =>
                {
                    started.TrySetResult(true);
                    await release;
                    return new GeneratorRunOutput(
                        OutputText: $"out-{wsId}", CostUsd: 0m, InputTokens: 1, OutputTokens: 1,
                        FailureReason: "test short-circuit", FailureKind: GeneratorFailureKinds.Other);
                },
                Evaluator: (iter, genText, ct) =>
                    throw new InvalidOperationException("evaluator should not run when gen failed"),
                WorkItemPoints: 1), CancellationToken.None);
        }

        var a = Task.Run(() => Run("ws-A", aGenStarted, bGateRelease.Task));
        var b = Task.Run(() => Run("ws-B", bGenStarted, bGateRelease.Task));

        // Both generators must enter without blocking each other.
        await Task.WhenAll(aGenStarted.Task, bGenStarted.Task);

        bGateRelease.SetResult(true);
        var results = await Task.WhenAll(a, b);
        Assert.All(results, r => Assert.NotEqual(DeliverableLoop.SkippedConcurrentVerdict, r.FinalVerdict));
    }

    [Fact]
    public async Task SequentialRunsSameWorkItem_BothProceed()
    {
        var dbPath = Path.Combine(this.testRoot, "forge.db");
        await BootstrapAsync(dbPath);

        async Task<DeliverableLoopResult> Run()
        {
            await using var ctl = BuildControl(dbPath);
            await using var prod = BuildProduct(dbPath);
            var loop = new DeliverableLoop(ctl, prod, channel: null);
            return await loop.RunAsync(new DeliverableLoopRequest(
                WorkItemId: "ws-seq",
                CrewId: "crew-x",
                GeneratorAgentId: "agent-g",
                EvaluatorAgentId: "agent-e",
                Generator: (iter, evalNotes, ct) => Task.FromResult(new GeneratorRunOutput(
                    OutputText: "seq",
                    CostUsd: 0m,
                    InputTokens: 1,
                    OutputTokens: 1,
                    FailureReason: "test short-circuit",
                    FailureKind: GeneratorFailureKinds.Other)),
                Evaluator: (iter, genText, ct) =>
                    throw new InvalidOperationException("evaluator should not run when gen failed"),
                WorkItemPoints: 1), CancellationToken.None);
        }

        // First call completes, releasing the mutex.
        var first = await Run();
        Assert.NotEqual(DeliverableLoop.SkippedConcurrentVerdict, first.FinalVerdict);

        // Second sequential call must NOT be skipped.
        var second = await Run();
        Assert.NotEqual(DeliverableLoop.SkippedConcurrentVerdict, second.FinalVerdict);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task BootstrapAsync(string dbPath)
    {
        // Idempotent schema seeding — run BOTH context CREATE scripts
        // against the same SQLite file, tolerating "already exists"
        // when the second pass tries to re-create a table the first
        // pass already made (Phase D interim — control + product
        // contexts share the same forge.db so re-using
        // EnsureCreatedAsync alone wouldn't work because EF skips on
        // the History marker).
        var ctlOpt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        await using (var ctl = new ControlDbContext(ctlOpt))
        {
            await RunCreateScriptAsync(ctl);
        }
        var prodOpt = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        await using (var prod = new ProductDbContext(prodOpt))
        {
            await RunCreateScriptAsync(prod);
        }
    }

    private static async Task RunCreateScriptAsync(DbContext ctx)
    {
        var script = ctx.Database.GenerateCreateScript();
        foreach (var stmt in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length == 0) continue;
            try { await ctx.Database.ExecuteSqlRawAsync(trimmed); }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Tolerate cross-context overlap during Phase D interim.
            }
        }
    }

    private static ControlDbContext BuildControl(string dbPath)
    {
        var opt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        return new ControlDbContext(opt);
    }

    private static ProductDbContext BuildProduct(string dbPath)
    {
        var opt = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        return new ProductDbContext(opt);
    }
}

[CollectionDefinition("Phase74SerialTests", DisableParallelization = true)]
public class Phase74SerialTestsCollection
{
}
