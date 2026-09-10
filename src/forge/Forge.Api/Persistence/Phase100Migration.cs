// -----------------------------------------------------------------------
// <copyright file="Phase100Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public static class Phase100Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        await control.Database.OpenConnectionAsync(ct);
        await using var tx = await control.Database.BeginTransactionAsync(ct);
        foreach (var (name, type) in new[]
        {
            ("ScopeKind", "TEXT NOT NULL DEFAULT 'legacy'"), ("HostId", "TEXT NULL"), ("ArtifactKey", "TEXT NULL"),
            ("CatalogVersion", "INTEGER NOT NULL DEFAULT 0"), ("PayloadVersion", "INTEGER NOT NULL DEFAULT 0"),
            ("PayloadJson", "TEXT NULL"), ("DomainRevision", "INTEGER NOT NULL DEFAULT 0"),
            ("RouteState", "TEXT NOT NULL DEFAULT 'legacy'"), ("RouteReason", "TEXT NULL"),
        })
        {
            await using var command = control.Database.GetDbConnection().CreateCommand();
            command.Transaction = tx.GetDbTransaction();
            command.CommandText = "SELECT 1 FROM pragma_table_info('chat_notifications') WHERE name = $name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$name"; parameter.Value = name;
            command.Parameters.Add(parameter);
            if (await command.ExecuteScalarAsync(ct) is not null) continue;
            command.Parameters.Clear();
            command.CommandText = $"ALTER TABLE chat_notifications ADD COLUMN {name} {type}";
            await command.ExecuteNonQueryAsync(ct);
        }
        using var schema = new Schema(new DbContextOptionsBuilder<Schema>().UseSqlite("Data Source=:memory:").Options);
        await using (var command = control.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = tx.GetDbTransaction();
            command.CommandText = "SELECT 1 FROM pragma_table_info('chat_subscriptions') WHERE name = 'ContentMode'";
            if (await command.ExecuteScalarAsync(ct) is null)
            {
                command.CommandText = "ALTER TABLE chat_subscriptions ADD COLUMN ContentMode TEXT NOT NULL DEFAULT 'metadata'";
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        await control.Database.ExecuteSqlRawAsync(Idempotent(schema.Database.GenerateCreateScript()), ct);
        await tx.CommitAsync(ct);
    }

    internal static string Idempotent(string sql) => sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
        .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
        .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);

    private sealed class Schema(DbContextOptions<Schema> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            ChatConfigurationModel.Configure(mb);
            mb.Entity<ChatNotificationRow>().ToTable("chat_notifications").HasKey(x => x.Id);
            ChatDeliveryModel.Configure(mb);
        }
    }
}
