// -----------------------------------------------------------------------
// <copyright file="RunHistoryWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// Appends run events to per-run JSONL files under RunsRoot/{runId}/events.jsonl.
/// </summary>
public class RunHistoryWriter
{
    private readonly PersistenceOptions options;
    private readonly ILogger<RunHistoryWriter> logger;

    public RunHistoryWriter(IOptions<PersistenceOptions> options, ILogger<RunHistoryWriter> logger)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    public Task AppendAsync(RunEvent runEvent, CancellationToken ct = default)
    {
        var runDir = Path.Combine(this.options.RunsRoot, runEvent.RunId.ToString());
        if (!Directory.Exists(runDir))
        {
            Directory.CreateDirectory(runDir);
        }

        var filePath = Path.Combine(runDir, "events.jsonl");
        var json = JsonSerializer.Serialize(runEvent);
        File.AppendAllText(filePath, json + Environment.NewLine);

        return Task.CompletedTask;
    }
}
