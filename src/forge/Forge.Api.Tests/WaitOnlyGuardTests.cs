// -----------------------------------------------------------------------
// <copyright file="WaitOnlyGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using Forge.Api.Agents;
using Xunit;

/// <summary>
/// <see cref="WaitOnlyGuard"/> ends a generator turn that only waits on
/// background work it started. The two "trips" cases replay the patterns seen
/// on harness:DSH-M2 2026-11-25 (stage 2.8 and 2.8-e2e); the rest are the
/// shapes of real work the guard must leave alone.
/// </summary>
public sealed class WaitOnlyGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(15);

    [Theory]
    [InlineData("read_powershell", "{\"delay\":600,\"shellId\":\"winlane\"}", true)]
    [InlineData("read_bash", "{\"shellId\":\"1\"}", true)]
    [InlineData("powershell", "{\"command\":\"Start-Sleep 20; Get-Content lane.log -Tail 5\"}", true)]
    [InlineData("powershell", "{\"command\":\"  start-sleep -Seconds 60\"}", true)]
    [InlineData("bash", "{\"command\":\"sleep 30 && tail -5 lane.log\"}", true)]
    [InlineData("powershell", "{\"command\":\"go test ./...\"}", false)]
    [InlineData("powershell", "{\"command\":\"Start-SleepyThing\"}", false)]
    [InlineData("bash", "{\"command\":\"sleepwalk --fast\"}", false)]
    [InlineData("powershell", "not json", false)]
    [InlineData("view", "{\"path\":\"a.go\"}", false)]
    [InlineData(null, null, false)]
    public void IsWaitCall_ClassifiesShellReadsAndSleeps(string? tool, string? args, bool expected)
    {
        Assert.Equal(expected, WaitOnlyGuard.IsWaitCall(tool, args));
    }

    [Fact]
    public void Trips_WhenTheTurnOnlyPollsABackgroundLane()
    {
        // 2.8-e2e: edits, then 10-minute read_powershell waits with short log
        // polls in between. The short polls neither count as progress nor
        // break the waiting share.
        var guard = new WaitOnlyGuard(Limit, T0);
        Call(guard, "e1", "edit", null, 0, 1);
        Call(guard, "w1", "read_powershell", "{\"delay\":600}", 2, 12);
        Call(guard, "p1", "powershell", "{\"command\":\"Get-Content lane.log -Tail 3\"}", 12, 12.1);

        Assert.False(guard.ShouldEnd(At(14), out _));

        guard.OnToolStart("w2", "read_powershell", "{\"delay\":600}", At(12.2));
        Assert.True(guard.ShouldEnd(At(16.5), out var waited));
        Assert.True(waited >= TimeSpan.FromMinutes(14));
    }

    [Fact]
    public void Trips_WhenTheTurnStartsWaitingRightAway()
    {
        // 2.8 iteration 2: the turn resumed straight into waiting on a gate.
        var guard = new WaitOnlyGuard(Limit, T0);
        guard.OnToolStart("w1", "read_powershell", "{\"delay\":600}", At(0.1));

        Assert.False(guard.ShouldEnd(At(10), out _));
        Assert.True(guard.ShouldEnd(At(15.5), out _));
    }

    [Fact]
    public void DoesNotTrip_WhileTheAgentReadsAndThinks()
    {
        // Twenty minutes of reading code and thinking, no wait calls.
        var guard = new WaitOnlyGuard(Limit, T0);
        for (var i = 0; i < 40; i++)
        {
            Call(guard, $"v{i}", "view", "{\"path\":\"x.go\"}", i * 0.5, (i * 0.5) + 0.05);
        }

        Assert.False(guard.ShouldEnd(At(20), out _));
    }

    [Fact]
    public void DoesNotTrip_WhileAForegroundBuildRuns()
    {
        var guard = new WaitOnlyGuard(Limit, T0);
        Call(guard, "w1", "read_powershell", "{\"delay\":600}", 1, 11);
        guard.OnToolStart("b1", "powershell", "{\"command\":\"go test ./...\"}", At(11));

        Assert.False(guard.ShouldEnd(At(20), out _));
    }

    [Fact]
    public void LongCommandOrEdit_ResetsTheWaitingSpan()
    {
        var guard = new WaitOnlyGuard(Limit, T0);
        Call(guard, "w1", "read_powershell", "{\"delay\":600}", 0, 10);
        Call(guard, "b1", "powershell", "{\"command\":\"go vet ./...\"}", 10, 11);
        Call(guard, "w2", "read_powershell", "{\"delay\":600}", 11, 21);

        Assert.False(guard.ShouldEnd(At(21), out _));

        Call(guard, "e1", "create", "{\"path\":\"a.go\"}", 21, 21.01);
        Call(guard, "w3", "read_powershell", "{\"delay\":600}", 21.1, 31.1);
        Assert.False(guard.ShouldEnd(At(31.2), out _));

        // 16 min since the edit, 10 of them waiting: under the 80% share.
        Assert.False(guard.ShouldEnd(At(37), out _));
    }

    [Fact]
    public void ShortCommands_AreNotProgress()
    {
        var guard = new WaitOnlyGuard(Limit, T0);
        Call(guard, "w1", "read_powershell", "{\"delay\":600}", 0, 10);
        Call(guard, "p1", "powershell", "{\"command\":\"git status\"}", 10, 10.1);
        Call(guard, "w2", "read_powershell", "{\"delay\":600}", 10.2, 16);

        Assert.True(guard.ShouldEnd(At(16), out _));
    }

    [Fact]
    public void ZeroLimit_DisablesTheGuard()
    {
        var guard = new WaitOnlyGuard(TimeSpan.Zero, T0);
        Call(guard, "w1", "read_powershell", "{\"delay\":600}", 0, 60);

        Assert.False(guard.ShouldEnd(At(61), out _));
    }

    private static DateTimeOffset At(double minutes) => T0.AddMinutes(minutes);

    private static void Call(WaitOnlyGuard guard, string id, string tool, string? args, double start, double end)
    {
        guard.OnToolStart(id, tool, args, At(start));
        guard.OnToolComplete(id, At(end));
    }
}