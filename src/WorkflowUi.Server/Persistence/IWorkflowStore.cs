// -----------------------------------------------------------------------
// <copyright file="IWorkflowStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Abstraction for workflow CRUD operations with role enforcement.
/// </summary>
public interface IWorkflowStore
{
    Task<IReadOnlyList<WorkflowSummary>> ListAsync(ClaimsPrincipal user, CancellationToken ct);

    Task<WorkflowRecord?> GetAsync(string id, ClaimsPrincipal user, CancellationToken ct);

    Task<SaveResult> SaveAsync(
        string id,
        string yaml,
        string sidecarJson,
        string? etag,
        ClaimsPrincipal user,
        CancellationToken ct);

    Task<bool> DeleteAsync(string id, string etag, ClaimsPrincipal user, CancellationToken ct);
}

/// <summary>
/// Summary of a workflow in the store (lightweight, no YAML body).
/// </summary>
public record WorkflowSummary(string Id, DateTime LastModifiedUtc, long SizeBytes);

/// <summary>
/// Full workflow record with YAML, sidecar, and ETag.
/// </summary>
public record WorkflowRecord(
    string Id,
    string Yaml,
    string SidecarJson,
    string ETag,
    DateTime LastModifiedUtc);

/// <summary>
/// Result of a save operation.
/// </summary>
public enum SaveResult
{
    Created,
    Updated,
    PreconditionFailed,
}
