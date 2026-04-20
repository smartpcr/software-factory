// -----------------------------------------------------------------------
// <copyright file="LogLevel.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Enums;

/// <summary>
/// Log severity level for LogNode.
/// </summary>
public enum LogLevel
{
    /// <summary>Debug-level diagnostic information.</summary>
    Debug,

    /// <summary>Informational messages.</summary>
    Info,

    /// <summary>Warning conditions.</summary>
    Warn,

    /// <summary>Error conditions.</summary>
    Error
}
