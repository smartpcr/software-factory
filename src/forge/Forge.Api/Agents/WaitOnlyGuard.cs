// -----------------------------------------------------------------------
// <copyright file="WaitOnlyGuard.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Decides when a generator turn has stopped working and is only waiting on
/// background work (a test lane, a long build) that it started itself.
/// <para>
/// Observed 2026-11-25 on harness:DSH-M2 stages 2.8 and 2.8-e2e: the agent
/// started a 40-70 minute gate run asynchronously, then polled it in
/// 10-minute <c>read_powershell</c> waits until the iteration's hard timeout
/// killed it. Each timeout scored 0, skipped the evaluator and committed
/// nothing, even when the work was finished. Ending the turn once it has done
/// nothing but wait for <see cref="Limit"/> lets the iteration finish normally:
/// Forge commits the work, runs its gates and the evaluator, and the next
/// iteration reads the background job's log.
/// </para>
/// <para>
/// The rule: since the last real progress, at least <see cref="WaitShare"/>
/// of the wall time was spent inside wait calls, and that span is at least
/// <see cref="Limit"/> long. Real progress is a file edit, or a non-wait tool
/// call that ran for at least <see cref="ProgressCallMinDuration"/> (a build or
/// a foreground test). Short reads and log polls between waits are neither
/// progress nor waiting, so a "wait, poll, wait" loop still trips the guard,
/// while an agent reading code or thinking (no wait calls) never does. A
/// non-wait call still in flight blocks the guard: the per-tool watchdog owns
/// hung calls.
/// </para>
/// Thread-safe: the SDK event handler feeds it and the watchdog polls it.
/// </summary>
public sealed class WaitOnlyGuard
{
    /// <summary>Share of the span since the last progress that must be spent waiting.</summary>
    public const double WaitShare = 0.8;

    /// <summary>A non-wait tool call at least this long counts as progress.</summary>
    public static readonly TimeSpan ProgressCallMinDuration = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> WaitTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_powershell",
        "read_bash",
        "read_shell",
        "read_terminal",
        "await_terminal",
        "wait",
        "sleep",
    };

    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell",
        "bash",
        "shell",
        "run_in_terminal",
    };

    private static readonly HashSet<string> EditTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit",
        "create",
        "write",
        "str_replace",
        "str_replace_editor",
        "apply_patch",
        "write_file",
        "create_file",
        "edit_file",
        "multi_edit",
    };

    private readonly object gate = new();
    private readonly Dictionary<string, InFlightCall> inFlight = new(StringComparer.Ordinal);
    private DateTimeOffset lastProgress;
    private TimeSpan waitedSinceProgress;

    /// <summary>Initializes a guard for a turn that started at <paramref name="start"/>.</summary>
    /// <param name="limit">Minimum waiting span before the guard trips; zero or negative disables it.</param>
    /// <param name="start">When the turn started.</param>
    public WaitOnlyGuard(TimeSpan limit, DateTimeOffset start)
    {
        this.Limit = limit;
        this.lastProgress = start;
    }

    /// <summary>Gets the minimum waiting span before the guard trips.</summary>
    public TimeSpan Limit { get; }

    /// <summary>
    /// True when the call only waits: a shell read (<c>read_powershell</c>,
    /// <c>read_bash</c>, ...), or a shell command that starts with a sleep
    /// (<c>Start-Sleep 20; Get-Content log -Tail 5</c> is a poll).
    /// </summary>
    public static bool IsWaitCall(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrEmpty(toolName)) return false;
        if (WaitTools.Contains(toolName)) return true;
        if (!ShellTools.Contains(toolName)) return false;

        var command = ReadCommand(argumentsJson)?.TrimStart();
        if (string.IsNullOrEmpty(command)) return false;
        return StartsWithWord(command, "Start-Sleep")
            || StartsWithWord(command, "sleep")
            || StartsWithWord(command, "timeout");
    }

    /// <summary>True when the call writes a file.</summary>
    public static bool IsEditCall(string? toolName)
        => !string.IsNullOrEmpty(toolName) && EditTools.Contains(toolName);

    /// <summary>Records a tool call starting.</summary>
    public void OnToolStart(string? callId, string? toolName, string? argumentsJson, DateTimeOffset at)
    {
        var call = new InFlightCall(at, IsWaitCall(toolName, argumentsJson), IsEditCall(toolName));
        lock (this.gate)
        {
            this.inFlight[callId ?? string.Empty] = call;
        }
    }

    /// <summary>Records a tool call completing.</summary>
    public void OnToolComplete(string? callId, DateTimeOffset at)
    {
        lock (this.gate)
        {
            var key = callId ?? string.Empty;
            if (!this.inFlight.Remove(key, out var call)) return;

            var duration = at - call.Start;
            if (call.IsWait)
            {
                // Only the part after the last progress counts toward this span.
                var from = call.Start > this.lastProgress ? call.Start : this.lastProgress;
                if (at > from) this.waitedSinceProgress += at - from;
            }
            else if (call.IsEdit || duration >= ProgressCallMinDuration)
            {
                this.lastProgress = at;
                this.waitedSinceProgress = TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// True when the turn has only waited for at least <see cref="Limit"/>.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="waited">The waiting span since the last progress.</param>
    public bool ShouldEnd(DateTimeOffset now, out TimeSpan waited)
    {
        lock (this.gate)
        {
            waited = this.waitedSinceProgress;
            if (this.Limit <= TimeSpan.Zero) return false;

            foreach (var call in this.inFlight.Values)
            {
                if (!call.IsWait) return false;
                var from = call.Start > this.lastProgress ? call.Start : this.lastProgress;
                if (now > from) waited += now - from;
            }

            var span = now - this.lastProgress;
            if (span < this.Limit) return false;
            return waited.Ticks >= span.Ticks * WaitShare;
        }
    }

    private static string? ReadCommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String
                ? cmd.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool StartsWithWord(string text, string word)
        => text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
            && (text.Length == word.Length || !char.IsLetterOrDigit(text[word.Length]) && text[word.Length] != '-');

    private readonly record struct InFlightCall(DateTimeOffset Start, bool IsWait, bool IsEdit);
}