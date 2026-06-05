// -----------------------------------------------------------------------
// <copyright file="ChildProcessEnvIsolationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Agents;
using Xunit;

/// <summary>
/// Locks the environment-isolation guarantee in code (not via the agent
/// prompt): no child build/agent process may inherit Forge.Api's MSBuild
/// SDK-resolver pollution. Regression guard for the 2026-05-25 root cause
/// where the engineer agent's restore/build died with MSB4276 because the
/// Copilot CLI inherited Forge.Api's RegisterDefaults() env, burning the full
/// 24-min budget at score 0. See <see cref="ChildProcessEnv"/>.
/// </summary>
public class ChildProcessEnvIsolationTests
{
    private static readonly string[] PollutionVars =
    {
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildSDKsPath",
    };

    [Fact]
    public void DenyList_covers_every_msbuild_sdk_resolver_var()
    {
        foreach (var v in PollutionVars)
        {
            Assert.True(ChildProcessEnv.IsDenied(v), $"{v} must be denied to child processes.");
            // Case-insensitive — Windows env var names are case-insensitive.
            Assert.True(ChildProcessEnv.IsDenied(v.ToLowerInvariant()));
        }
    }

    [Fact]
    public void ToDictionary_never_contains_pollution_even_when_live_env_has_it()
    {
        // Simulate MSBuildLocator having polluted the live process env AFTER
        // the baseline was captured. The clean baseline must not carry it.
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", @"C:\Program Files\dotnet\sdk\8.0.421\Sdks");
        Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", @"C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll");
        try
        {
            var env = ChildProcessEnv.ToDictionary();
            foreach (var v in PollutionVars)
            {
                Assert.False(env.ContainsKey(v), $"clean child env leaked {v}.");
            }

            // Sanity: a fresh-shell essential survives so the child can still run.
            Assert.True(env.ContainsKey("PATH") || env.ContainsKey("Path") || env.ContainsKey("SystemRoot"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBuildSDKsPath", null);
            Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", null);
        }
    }

    [Fact]
    public void Apply_strips_inherited_pollution_from_ProcessStartInfo()
    {
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", @"C:\Program Files\dotnet\sdk\8.0.421\Sdks");
        try
        {
            var psi = new ProcessStartInfo("dotnet");
            // ProcessStartInfo.Environment starts as a copy of the (polluted) live env.
            Assert.True(psi.Environment.ContainsKey("MSBuildSDKsPath"));

            ChildProcessEnv.Apply(psi);

            foreach (var v in PollutionVars)
            {
                Assert.False(psi.Environment.ContainsKey(v), $"Apply left {v} on the child psi.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBuildSDKsPath", null);
        }
    }

    [Fact]
    public void StripDenied_removes_pollution_but_keeps_other_inherited_vars()
    {
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", @"C:\dummy\Sdks");
        try
        {
            var psi = new ProcessStartInfo("dotnet");
            psi.Environment["FORGE_TEST_KEEPER"] = "keep-me";
            Assert.True(psi.Environment.ContainsKey("MSBuildSDKsPath"));

            ChildProcessEnv.StripDenied(psi);

            Assert.False(psi.Environment.ContainsKey("MSBuildSDKsPath"));
            Assert.Equal("keep-me", psi.Environment["FORGE_TEST_KEEPER"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBuildSDKsPath", null);
        }
    }

    /// <summary>
    /// Regression for the 2026-06-04 PR-INGESTER gate stall:
    /// <c>go build ./...</c> failed with <c>'go' is not recognized as an
    /// internal or external command</c> on every per-iter build because
    /// Forge.Api had been launched from a shell whose PATH predated a
    /// <c>C:\Program Files\Go\bin</c> install — <see cref="ChildProcessEnv.CaptureBaseline"/>
    /// snapshotted that stale PATH, so every gate subprocess inherited a
    /// PATH without <c>go</c> for the entire lifetime of the host process.
    /// <see cref="ChildProcessEnv.OverlayLiveRegistryPath"/> fixes this by
    /// composing PATH/PSModulePath from the LIVE Machine+User registry on
    /// each child launch — the same way a NEW interactive shell would.
    /// </summary>
    [Fact]
    public void OverlayLiveRegistryPath_replaces_stale_psi_path_with_live_machine_plus_user()
    {
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrEmpty(machine))
        {
            // No Machine PATH visible on this host (extremely unusual; e.g. a
            // container with the registry hive stripped). Skip rather than
            // assert against an empty ground truth.
            return;
        }

        var psi = new ProcessStartInfo("dotnet");
        psi.Environment.Clear();
        psi.Environment["Path"] = @"C:\stale\baseline\path";

        ChildProcessEnv.OverlayLiveRegistryPath(psi);

        Assert.True(psi.Environment.TryGetValue("Path", out var path));
        Assert.False(string.IsNullOrEmpty(path));

        // The stale baseline value must have been replaced (not appended to).
        Assert.DoesNotContain(@"C:\stale\baseline\path", path!, StringComparison.OrdinalIgnoreCase);

        // The first segment of the live Machine PATH (after %SystemRoot%
        // expansion) must be present — proves we sourced from the live
        // registry, not the captured baseline.
        var firstMachineSegment = Environment.ExpandEnvironmentVariables(machine)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .First();
        Assert.Contains(firstMachineSegment, path!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverlayLiveRegistryPath_composes_machine_then_user_in_logon_order()
    {
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(machine) || string.IsNullOrEmpty(user))
        {
            // Need both halves to verify ordering; skip on hosts that lack one.
            return;
        }

        var psi = new ProcessStartInfo("dotnet");
        psi.Environment.Clear();

        ChildProcessEnv.OverlayLiveRegistryPath(psi);

        Assert.True(psi.Environment.TryGetValue("Path", out var path));
        var expected = Environment.ExpandEnvironmentVariables(machine).TrimEnd(';')
            + ";" + Environment.ExpandEnvironmentVariables(user);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void OverlayLiveRegistryPath_is_idempotent_and_never_throws()
    {
        var psi = new ProcessStartInfo("dotnet");

        var ex1 = Record.Exception(() => ChildProcessEnv.OverlayLiveRegistryPath(psi));
        var ex2 = Record.Exception(() => ChildProcessEnv.OverlayLiveRegistryPath(psi));

        Assert.Null(ex1);
        Assert.Null(ex2);
    }
}
