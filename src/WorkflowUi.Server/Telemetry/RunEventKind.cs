// -----------------------------------------------------------------------
// <copyright file="RunEventKind.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

/// <summary>
/// Discriminator for the 11 event kinds (9 native + 2 synthetic).
/// </summary>
public enum RunEventKind
{
    // ---- Native (from engine CLR events) ----
    WorkflowStarted,
    WorkflowCompleted,
    WorkflowFailed,
    WorkflowCancelled,
    NodeStarted,
    NodeCompleted,
    NodeFailed,
    NodeCancelled,
    NodeProgress,

    // ---- Synthetic (derived by bridge) ----
    EdgeActivated,
    NodeSkipped,
}