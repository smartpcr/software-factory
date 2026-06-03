// -----------------------------------------------------------------------
// <copyright file="GoTestFailureParseTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Linq;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Lock-in for <see cref="GateLogPaths.ParseGoTestFailuresFromText"/> — the
/// Go-ecosystem mirror of the dotnet TRX/text parser. The compiler-oriented
/// ErrorLineRegex in StoryBuildRunner never matches Go's `--- FAIL:` /
/// `panic:` / `FAIL pkg [build failed]` output, so for a Go repo the test-gate
/// ErrorSummary used to degrade to a useless list of trailing `ok` lines.
/// (Root cause: code-intelligence PR-INGESTER stage 5.3 stalled ~1 day on a
/// flaky go-test failure whose name was lost to the `ok`-line tail.)
/// </summary>
public sealed class GoTestFailureParseTests
{
    [Fact]
    public void Parses_per_test_fail_header_with_detail_message()
    {
        const string output = """
            === RUN   TestReadPrRecords
            --- FAIL: TestReadPrRecords (0.12s)
                pr_records_test.go:88: want 3 records, got 0
            FAIL
            ok      github.com/x/agent-memory/store 0.004s
            """;

        var failures = GateLogPaths.ParseGoTestFailuresFromText(output);

        var failure = Assert.Single(failures);
        Assert.Equal("TestReadPrRecords", failure.TestName);
        Assert.Equal("Failed", failure.Outcome);
        Assert.Equal(0.12d, failure.DurationSec, 3);
        Assert.Contains("pr_records_test.go:88", failure.Message);
    }

    [Fact]
    public void Parses_subtest_paths_and_dedupes()
    {
        const string output = """
            --- FAIL: TestSuite/case_one (0.00s)
                suite_test.go:12: boom
            --- FAIL: TestSuite/case_one (0.00s)
            --- FAIL: TestSuite/case_two (1.50s)
            """;

        var failures = GateLogPaths.ParseGoTestFailuresFromText(output);

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.TestName == "TestSuite/case_one");
        Assert.Contains(failures, f => f.TestName == "TestSuite/case_two" && f.DurationSec == 1.50d);
    }

    [Fact]
    public void Parses_package_build_failed_line()
    {
        const string output = """
            # github.com/x/agent-memory/loadtest
            ./loadtest.go:7:2: undefined: Foo
            FAIL    github.com/x/agent-memory/loadtest [build failed]
            """;

        var failures = GateLogPaths.ParseGoTestFailuresFromText(output);

        Assert.Contains(
            failures,
            f => f.TestName == "github.com/x/agent-memory/loadtest" && f.Message == "build failed");
    }

    [Fact]
    public void Parses_panic_line()
    {
        const string output = """
            --- FAIL: TestPartitionMaintainer (0.00s)
            panic: runtime error: invalid memory address or nil pointer dereference [recovered]
            goroutine 42 [running]:
            """;

        var failures = GateLogPaths.ParseGoTestFailuresFromText(output);

        Assert.Contains(failures, f => f.TestName == "panic" && f.Message!.StartsWith("panic:"));
    }

    [Fact]
    public void Returns_empty_for_all_passing_output()
    {
        const string output = """
            ok      github.com/x/agent-memory/store 0.004s
            ok      github.com/x/agent-memory/index 0.221s
            ok      github.com/x/clean-code/audit   1.100s
            """;

        Assert.Empty(GateLogPaths.ParseGoTestFailuresFromText(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_empty_for_blank_input(string? input)
    {
        Assert.Empty(GateLogPaths.ParseGoTestFailuresFromText(input!));
    }

    [Fact]
    public void Caps_results_at_fifty()
    {
        var lines = Enumerable.Range(0, 200)
            .Select(i => $"--- FAIL: TestN{i} (0.00s)");
        var output = string.Join("\n", lines);

        var failures = GateLogPaths.ParseGoTestFailuresFromText(output);

        Assert.Equal(50, failures.Count);
    }
}
