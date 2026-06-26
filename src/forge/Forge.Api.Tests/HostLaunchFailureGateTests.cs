// -----------------------------------------------------------------------
// <copyright file="HostLaunchFailureGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.Diagnostics;
using Forge.Api.Agents;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// 2026-06-25 — host process loader/init-failure detection for the build/test
/// gate. Root cause: code-intelligence 3D-RENDER stage 4.1, where
/// <c>go build ./...</c> exited <c>-1073741502</c> (<c>0xC0000142</c>
/// <c>STATUS_DLL_INIT_FAILED</c>) every iter because leaked gate child-process
/// trees had exhausted the Forge.Api host's session desktop heap. The gate was
/// feeding that content-free failure ("no parsed error lines", empty tail) into
/// the engineer agent's iterate-to-fix loop, which had no lever over a HOST
/// init failure — it burned iterations and stalled.
///
/// <para>These tests pin the classifier that now routes such failures to
/// immediate operator triage (via <see cref="BuildFailedException.HostLaunchFailure"/>)
/// instead of the agent loop, and confirm genuine code failures still flow down
/// the normal fix path.</para>
/// </summary>
public class HostLaunchFailureGateTests
{
    /// <summary>
    /// The reported exit code <c>-1073741502</c> is exactly <c>0xC0000142</c>
    /// (<c>STATUS_DLL_INIT_FAILED</c>) and must be recognised as a loader/init
    /// failure.
    /// </summary>
    [Fact]
    public void ReportedExitCode_Is_DllInitFailed_And_Classified_Host()
    {
        Assert.Equal(0xC0000142u, unchecked((uint)(-1073741502)));
        Assert.True(StoryBuildRunner.IsHostProcessInitFailureExit(-1073741502));
    }

    [Theory]
    // Loader / init failures — process never ran user code → HOST problem.
    [InlineData(unchecked((int)0xC0000142u), true)] // STATUS_DLL_INIT_FAILED
    [InlineData(unchecked((int)0xC0000017u), true)] // STATUS_NO_MEMORY
    [InlineData(unchecked((int)0xC0000135u), true)] // STATUS_DLL_NOT_FOUND
    [InlineData(unchecked((int)0xC0000139u), true)] // STATUS_ENTRYPOINT_NOT_FOUND
    [InlineData(unchecked((int)0xC000007Bu), true)] // STATUS_INVALID_IMAGE_FORMAT
    // Execution crashes — could be a real code bug → NOT a host failure.
    [InlineData(unchecked((int)0xC0000005u), false)] // access violation
    [InlineData(unchecked((int)0xC00000FDu), false)] // stack overflow
    [InlineData(unchecked((int)0xC0000409u), false)] // stack-buffer overrun
    // Normal tool exits + the runner's own sentinel — never host failures.
    [InlineData(0, false)]    // success
    [InlineData(1, false)]    // dotnet/go build error
    [InlineData(2, false)]    // go test "exit status 2"
    [InlineData(101, false)]  // cargo panic
    [InlineData(-1, false)]   // StoryBuildRunner sentinel (start-fail / timeout / no workspace)
    public void Classifier_Distinguishes_LoaderInit_From_Crash_And_Normal(int exitCode, bool expected)
    {
        Assert.Equal(expected, StoryBuildRunner.IsHostProcessInitFailureExit(exitCode));
    }

    /// <summary>
    /// <see cref="StoryBuildResult.IsHostLaunchFailure"/> fires only when the
    /// run failed, produced NO parsed compiler errors, AND exited with a
    /// loader/init code — the signature of the observed gate-leak failure.
    /// </summary>
    [Fact]
    public void IsHostLaunchFailure_True_For_DllInit_With_No_Parsed_Errors()
    {
        var r = new StoryBuildResult(
            Success: false,
            ExitCode: -1073741502, // 0xC0000142
            ErrorCount: 0,
            WarningCount: 0,
            ErrorSummary: "(no parsed error lines; raw output tail below)\n",
            Duration: TimeSpan.FromSeconds(1.09),
            TimedOut: false);

        Assert.True(r.IsHostLaunchFailure);
    }

    /// <summary>
    /// A loader/init exit that nonetheless carried parsed compiler errors is
    /// treated as a CODE failure (the agent has something to act on), so the
    /// host-triage short-circuit does not engage.
    /// </summary>
    [Fact]
    public void IsHostLaunchFailure_False_When_Compiler_Errors_Present()
    {
        var r = new StoryBuildResult(
            Success: false,
            ExitCode: -1073741502,
            ErrorCount: 3,
            WarningCount: 0,
            ErrorSummary: "Foo.cs(10,5): error CS0103: name 'bar' does not exist",
            Duration: TimeSpan.FromSeconds(4),
            TimedOut: false);

        Assert.False(r.IsHostLaunchFailure);
    }

    [Fact]
    public void IsHostLaunchFailure_False_On_Success_And_On_Normal_Failure_And_On_Sentinel()
    {
        // Successful build.
        Assert.False(new StoryBuildResult(true, 0, 0, 0, "", TimeSpan.Zero, false).IsHostLaunchFailure);
        // Ordinary build error (exit 1).
        Assert.False(new StoryBuildResult(false, 1, 0, 0, "go build failed", TimeSpan.FromSeconds(2), false).IsHostLaunchFailure);
        // Runner sentinel (timeout / start failure / missing workspace).
        Assert.False(new StoryBuildResult(false, -1, 0, 0, "timed out", TimeSpan.FromMinutes(8), true).IsHostLaunchFailure);
        // Execution crash (AV) — not a loader/init failure.
        Assert.False(new StoryBuildResult(false, unchecked((int)0xC0000005u), 0, 0, "panic", TimeSpan.FromSeconds(3), false).IsHostLaunchFailure);
    }

    /// <summary>
    /// The per-run kill-on-close job (part of the leak fix) is best-effort: a
    /// null / already-exited process yields an inert instance, and Dispose is
    /// idempotent and never throws — so wrapping it in <c>using</c> in
    /// <see cref="StoryBuildRunner.RunBuildAsync"/> is always safe regardless of
    /// platform or process state.
    /// </summary>
    [Fact]
    public void ChildProcessJob_Inert_For_Exited_Process_And_Dispose_Is_Idempotent()
    {
        // An already-exited process can't be jobbed → inert.
        using (var p = new Process())
        {
            p.StartInfo.FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            p.StartInfo.Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            p.WaitForExit();

            var job = ChildProcessJob.TryCreateAndAssign(p);
            Assert.False(job.IsActive);
            job.Dispose();
            job.Dispose(); // idempotent, no throw
        }
    }
}
