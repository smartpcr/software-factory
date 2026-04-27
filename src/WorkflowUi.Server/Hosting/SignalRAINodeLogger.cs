// -----------------------------------------------------------------------
// <copyright file="SignalRAINodeLogger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using ExecutionEngine.AI;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// Streams AI node output lines to the UI via SignalR as NodeLog events.
/// </summary>
public class SignalRAINodeLogger : IAINodeLogger
{
    private readonly IRunEventSink eventSink;
    private readonly SequenceAllocator seqAllocator;

    public SignalRAINodeLogger(
        IRunEventSink eventSink,
        SequenceAllocator seqAllocator)
    {
        this.eventSink = eventSink;
        this.seqAllocator = seqAllocator;
    }

    public void EmitLine(Guid workflowInstanceId, string nodeId, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var runEvent = new RunEvent
        {
            RunId = workflowInstanceId,
            Seq = this.seqAllocator.Next(workflowInstanceId),
            Kind = RunEventKind.NodeLog,
            Synthetic = false,
            TimestampUtc = DateTime.UtcNow,
            NodeId = nodeId,
            Payload = new Dictionary<string, object> { ["message"] = line },
        };

        // Fire-and-forget through the full sink pipeline (buffer + SignalR)
        _ = this.eventSink.PublishAsync(runEvent);
    }
}