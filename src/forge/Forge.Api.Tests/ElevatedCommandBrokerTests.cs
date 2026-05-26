// -----------------------------------------------------------------------
// <copyright file="ElevatedCommandBrokerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Agents;
using Xunit;

/// <summary>
/// Integration coverage for the privilege-split test broker
/// (<see cref="ElevatedCommandBroker"/>): a non-elevated client marshals a
/// build/test command over a named pipe to the broker, which runs it and
/// returns the result. These tests exercise the REAL pipe + framing + JSON
/// envelope + op dispatch + subprocess execution end-to-end, in-process.
///
/// <para>What is NOT covered automatically: the actual medium→high integrity
/// hop needs an elevated broker + UAC, which can't run unattended. Here both
/// ends share the test process's integrity level (the SACL stamp fails without
/// SeSecurityPrivilege and <see cref="ElevatedCommandBroker"/> falls back to a
/// DACL-only pipe), so the marshalling path is fully exercised; the elevation
/// hop itself is verified manually via scripts/run-forge-ui.ps1.</para>
///
/// <para>All tests run on an ISOLATED pipe name (<c>PipeNameOverride</c>) so
/// they never collide with — or send <c>shutdown</c> to — a real broker that
/// may be live on the production per-user pipe. They share one class so xUnit
/// runs them sequentially (the broker is single-instance-per-process).</para>
/// </summary>
public class ElevatedCommandBrokerTests
{
    private static string UniquePipe() => $"forge.test.{Guid.NewGuid():N}";

    // Spin up an in-process broker on the given isolated pipe and wait until it
    // is accepting connections. Returns the loop CTS + the server task so the
    // caller can tear it down.
    private static async Task<(CancellationTokenSource Cts, Task Server)> StartBrokerAsync(string pipe)
    {
        ElevatedCommandBroker.PipeNameOverride = pipe;
        var cts = new CancellationTokenSource();
        var server = Task.Run(() => ElevatedCommandBroker.RunServerAsync(cts.Token));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await ElevatedCommandBroker.IsAvailableAsync(CancellationToken.None, 250))
            {
                return (cts, server);
            }
            await Task.Delay(50);
        }

        cts.Cancel();
        throw new Xunit.Sdk.XunitException($"broker did not start listening on {pipe} within 10s.");
    }

    private static async Task StopBrokerAsync(CancellationTokenSource cts, Task server)
    {
        cts.Cancel();
        try { await server.WaitForAsync(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        cts.Dispose();
        ElevatedCommandBroker.PipeNameOverride = null;
    }

    [Fact]
    public async Task Script_round_trip_runs_the_command_and_returns_success()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipe = UniquePipe();
        var (cts, server) = await StartBrokerAsync(pipe);
        var cwd = Directory.CreateTempSubdirectory("forge-broker-test").FullName;
        try
        {
            var req = new ElevatedScriptRequest(
                Script: "@echo off\r\necho FORGE_MARKER_OK\r\nexit /b 0\r\n",
                Cwd: cwd,
                TimeoutSeconds: 30,
                LogPath: null);

            var (code, stdout, _, timedOut) = await ElevatedCommandBroker.RunScriptAsync(
                req, CancellationToken.None);

            Assert.Equal(0, code);
            Assert.False(timedOut);
            Assert.Contains("FORGE_MARKER_OK", stdout);
        }
        finally
        {
            await StopBrokerAsync(cts, server);
            try { Directory.Delete(cwd, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Script_round_trip_propagates_a_nonzero_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipe = UniquePipe();
        var (cts, server) = await StartBrokerAsync(pipe);
        var cwd = Directory.CreateTempSubdirectory("forge-broker-test").FullName;
        try
        {
            // A failing test command must surface as a non-zero code so the
            // story test gate can fail the iteration (not silently pass).
            var req = new ElevatedScriptRequest(
                Script: "@echo off\r\nexit /b 7\r\n",
                Cwd: cwd,
                TimeoutSeconds: 30,
                LogPath: null);

            var (code, _, _, timedOut) = await ElevatedCommandBroker.RunScriptAsync(
                req, CancellationToken.None);

            Assert.Equal(7, code);
            Assert.False(timedOut);
        }
        finally
        {
            await StopBrokerAsync(cts, server);
            try { Directory.Delete(cwd, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RequestShutdownAsync_stops_the_broker_loop_gracefully()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipe = UniquePipe();
        var (cts, server) = await StartBrokerAsync(pipe);
        try
        {
            Assert.True(await ElevatedCommandBroker.IsAvailableAsync(CancellationToken.None, 500));

            // The graceful path used by run-forge-ui.ps1 -Stop: a non-elevated
            // signal over the pipe stops the accept loop — no cts.Cancel(),
            // no taskkill. The server task must complete on its own.
            await ElevatedCommandBroker.RequestShutdownAsync(CancellationToken.None);

            await server.WaitForAsync(TimeSpan.FromSeconds(5));
            Assert.True(server.IsCompleted, "broker loop did not exit after a shutdown request.");
        }
        finally
        {
            await StopBrokerAsync(cts, server);
        }
    }

    [Fact]
    public async Task RunScriptAsync_throws_BrokerUnavailable_when_no_broker_is_listening()
    {
        if (!OperatingSystem.IsWindows()) return;

        // No server on this pipe → a RunTestsElevated gate must STALL
        // (ElevatedBrokerUnavailableException → BuildFailedException), never
        // silently run the tests non-elevated.
        ElevatedCommandBroker.PipeNameOverride = UniquePipe();
        try
        {
            var req = new ElevatedScriptRequest("@echo off\r\nexit /b 0\r\n", Path.GetTempPath(), 5, null);

            await Assert.ThrowsAsync<ElevatedBrokerUnavailableException>(
                () => ElevatedCommandBroker.RunScriptAsync(req, CancellationToken.None, connectTimeoutMs: 750));
        }
        finally
        {
            ElevatedCommandBroker.PipeNameOverride = null;
        }
    }

    [Fact]
    public async Task RunBuildAsync_throws_BrokerUnavailable_when_no_broker_is_listening()
    {
        if (!OperatingSystem.IsWindows()) return;

        ElevatedCommandBroker.PipeNameOverride = UniquePipe();
        try
        {
            var req = new ElevatedBuildRequest(
                Workspace: Path.GetTempPath(),
                TimeoutMs: 1000,
                BuildCommand: "echo hi",
                LogFilePath: null,
                LogRelPath: null,
                SessionSetupCommands: null,
                CollectTestFailures: false,
                TrxScopeTag: null);

            await Assert.ThrowsAsync<ElevatedBrokerUnavailableException>(
                () => ElevatedCommandBroker.RunBuildAsync(req, CancellationToken.None, connectTimeoutMs: 750));
        }
        finally
        {
            ElevatedCommandBroker.PipeNameOverride = null;
        }
    }

    [Fact]
    public async Task IsAvailableAsync_is_false_when_no_broker_is_listening()
    {
        if (!OperatingSystem.IsWindows()) return;

        ElevatedCommandBroker.PipeNameOverride = UniquePipe();
        try
        {
            Assert.False(await ElevatedCommandBroker.IsAvailableAsync(CancellationToken.None, 500));
        }
        finally
        {
            ElevatedCommandBroker.PipeNameOverride = null;
        }
    }
}

internal static class TaskTimeoutExtensions
{
    /// <summary>Await <paramref name="task"/> but throw <see cref="TimeoutException"/> if it overruns.</summary>
    public static async Task WaitForAsync(this Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException($"task did not complete within {timeout.TotalSeconds:0.#}s.");
        }
        await task; // surface exceptions
    }
}
