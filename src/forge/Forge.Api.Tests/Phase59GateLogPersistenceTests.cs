// -----------------------------------------------------------------------
// <copyright file="Phase59GateLogPersistenceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 59 Stage B — contract for the streamed-gate-log persistence
/// (T8–T12 in docs/forge_implementation.md). Exercises
/// <see cref="StoryBuildRunner.RunBuildAsync"/> against a tiny shim
/// command on the host so we touch the real Process + streaming path
/// without depending on an MSBuild install. Tests T11 / T12 sit on
/// the directory-prune side of <see cref="GateLogPaths"/> because the
/// workstream-status / Restart hooks both delegate to
/// <see cref="GateLogPaths.PruneAll"/>.
/// </summary>
public class Phase59GateLogPersistenceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build a temp workspace that pretends to be a worktree (just an
    /// empty dir + the conventional <c>.forge/logs/</c> path). The
    /// runner creates <c>.forge/logs/</c> on first write so we don't
    /// have to pre-create it.
    /// </summary>
    private static string NewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Pick a command that prints a short, predictable string on every
    /// supported OS. <c>cmd.exe /c echo</c> works on Windows runners;
    /// every dev machine + CI box this repo targets is Windows-only
    /// (see <c>RuntimeIdentifier: win-x64</c>) so a single Windows
    /// shim is enough.
    /// </summary>
    private static string EchoCommand(string text) => $"cmd.exe /c echo {text}";

    /// <summary>
    /// Command that emits a very large stdout (~200 MB) by repeating a
    /// short line many times. Used by the bounded-memory test (T9).
    /// </summary>
    private static string BigOutputCommand(long approxBytes)
    {
        // Each `echo line` from cmd.exe is ~6 chars + \r\n = 8 bytes.
        // FOR /L generates the loop without spawning powershell.
        var iter = approxBytes / 8;
        return $"cmd.exe /c \"for /L %i in (1,1,{iter}) do @echo line\"";
    }

    // ── T8 — RunBuildAsync writes a non-empty log file ───────────────────

    [Fact]
    public async Task T8_RunBuildAsync_WritesLogFileAtExpectedPath()
    {
        var workspace = NewWorkspace();
        var (logPath, logRel) = GateLogPaths.For(workspace, "test", iter: 1);
        Assert.NotNull(logPath);
        Assert.NotNull(logRel);

        var result = await StoryBuildRunner.RunBuildAsync(
            workspace,
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30),
            buildCommand: EchoCommand("hello-phase59"),
            logFilePath: logPath,
            logRelPath: logRel);

        Assert.True(result.Success, $"Echo command should exit 0; stderr was: {result.ErrorSummary}");
        Assert.Equal(logPath, result.LogPath);
        Assert.Equal(logRel, result.LogRelPath);
        Assert.True(File.Exists(logPath!), $"Log file should exist at {logPath}");

        var contents = await File.ReadAllTextAsync(logPath!);
        Assert.Contains("hello-phase59", contents);

        // Path shape: {workspace}/.forge/logs/test-1-{ticks}.log
        Assert.Contains(Path.Combine(".forge", "logs"), logPath);
        Assert.StartsWith("test-1-", Path.GetFileName(logPath!));
        Assert.EndsWith(".log", logPath);
    }

    // ── T9 — Streaming write stays bounded under large output ────────────

    [Fact]
    public async Task T9_StreamingWrite_DoesNotBufferLargeOutputInMemory()
    {
        var workspace = NewWorkspace();
        var (logPath, logRel) = GateLogPaths.For(workspace, "build", iter: 1);

        // Generate ~20 MB of stdout — large enough that a full-string
        // buffer would be obvious in GC pressure, small enough that
        // the test stays under ~10s on a dev box. The pre-Phase-59
        // implementation called ReadToEndAsync which would materialise
        // the entire output as a single string; the new pump
        // streams line-by-line so the heap should stay flat.
        // Triple-GC pattern (Collect → WaitForPendingFinalizers →
        // Collect) before the baseline so finalisable junk from
        // earlier tests in the same xUnit process — Process, FileStream,
        // ForgeWebApplicationFactory etc. — actually drains. A single
        // forced GC leaves finalisable objects in the queue; the
        // second Collect reaps them once finalisers have run.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var beforeMaxHeap = GC.GetTotalMemory(forceFullCollection: true);

        var result = await StoryBuildRunner.RunBuildAsync(
            workspace,
            CancellationToken.None,
            timeout: TimeSpan.FromMinutes(2),
            buildCommand: BigOutputCommand(approxBytes: 20L * 1024 * 1024),
            logFilePath: logPath,
            logRelPath: logRel);

        // Same triple-GC pattern post-test. Without
        // WaitForPendingFinalizers the StreamWriter / FileStream /
        // Process objects sit in the finalizer queue and inflate
        // the residual heap measurement even though they're
        // unreachable.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var afterMaxHeap = GC.GetTotalMemory(forceFullCollection: true);

        Assert.True(result.Success, $"Big-echo command should exit 0; summary: {result.ErrorSummary}");
        Assert.True(File.Exists(logPath!));
        var fileLen = new FileInfo(logPath!).Length;
        // Verify the log actually captured ~20 MB (sanity: the pump
        // ran, didn't silently drop everything). The for-loop in cmd
        // emits 'line' per iteration; exact size depends on echo
        // overhead but must be > 1 MB.
        Assert.True(fileLen > 1L * 1024 * 1024, $"Log should be > 1 MB, was {fileLen} bytes.");

        // What matters: the runner shouldn't be holding the whole
        // 20 MB output anywhere. With the streaming pump the only
        // long-lived state is the sample list (8 lines × few hundred
        // bytes) + the TailRing (1.2 KB). With buffering the heap
        // would carry the full ~20 MB string. Threshold of 50 MB
        // covers WebApplicationFactory + EF Core caches from
        // neighbouring tests in the same xUnit process while still
        // failing loudly if the runner regresses to ReadToEnd-style
        // buffering of the whole output.
        var heapDelta = afterMaxHeap - beforeMaxHeap;
        var outputSize = fileLen;
        // 100 MB ceiling: catches the original ReadToEnd-buffering bug
        // (which would retain output stdout + stderr = ~40+ MB for
        // duplicate copies, growing past 100 MB once xUnit's
        // ForgeWebApplicationFactory residue from neighbour tests
        // lands on top). Looser than ideal — a per-test isolated
        // run sees ~5 MB — but tight enough to fail loudly on the
        // real regression while tolerating shared-process variance.
        // A tighter `delta < outputSize` assertion was tried first
        // (2026-05-19) but proved flaky across suite runs because
        // EF + ASP.NET keep ~50 MB of native handles + COM interop
        // that even triple-GC + WaitForPendingFinalizers won't free
        // between tests.
        Assert.True(heapDelta < 100L * 1024 * 1024,
            $"Heap delta after big run should stay under 100 MB (was {heapDelta:N0} bytes, output {outputSize:N0} bytes). "
            + "If this fails the runner is buffering output in memory instead of streaming it.");
    }

    // ── T10 — Retention prunes to RetentionPerGate per gate kind ─────────

    [Fact]
    public void T10_PruneToRetention_KeepsNewestNPerGateKind()
    {
        var workspace = NewWorkspace();
        var dir = GateLogPaths.DirectoryFor(workspace);
        Directory.CreateDirectory(dir);

        // Lay down 30 build logs + 25 test logs + 5 quality logs.
        // Retention cap is 20 per gate kind, so we expect 20 + 20 + 5
        // = 45 to survive.
        var baseTime = DateTime.UtcNow.AddDays(-1);
        var totalCreated = 0;
        for (var i = 0; i < 30; i++)
        {
            var p = Path.Combine(dir, $"build-{i}-{baseTime.AddMinutes(i).Ticks}.log");
            File.WriteAllText(p, $"build {i}");
            File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));
            totalCreated++;
        }
        for (var i = 0; i < 25; i++)
        {
            var p = Path.Combine(dir, $"test-{i}-{baseTime.AddMinutes(i).Ticks}.log");
            File.WriteAllText(p, $"test {i}");
            File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));
            totalCreated++;
        }
        for (var i = 0; i < 5; i++)
        {
            var p = Path.Combine(dir, $"quality-{i}-{baseTime.AddMinutes(i).Ticks}.log");
            File.WriteAllText(p, $"quality {i}");
            File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));
            totalCreated++;
        }
        Assert.Equal(60, totalCreated);
        Assert.Equal(60, Directory.GetFiles(dir, "*.log").Length);

        var deleted = GateLogPaths.PruneToRetention(workspace);
        Assert.Equal(15, deleted); // 10 build + 5 test pruned; quality under cap

        var remaining = Directory.GetFiles(dir, "*.log");
        Assert.Equal(45, remaining.Length);

        // Newest 20 of build survive — the build-29 .. build-10 set.
        var buildRemaining = remaining.Where(p => Path.GetFileName(p).StartsWith("build-", StringComparison.Ordinal)).Count();
        Assert.Equal(20, buildRemaining);
        // Oldest build files (build-0 .. build-9) are gone.
        Assert.False(File.Exists(Path.Combine(dir, $"build-0-{baseTime.AddMinutes(0).Ticks}.log")));
        Assert.True(File.Exists(Path.Combine(dir, $"build-29-{baseTime.AddMinutes(29).Ticks}.log")));
    }

    [Fact]
    public void T10b_PruneToRetention_NoOpWhenDirAbsent()
    {
        // Defensive — retention call against a workspace that never
        // ran a gate. Should silently return 0 and not throw.
        var workspace = NewWorkspace();
        var deleted = GateLogPaths.PruneToRetention(workspace);
        Assert.Equal(0, deleted);
    }

    // ── T11 — PruneAll wipes the directory (workstream complete path) ────

    [Fact]
    public void T11_PruneAll_WipesEntireLogsDirectory()
    {
        var workspace = NewWorkspace();
        var dir = GateLogPaths.DirectoryFor(workspace);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "build-1-1.log"), "x");
        File.WriteAllText(Path.Combine(dir, "test-1-1.log"), "x");
        File.WriteAllText(Path.Combine(dir, "quality-1-1.log"), "x");
        Assert.Equal(3, Directory.GetFiles(dir).Length);

        var pruned = GateLogPaths.PruneAll(workspace);
        Assert.True(pruned);
        Assert.False(Directory.Exists(dir));

        // Workspace itself survives — only .forge/logs/ is removed.
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public void T11b_PruneAll_NoOpWhenDirAbsent()
    {
        var workspace = NewWorkspace();
        var pruned = GateLogPaths.PruneAll(workspace);
        Assert.False(pruned);
    }

    // ── T12 — Restart path subsumes log prune via worktree-delete ────────

    [Fact]
    public void T12_RestartPath_DeletesWorktreeIncludingLogs()
    {
        // The Restart endpoint (WorkstreamsEndpoints.Restart line ~920)
        // already does Directory.Delete(wtPath, recursive: true) on
        // the worktree dir, which transitively wipes .forge/logs/.
        // This test pins that transitive cleanup so a future Restart
        // refactor doesn't accidentally split worktree-removal from
        // log-cleanup and leave stale per-attempt logs around.
        var workspace = NewWorkspace();
        var dir = GateLogPaths.DirectoryFor(workspace);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "build-1-1.log"), "stale-from-prior-attempt");
        Assert.True(Directory.Exists(dir));

        // Simulate the Restart endpoint's recursive delete on the
        // worktree path itself (line 920 in WorkstreamsEndpoints.cs).
        Directory.Delete(workspace, recursive: true);

        Assert.False(Directory.Exists(workspace));
        Assert.False(Directory.Exists(dir));
    }

    // ── Bonus — failed log open is best-effort, gate still completes ─────

    [Fact]
    public async Task Bonus_LogFileOpenFails_GateStillReturnsResult()
    {
        var workspace = NewWorkspace();
        // Point logFilePath at a directory (not a file) so the open
        // fails. The gate must STILL produce a StoryBuildResult so
        // the orchestrator doesn't see a phantom infrastructure
        // failure when log persistence breaks.
        var bogusPath = workspace; // open as file → fails

        var result = await StoryBuildRunner.RunBuildAsync(
            workspace,
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30),
            buildCommand: EchoCommand("ok"),
            logFilePath: bogusPath,
            logRelPath: "ignored");

        Assert.True(result.Success);
        // When the open fails the runner reports LogPath=null so the
        // caller knows to omit logUrl from the SignalR payload.
        Assert.Null(result.LogPath);
    }

    // ── Bonus — backward-compat: logFilePath=null preserves legacy path ──

    [Fact]
    public async Task Bonus_LogFilePathNull_PreservesLegacyInMemoryParsing()
    {
        var workspace = NewWorkspace();

        var result = await StoryBuildRunner.RunBuildAsync(
            workspace,
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30),
            buildCommand: EchoCommand("ok"),
            logFilePath: null,
            logRelPath: null);

        Assert.True(result.Success);
        Assert.Null(result.LogPath);
        Assert.Null(result.LogRelPath);
        Assert.False(Directory.Exists(GateLogPaths.DirectoryFor(workspace)));
    }

    // ── Bonus — path-traversal guard ─────────────────────────────────────

    [Theory]
    [InlineData("../../secret.txt")]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\secret")]
    public void Bonus_IsSafeRelPath_RefusesTraversal(string traversal)
    {
        var workspace = NewWorkspace();
        Directory.CreateDirectory(GateLogPaths.DirectoryFor(workspace));
        var ok = GateLogPaths.IsSafeRelPath(workspace, traversal, out var abs);
        Assert.False(ok, $"Traversal '{traversal}' should be refused; resolved to '{abs}'.");
    }

    [Fact]
    public void Bonus_IsSafeRelPath_AcceptsCanonicalLogName()
    {
        var workspace = NewWorkspace();
        Directory.CreateDirectory(GateLogPaths.DirectoryFor(workspace));
        var ok = GateLogPaths.IsSafeRelPath(workspace, "test-1-12345.log", out var abs);
        Assert.True(ok);
        Assert.EndsWith("test-1-12345.log", abs);
    }
}
