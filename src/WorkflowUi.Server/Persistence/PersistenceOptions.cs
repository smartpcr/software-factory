// -----------------------------------------------------------------------
// <copyright file="PersistenceOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

/// <summary>
/// Configuration for file-based workflow persistence.
/// </summary>
public class PersistenceOptions
{
    public const string SectionName = "WorkflowUi:Persistence";

    /// <summary>Root directory for workflow YAML + sidecar files.</summary>
    public string WorkflowsRoot { get; set; } = "workflows";

    /// <summary>Root directory for run history JSONL files.</summary>
    public string RunsRoot { get; set; } = "runs";

    /// <summary>Maximum age in days for run history before cleanup.</summary>
    public int RunRetentionDays { get; set; } = 30;
}
