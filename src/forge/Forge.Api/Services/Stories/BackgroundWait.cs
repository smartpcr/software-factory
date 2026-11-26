// -----------------------------------------------------------------------
// <copyright file="BackgroundWait.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Background work an impl generator declared it is waiting on, read from
/// <c>.forge/waiting.json</c> in its worktree.
/// <para>
/// Observed 2026-11-26 on harness:DSH-M2 stage 5.9: the agent committed, started
/// two 25-35 minute harness-gate lanes detached, then ended each following turn
/// after one quick status check. Every such turn changed no file, so three of
/// them tripped the zero-change stall (NoProgressException) while the lanes were
/// still running, and each one cost a full generator and evaluator round just to
/// poll. The wait-only guard (<see cref="Forge.Api.Agents.WaitOnlyGuard"/>) does
/// not apply: it covers a turn that waits inside itself, not one that ends early.
/// </para>
/// <para>
/// With a declaration naming at least one job that is still running, Forge holds
/// the next iteration (no agent runs) until every job is done or the declared
/// limit passes, and the turn does not count toward the stall. A declaration
/// whose jobs are all done, one that does not parse, and a hold that reaches its
/// limit leave the zero-change rule as it was, so an agent cannot wait forever.
/// </para>
/// </summary>
public sealed class BackgroundWait
{
    /// <summary>The declaration's path relative to the worktree root.</summary>
    public const string RelativePath = ".forge/waiting.json";

    /// <summary>The limit when the declaration names none.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromMinutes(90);

    /// <summary>The longest limit a declaration may ask for.</summary>
    public static readonly TimeSpan MaxLimit = TimeSpan.FromHours(3);

    /// <summary>The most of a job's log read when matching its done pattern.</summary>
    private const int MaxLogTailBytes = 1 << 20;

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    private BackgroundWait(string reason, IReadOnlyList<BackgroundJob> jobs, DateTimeOffset deadline)
    {
        this.Reason = reason;
        this.Jobs = jobs;
        this.Deadline = deadline;
    }

    /// <summary>Gets what the agent said it is waiting for.</summary>
    public string Reason { get; }

    /// <summary>Gets the declared jobs.</summary>
    public IReadOnlyList<BackgroundJob> Jobs { get; }

    /// <summary>Gets when the hold gives up: the declaration's write time plus its limit.</summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>
    /// Reads the declaration in <paramref name="workspace"/>. Returns null with a
    /// null <paramref name="error"/> when there is none, and null with the reason
    /// when it does not parse or names no usable job.
    /// </summary>
    public static BackgroundWait? Read(string workspace, out string? error)
    {
        error = null;
        var path = Path.Combine(workspace, ".forge", "waiting.json");
        if (!File.Exists(path)) return null;
        try
        {
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return Parse(File.ReadAllText(path), workspace, written, out error);
        }
        catch (IOException ex)
        {
            error = $"cannot read {RelativePath}: {ex.Message}";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"cannot read {RelativePath}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Parses a declaration written at <paramref name="writtenAt"/>. A relative log
    /// path is taken from <paramref name="workspace"/>.
    /// </summary>
    public static BackgroundWait? Parse(string json, string workspace, DateTimeOffset writtenAt, out string? error)
    {
        error = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"{RelativePath} is not JSON: {ex.Message}";
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"{RelativePath} must be a JSON object";
                return null;
            }

            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
            var limit = DefaultLimit;
            if (root.TryGetProperty("maxMinutes", out var m))
            {
                if (m.ValueKind != JsonValueKind.Number || !m.TryGetDouble(out var minutes) || minutes <= 0)
                {
                    error = "maxMinutes must be a positive number";
                    return null;
                }

                limit = TimeSpan.FromMinutes(Math.Min(minutes, MaxLimit.TotalMinutes));
            }

            if (!root.TryGetProperty("jobs", out var jobsEl) || jobsEl.ValueKind != JsonValueKind.Array || jobsEl.GetArrayLength() == 0)
            {
                error = "jobs must be a non-empty array";
                return null;
            }

            var jobs = new List<BackgroundJob>();
            var index = 0;
            foreach (var j in jobsEl.EnumerateArray())
            {
                index++;
                var job = ParseJob(j, index, workspace, out error);
                if (job is null) return null;
                jobs.Add(job);
            }

            return new BackgroundWait(reason, jobs, writtenAt + limit);
        }
    }

    /// <summary>
    /// True when <paramref name="job"/> has finished: its process is gone, or its
    /// log has a line matching its done pattern.
    /// </summary>
    public static bool IsDone(BackgroundJob job, Func<int, bool> processAlive, Func<string, string?> readLogTail)
    {
        if (job.Pid is int pid && !processAlive(pid)) return true;
        if (job.Log is not null && job.DoneWhen is not null)
        {
            var text = readLogTail(job.Log);
            if (text is not null)
            {
                try
                {
                    if (job.DoneWhen.IsMatch(text)) return true;
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological pattern never declares a job done; the
                    // deadline still ends the hold.
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Holds until every job is done, the deadline passes, or
    /// <paramref name="ct"/> is cancelled. <paramref name="onHeartbeat"/> runs
    /// every <paramref name="heartbeat"/> with the jobs still running, so the
    /// workstream shows activity while nothing else happens.
    /// </summary>
    public async Task<BackgroundWaitOutcome> HoldAsync(
        Func<BackgroundJob, bool> isDone,
        Func<DateTimeOffset> now,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan poll,
        TimeSpan heartbeat,
        Func<IReadOnlyList<string>, Task> onHeartbeat,
        CancellationToken ct)
    {
        var start = now();
        var lastBeat = start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = this.Jobs.Where(j => !isDone(j)).Select(j => j.Name).ToList();
            var at = now();
            if (pending.Count == 0) return new BackgroundWaitOutcome(false, pending, at - start);
            if (at >= this.Deadline) return new BackgroundWaitOutcome(true, pending, at - start);
            if (at - lastBeat >= heartbeat)
            {
                await onHeartbeat(pending);
                lastBeat = at;
            }

            var remaining = this.Deadline - at;
            await delay(remaining < poll ? remaining : poll, ct);
        }
    }

    /// <summary>True when a process with <paramref name="pid"/> is running on this machine.</summary>
    public static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The last megabyte of a log, or null when it cannot be read yet.</summary>
    public static string? ReadLogTail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxLogTailBytes) fs.Seek(-MaxLogTailBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BackgroundJob? ParseJob(JsonElement j, int index, string workspace, out string? error)
    {
        error = null;
        if (j.ValueKind != JsonValueKind.Object)
        {
            error = $"jobs[{index}] must be an object";
            return null;
        }

        var name = j.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
            ? n.GetString()!
            : $"job {index}";
        int? pid = null;
        if (j.TryGetProperty("pid", out var p))
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var v) || v <= 0)
            {
                error = $"{name}: pid must be a positive integer";
                return null;
            }

            pid = v;
        }

        string? log = null;
        Regex? doneWhen = null;
        var hasLog = j.TryGetProperty("log", out var l) && l.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.GetString());
        var hasPattern = j.TryGetProperty("doneWhen", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(d.GetString());
        if (hasLog != hasPattern)
        {
            error = $"{name}: log and doneWhen are given together or not at all";
            return null;
        }

        if (hasLog)
        {
            var raw = l.GetString()!;
            log = Path.IsPathFullyQualified(raw) ? raw : Path.GetFullPath(Path.Combine(workspace, raw));
            try
            {
                doneWhen = new Regex(d.GetString()!, RegexOptions.Multiline | RegexOptions.CultureInvariant, PatternTimeout);
            }
            catch (ArgumentException ex)
            {
                error = $"{name}: doneWhen is not a valid pattern: {ex.Message}";
                return null;
            }
        }

        if (pid is null && log is null)
        {
            error = $"{name}: give a pid, or a log with a doneWhen pattern";
            return null;
        }

        return new BackgroundJob(name, pid, log, doneWhen);
    }
}

/// <summary>One declared background job.</summary>
/// <param name="Name">What the agent calls it.</param>
/// <param name="Pid">A Windows process id that runs until the job ends.</param>
/// <param name="Log">A log the job writes; with <paramref name="DoneWhen"/>.</param>
/// <param name="DoneWhen">A line pattern in <paramref name="Log"/> that marks the job done.</param>
public sealed record BackgroundJob(string Name, int? Pid, string? Log, Regex? DoneWhen);

/// <summary>How a hold ended.</summary>
/// <param name="TimedOut">True when the deadline passed with jobs still running.</param>
/// <param name="Pending">The jobs still running when it ended.</param>
/// <param name="Waited">How long it held.</param>
public sealed record BackgroundWaitOutcome(bool TimedOut, IReadOnlyList<string> Pending, TimeSpan Waited);