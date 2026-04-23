// -----------------------------------------------------------------------
// <copyright file="RuntimeType.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Enums;

/// <summary>
/// Defines the runtime type for node execution.
/// </summary>
public enum RuntimeType
{
    /// <summary>
    /// Noop node that performs no operation.
    /// </summary>
    Noop = 0,

    /// <summary>
    /// Compiled C# assembly node.
    /// </summary>
    CSharp,

    /// <summary>
    /// C# script node using Roslyn scripting.
    /// </summary>
    CSharpScript,

    /// <summary>
    /// C# task node (supports both inline scripts and compiled executors).
    /// </summary>
    CSharpTask,

    /// <summary>
    /// PowerShell script node.
    /// </summary>
    PowerShell,

    /// <summary>
    /// PowerShell task node (supports both inline scripts and script files).
    /// </summary>
    PowerShellTask,

    /// <summary>
    /// If-else control flow node for conditional branching.
    /// </summary>
    IfElse,

    /// <summary>
    /// ForEach control flow node for collection iteration.
    /// </summary>
    ForEach,

    /// <summary>
    /// While control flow node for condition-based iteration.
    /// </summary>
    While,

    /// <summary>
    /// Switch control flow node for multi-way branching based on expression value.
    /// </summary>
    Switch,

    /// <summary>
    /// Subflow node for executing another workflow as a child/nested workflow.
    /// </summary>
    Subflow,

    /// <summary>
    /// Timer node for scheduled workflow execution.
    /// </summary>
    Timer,

    /// <summary>
    /// Group node for grouping related nodes into logical units with encapsulated execution flow.
    /// Similar to Subflow but with inline definition instead of external file.
    /// </summary>
    Group,

    /// <summary>
    /// Start node — workflow entry point with manual, schedule, or event trigger.
    /// </summary>
    Start,

    /// <summary>
    /// HTTP webhook trigger node.
    /// </summary>
    Trigger,

    /// <summary>
    /// Generic action/task node.
    /// </summary>
    Action,

    /// <summary>
    /// HTTP API call node.
    /// </summary>
    HttpRequest,

    /// <summary>
    /// Data transformation node.
    /// </summary>
    Transform,

    /// <summary>
    /// Wait/pause node.
    /// </summary>
    Delay,

    /// <summary>
    /// Log message output node.
    /// </summary>
    Log,

    /// <summary>
    /// AI skill/subagent node.
    /// </summary>
    AI,

    /// <summary>
    /// Workflow terminator node.
    /// </summary>
    End,

    /// <summary>
    /// Fan-out parallel branches node.
    /// </summary>
    Parallel
}