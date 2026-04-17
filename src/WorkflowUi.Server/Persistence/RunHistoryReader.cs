// -----------------------------------------------------------------------
// <copyright file="RunHistoryReader.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// Reads run events from per-run JSONL files.
/// </summary>
public class RunHistoryReader
{
    private readonly PersistenceOptions options;

    public RunHistoryReader(IOptions<PersistenceOptions> options)
    {
        this.options = options.Value;
    }

    public IReadOnlyList<RunEvent> Read(Guid runId)
    {
        var filePath = Path.Combine(this.options.RunsRoot, runId.ToString(), "events.jsonl");
        if (!File.Exists(filePath))
        {
            return Array.Empty<RunEvent>();
        }

        var events = new List<RunEvent>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize<RunEvent>(line);
            if (evt != null)
            {
                events.Add(evt);
            }
        }

        return events;
    }
}
