// -----------------------------------------------------------------------
// <copyright file="ChatMigrationModel.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>Durable engine selection and a repeatable import journal. Never contains credential values.</summary>
public sealed class ChatMigrationStateRow
{
    public string Id { get; set; } = "legacy-telegram";
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public string Owner { get; set; } = "legacy";
    public string Phase { get; set; } = "imported";
    public string SourceHash { get; set; } = "";
    public string SourceJson { get; set; } = "{}";
    public string SelectedScopesJson { get; set; } = "[]";
    public string IssuesJson { get; set; } = "[]";
    public long Cursor { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ChatMigrationItemRow
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
}

internal static class ChatMigrationModel
{
    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<ChatMigrationStateRow>().ToTable("chat_migration_state", t =>
        {
            t.HasCheckConstraint("CK_chat_migration_owner", "Owner IN ('legacy','plugins')");
            t.HasCheckConstraint("CK_chat_migration_version", "Version = 1");
        }).HasKey(x => x.Id);
        mb.Entity<ChatMigrationStateRow>().Property(x => x.Revision).IsConcurrencyToken();
        mb.Entity<ChatMigrationItemRow>().ToTable("chat_migration_items").HasKey(x => x.Key);
        mb.Entity<ChatMigrationItemRow>().HasIndex(x => new { x.Kind, x.SourceId }).IsUnique();
    }
}