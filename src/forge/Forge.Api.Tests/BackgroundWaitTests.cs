// -----------------------------------------------------------------------
// <copyright file="BackgroundWaitTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// <see cref="BackgroundWait"/> lets an impl generator that is waiting on
/// background work it started (harness:DSH-M2 stage 5.9, 2026-11-26: two
/// harness-gate lanes) declare it, so Forge holds the next iteration instead of
/// counting quick status-check turns toward the zero-change stall.
/// </summary>
public sealed class BackgroundWaitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 12, 35, 0, TimeSpan.Zero);
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "bgwait-ws");

    [Fact]
    public void Parse_ReadsJobsAndDeadline()
    {
        var wait = BackgroundWait.Parse(
            """
            {"reason":"lanes at eff3750","maxMinutes":45,"jobs":[
              {"name":"win","pid":32036},
              {"name":"wsl","log":"lanes/wsl.log","doneWhen":"^exit: \\d+"}]}
            """,
            Workspace, T0, out var error);

        Assert.Null(error);
        Assert.NotNull(wait);
        Assert.Equal("lanes at eff3750", wait!.Reason);
        Assert.Equal(T0.AddMinutes(45), wait.Deadline);
        Assert.Equal(2, wait.Jobs.Count);
        Assert.Equal(32036, wait.Jobs[0].Pid);
        Assert.Equal(Path.GetFullPath(Path.Combine(Workspace, "lanes/wsl.log")), wait.Jobs[1].Log);
        Assert.Matches(wait.Jobs[1].DoneWhen!, "rehearse\nexit: 1\n");
    }

    [Fact]
    public void Parse_DefaultsAndCapsTheLimit()
    {
        var byDefault = BackgroundWait.Parse("""{"jobs":[{"pid":1}]}""", Workspace, T0, out _);
        Assert.Equal(T0 + BackgroundWait.DefaultLimit, byDefault!.Deadline);
        Assert.Equal("job 1", byDefault.Jobs[0].Name);

        var capped = BackgroundWait.Parse("""{"maxMinutes":10000,"jobs":[{"pid":1}]}""", Workspace, T0, out _);
        Assert.Equal(T0 + BackgroundWait.MaxLimit, capped!.Deadline);
    }

    [Theory]
    [InlineData("not json", "not JSON")]
    [InlineData("[1]", "JSON object")]
    [InlineData("""{"jobs":[]}""", "non-empty")]
    [InlineData("""{"reason":"x"}""", "non-empty")]
    [InlineData("""{"maxMinutes":0,"jobs":[{"pid":1}]}""", "maxMinutes")]
    [InlineData("""{"jobs":[{"name":"a"}]}""", "give a pid")]
    [InlineData("""{"jobs":[{"name":"a","pid":-4}]}""", "positive integer")]
    [InlineData("""{"jobs":[{"name":"a","log":"x.log"}]}""", "together")]
    [InlineData("""{"jobs":[{"name":"a","doneWhen":"^exit"}]}""", "together")]
    [InlineData("""{"jobs":[{"name":"a","log":"x.log","doneWhen":"(unclosed"}]}""", "not a valid pattern")]
    [InlineData("""{"jobs":["lane"]}""", "must be an object")]
    public void Parse_RefusesWhatCannotBeWaitedOn(string json, string expected)
    {
        var wait = BackgroundWait.Parse(json, Workspace, T0, out var error);

        Assert.Null(wait);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void IsDone_OnProcessExitOrDoneLine()
    {
        var byPid = new BackgroundJob("win", 7, null, null);
        Assert.False(BackgroundWait.IsDone(byPid, _ => true, _ => null));
        Assert.True(BackgroundWait.IsDone(byPid, _ => false, _ => null));

        var byLog = BackgroundWait.Parse(
            """{"jobs":[{"name":"wsl","log":"C:\\l.log","doneWhen":"^exit: \\d+"}]}""", Workspace, T0, out _)!.Jobs[0];
        Assert.False(BackgroundWait.IsDone(byLog, _ => true, _ => null));
        Assert.False(BackgroundWait.IsDone(byLog, _ => true, _ => "harness-gate: PASS gofmt\n"));
        Assert.True(BackgroundWait.IsDone(byLog, _ => true, _ => "PASS gofmt\nexit: 0\n"));
    }

    [Fact]
    public void Read_FromTheWorktree()
    {
        var ws = Directory.CreateTempSubdirectory("bgwait-").FullName;
        try
        {
            Assert.Null(BackgroundWait.Read(ws, out var none));
            Assert.Null(none);

            Directory.CreateDirectory(Path.Combine(ws, ".forge"));
            File.WriteAllText(Path.Combine(ws, ".forge", "waiting.json"), "{");
            Assert.Null(BackgroundWait.Read(ws, out var bad));
            Assert.Contains("not JSON", bad);

            File.WriteAllText(Path.Combine(ws, ".forge", "waiting.json"), """{"jobs":[{"pid":1}]}""");
            var wait = BackgroundWait.Read(ws, out var ok);
            Assert.Null(ok);
            Assert.Single(wait!.Jobs);
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void ReadLogTail_WhileTheWriterHoldsIt()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("bgwait-").FullName, "lane.log");
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write("exit: 0\n"u8);
            writer.Flush();
            Assert.Equal("exit: 0\n", BackgroundWait.ReadLogTail(path));
        }

        Assert.Null(BackgroundWait.ReadLogTail(path + ".missing"));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public async Task HoldAsync_EndsWhenEveryJobIsDone()
    {
        var wait = TwoJobs(limitMinutes: 90);
        var clock = new FakeClock(T0);
        var done = new HashSet<string>();
        var beats = new List<IReadOnlyList<string>>();

        // The Windows lane ends at 12 minutes, the WSL lane at 31.
        var outcome = await wait.HoldAsync(
            j => done.Contains(j.Name),
            clock.Now,
            (d, _) =>
            {
                clock.Advance(d);
                if (clock.Now() >= T0.AddMinutes(12)) done.Add("win");
                if (clock.Now() >= T0.AddMinutes(31)) done.Add("wsl");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5),
            p => { beats.Add(p); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.False(outcome.TimedOut);
        Assert.Empty(outcome.Pending);
        Assert.Equal(TimeSpan.FromMinutes(31), outcome.Waited);
        Assert.Equal(6, beats.Count);                 // at 5, 10, 15, 20, 25, 30 minutes
        Assert.Equal(new[] { "win", "wsl" }, beats[0]);
        Assert.Equal(new[] { "wsl" }, beats[^1]);
    }

    [Fact]
    public async Task HoldAsync_GivesUpAtTheDeadline()
    {
        var wait = TwoJobs(limitMinutes: 20);
        var clock = new FakeClock(T0);

        var outcome = await wait.HoldAsync(
            _ => false,
            clock.Now,
            (d, _) => { clock.Advance(d); return Task.CompletedTask; },
            TimeSpan.FromSeconds(45),
            TimeSpan.FromMinutes(5),
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(outcome.TimedOut);
        Assert.Equal(new[] { "win", "wsl" }, outcome.Pending);
        Assert.Equal(TimeSpan.FromMinutes(20), outcome.Waited); // the last delay is cut to the deadline
    }

    [Fact]
    public async Task HoldAsync_StopsWhenCancelled()
    {
        var wait = TwoJobs(limitMinutes: 90);
        var clock = new FakeClock(T0);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.HoldAsync(
            _ => false,
            clock.Now,
            (d, _) => { clock.Advance(d); cts.Cancel(); return Task.CompletedTask; },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5),
            _ => Task.CompletedTask,
            cts.Token));
    }

    [Fact]
    public void ProcessAlive_SeesThisProcessAndNotAnUnusedId()
    {
        Assert.True(BackgroundWait.ProcessAlive(Environment.ProcessId));
        Assert.False(BackgroundWait.ProcessAlive(int.MaxValue));
    }

    private static BackgroundWait TwoJobs(int limitMinutes) => BackgroundWait.Parse(
        "{\"maxMinutes\":" + limitMinutes + ",\"jobs\":[{\"name\":\"win\",\"pid\":11},{\"name\":\"wsl\",\"pid\":12}]}",
        Workspace, T0, out _)!;

    private sealed class FakeClock(DateTimeOffset start)
    {
        private DateTimeOffset now = start;

        public DateTimeOffset Now() => this.now;

        public void Advance(TimeSpan by) => this.now += by;
    }
}