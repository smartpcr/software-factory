namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>Repairs legacy control databases whose role table predates UpdatedAt.</summary>
public static class OperatorRoleSchemaMigration
{
    public static async Task ApplyAsync(ControlDbContext control, CancellationToken ct = default)
    {
        var connection = control.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(user_product_roles)";
        var found = false;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                if (reader.GetString(1) == "UpdatedAt") found = true;
        }
        if (found) return;
        command.CommandText = "ALTER TABLE user_product_roles ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01 00:00:00+00:00'";
        await command.ExecuteNonQueryAsync(ct);
    }
}
