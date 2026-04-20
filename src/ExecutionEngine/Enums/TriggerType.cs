// -----------------------------------------------------------------------
// <copyright file="TriggerType.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Enums;

/// <summary>
/// Defines the trigger type for a Start node.
/// </summary>
public enum TriggerType
{
    /// <summary>
    /// Manually triggered workflow.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Triggered on a cron schedule.
    /// </summary>
    Schedule,

    /// <summary>
    /// Triggered by an external event or webhook.
    /// </summary>
    Event
}