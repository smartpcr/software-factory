// -----------------------------------------------------------------------
// <copyright file="Phase49TestGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 49 follow-up — pin the post-pass TEST gate that runs after
/// the build + quality gates pass and before Forge opens a PR.
/// <para>The new gate uses the same plumbing as the existing build /
/// quality gates: <see cref="StoryBuildRunner.RunBuildAsync"/> with a
/// resolved command, emit telemetry, throw <see cref="BuildFailedException"/>
/// on failure to route the workstream to <c>stalled</c> via
/// <c>DriveImplementAsync</c>'s catch. This test pins the parts that
/// can be exercised without a full app-host: detector → test command
/// resolution, runner exit-code propagation, and the build-runner's
/// error-summary extraction against a real test failure.</para>
/// <para>Whole-orchestrator integration is covered by Phase26 +
/// Phase49 spawn-orchestrator suites; the new test-gate trip point
/// is structurally identical to the existing build / quality gate
/// trip points so it inherits their coverage.</para>
/// </summary>
public class Phase49TestGateTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-testgate-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;
        Directory.CreateDirectory(this.repoRoot);
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.repoRoot, recursive: true); }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="BuildSystemDetector.Detect"/> picks the right
    /// ecosystem-default test command for each supported layout. The
    /// test gate falls back to this when <see cref="Forge.Api.Persistence.RepoRow.TestCommand"/>
    /// isn't operator-overridden, so wrong defaults silently turn the
    /// gate into a no-op for that ecosystem.
    /// </summary>
    [Theory]
    [InlineData("Cargo.toml",        "cargo test")]
    [InlineData("go.mod",            "go test ./...")]
    public async Task BuildSystemDetector_TestCommand_PicksEcosystemDefault(
        string fixtureFile, string expectedTestCommand)
    {
        var dir = Path.Combine(this.repoRoot, $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, fixtureFile), string.Empty);

        var detected = BuildSystemDetector.Detect(dir);

        Assert.False(detected.Ambiguous, $"detector flagged the {fixtureFile} fixture as ambiguous");
        Assert.Equal(expectedTestCommand, detected.TestCommand);
    }

    /// <summary>
    /// <see cref="BuildSystemDetector"/> uses a different shape for
    /// .NET (a parsed sln/csproj filename gets baked into the test
    /// command). Verify the resolution path produces a non-null,
    /// dotnet-test-shaped string when a single sln is present.
    /// </summary>
    [Fact]
    public async Task BuildSystemDetector_DotnetSln_TestCommandContainsDotnetTest()
    {
        var dir = Path.Combine(this.repoRoot, $"dotnet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "MyApp.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00\n");

        var detected = BuildSystemDetector.Detect(dir);

        Assert.False(detected.Ambiguous);
        Assert.NotNull(detected.TestCommand);
        Assert.StartsWith("dotnet test ", detected.TestCommand);
        Assert.Contains("MyApp.sln", detected.TestCommand);
    }

    /// <summary>
    /// Ambiguous detection (multiple sln files at the root) marks
    /// <see cref="BuildSystemDetector.DetectedCommands.Ambiguous"/>
    /// true so callers can ignore the best-guess TestCommand. The
    /// gate path threads this through <c>ResolveTestCommand</c> as
    /// "no test command resolved → skip the gate" rather than
    /// failing closed, mirroring how the build gate handles ambiguous
    /// workspaces.
    /// </summary>
    [Fact]
    public async Task BuildSystemDetector_AmbiguousLayout_FlagsAmbiguousTrue()
    {
        var dir = Path.Combine(this.repoRoot, $"ambig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "A.sln"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(dir, "B.sln"), string.Empty);

        var detected = BuildSystemDetector.Detect(dir);

        Assert.True(detected.Ambiguous,
            "detector should flag multiple .sln files as ambiguous so ResolveTestCommand returns null");
        Assert.False(string.IsNullOrEmpty(detected.AmbiguityDetail),
            "AmbiguityDetail should describe why detection was inconclusive");
    }

    /// <summary>
    /// End-to-end runner check — <see cref="StoryBuildRunner.RunBuildAsync"/>
    /// against a real cargo workspace whose tests deliberately fail
    /// returns Success=false with a non-zero ExitCode AND a non-empty
    /// ErrorSummary. This is exactly the input the test-gate trip
    /// point inspects to decide whether to throw
    /// <see cref="BuildFailedException"/>; failure to surface a
    /// non-zero exit here would let the gate accept a broken diff.
    /// </summary>
    [Fact]
    public async Task StoryBuildRunner_FailingCargoTest_ReturnsFailureWithErrorSummary()
    {
        if (!GitAvailable()) return;
        if (!CargoAvailable())
        {
            // Cargo not installed on this runner; skip the heavy test
            // rather than fail. The unit tests above still cover the
            // detector + resolution paths.
            return;
        }

        var crateDir = Path.Combine(this.repoRoot, $"crate-{Guid.NewGuid():N}");
        await SeedFailingTestCrateAsync(crateDir);

        var result = await StoryBuildRunner.RunBuildAsync(
            crateDir, CancellationToken.None, TimeSpan.FromMinutes(2),
            buildCommand: "cargo test");

        Assert.False(result.Success, $"expected failure, got exit={result.ExitCode}");
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorSummary),
            "ErrorSummary should carry the test-failure context the gate surfaces in its BuildFailedException");
    }

    /// <summary>
    /// Companion to the failing-tests case — verify the runner
    /// correctly reports Success=true for a crate whose tests pass.
    /// Without this, a regression that always returned failure would
    /// silently stall every workstream regardless of test outcome.
    /// </summary>
    [Fact]
    public async Task StoryBuildRunner_PassingCargoTest_ReturnsSuccess()
    {
        if (!GitAvailable()) return;
        if (!CargoAvailable()) return;

        var crateDir = Path.Combine(this.repoRoot, $"crate-{Guid.NewGuid():N}");
        await SeedPassingTestCrateAsync(crateDir);

        var result = await StoryBuildRunner.RunBuildAsync(
            crateDir, CancellationToken.None, TimeSpan.FromMinutes(2),
            buildCommand: "cargo test");

        Assert.True(result.Success, $"expected success, got exit={result.ExitCode}\n{result.ErrorSummary}");
        Assert.Equal(0, result.ExitCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>Create a minimal cargo crate with one always-failing unit test.</summary>
    private static async Task SeedFailingTestCrateAsync(string crateDir)
    {
        Directory.CreateDirectory(crateDir);
        var src = Path.Combine(crateDir, "src");
        Directory.CreateDirectory(src);

        await File.WriteAllTextAsync(Path.Combine(crateDir, "Cargo.toml"), """
            [package]
            name = "test-gate-fixture"
            version = "0.1.0"
            edition = "2021"

            [dependencies]
            """);

        await File.WriteAllTextAsync(Path.Combine(src, "lib.rs"), """
            pub fn add(a: i32, b: i32) -> i32 {
                a + b
            }

            #[cfg(test)]
            mod tests {
                use super::*;

                #[test]
                fn test_always_fails() {
                    // Deliberately wrong assertion so the test fails.
                    assert_eq!(add(1, 2), 5, "test gate fixture: this assertion always fails");
                }
            }
            """);
    }

    /// <summary>Create a minimal cargo crate with one passing unit test.</summary>
    private static async Task SeedPassingTestCrateAsync(string crateDir)
    {
        Directory.CreateDirectory(crateDir);
        var src = Path.Combine(crateDir, "src");
        Directory.CreateDirectory(src);

        await File.WriteAllTextAsync(Path.Combine(crateDir, "Cargo.toml"), """
            [package]
            name = "test-gate-fixture-ok"
            version = "0.1.0"
            edition = "2021"

            [dependencies]
            """);

        await File.WriteAllTextAsync(Path.Combine(src, "lib.rs"), """
            pub fn add(a: i32, b: i32) -> i32 {
                a + b
            }

            #[cfg(test)]
            mod tests {
                use super::*;

                #[test]
                fn test_addition() {
                    assert_eq!(add(1, 2), 3);
                }
            }
            """);
    }

    private static bool GitAvailable() => CommandAvailable("git", "--version");
    private static bool CargoAvailable() => CommandAvailable("cargo", "--version");

    private static bool CommandAvailable(string exe, string arg)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
