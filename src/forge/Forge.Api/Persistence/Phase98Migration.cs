// -----------------------------------------------------------------------
// <copyright file="Phase98Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>Stage 1 chat configuration. Additive, control-only; no legacy import or product changes.</summary>
public static class Phase98Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        // Generate ONLY this increment's six tables, not the evolving full control model.
        // Fresh and upgraded installations therefore receive identical keys, FKs, checks and indexes.
        using var schema = new ConfigurationSchema(new DbContextOptionsBuilder<ConfigurationSchema>()
            .UseSqlite("Data Source=:memory:").Options);
        var sql = schema.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
        await using var transaction = await control.Database.BeginTransactionAsync(ct);
        await control.Database.ExecuteSqlRawAsync(sql, ct);
        await transaction.CommitAsync(ct);
    }

    private sealed class ConfigurationSchema(DbContextOptions<ConfigurationSchema> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb) => ChatConfigurationModel.Configure(mb);
    }
}