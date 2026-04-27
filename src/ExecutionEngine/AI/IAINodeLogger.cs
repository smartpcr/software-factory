// -----------------------------------------------------------------------
// <copyright file="IAINodeLogger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.AI;

/// <summary>
/// Callback interface for streaming AI node output lines to the UI.
/// Registered in DI by the host (server) to push lines via SignalR.
/// </summary>
public interface IAINodeLogger
{
    /// <summary>
    /// Emits a log line from an AI node execution.
    /// </summary>
    /// <param name="workflowInstanceId">The workflow run ID.</param>
    /// <param name="nodeId">The node emitting the log.</param>
    /// <param name="line">The output line text.</param>
    void EmitLine(Guid workflowInstanceId, string nodeId, string line);
}