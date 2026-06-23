// -----------------------------------------------------------------------
// <copyright file="StoryBuildRunnerGoCaptureTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Generic;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Lock-in for <see cref="StoryBuildRunner.ShouldCaptureGoFailLine"/> — the
/// verdict-aware streaming capture that feeds the test-gate's bounded Go
/// failure buffer. The capture must surface real failures (`--- FAIL:`,
/// `panic:`, `# pkg` build errors, `FAIL pkg [build failed]`) and their
/// `file.go:NN:` detail, while dropping the byte-identical detail lines emitted
/// by `t.Log` / `t.Skip` under passing/skipped tests.
///
/// Root cause this guards: code-intelligence stage 1.2, where a genuine
/// `internal/mcp/backend [build failed]` (`fake_test.go:12:30: undefined: Fake`)
/// was masked in the operator view by a wall of `t.Skip` detail from the
/// passing clean-code service that ran first — the broad `\.go:\d+:` capture
/// filled the bounded buffer with skip noise before the real failure streamed.
/// </summary>
public sealed class StoryBuildRunnerGoCaptureTests
{
    // Mirror of the pump's per-stream call: feed lines through the state machine
    // and collect what would have been captured into goFailLines.
    private static List<string> Capture(string output)
    {
        var captured = new List<string>();
        var inFailContext = false;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (StoryBuildRunner.ShouldCaptureGoFailLine(raw, ref inFailContext))
            {
                captured.Add(raw.TrimEnd());
            }
        }

        return captured;
    }

    [Fact]
    public void Skip_detail_lines_are_not_captured()
    {
        // Verbose `go test -v` output: a skipped test prints its --- SKIP header
        // and a file.go:NN: detail line that is shaped exactly like a failure
        // assertion. Neither must be captured.
        const string output = """
            === RUN   TestDetectHeadSHA_RealGitRepo
            --- SKIP: TestDetectHeadSHA_RealGitRepo (0.01s)
                repocontext_test.go:190: git not installed on this host; skipping live-repo test
            ok      github.com/x/clean-code/internal/cli/repocontext    3.757s
            """;

        Assert.Empty(Capture(output));
    }

    [Fact]
    public void Pass_log_detail_is_not_captured()
    {
        const string output = """
            === RUN   TestOpenMgmtDB_ReusesIngestorHandleWhenSharedOptIn
            --- PASS: TestOpenMgmtDB_ReusesIngestorHandleWhenSharedOptIn (0.00s)
                handler_test.go:88: scenario: churn-writes-no-metric-sample
            PASS
            ok      github.com/x/clean-code/cmd/metric-ingestor    2.678s
            """;

        Assert.Empty(Capture(output));
    }

    [Fact]
    public void Fail_header_and_its_detail_line_are_captured()
    {
        const string output = """
            === RUN   TestReadPrRecords
            --- FAIL: TestReadPrRecords (0.12s)
                pr_records_test.go:88: want 3 records, got 0
            FAIL
            ok      github.com/x/agent-memory/store 0.004s
            """;

        var captured = Capture(output);

        Assert.Contains(captured, l => l.Contains("--- FAIL: TestReadPrRecords"));
        Assert.Contains(captured, l => l.Contains("pr_records_test.go:88: want 3 records, got 0"));
        Assert.Contains(captured, l => l == "FAIL");
    }

    [Fact]
    public void Build_error_block_is_captured()
    {
        // The exact stage-1.2 shape: `# pkg` header opens a build-error context,
        // the `file.go:NN:CC: undefined` detail lines are kept, and the
        // package-level `FAIL … [build failed]` is captured.
        const string output = """
            # github.com/x/agent-memory/internal/mcp/backend [.../backend.test]
            internal\mcp\backend\fake_test.go:12:30: undefined: Fake
            internal\mcp\backend\fake_test.go:14:12: undefined: NewFakeFromFile
            FAIL    github.com/x/agent-memory/internal/mcp/backend [build failed]
            """;

        var captured = Capture(output);

        Assert.Contains(captured, l => l.StartsWith("# github.com/x/agent-memory/internal/mcp/backend"));
        Assert.Contains(captured, l => l.Contains("fake_test.go:12:30: undefined: Fake"));
        Assert.Contains(captured, l => l.Contains("fake_test.go:14:12: undefined: NewFakeFromFile"));
        Assert.Contains(captured, l => l.Contains("[build failed]"));
    }

    [Fact]
    public void Panic_line_is_captured()
    {
        const string output = """
            --- FAIL: TestPartitionMaintainer (0.00s)
            panic: runtime error: invalid memory address or nil pointer dereference
            goroutine 42 [running]:
            """;

        var captured = Capture(output);

        Assert.Contains(captured, l => l.StartsWith("panic:"));
    }

    [Fact]
    public void Real_build_failure_survives_a_wall_of_skip_noise()
    {
        // The masking regression: many passing/skipped packages stream first,
        // each emitting skip detail shaped like a failure, then the real build
        // failure arrives. Only the real failure must be captured — the skip
        // noise must not consume the buffer ahead of it.
        var lines = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            lines.Add($"=== RUN   TestSkipped{i}");
            lines.Add($"--- SKIP: TestSkipped{i} (0.00s)");
            lines.Add($"    thing_test.go:{i}: CLEAN_CODE_PG_URL unset; skipping");
            lines.Add($"ok      github.com/x/clean-code/pkg{i}    0.10s");
        }

        lines.Add("# github.com/x/agent-memory/internal/mcp/backend [.../backend.test]");
        lines.Add(@"internal\mcp\backend\fake_test.go:12:30: undefined: Fake");
        lines.Add("FAIL    github.com/x/agent-memory/internal/mcp/backend [build failed]");

        var captured = Capture(string.Join("\n", lines));

        // None of the 200 skip-detail lines leak through.
        Assert.DoesNotContain(captured, l => l.Contains("skipping"));
        // The real build failure is present.
        Assert.Contains(captured, l => l.Contains("fake_test.go:12:30: undefined: Fake"));
        Assert.Contains(captured, l => l.Contains("[build failed]"));
    }

    [Fact]
    public void All_passing_output_captures_nothing()
    {
        const string output = """
            ok      github.com/x/agent-memory/store 0.004s
            ok      github.com/x/agent-memory/index 0.221s
            ?       github.com/x/agent-memory/cmd/foo    [no test files]
            PASS
            """;

        Assert.Empty(Capture(output));
    }
}
