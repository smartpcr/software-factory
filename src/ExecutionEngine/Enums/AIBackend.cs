// -----------------------------------------------------------------------
// <copyright file="AIBackend.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Enums;

using System.Runtime.Serialization;

/// <summary>
/// Defines the AI backend used for execution.
/// </summary>
public enum AIBackend
{
    /// <summary>
    /// GitHub Copilot CLI / SDK backend.
    /// </summary>
    [EnumMember(Value = "copilot")]
    Copilot = 0,

    /// <summary>
    /// Anthropic Claude CLI backend.
    /// </summary>
    [EnumMember(Value = "claude")]
    Claude,
}