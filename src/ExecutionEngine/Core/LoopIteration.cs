// -----------------------------------------------------------------------
// <copyright file="LoopIteration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Core;

/// <summary>
/// Tracks a single iteration of a While or ForEach loop.
/// Each iteration maintains its own set of body node instances for history and isolation.
/// </summary>
public class LoopIteration
{
    /// <summary>
    /// Gets or sets the zero-based iteration index.
    /// </summary>
    public int IterationIndex { get; set; }

    /// <summary>
    /// Gets the body node instances for this iteration.
    /// Key: NodeId, Value: NodeInstance created for this iteration.
    /// </summary>
    public Dictionary<string, NodeInstance> BodyInstances { get; } = new();

    /// <summary>
    /// Gets or sets the start time of this iteration.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time of this iteration.
    /// </summary>
    public DateTime? EndTime { get; set; }
}