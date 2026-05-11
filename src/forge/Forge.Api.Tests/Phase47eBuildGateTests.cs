// -----------------------------------------------------------------------
// <copyright file="Phase47eBuildGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 47e — hard build gate. After 2026-05-09 production observation
/// (engineer agent deleted 18 production .cs files inside an integration-test
/// workstream and the evaluator scored the diff at 68/iterate without ever
/// noticing the broken build), the workstream-impl path now requires
/// <c>dotnet build</c> success at two trip points:
/// <list type="number">
///   <item>per-iter — three consecutive broken-build iters trip
///         <see cref="BuildFailedException"/>;</item>
///   <item>post-pass — even a single broken build at pass-time aborts the
///         commit + PR flow.</item>
/// </list>
/// Both routes mark the workstream <c>blocked</c> via
/// <see cref="Forge.Api.Services.Stories.StorySpawnOrchestrator"/>'s
/// catch handler. These tests exercise <see cref="StoryBuildRunner"/>
/// directly so the build-gate behaviour is independently verifiable
/// without the full orchestrator stack.
/// </summary>
public class Phase47eBuildGateTests : IAsyncLifetime
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), $"forge-buildgate-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(this.workspace);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.workspace, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A workspace with a healthy classlib csproj returns <c>Success=true</c>
    /// from <see cref="StoryBuildRunner.RunBuildAsync"/>. Sanity-check that
    /// the harness can actually invoke <c>dotnet build</c>.
    /// </summary>
    [Fact]
    public async Task RunBuildAsync_Healthy_Classlib_Returns_Success()
    {
        if (!DotnetAvailable()) return;
        WriteHealthyClasslib(this.workspace);

        var result = await StoryBuildRunner.RunBuildAsync(
            this.workspace, CancellationToken.None, TimeSpan.FromMinutes(2));

        Assert.True(result.Success, $"build failed: exit={result.ExitCode} summary={result.ErrorSummary}");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.ErrorCount);
        Assert.False(result.TimedOut);
    }

    /// <summary>
    /// Headline regression: a workspace with a deliberately broken .cs
    /// (references a type that doesn't exist) returns <c>Success=false</c>
    /// AND populates <see cref="StoryBuildResult.ErrorSummary"/> + a
    /// non-zero <see cref="StoryBuildResult.ErrorCount"/>. This is what
    /// the per-iter and post-pass build gates feed into
    /// <see cref="BuildFailedException"/>.
    /// </summary>
    [Fact]
    public async Task RunBuildAsync_Broken_Classlib_Reports_Errors_With_Summary()
    {
        if (!DotnetAvailable()) return;
        WriteBrokenClasslib(this.workspace);

        var result = await StoryBuildRunner.RunBuildAsync(
            this.workspace, CancellationToken.None, TimeSpan.FromMinutes(2));

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(result.ErrorCount >= 1,
            $"expected at least one error parsed; got {result.ErrorCount}. summary={result.ErrorSummary}");
        Assert.False(string.IsNullOrEmpty(result.ErrorSummary));
        // The CSC error code namespace shows up in stdout for missing-type
        // errors (CS0246 "type or namespace name 'X' could not be found").
        Assert.Contains("error", result.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.TimedOut);
    }

    /// <summary>
    /// A non-existent workspace path returns <c>Success=false</c> without
    /// trying to launch <c>dotnet</c> — so the per-iter build gate degrades
    /// gracefully when the worktree was wiped between iters.
    /// </summary>
    [Fact]
    public async Task RunBuildAsync_Missing_Workspace_Returns_Failure_Without_Spawning_Dotnet()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"forge-nonexistent-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(nonexistent));

        var result = await StoryBuildRunner.RunBuildAsync(
            nonexistent, CancellationToken.None, TimeSpan.FromSeconds(1));

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("does not exist", result.ErrorSummary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="BuildFailedException"/> carries the error count + summary
    /// + per-iter trip metadata so the orchestrator's catch handler can
    /// publish a useful spawn-finished payload to the transcript.
    /// </summary>
    [Fact]
    public void BuildFailedException_Carries_Diagnostic_Fields()
    {
        var ex = new BuildFailedException("build broke")
        {
            ExitCode = 1,
            ErrorCount = 4,
            WarningCount = 0,
            ErrorSummary = "src/Foo.cs(12,9): error CS0246: missing type 'Bar'",
            TrippedByConsecutiveIters = true,
            ConsecutiveBrokenIters = 3,
            LastIteration = 7,
        };

        Assert.Equal("build broke", ex.Message);
        Assert.Equal(1, ex.ExitCode);
        Assert.Equal(4, ex.ErrorCount);
        Assert.Contains("CS0246", ex.ErrorSummary);
        Assert.True(ex.TrippedByConsecutiveIters);
        Assert.Equal(3, ex.ConsecutiveBrokenIters);
        Assert.Equal(7, ex.LastIteration);
    }

    /// <summary>
    /// Phase 49 — when the caller supplies a custom <c>buildCommand</c>,
    /// the runner must honour those args instead of unconditionally
    /// appending <c>build --nologo --verbosity minimal</c>. Regression
    /// guard for the args bug observed on the failover-cluster:XRAFT
    /// stage 1.1 spawn (2026-05-10): the runner happily started
    /// <c>cargo</c> but then sent it dotnet-flavour switches which
    /// cargo can't parse.
    /// </summary>
    [Fact]
    public async Task RunBuildAsync_Custom_BuildCommand_Honors_Parsed_Args()
    {
        // `dotnet --info` exits 0 cleanly without needing a project. If
        // args were ignored (the pre-fix behaviour), the runner would
        // launch `dotnet --info build --nologo --verbosity minimal` and
        // dotnet would treat `build` as a sub-command in the workspace,
        // failing with MSBUILD : error MSB1003 (no project file). With
        // the fix, only `--info` reaches dotnet and the call exits 0.
        if (!DotnetAvailable()) return;

        var result = await StoryBuildRunner.RunBuildAsync(
            this.workspace,
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            buildCommand: "dotnet --info");

        Assert.True(result.Success,
            $"expected success when args are honoured; got exit={result.ExitCode} summary={result.ErrorSummary}");
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// Phase 49 — when the program in <c>buildCommand</c> isn't on PATH
    /// (e.g. <c>cargo</c> on a Windows worker without the Rust
    /// toolchain), the runner must convert the Win32Exception into a
    /// failed <see cref="StoryBuildResult"/> instead of throwing.
    /// Without this fix, a missing toolchain would propagate to
    /// <c>StorySpawnOrchestrator</c>'s generic catch and route the
    /// workstream into the retry loop instead of the BuildFailed →
    /// blocked path, hiding the real problem.
    /// </summary>
    [Fact]
    public async Task RunBuildAsync_Missing_Program_Returns_Failure_Without_Throwing()
    {
        var result = await StoryBuildRunner.RunBuildAsync(
            this.workspace,
            CancellationToken.None,
            TimeSpan.FromSeconds(5),
            buildCommand: "definitely-not-a-real-program-xyz999 build");

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.ErrorSummary));
        Assert.Contains("definitely-not-a-real-program-xyz999",
            result.ErrorSummary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Phase 49 — <see cref="StoryBuildRunner.ParseCommandLine"/> must
    /// keep quoted whitespace together so a build command like
    /// <c>dotnet build "My Solution.sln"</c> (emitted by
    /// <c>BuildSystemDetector.QuotePath</c> when the .sln file has a
    /// space in its name) parses to three tokens, not four.
    /// </summary>
    [Theory]
    [InlineData("dotnet build", new[] { "dotnet", "build" })]
    [InlineData("dotnet build Workflow.sln", new[] { "dotnet", "build", "Workflow.sln" })]
    [InlineData("cargo build", new[] { "cargo", "build" })]
    [InlineData("npm run build", new[] { "npm", "run", "build" })]
    [InlineData("go build ./...", new[] { "go", "build", "./..." })]
    [InlineData("dotnet build \"My Solution.sln\"", new[] { "dotnet", "build", "My Solution.sln" })]
    [InlineData("  dotnet   build  ", new[] { "dotnet", "build" })]
    [InlineData("dotnet\tbuild\tWorkflow.sln", new[] { "dotnet", "build", "Workflow.sln" })]
    [InlineData("", new string[0])]
    public void ParseCommandLine_Splits_With_Quote_And_Whitespace_Awareness(string input, string[] expected)
    {
        var actual = StoryBuildRunner.ParseCommandLine(input);
        Assert.Equal(expected, actual);
    }

    // ── Fixture helpers ─────────────────────────────────────────────────

    private static void WriteHealthyClasslib(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "test.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <LangVersion>latest</LangVersion>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Sample.cs"), """
            namespace TestProject;
            public sealed class Sample
            {
                public int Add(int a, int b) => a + b;
            }
            """);
    }

    private static void WriteBrokenClasslib(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "test.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <LangVersion>latest</LangVersion>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>
            </Project>
            """);
        // References MissingType — guaranteed CS0246. The per-iter gate
        // would see this and increment consecutiveBrokenBuild.
        File.WriteAllText(Path.Combine(dir, "Broken.cs"), """
            namespace TestProject;
            public sealed class Broken
            {
                public MissingType Field { get; set; } = default!;
            }
            """);
    }

    private static bool DotnetAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.WaitForExit(3_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
