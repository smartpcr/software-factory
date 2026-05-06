// -----------------------------------------------------------------------
// <copyright file="SqliteFileSyncStateStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Data.SQLite;
using Ast.Memory.Interfaces;

/// <summary>
/// SQLite-backed implementation of <see cref="IFileSyncStateStore"/>.
/// Persists last-synced ContentHash per file across process restarts,
/// enabling the file-system watcher to skip unchanged files without
/// re-extraction. See architecture.md §3.6.
///
/// Each operation opens its own connection to avoid thread-safety issues
/// with concurrent watcher callbacks. WAL mode and a busy timeout are set
/// on first use for resilience under contention.
/// </summary>
public sealed class SqliteFileSyncStateStore : IFileSyncStateStore
{
    private readonly string connectionString;

    /// <summary>
    /// Initializes a new instance and ensures the backing table exists.
    /// </summary>
    /// <param name="dbPath">
    /// Full path to the SQLite database file. The directory is created if absent.
    /// </param>
    public SqliteFileSyncStateStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        this.connectionString = $"Data Source={dbPath};Version=3;";
        this.EnsureTable();
    }

    /// <inheritdoc/>
    public async Task<string?> GetContentHashAsync(string relativePath, CancellationToken ct = default)
    {
        var normalized = NormalizePath(relativePath);
        using var conn = new SQLiteConnection(this.connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content_hash FROM file_sync_state WHERE file_path = @path";
        cmd.Parameters.AddWithValue("@path", normalized);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    /// <inheritdoc/>
    public async Task SetContentHashAsync(string relativePath, string contentHash, CancellationToken ct = default)
    {
        var normalized = NormalizePath(relativePath);
        using var conn = new SQLiteConnection(this.connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO file_sync_state (file_path, content_hash)
            VALUES (@path, @hash)
            ON CONFLICT(file_path) DO UPDATE SET content_hash = @hash";
        cmd.Parameters.AddWithValue("@path", normalized);
        cmd.Parameters.AddWithValue("@hash", contentHash);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(string relativePath, CancellationToken ct = default)
    {
        var normalized = NormalizePath(relativePath);
        using var conn = new SQLiteConnection(this.connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_sync_state WHERE file_path = @path";
        cmd.Parameters.AddWithValue("@path", normalized);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void EnsureTable()
    {
        using var conn = new SQLiteConnection(this.connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS file_sync_state (
                file_path   TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL
            )";
        cmd.ExecuteNonQuery();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
