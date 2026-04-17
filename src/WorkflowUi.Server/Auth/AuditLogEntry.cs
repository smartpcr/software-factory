// -----------------------------------------------------------------------
// <copyright file="AuditLogEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

using System;

/// <summary>
/// Represents a single audit log entry for a write operation.
/// </summary>
public record AuditLogEntry(
    DateTime TimestampUtc,
    string UserId,
    string Action,
    string TargetPath,
    string? ETag,
    int OutcomeCode);
