namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Repairs control databases whose role table predates the current EF model:
/// adds UpdatedAt, GrantedAt and GrantedBy when missing. Existing grants are kept.
/// Idempotent.
/// </summary>
public static class OperatorRoleSchemaMigration
{
    private static readonly (string Name, string Ddl)[] Columns =
    {
        ("UpdatedAt", "ALTER TABLE user_product_roles ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01 00:00:00+00:00'"),
        ("GrantedAt", "ALTER TABLE user_product_roles ADD COLUMN GrantedAt TEXT NOT NULL DEFAULT '1970-01-01 00:00:00+00:00'"),
        ("GrantedBy", "ALTER TABLE user_product_roles ADD COLUMN GrantedBy TEXT NULL"),
    };

    public static async Task ApplyAsync(ControlDbContext control, CancellationToken ct = default)
    {
        var connection = control.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(user_product_roles)";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetString(1));
        }
        if (existing.Count == 0) return;
        foreach (var (name, ddl) in Columns)
        {
            if (existing.Contains(name)) continue;
            command.CommandText = ddl;
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}