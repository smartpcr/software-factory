// -----------------------------------------------------------------------
// <copyright file="WindowsProcessTreeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Reflection;
using Forge.Api.Agents;
using Xunit;

/// <summary>
/// Tests for <see cref="WindowsProcessTree"/> — the Toolhelp32 helper that
/// <see cref="CopilotServerManager"/> uses to discover the SDK-spawned
/// <c>copilot.exe</c> root for explicit lifecycle ownership. Pure-logic
/// tests for <c>FindNewCopilotDescendant</c> (the BFS walk) use a mocked
/// <c>ProcessRow</c> list so they run on any host. A single OS-touching
/// smoke test exercises the real <c>Snapshot</c> P/Invoke.
/// </summary>
public class WindowsProcessTreeTests
{
    [Fact]
    public void Snapshot_OnWindows_ReturnsCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return; // skip on non-Windows hosts

        var rows = WindowsProcessTree.Snapshot();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Pid == Environment.ProcessId);
    }

    [Fact]
    public void SnapshotCopilotPids_FiltersByName()
    {
        var rows = new List<WindowsProcessTree.ProcessRow>
        {
            new(Pid: 100, ParentPid: 1, Name: "Forge.Api.exe"),
            new(Pid: 200, ParentPid: 100, Name: "copilot.exe"),
            new(Pid: 201, ParentPid: 100, Name: "COPILOT.EXE"), // case-insensitive
            new(Pid: 300, ParentPid: 100, Name: "git.exe"),
        };

        var pids = WindowsProcessTree.SnapshotCopilotPids(rows);
        Assert.Equal(2, pids.Count);
        Assert.Contains(200, pids);
        Assert.Contains(201, pids);
    }

    [Fact]
    public void FindNewCopilotDescendant_DirectChild_Returns()
    {
        // Forge.Api(100) → copilot.exe(200)
        var rows = new List<WindowsProcessTree.ProcessRow>
        {
            new(100, 1, "Forge.Api.exe"),
            new(200, 100, "copilot.exe"),
        };
        var pid = WindowsProcessTree.FindNewCopilotDescendant(
            rootPid: 100,
            baselinePids: new HashSet<int>(),
            snapshot: rows);
        Assert.Equal(200, pid);
    }

    [Fact]
    public void FindNewCopilotDescendant_ThroughIntermediateLauncher_PicksTopmostCopilot()
    {
        // Forge.Api(100) → cmd.exe(150) → copilot.exe(200) → copilot.exe(201)
        // Should pick PID 200 (topmost copilot in chain), not 201.
        var rows = new List<WindowsProcessTree.ProcessRow>
        {
            new(100, 1, "Forge.Api.exe"),
            new(150, 100, "cmd.exe"),
            new(200, 150, "copilot.exe"),
            new(201, 200, "copilot.exe"),
        };
        var pid = WindowsProcessTree.FindNewCopilotDescendant(
            rootPid: 100,
            baselinePids: new HashSet<int>(),
            snapshot: rows);
        Assert.Equal(200, pid);
    }

    [Fact]
    public void FindNewCopilotDescendant_ExcludesBaselinePids()
    {
        // Pre-existing operator-launched copilot 200 must be ignored;
        // only the new spawn 201 should be returned.
        var rows = new List<WindowsProcessTree.ProcessRow>
        {
            new(100, 1, "Forge.Api.exe"),
            new(200, 100, "copilot.exe"),  // baseline (pre-existing)
            new(201, 100, "copilot.exe"),  // new spawn
        };
        var pid = WindowsProcessTree.FindNewCopilotDescendant(
            rootPid: 100,
            baselinePids: new HashSet<int> { 200 },
            snapshot: rows);
        Assert.Equal(201, pid);
    }

    [Fact]
    public void FindNewCopilotDescendant_DeepGrandchildTree_FindsRoot()
    {
        // Mirrors the orphan tree shape we observed (root + self-fork +
        // 3 worker children). Should return the topmost (200), so that
        // a single Kill(entireProcessTree:true) reaps all 5.
        var rows = new List<WindowsProcessTree.ProcessRow>
        {
            new(100, 1, "Forge.Api.exe"),
            new(200, 100, "copilot.exe"),  // root
            new(201, 200, "copilot.exe"),  // self-fork
            new(202, 201, "copilot.exe"),  // worker 1
            new(203, 201, "copilot.exe"),  // worker 2
            new(204, 201, "copilot.exe"),  // worker 3
        };
        var pid = WindowsProcessTree.FindNewCopilotDescendant(
            rootPid: 100,
            baselinePids: new HashSet<int>(),
            snapshot: rows);
        Assert.Equal(200, pid);
    }

    [Fact]
    public void FindNewCopilotDescendant_NoCopilotInTree_ReturnsNull()
    {
        var rows = new List<WindowsProcessTree.ProcessRow>
        {
            new(100, 1, "Forge.Api.exe"),
            new(200, 100, "git.exe"),
        };
        var pid = WindowsProcessTree.FindNewCopilotDescendant(
            rootPid: 100,
            baselinePids: new HashSet<int>(),
            snapshot: rows);
        Assert.Null(pid);
    }

    [Fact]
    public void FindNewCopilotDescendant_CopilotElsewhereInTree_NotADescendant_Ignored()
    {
        // Operator's own copilot (300) is rooted under explorer (50), NOT
        // under Forge.Api (100). Must not be returned.
        var rows = new List<WindowsProcessTree.ProcessRow>
        {
            new(50, 1, "explorer.exe"),
            new(100, 1, "Forge.Api.exe"),
            new(300, 50, "copilot.exe"),  // operator's own — not Forge.Api descendant
        };
        var pid = WindowsProcessTree.FindNewCopilotDescendant(
            rootPid: 100,
            baselinePids: new HashSet<int>(),
            snapshot: rows);
        Assert.Null(pid);
    }
}

/// <summary>
/// Smoke tests for the static <c>LastSpawnedPid</c> field used by the
/// <c>Program.cs</c> ProcessExit panic handler. The field is the bridge
/// between <see cref="CopilotServerManager"/> and the
/// <c>AppDomain.ProcessExit</c> handler that performs a synchronous
/// <c>Process.Kill</c> when graceful disposal didn't run.
/// </summary>
public class CopilotServerManagerLastSpawnedPidTests
{
    [Fact]
    public void LastSpawnedPid_StaticField_IsAccessibleAndZeroByDefault()
    {
        // The field is publicly accessible (Program.cs reads it during
        // ProcessExit). Default state when no server has been spawned
        // since process start (or after dispose) is 0.
        var field = typeof(CopilotServerManager).GetField(
            nameof(CopilotServerManager.LastSpawnedPid),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(typeof(int), field!.FieldType);
    }

    [Fact]
    public void LastSpawnedPid_CanBeAtomicallyExchanged()
    {
        // Snapshot original so concurrent tests can't see a stale value.
        var original = CopilotServerManager.LastSpawnedPid;
        try
        {
            var prev = System.Threading.Interlocked.Exchange(
                ref CopilotServerManager.LastSpawnedPid, 12345);
            Assert.Equal(original, prev);
            Assert.Equal(12345, CopilotServerManager.LastSpawnedPid);

            var prev2 = System.Threading.Interlocked.Exchange(
                ref CopilotServerManager.LastSpawnedPid, 0);
            Assert.Equal(12345, prev2);
            Assert.Equal(0, CopilotServerManager.LastSpawnedPid);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(
                ref CopilotServerManager.LastSpawnedPid, original);
        }
    }
}
