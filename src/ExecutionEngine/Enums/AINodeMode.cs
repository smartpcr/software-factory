// -----------------------------------------------------------------------
// <copyright file="AINodeMode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Enums;

using System.Runtime.Serialization;

/// <summary>
/// Defines the execution mode for an AI node.
/// </summary>
public enum AINodeMode
{
    /// <summary>
    /// Invoke a registered skill by identifier.
    /// </summary>
    [EnumMember(Value = "skill")]
    Skill = 0,

    /// <summary>
    /// Delegate work to a sub-agent.
    /// </summary>
    [EnumMember(Value = "subagent")]
    SubAgent,

    /// <summary>
    /// Call an external tool via Model Context Protocol.
    /// </summary>
    [EnumMember(Value = "mcp")]
    MCP,

    /// <summary>
    /// Send a raw prompt directly to a model.
    /// </summary>
    [EnumMember(Value = "prompt")]
    RawPrompt,
}