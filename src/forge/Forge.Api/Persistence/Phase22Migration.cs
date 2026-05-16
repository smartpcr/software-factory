// -----------------------------------------------------------------------
// <copyright file="Phase22Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 22 — splits the runtime-assigned
/// agent from the WIT-level assignee.
///
/// <list type="bullet">
///   <item>Adds <c>AssignedAgentJson TEXT NULL</c> to <c>work_items</c>.</item>
///   <item>Creates <c>agent_assignments</c> append-only history table.</item>
///   <item>One-shot backfill of pre-Phase-22 runtime-shaped <c>AssignedToJson</c>
///         into the new field.</item>
/// </list>
/// </summary>
public static class Phase22Migration
{
    private const string BackfillSeedKey = "phase22-backfill-done";

    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // 22.1 — add AssignedAgentJson column to work_items.
        await EnsureColumnAsync(conn, "work_items", "AssignedAgentJson", "TEXT NULL", ct);

        // 22.1 — create agent_assignments table.
        await EnsureAgentAssignmentsTableAsync(conn, ct);

        // 22.7 — one-shot backfill.
        await BackfillRuntimeAssignmentsAsync(db, conn, ct);
    }

    private static async Task EnsureAgentAssignmentsTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (await TableExistsAsync(conn, "agent_assignments", ct)) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE agent_assignments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkItemId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                AgentIdsJson TEXT NOT NULL DEFAULT '[]',
                Team TEXT NULL,
                CrewId TEXT NULL,
                Role TEXT NULL,
                FromExecutionStatus TEXT NULL,
                ToExecutionStatus TEXT NULL,
                AssignedBy TEXT NULL,
                Reason TEXT NULL,
                At TEXT NOT NULL
            );
            CREATE INDEX IX_agent_assignments_workitem_at ON agent_assignments (WorkItemId, At DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Phase 22.7 — one-shot backfill of pre-Phase-22 data. Walks every
    /// <c>WorkItemRow</c> whose <c>AssignedToJson</c> parses to the runtime
    /// shape (has <c>generatorAgentId</c> or <c>crewId</c> at top level,
    /// no <c>kind</c>). For each match: copies data into <c>AssignedAgentJson</c>
    /// in Q22.2 shape and rebuilds <c>AssignedToJson</c> as WIT-shape.
    /// Gated by a <c>forge_seed_state</c> row so re-boots skip it.
    /// </summary>
    private static async Task BackfillRuntimeAssignmentsAsync(
        ProductDbContext db, SqliteConnection conn, CancellationToken ct)
    {
        // Gate check — skip if already done.
        if (await IsSeedStateDoneAsync(conn, BackfillSeedKey, ct)) return;

        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var migrated = 0;

        var rows = await db.WorkItems
            .Where(w => w.AssignedToJson != null)
            .ToListAsync(ct);

        foreach (var wi in rows)
        {
            if (string.IsNullOrWhiteSpace(wi.AssignedToJson)) continue;

            // Heuristic: runtime-shaped means the JSON has
            // 'generatorAgentId' OR 'crewId' at top level, AND no 'kind'.
            try
            {
                using var doc = JsonDocument.Parse(wi.AssignedToJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var hasKind = root.TryGetProperty("kind", out _);
                if (hasKind) continue; // WIT-shape — skip.

                var hasGen = root.TryGetProperty("generatorAgentId", out var genProp);
                var hasCrew = root.TryGetProperty("crewId", out var crewProp);
                if (!hasGen && !hasCrew) continue;

                // Build the Q22.2 AssignedAgent shape.
                var agents = new List<string>();
                if (hasGen && genProp.ValueKind == JsonValueKind.String)
                    agents.Add(genProp.GetString()!);
                if (root.TryGetProperty("evaluatorAgentId", out var evalProp) && evalProp.ValueKind == JsonValueKind.String)
                    agents.Add(evalProp.GetString()!);

                var crewId = hasCrew && crewProp.ValueKind == JsonValueKind.String ? crewProp.GetString() : null;

                // Resolve team from crew row (best-effort).
                string? team = null;
                if (crewId is not null)
                {
                    var crew = await db.Crews.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == crewId, ct);
                    if (crew is not null)
                    {
                        var cn = crew.Name.ToLowerInvariant();
                        if (cn.StartsWith("arch")) team = "Architecture";
                        else if (cn.StartsWith("eng")) team = "Engineering";
                        else if (cn.StartsWith("rev")) team = "Review";
                        else if (cn.StartsWith("qa")) team = "QA";
                    }
                }

                var assignedAgent = new
                {
                    agents,
                    team,
                    crewId,
                    role = "generator",
                    assignedAt = DateTimeOffset.UtcNow.ToString("o"),
                    assignedBy = "backfill",
                };
                wi.AssignedAgentJson = JsonSerializer.Serialize(assignedAgent, jsonOpts);

                // Rebuild AssignedToJson as WIT-shape.
                var witTeam = team ?? "Engineering";
                wi.AssignedToJson = JsonSerializer.Serialize(new
                {
                    kind = "team",
                    id = witTeam,
                    displayName = witTeam,
                }, jsonOpts);

                // Append history row.
                db.AgentAssignments.Add(new AgentAssignmentRow
                {
                    WorkItemId = wi.Id,
                    Kind = AgentAssignmentKinds.Backfill,
                    AgentIdsJson = JsonSerializer.Serialize(agents, jsonOpts),
                    Team = team,
                    CrewId = crewId,
                    Role = "generator",
                    AssignedBy = "backfill",
                    At = DateTimeOffset.UtcNow,
                });

                migrated++;
            }
            catch (JsonException)
            {
                // Malformed JSON — skip gracefully.
            }
        }

        if (migrated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        // Mark as done.
        await SetSeedStateDoneAsync(conn, BackfillSeedKey, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string typeDecl, CancellationToken ct)
    {
        if (await ColumnExistsAsync(conn, table, column, ct)) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Simple key-value gate using the <c>forge_seed_state</c> table.
    /// Creates the table if it doesn't exist.
    /// </summary>
    private static async Task<bool> IsSeedStateDoneAsync(SqliteConnection conn, string key, CancellationToken ct)
    {
        // Ensure table exists.
        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);";
        await createCmd.ExecuteNonQueryAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM forge_seed_state WHERE Key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    private static async Task SetSeedStateDoneAsync(SqliteConnection conn, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO forge_seed_state (Key, DoneAt) VALUES (@key, @at);";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}