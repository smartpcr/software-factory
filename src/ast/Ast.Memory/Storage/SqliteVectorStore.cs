// -----------------------------------------------------------------------
// <copyright file="SqliteVectorStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Storage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ast.VectorDb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pure-managed SQLite vector store for local mode.
/// Stores vectors as binary blobs in regular SQLite tables and performs
/// brute-force cosine similarity search in memory. No native extensions required.
/// <para>
/// Tables per collection:
/// <c>collections(name TEXT PRIMARY KEY, vector_size INTEGER)</c> — registry,
/// <c>{safe}_vectors(id TEXT PRIMARY KEY, vector BLOB NOT NULL, payload TEXT)</c> — data.
/// </para>
/// <para>
/// This implementation trades ANN speed for zero-dependency reliability:
/// search loads all vectors and computes cosine similarity in managed code.
/// Suitable for local/single-user workloads (≤ ~100 k vectors).
/// </para>
/// </summary>
public sealed class SqliteVectorStore : IVectorStore
{
    private readonly SqliteConnection connection;
    private readonly ILogger logger;
    private readonly object syncLock = new();
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteVectorStore"/> class.
    /// </summary>
    /// <param name="dbPath">
    /// Path to the SQLite database file, or <c>":memory:"</c> for an in-memory database.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public SqliteVectorStore(string dbPath, ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var connectionString = string.Equals(dbPath, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? "Data Source=:memory:"
            : $"Data Source={dbPath}";

        this.connection = new SqliteConnection(connectionString);
        this.connection.Open();

        this.EnableWalMode();
        this.InitializeMetaTable();

        this.logger.LogInformation("SqliteVectorStore opened at '{Path}'", dbPath);
    }

    /// <inheritdoc/>
    public string Name => "Sqlite";

    /// <inheritdoc/>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(!this.disposed);
    }

    /// <inheritdoc/>
    public Task CreateCollectionAsync(
        string collectionName,
        int vectorSize,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();
        var safeName = SanitizeName(collectionName);

        lock (this.syncLock)
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = $@"
                INSERT OR REPLACE INTO collections (name, vector_size)
                VALUES (@name, @size);

                CREATE TABLE IF NOT EXISTS [{safeName}_vectors] (
                    id TEXT PRIMARY KEY,
                    vector BLOB NOT NULL,
                    payload TEXT
                );";

            cmd.Parameters.AddWithValue("@name", collectionName);
            cmd.Parameters.AddWithValue("@size", vectorSize);
            cmd.ExecuteNonQuery();
        }

        this.logger.LogInformation(
            "Created collection '{Collection}' (dim={Size})",
            collectionName, vectorSize);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> CollectionExistsAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();

        lock (this.syncLock)
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM collections WHERE name = @name";
            cmd.Parameters.AddWithValue("@name", collectionName);
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            return Task.FromResult(count > 0);
        }
    }

    /// <inheritdoc/>
    public Task DeleteCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();
        var safeName = SanitizeName(collectionName);

        lock (this.syncLock)
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = $@"
                DROP TABLE IF EXISTS [{safeName}_vectors];
                DELETE FROM collections WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", collectionName);
            cmd.ExecuteNonQuery();
        }

        this.logger.LogInformation("Deleted collection '{Collection}'", collectionName);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpsertAsync(
        string collectionName,
        IEnumerable<VectorPoint> points,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();
        var safeName = SanitizeName(collectionName);
        var pointsList = points.ToList();

        if (pointsList.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (this.syncLock)
        {
            using var transaction = this.connection.BeginTransaction();
            try
            {
                foreach (var point in pointsList)
                {
                    using var cmd = this.connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = $@"
                        INSERT INTO [{safeName}_vectors] (id, vector, payload)
                        VALUES (@id, @vector, @payload)
                        ON CONFLICT(id) DO UPDATE SET
                            vector = excluded.vector,
                            payload = excluded.payload;";
                    cmd.Parameters.AddWithValue("@id", point.Id);
                    cmd.Parameters.AddWithValue("@vector", SerializeVector(point.Vector));
                    cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(point.Payload));
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        this.logger.LogDebug(
            "Upserted {Count} vectors into collection '{Collection}'",
            pointsList.Count, collectionName);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        int limit = 10,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();
        var safeName = SanitizeName(collectionName);

        var candidates = new List<VectorSearchResult>();

        lock (this.syncLock)
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = $"SELECT id, vector, payload FROM [{safeName}_vectors]";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var vector = DeserializeVector((byte[])reader[1]);
                var payloadJson = reader.IsDBNull(2) ? "{}" : reader.GetString(2);
                var payload = DeserializePayload(payloadJson);

                if (filter != null && !MatchesFilter(payload, filter))
                {
                    continue;
                }

                var score = CosineSimilarity(queryVector, vector);
                candidates.Add(new VectorSearchResult
                {
                    Id = id,
                    Score = score,
                    Payload = payload
                });
            }
        }

        IReadOnlyList<VectorSearchResult> results = candidates
            .OrderByDescending(c => c.Score)
            .Take(limit)
            .ToList();

        return Task.FromResult(results);
    }

    /// <inheritdoc/>
    public Task<VectorPoint?> GetAsync(
        string collectionName,
        string id,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();
        var safeName = SanitizeName(collectionName);

        lock (this.syncLock)
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = $"SELECT id, vector, payload FROM [{safeName}_vectors] WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return Task.FromResult<VectorPoint?>(null);
            }

            return Task.FromResult<VectorPoint?>(new VectorPoint
            {
                Id = reader.GetString(0),
                Vector = DeserializeVector((byte[])reader[1]),
                Payload = DeserializePayload(reader.IsDBNull(2) ? "{}" : reader.GetString(2))
            });
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(
        string collectionName,
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();
        var safeName = SanitizeName(collectionName);
        var idsList = ids.ToList();

        if (idsList.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (this.syncLock)
        {
            foreach (var id in idsList)
            {
                using var cmd = this.connection.CreateCommand();
                cmd.CommandText = $"DELETE FROM [{safeName}_vectors] WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        this.ThrowIfDisposed();
        var safeName = SanitizeName(collectionName);

        lock (this.syncLock)
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM [{safeName}_vectors]";

            try
            {
                var count = Convert.ToInt64(cmd.ExecuteScalar());
                return Task.FromResult(count);
            }
            catch (SqliteException)
            {
                return Task.FromResult(0L);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        try
        {
            this.connection.Close();
            this.connection.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        this.logger.LogInformation("SqliteVectorStore disposed");
    }

    /// <summary>
    /// Validates and sanitizes a collection name for use in SQL identifiers.
    /// Rejects names containing characters that are not alphanumeric, underscores,
    /// hyphens, dots, or spaces after sanitization.
    /// </summary>
    internal static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Collection name must not be null or empty.", nameof(name));
        }

        var sanitized = name.Replace("-", "_").Replace(".", "_").Replace(" ", "_");

        if (!Regex.IsMatch(sanitized, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException(
                $"Collection name '{name}' contains invalid characters after sanitization. " +
                "Only alphanumeric characters, underscores, hyphens, dots, and spaces are allowed.",
                nameof(name));
        }

        return sanitized;
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeVector(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return magnitude == 0 ? 0 : dotProduct / magnitude;
    }

    private static Dictionary<string, object> DeserializePayload(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        if (raw == null)
        {
            return new Dictionary<string, object>();
        }

        var result = new Dictionary<string, object>(raw.Count);
        foreach (var (key, element) in raw)
        {
            result[key] = ConvertJsonElement(element);
        }

        return result;
    }

    private static object ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => element.ToString()
        };
    }

    private static bool MatchesFilter(Dictionary<string, object> payload, VectorFilter filter)
    {
        foreach (var condition in filter.Must)
        {
            if (!EvaluateCondition(payload, condition))
            {
                return false;
            }
        }

        if (filter.MustNot.Count > 0)
        {
            foreach (var condition in filter.MustNot)
            {
                if (EvaluateCondition(payload, condition))
                {
                    return false;
                }
            }
        }

        if (filter.Should.Count > 0)
        {
            var anyMatch = filter.Should.Any(c => EvaluateCondition(payload, c));
            if (!anyMatch)
            {
                return false;
            }
        }

        return true;
    }

    private static bool EvaluateCondition(Dictionary<string, object> payload, FilterCondition condition)
    {
        if (!payload.TryGetValue(condition.Field, out var value))
        {
            return false;
        }

        var valueStr = value?.ToString() ?? string.Empty;
        var conditionStr = condition.Value?.ToString() ?? string.Empty;

        return condition.Operator switch
        {
            FilterOperator.Equals => string.Equals(valueStr, conditionStr, StringComparison.Ordinal),
            FilterOperator.NotEquals => !string.Equals(valueStr, conditionStr, StringComparison.Ordinal),
            FilterOperator.Contains => valueStr.Contains(conditionStr, StringComparison.Ordinal),
            FilterOperator.In when condition.Value is IEnumerable<string> list => list.Contains(valueStr),
            _ => string.Equals(valueStr, conditionStr, StringComparison.Ordinal)
        };
    }

    private void EnableWalMode()
    {
        try
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            this.logger.LogDebug(ex, "Could not enable WAL mode (in-memory DB or read-only)");
        }
    }

    private void InitializeMetaTable()
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS collections (
                name TEXT PRIMARY KEY,
                vector_size INTEGER NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
    }
}