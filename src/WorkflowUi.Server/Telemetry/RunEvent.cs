// -----------------------------------------------------------------------
// <copyright file="RunEvent.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Transport-neutral telemetry DTO emitted by the engine event bridge.
/// Every downstream surface (live monitor, run history, audit log)
/// consumes this shape.
/// </summary>
public class RunEvent
{
    /// <summary>
    /// Gets or sets the workflow instance (run) ID.
    /// </summary>
    public Guid RunId { get; set; }

    /// <summary>
    /// Gets or sets the monotonically increasing sequence number within the run.
    /// </summary>
    public long Seq { get; set; }

    /// <summary>
    /// Gets or sets the event kind discriminator.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RunEventKind Kind { get; set; }

    /// <summary>
    /// Gets or sets whether this event is synthetically derived.
    /// </summary>
    public bool Synthetic { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the event.
    /// </summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// Gets or sets the node ID (for node-level events, null for workflow-level).
    /// </summary>
    public string? NodeId { get; set; }

    /// <summary>
    /// Gets or sets the node name (for node-level events).
    /// </summary>
    public string? NodeName { get; set; }

    /// <summary>
    /// Gets or sets the event-specific payload as a dictionary.
    /// </summary>
    public Dictionary<string, object>? Payload { get; set; }
}
