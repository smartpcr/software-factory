// -----------------------------------------------------------------------
// <copyright file="AuditLogTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Auth;

using System;
using WorkflowUi.Server.Auth;
using Xunit;

public class AuditLogTests
{
    [Fact]
    public void Record_And_GetEntries_Works()
    {
        var log = new InMemoryAuditLog();
        var entry = new AuditLogEntry(DateTime.UtcNow, "user1", "Save", "/api/workflows/test", null, 200);

        log.Record(entry);

        var entries = log.GetEntries();
        Assert.Single(entries);
        Assert.Equal("user1", entries[0].UserId);
        Assert.Equal("Save", entries[0].Action);
        Assert.Equal("/api/workflows/test", entries[0].TargetPath);
    }

    [Fact]
    public void Multiple_Records_Preserved()
    {
        var log = new InMemoryAuditLog();

        log.Record(new AuditLogEntry(DateTime.UtcNow, "u1", "Save", "/a", null, 200));
        log.Record(new AuditLogEntry(DateTime.UtcNow, "u2", "Delete", "/b", "etag1", 200));

        var entries = log.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("u1", entries[0].UserId);
        Assert.Equal("u2", entries[1].UserId);
    }

    [Fact]
    public void GetEntries_Returns_Snapshot()
    {
        var log = new InMemoryAuditLog();
        log.Record(new AuditLogEntry(DateTime.UtcNow, "u1", "Save", "/a", null, 200));

        var snapshot1 = log.GetEntries();
        log.Record(new AuditLogEntry(DateTime.UtcNow, "u2", "Save", "/b", null, 200));
        var snapshot2 = log.GetEntries();

        Assert.Single(snapshot1);
        Assert.Equal(2, snapshot2.Count);
    }
}
