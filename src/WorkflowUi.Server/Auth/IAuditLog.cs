// -----------------------------------------------------------------------
// <copyright file="IAuditLog.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

using System.Collections.Generic;

/// <summary>
/// Sink for audit log entries.
/// </summary>
public interface IAuditLog
{
    void Record(AuditLogEntry entry);

    IReadOnlyList<AuditLogEntry> GetEntries();
}

/// <summary>
/// In-memory audit log for testing and development.
/// </summary>
public class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditLogEntry> entries = new();

    public void Record(AuditLogEntry entry)
    {
        lock (this.entries)
        {
            this.entries.Add(entry);
        }
    }

    public IReadOnlyList<AuditLogEntry> GetEntries()
    {
        lock (this.entries)
        {
            return this.entries.ToList().AsReadOnly();
        }
    }
}
