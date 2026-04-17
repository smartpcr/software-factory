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
/// Workflows are stored as {id}.yaml + {id}.ui.json pairs under WorkflowsRoot.
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

        var yamlFiles = Directory.GetFiles(root, "*.yaml", SearchOption.AllDirectories);
        var summaries = yamlFiles.Select(f =>
        {
            var info = new FileInfo(f);
            var id = Path.GetRelativePath(root, f)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(".yaml", string.Empty, StringComparison.OrdinalIgnoreCase);
            return new WorkflowSummary(id, info.LastWriteTimeUtc, info.Length);
        }).ToList();

        return Task.FromResult<IReadOnlyList<WorkflowSummary>>(summaries);
    }

    public Task<WorkflowRecord?> GetAsync(string id, ClaimsPrincipal user, CancellationToken ct)
    {
        this.RequireRole(user, AuthPolicies.ViewerRole);

        var yamlPath = this.ResolvePath(id, ".yaml");
        if (yamlPath == null)
        {
            this.logger.LogWarning("Path traversal rejected for id {Id}", id);
            return Task.FromResult<WorkflowRecord?>(null);
        }

        if (!File.Exists(yamlPath))
        {
            return Task.FromResult<WorkflowRecord?>(null);
        }

        var yaml = File.ReadAllText(yamlPath);
        var sidecarPath = Path.ChangeExtension(yamlPath, ".ui.json");
        var sidecar = File.Exists(sidecarPath) ? File.ReadAllText(sidecarPath) : "{}";
        var etag = ETagComputer.Compute(yaml, sidecar);
        var lastModified = new FileInfo(yamlPath).LastWriteTimeUtc;

        return Task.FromResult<WorkflowRecord?>(
            new WorkflowRecord(id, yaml, sidecar, etag, lastModified));
    }

    public Task<SaveResult> SaveAsync(
        string id,
        string yaml,
        string sidecarJson,
        string? etag,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        this.RequireRole(user, AuthPolicies.EditorRole);

        var yamlPath = this.ResolvePath(id, ".yaml");
        if (yamlPath == null)
        {
            this.logger.LogWarning("Path traversal rejected on save for id {Id}", id);
            return Task.FromResult(SaveResult.PreconditionFailed);
        }

        var sidecarPath = Path.ChangeExtension(yamlPath, ".ui.json");
        var exists = File.Exists(yamlPath);

        if (exists && etag != null)
        {
            var currentYaml = File.ReadAllText(yamlPath);
            var currentSidecar = File.Exists(sidecarPath) ? File.ReadAllText(sidecarPath) : "{}";
            var currentEtag = ETagComputer.Compute(currentYaml, currentSidecar);

            if (!string.Equals(etag, currentEtag, StringComparison.Ordinal))
            {
                return Task.FromResult(SaveResult.PreconditionFailed);
            }
        }

        var dir = Path.GetDirectoryName(yamlPath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(yamlPath, yaml);
        File.WriteAllText(sidecarPath, sidecarJson ?? "{}");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        this.auditLog.Record(new AuditLogEntry(
            DateTime.UtcNow, userId, "Save", id, etag, exists ? 200 : 201));

        return Task.FromResult(exists ? SaveResult.Updated : SaveResult.Created);
    }

    public Task<bool> DeleteAsync(string id, string etag, ClaimsPrincipal user, CancellationToken ct)
    {
        this.RequireRole(user, AuthPolicies.EditorRole);

        var yamlPath = this.ResolvePath(id, ".yaml");
        if (yamlPath == null)
        {
            this.logger.LogWarning("Path traversal rejected on delete for id {Id}", id);
            return Task.FromResult(false);
        }

        if (!File.Exists(yamlPath))
        {
            return Task.FromResult(false);
        }

        var sidecarPath = Path.ChangeExtension(yamlPath, ".ui.json");
        var currentYaml = File.ReadAllText(yamlPath);
        var currentSidecar = File.Exists(sidecarPath) ? File.ReadAllText(sidecarPath) : "{}";
        var currentEtag = ETagComputer.Compute(currentYaml, currentSidecar);

        if (!string.Equals(etag, currentEtag, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        File.Delete(yamlPath);
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
