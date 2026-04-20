// -----------------------------------------------------------------------
// <copyright file="FileWorkflowStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Auth;

/// <summary>
/// File-based implementation of <see cref="IWorkflowStore"/>.
/// Workflows are stored as {id}.json + {id}.ui.json pairs under WorkflowsRoot.
/// </summary>
public class FileWorkflowStore : IWorkflowStore
{
    private readonly PersistenceOptions options;
    private readonly IAuditLog auditLog;
    private readonly ILogger<FileWorkflowStore> logger;

    public FileWorkflowStore(
        IOptions<PersistenceOptions> options,
        IAuditLog auditLog,
        ILogger<FileWorkflowStore> logger)
    {
        this.options = options.Value;
        this.auditLog = auditLog;
        this.logger = logger;
    }

    public Task<IReadOnlyList<WorkflowSummary>> ListAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        this.RequireRole(user, AuthPolicies.ViewerRole);

        var root = this.options.WorkflowsRoot;
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<WorkflowSummary>>(Array.Empty<WorkflowSummary>());
        }

        var defFiles = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var summaries = defFiles.Select(f =>
        {
            var info = new FileInfo(f);
            var id = Path.GetRelativePath(root, f)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(".json", string.Empty, StringComparison.OrdinalIgnoreCase);
            return new WorkflowSummary(id, info.LastWriteTimeUtc, info.Length);
        }).ToList();

        return Task.FromResult<IReadOnlyList<WorkflowSummary>>(summaries);
    }

    public Task<WorkflowRecord?> GetAsync(string id, ClaimsPrincipal user, CancellationToken ct)
    {
        this.RequireRole(user, AuthPolicies.ViewerRole);

        var defPath = this.ResolvePath(id, ".json");
        if (defPath == null)
        {
            this.logger.LogWarning("Path traversal rejected for id {Id}", id);
            return Task.FromResult<WorkflowRecord?>(null);
        }

        if (!File.Exists(defPath))
        {
            return Task.FromResult<WorkflowRecord?>(null);
        }

        var definition = File.ReadAllText(defPath);
        var sidecarPath = Path.ChangeExtension(defPath, ".ui.json");
        var sidecar = File.Exists(sidecarPath) ? File.ReadAllText(sidecarPath) : "{}";
        var etag = ETagComputer.Compute(definition, sidecar);
        var lastModified = new FileInfo(defPath).LastWriteTimeUtc;

        return Task.FromResult<WorkflowRecord?>(
            new WorkflowRecord(id, definition, sidecar, etag, lastModified));
    }

    public Task<SaveResult> SaveAsync(
        string id,
        string definition,
        string sidecarJson,
        string? etag,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        this.RequireRole(user, AuthPolicies.EditorRole);

        var defPath = this.ResolvePath(id, ".json");
        if (defPath == null)
        {
            this.logger.LogWarning("Path traversal rejected on save for id {Id}", id);
            return Task.FromResult(SaveResult.PreconditionFailed);
        }

        var sidecarPath = Path.ChangeExtension(defPath, ".ui.json");
        var exists = File.Exists(defPath);

        if (exists && etag != null)
        {
            var currentDef = File.ReadAllText(defPath);
            var currentSidecar = File.Exists(sidecarPath) ? File.ReadAllText(sidecarPath) : "{}";
            var currentEtag = ETagComputer.Compute(currentDef, currentSidecar);

            if (!string.Equals(etag, currentEtag, StringComparison.Ordinal))
            {
                return Task.FromResult(SaveResult.PreconditionFailed);
            }
        }

        var dir = Path.GetDirectoryName(defPath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(defPath, definition);
        File.WriteAllText(sidecarPath, sidecarJson ?? "{}");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        this.auditLog.Record(new AuditLogEntry(
            DateTime.UtcNow, userId, "Save", id, etag, exists ? 200 : 201));

        return Task.FromResult(exists ? SaveResult.Updated : SaveResult.Created);
    }

    public Task<bool> DeleteAsync(string id, string etag, ClaimsPrincipal user, CancellationToken ct)
    {
        this.RequireRole(user, AuthPolicies.EditorRole);

        var defPath = this.ResolvePath(id, ".json");
        if (defPath == null)
        {
            this.logger.LogWarning("Path traversal rejected on delete for id {Id}", id);
            return Task.FromResult(false);
        }

        if (!File.Exists(defPath))
        {
            return Task.FromResult(false);
        }

        var sidecarPath = Path.ChangeExtension(defPath, ".ui.json");
        var currentDef = File.ReadAllText(defPath);
        var currentSidecar = File.Exists(sidecarPath) ? File.ReadAllText(sidecarPath) : "{}";
        var currentEtag = ETagComputer.Compute(currentDef, currentSidecar);

        if (!string.Equals(etag, currentEtag, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        File.Delete(defPath);
        if (File.Exists(sidecarPath))
        {
            File.Delete(sidecarPath);
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        this.auditLog.Record(new AuditLogEntry(
            DateTime.UtcNow, userId, "Delete", id, etag, 200));

        return Task.FromResult(true);
    }

    private string? ResolvePath(string id, string extension)
    {
        var safePath = PathSafety.CanonicalizeAndCheck(this.options.WorkflowsRoot, id + extension);
        return safePath;
    }

    private void RequireRole(ClaimsPrincipal user, string role)
    {
        if (!user.IsInRole(role))
        {
            throw new UnauthorizedAccessException($"Required role: {role}");
        }
    }
}
