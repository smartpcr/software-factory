// -----------------------------------------------------------------------
// <copyright file="SequenceAllocatorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Telemetry;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using WorkflowUi.Server.Telemetry;
using Xunit;

/// <summary>
/// Tests for SequenceAllocator including thread-safety stress test.
/// </summary>
public class SequenceAllocatorTests
{
    [Fact]
    public void Next_starts_at_one()
    {
        var alloc = new SequenceAllocator();
        var runId = Guid.NewGuid();
        Assert.Equal(1, alloc.Next(runId));
    }

    [Fact]
    public void Next_is_strictly_increasing()
    {
        var alloc = new SequenceAllocator();
        var runId = Guid.NewGuid();

        for (int i = 1; i <= 100; i++)
        {
            Assert.Equal(i, alloc.Next(runId));
        }
    }

    [Fact]
    public void Different_runs_have_independent_sequences()
    {
        var alloc = new SequenceAllocator();
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();

        Assert.Equal(1, alloc.Next(run1));
        Assert.Equal(1, alloc.Next(run2));
        Assert.Equal(2, alloc.Next(run1));
        Assert.Equal(2, alloc.Next(run2));
    }

    [Fact]
    public void Current_returns_last_allocated()
    {
        var alloc = new SequenceAllocator();
        var runId = Guid.NewGuid();
        Assert.Equal(0, alloc.Current(runId));
        alloc.Next(runId);
        alloc.Next(runId);
        Assert.Equal(2, alloc.Current(runId));
    }

    [Fact]
    public void Reset_clears_sequence()
    {
        var alloc = new SequenceAllocator();
        var runId = Guid.NewGuid();
        alloc.Next(runId);
        alloc.Next(runId);
        alloc.Reset(runId);
        Assert.Equal(0, alloc.Current(runId));
        Assert.Equal(1, alloc.Next(runId));
    }

    [Fact]
    public async Task Monotonic_under_10_thread_stress()
    {
        var alloc = new SequenceAllocator();
        var runId = Guid.NewGuid();
        const int threadsCount = 10;
        const int callsPerThread = 10_000;
        var results = new ConcurrentBag<long>();

        var tasks = Enumerable.Range(0, threadsCount).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < callsPerThread; i++)
            {
                results.Add(alloc.Next(runId));
            }
        }));

        await Task.WhenAll(tasks);

        var sorted = results.OrderBy(x => x).ToList();
        Assert.Equal(threadsCount * callsPerThread, sorted.Count);

        // Every value should be unique (strictly monotonic)
        Assert.Equal(sorted.Count, sorted.Distinct().Count());

        // Values should be 1..N
        Assert.Equal(1, sorted.First());
        Assert.Equal(threadsCount * callsPerThread, sorted.Last());
    }
}
