// -----------------------------------------------------------------------
// <copyright file="BuildValidationTrxCaptureTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// 2026-05-25 — contract for the TRX-capture pipeline that surfaces
/// failed test names + assertion messages into the next-iter generator
/// prompt (so the agent can fix the failing tests before opening a PR).
/// Covers four units:
///
/// <list type="number">
///   <item><see cref="GateLogPaths.EnsureGitignoreLinesAsync"/> is
///   idempotent and tolerates a missing <c>.gitignore</c> file.</item>
///   <item><see cref="GateLogPaths.CollectNewTrxFiles"/> respects the
///   <c>sinceUtc</c> filter and skips excluded subdirectories (critical
///   to avoid worktree contamination in repo-level validate runs).</item>
///   <item><see cref="GateLogPaths.ParseTrxFailures"/> extracts failed
///   <c>UnitTestResult</c> nodes from both namespaced (vstest standard)
///   and namespace-less TRX shapes.</item>
///   <item><see cref="GateLogPaths.ParseDotnetTestFailuresFromText"/>
///   scrapes <c>  Failed FQTN</c> lines from <c>dotnet test</c> stdout —
///   the fallback for repos that don't emit TRX by default (e.g. the
///   <c>update</c> repo's build.proj driver).</item>
/// </list>
/// </summary>
public class BuildValidationTrxCaptureTests
{
    // ── EnsureGitignoreLinesAsync ────────────────────────────────────────

    [Fact]
    public async Task EnsureGitignoreLines_AppendsMissingEntries_WhenFileAbsent()
    {
        var root = NewTempDir();
        try
        {
            var ok = await GateLogPaths.EnsureGitignoreLinesAsync(
                root, new[] { ".worktree/", ".forge/" });

            Assert.True(ok);
            var lines = File.ReadAllLines(Path.Combine(root, ".gitignore"));
            Assert.Contains(".worktree/", lines);
            Assert.Contains(".forge/", lines);
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task EnsureGitignoreLines_DoesNotDuplicate_OnRepeatedCalls()
    {
        var root = NewTempDir();
        try
        {
            await GateLogPaths.EnsureGitignoreLinesAsync(root, new[] { ".forge/" });
            await GateLogPaths.EnsureGitignoreLinesAsync(root, new[] { ".forge/" });
            await GateLogPaths.EnsureGitignoreLinesAsync(root, new[] { ".forge/" });

            var lines = File.ReadAllLines(Path.Combine(root, ".gitignore"));
            // .forge/ should appear exactly once regardless of how many calls.
            Assert.Equal(1, lines.Count(l => l.Trim() == ".forge/"));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task EnsureGitignoreLines_TreatsTrailingSlashVariants_AsEquivalent()
    {
        var root = NewTempDir();
        try
        {
            // Pre-seed with ".forge" (no trailing slash).
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".forge\n");

            await GateLogPaths.EnsureGitignoreLinesAsync(root, new[] { ".forge/" });

            var content = File.ReadAllText(Path.Combine(root, ".gitignore"));
            // Helper must not double-add ".forge/" when ".forge" is already there.
            Assert.DoesNotContain(".forge/\n", content);
            Assert.Contains(".forge", content);
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    // ── CollectNewTrxFiles ───────────────────────────────────────────────

    [Fact]
    public void CollectNewTrxFiles_ReturnsOnlyFilesModifiedAfterSnapshot()
    {
        var root = NewTempDir();
        try
        {
            var oldTrx = Path.Combine(root, "old.trx");
            File.WriteAllText(oldTrx, MinimalTrx(failedTests: 0));
            File.SetLastWriteTimeUtc(oldTrx, DateTime.UtcNow.AddHours(-1));

            // Snapshot AFTER the old file's mtime, BEFORE we write the new one.
            var snapshot = DateTime.UtcNow.AddMinutes(-5);

            var newTrx = Path.Combine(root, "new.trx");
            File.WriteAllText(newTrx, MinimalTrx(failedTests: 1));
            File.SetLastWriteTimeUtc(newTrx, DateTime.UtcNow);

            var found = GateLogPaths.CollectNewTrxFiles(root, snapshot);
            Assert.Contains(found, p => Path.GetFileName(p) == "new.trx");
            Assert.DoesNotContain(found, p => Path.GetFileName(p) == "old.trx");
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void CollectNewTrxFiles_ExcludesNamedSubdirectoriesAtAnyDepth()
    {
        // Critical for repo-level validate runs: a concurrent iteration
        // gate may be writing TRX under {repo}/.worktree/ws-X/ and we
        // must not pick those up — they belong to a different
        // workstream's test pass.
        var root = NewTempDir();
        try
        {
            var workTrxDir = Path.Combine(root, ".worktree", "ws-foo", "TestResults");
            Directory.CreateDirectory(workTrxDir);
            var workTrx = Path.Combine(workTrxDir, "worktree.trx");
            File.WriteAllText(workTrx, MinimalTrx(failedTests: 1));

            var goodTrxDir = Path.Combine(root, "src", "MyProject", "TestResults");
            Directory.CreateDirectory(goodTrxDir);
            var goodTrx = Path.Combine(goodTrxDir, "good.trx");
            File.WriteAllText(goodTrx, MinimalTrx(failedTests: 1));

            // Also seed our own .forge/logs/trx/ which must be excluded
            // (so a re-scan doesn't pick up files we copied last run).
            var prevCopyDir = Path.Combine(root, ".forge", "logs", "trx", "validate-prev");
            Directory.CreateDirectory(prevCopyDir);
            var prevCopy = Path.Combine(prevCopyDir, "previous.trx");
            File.WriteAllText(prevCopy, MinimalTrx(failedTests: 1));

            var snapshot = DateTime.UtcNow.AddMinutes(-10);
            var found = GateLogPaths.CollectNewTrxFiles(
                root, snapshot,
                excludeDirNames: new[] { ".worktree", ".forge" });

            Assert.Contains(found, p => p.EndsWith("good.trx", StringComparison.Ordinal));
            Assert.DoesNotContain(found, p => p.EndsWith("worktree.trx", StringComparison.Ordinal));
            Assert.DoesNotContain(found, p => p.EndsWith("previous.trx", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    // ── ParseTrxFailures (namespaced + namespace-less) ───────────────────

    [Fact]
    public void ParseTrxFailures_ExtractsFailedNodes_FromNamespacedTrx()
    {
        var root = NewTempDir();
        try
        {
            var trxPath = Path.Combine(root, "namespaced.trx");
            File.WriteAllText(trxPath, NamespacedTrxWith(
                ("UpdateResourceProvider.Tests.X.TestA", "Failed", "Expected 42 but got 17"),
                ("UpdateResourceProvider.Tests.X.TestB", "Failed", "Connection refused"),
                ("UpdateResourceProvider.Tests.X.TestC", "Passed", null)));

            var failures = GateLogPaths.ParseTrxFailures(trxPath);

            Assert.Equal(2, failures.Count);
            Assert.Contains(failures, f => f.TestName == "UpdateResourceProvider.Tests.X.TestA" && f.Message!.Contains("Expected 42"));
            Assert.Contains(failures, f => f.TestName == "UpdateResourceProvider.Tests.X.TestB" && f.Message!.Contains("Connection refused"));
            Assert.DoesNotContain(failures, f => f.TestName == "UpdateResourceProvider.Tests.X.TestC");
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void ParseTrxFailures_ExtractsFailedNodes_FromNamespaceLessTrx()
    {
        // Older or custom loggers may emit namespace-less TRX. We
        // compare on local names so both shapes work.
        var root = NewTempDir();
        try
        {
            var trxPath = Path.Combine(root, "bare.trx");
            File.WriteAllText(trxPath, MinimalTrx(failedTests: 2));

            var failures = GateLogPaths.ParseTrxFailures(trxPath);
            Assert.Equal(2, failures.Count);
            Assert.All(failures, f => Assert.Equal("Failed", f.Outcome));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void ParseTrxFailures_ReturnsEmpty_OnMalformedXml()
    {
        var root = NewTempDir();
        try
        {
            var trxPath = Path.Combine(root, "broken.trx");
            File.WriteAllText(trxPath, "<not actually xml");

            var failures = GateLogPaths.ParseTrxFailures(trxPath);
            Assert.Empty(failures);
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    // ── ParseDotnetTestFailuresFromText (stdout fallback) ────────────────

    [Fact]
    public void ParseDotnetTestFailuresFromText_ExtractsFailedLines_FromDotnetTestOutput()
    {
        // Representative `dotnet test` failure block format.
        var stdout =
            "Test run for UpdateResourceProvider.UpdateService.IntegrationTest.dll (.NETCoreApp,Version=v8.0)\n" +
            "Microsoft (R) Test Execution Command Line Tool Version 17.10.0\n" +
            "Starting test execution, please wait...\n" +
            "A total of 1 test files matched the specified pattern.\n" +
            "  Failed UpdateResourceProvider.Tests.UpdateService.ScanForUpdatesTests.WhenServiceUnavailable_ReturnsEmpty [Duration: 5 s]\n" +
            "    Error Message:\n" +
            "     Expected `ServiceUnavailable` but actual response was `OK`.\n" +
            "    Stack Trace:\n" +
            "     at UpdateResourceProvider.Tests.UpdateService.ScanForUpdatesTests... in C:\\src\\test.cs:line 42\n" +
            "  Failed UpdateResourceProvider.Tests.UpdateService.RestartTests.ShouldRestart [Duration: 12 s]\n" +
            "    Error Message:\n" +
            "     Timeout waiting for restart\n" +
            "Failed!  - Failed: 2, Passed: 276, Skipped: 0, Total: 278, Duration: 33m 12s\n";

        var failures = GateLogPaths.ParseDotnetTestFailuresFromText(stdout);

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.TestName == "UpdateResourceProvider.Tests.UpdateService.ScanForUpdatesTests.WhenServiceUnavailable_ReturnsEmpty");
        Assert.Contains(failures, f => f.TestName == "UpdateResourceProvider.Tests.UpdateService.RestartTests.ShouldRestart");
        var first = failures.First(f => f.TestName.EndsWith("WhenServiceUnavailable_ReturnsEmpty", StringComparison.Ordinal));
        Assert.NotNull(first.Message);
        Assert.Contains("Expected `ServiceUnavailable`", first.Message);
    }

    [Fact]
    public void ParseDotnetTestFailuresFromText_IgnoresSummaryFailedLine()
    {
        // The "Failed!" summary should not be mis-captured as a test name.
        var stdout = "Failed!  - Failed: 4, Passed: 276, Skipped: 0, Total: 280, Duration: 5m 33s\n";
        var failures = GateLogPaths.ParseDotnetTestFailuresFromText(stdout);
        Assert.Empty(failures);
    }

    [Fact]
    public void ParseDotnetTestFailuresFromText_DedupesIdenticalNames()
    {
        // Some test runners emit the failure line twice (xunit stderr +
        // stdout). Names must be unique in the output.
        var stdout =
            "  Failed Foo.Bar.Baz [Duration: 1 s]\n" +
            "  Failed Foo.Bar.Baz [Duration: 1 s]\n";
        var failures = GateLogPaths.ParseDotnetTestFailuresFromText(stdout);
        Assert.Single(failures);
    }

    // ── FormatFailureSummary ─────────────────────────────────────────────

    [Fact]
    public void FormatFailureSummary_RendersHumanReadableBlock_WithCapping()
    {
        var failures = new[]
        {
            new GateLogPaths.TrxFailure("Foo.Bar.Test1", "Failed", 1.0, "Short message", null),
            new GateLogPaths.TrxFailure("Foo.Bar.Test2", "Failed", 1.0, new string('x', 1000), null),
        };

        var summary = GateLogPaths.FormatFailureSummary(failures);

        Assert.Contains("FAILED TESTS (2)", summary);
        Assert.Contains("Foo.Bar.Test1", summary);
        Assert.Contains("Foo.Bar.Test2", summary);
        // Per-message body cap is 240 chars; the 1000-x message must be elided.
        Assert.Contains("…", summary);
    }

    // ── Test helpers ─────────────────────────────────────────────────────

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "forge-trx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Namespace-less TRX with the given number of Failed UnitTestResult nodes.</summary>
    private static string MinimalTrx(int failedTests)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<TestRun>");
        sb.AppendLine("  <Results>");
        for (var i = 0; i < failedTests; i++)
        {
            sb.AppendLine($"    <UnitTestResult testName=\"BareTest{i}\" outcome=\"Failed\" duration=\"00:00:01.0000000\">");
            sb.AppendLine("      <Output>");
            sb.AppendLine("        <ErrorInfo>");
            sb.AppendLine($"          <Message>assert failed in BareTest{i}</Message>");
            sb.AppendLine($"          <StackTrace>at BareTest{i}() in C:\\test.cs:line 1</StackTrace>");
            sb.AppendLine("        </ErrorInfo>");
            sb.AppendLine("      </Output>");
            sb.AppendLine("    </UnitTestResult>");
        }
        sb.AppendLine("  </Results>");
        sb.AppendLine("</TestRun>");
        return sb.ToString();
    }

    /// <summary>Namespaced (vstest schema) TRX with arbitrary results.</summary>
    private static string NamespacedTrxWith(params (string name, string outcome, string? message)[] results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">");
        sb.AppendLine("  <Results>");
        foreach (var (name, outcome, message) in results)
        {
            sb.AppendLine($"    <UnitTestResult testName=\"{name}\" outcome=\"{outcome}\" duration=\"00:00:01.0000000\">");
            if (outcome == "Failed" && message is not null)
            {
                sb.AppendLine("      <Output>");
                sb.AppendLine("        <ErrorInfo>");
                sb.AppendLine($"          <Message>{System.Security.SecurityElement.Escape(message)}</Message>");
                sb.AppendLine($"          <StackTrace>at {name}() in C:\\test.cs:line 1</StackTrace>");
                sb.AppendLine("        </ErrorInfo>");
                sb.AppendLine("      </Output>");
            }
            sb.AppendLine("    </UnitTestResult>");
        }
        sb.AppendLine("  </Results>");
        sb.AppendLine("</TestRun>");
        return sb.ToString();
    }
}
