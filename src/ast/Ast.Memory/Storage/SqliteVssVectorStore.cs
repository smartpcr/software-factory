// -----------------------------------------------------------------------
// <copyright file="SqliteVssVectorStore.cs" company="Microsoft Corp.">
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
/// SQLite-based vector store for local mode per architecture.md §3.2.
/// Implements the <c>SqliteVssVectorStore</c> described in the architecture:
/// attempts to load the <c>sqlite-vss</c> extension for HNSW-accelerated ANN
/// search via virtual tables (<c>vss_{collection}</c>); when the native
/// extension is unavailable, falls back to regular SQLite tables with
/// brute-force cosine similarity computed in managed code.
/// <para>
/// Architecture mapping (architecture.md §3.2):
/// <list type="bullet">
/// <item><c>collectionName</c> → virtual table <c>vss_{safe}</c> (vss mode)
/// or regular table <c>{safe}_vectors</c> (fallback mode)</item>
/// <item><c>vss_id_map</c> table bridges sqlite-vss integer rowids to string
/// node/expertise IDs when vss mode is active</item>
/// <item><c>collections</c> meta-table tracks registered collections and
/// their vector dimensions</item>
/// </list>
/// </para>
/// </summary>
public sealed class SqliteVssVectorStore : IVectorStore
{
    private readonly SqliteConnection connection;
    private readonly ILogger logger;
    private readonly object syncLock = new();
    private readonly bool vssAvailable;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteVssVectorStore"/> class.
    /// </summary>
    /// <param name="dbPath">
    /// Path to the SQLite database file, or <c>":memory:"</c> for an in-memory database.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public SqliteVssVectorStore(string dbPath, ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var connectionString = string.Equals(dbPath, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? "Data Source=:memory:"
            : $"Data Source={dbPath}";

        this.connection = new SqliteConnection(connectionString);
        this.connection.Open();

        this.EnableWalMode();
        this.InitializeMetaTable();
        this.vssAvailable = this.TryLoadVssExtension();

        if (this.vssAvailable)
        {
            this.InitializeVssIdMap();
            this.logger.LogInformation(
                "SqliteVssVectorStore opened at '{Path}' with sqlite-vss HNSW acceleration", dbPath);
        }
        else
        {
            this.logger.LogInformation(
                "SqliteVssVectorStore opened at '{Path}' in brute-force fallback mode " +
                "(sqlite-vss extension not available)", dbPath);
        }
    }

    /// <inheritdoc/>
    public string Name => "SqliteVss";

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

            if (this.vssAvailable)
            {
                cmd.CommandText = $@"
                    INSERT OR REPLACE INTO collections (name, vector_size)
                    VALUES (@name, @size);

                    CREATE VIRTUAL TABLE IF NOT EXISTS [vss_{safeName}]
                    USING vss0(embedding({vectorSize}));";
            }
            else
            {
                cmd.CommandText = $@"
                    INSERT OR REPLACE INTO collections (name, vector_size)
                    VALUES (@name, @size);

                    CREATE TABLE IF NOT EXISTS [{safeName}_vectors] (
                        id TEXT PRIMARY KEY,
                        vector BLOB NOT NULL,
                        payload TEXT
                    );";
            }

            cmd.Parameters.AddWithValue("@name", collectionName);
            cmd.Parameters.AddWithValue("@size", vectorSize);
            cmd.ExecuteNonQuery();
        }

        this.logger.LogInformation(
            "Created collection '{Collection}' (dim={Size}, vss={Vss})",
            collectionName, vectorSize, this.vssAvailable);
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

            if (this.vssAvailable)
            {
                cmd.CommandText = $@"
                    DROP TABLE IF EXISTS [vss_{safeName}];
                    DELETE FROM vss_id_map WHERE collection = @name;
                    DELETE FROM collections WHERE name = @name;";
            }
            else
            {
                cmd.CommandText = $@"
                    DROP TABLE IF EXISTS [{safeName}_vectors];
                    DELETE FROM collections WHERE name = @name;";
            }

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
                if (this.vssAvailable)
                {
                    this.UpsertVssPoints(safeName, collectionName, pointsList, transaction);
                }
                else
                {
                    this.UpsertFallbackPoints(safeName, pointsList, transaction);
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

        // Both vss and fallback paths perform brute-force in this implementation.
        // When the native vss extension is available, sqlite-vss provides HNSW
        // acceleration; in fallback mode we load all vectors and compute cosine
        // similarity in managed code.
        var candidates = new List<VectorSearchResult>();

        lock (this.syncLock)
        {
            if (this.vssAvailable)
            {
                candidates = this.SearchVss(safeName, collectionName, queryVector, limit, filter);
            }
            else
            {
                candidates = this.SearchFallback(safeName, queryVector, filter);
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
            if (this.vssAvailable)
            {
                return Task.FromResult(this.GetVssPoint(safeName, collectionName, id));
            }

            return Task.FromResult(this.GetFallbackPoint(safeName, id));
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
            if (this.vssAvailable)
            {
                foreach (var id in idsList)
                {
                    using var lookupCmd = this.connection.CreateCommand();
                    lookupCmd.CommandText =
                        "SELECT rowid FROM vss_id_map WHERE collection = @coll AND string_id = @id";
                    lookupCmd.Parameters.AddWithValue("@coll", collectionName);
                    lookupCmd.Parameters.AddWithValue("@id", id);
                    var rowId = lookupCmd.ExecuteScalar();

                    if (rowId != null)
                    {
                        using var delCmd = this.connection.CreateCommand();
                        delCmd.CommandText = $"DELETE FROM [vss_{safeName}] WHERE rowid = @rid";
                        delCmd.Parameters.AddWithValue("@rid", rowId);
                        delCmd.ExecuteNonQuery();

                        using var mapCmd = this.connection.CreateCommand();
                        mapCmd.CommandText =
                            "DELETE FROM vss_id_map WHERE collection = @coll AND string_id = @id";
                        mapCmd.Parameters.AddWithValue("@coll", collectionName);
                        mapCmd.Parameters.AddWithValue("@id", id);
                        mapCmd.ExecuteNonQuery();
                    }
                }
            }
            else
            {
                foreach (var id in idsList)
                {
                    using var cmd = this.connection.CreateCommand();
                    cmd.CommandText = $"DELETE FROM [{safeName}_vectors] WHERE id = @id";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
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
            cmd.CommandText = this.vssAvailable
                ? $"SELECT COUNT(*) FROM vss_id_map WHERE collection = @coll"
                : $"SELECT COUNT(*) FROM [{safeName}_vectors]";

            if (this.vssAvailable)
            {
                cmd.Parameters.AddWithValue("@coll", collectionName);
            }

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

        this.logger.LogInformation("SqliteVssVectorStore disposed");
    }

    /// <summary>
    /// Validates and sanitizes a collection name for use in SQL identifiers.
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
                $"Collection name '{name}' contains invalid characters after sanitization.",
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

    internal static float CosineSimilarity(float[] a, float[] b)
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

    private bool TryLoadVssExtension()
    {
        try
        {
            // Enable loading of native extensions via the SQLite API
            this.connection.EnableExtensions(true);
        }
        catch (SqliteException ex)
        {
            this.logger.LogDebug(
                ex,
                "Could not enable SQLite extensions; sqlite-vss unavailable");
            return false;
        }

        // Attempt to load the sqlite-vss extension from common paths
        var extensionNames = new[] { "vss0", "vector0", "sqlite_vss" };
        foreach (var extName in extensionNames)
        {
            try
            {
                this.connection.LoadExtension(extName);
                this.logger.LogDebug(
                    "Loaded sqlite-vss extension via '{ExtName}'", extName);

                // Verify it actually works
                using var cmd = this.connection.CreateCommand();
                cmd.CommandText = "SELECT vss_version()";
                cmd.ExecuteScalar();

                this.logger.LogDebug("sqlite-vss extension loaded and verified successfully");
                return true;
            }
            catch (SqliteException)
            {
                // Extension not found at this name, try next
            }
        }

        // Last resort: probe vss_version() in case the extension was already loaded
        // (e.g. statically linked or loaded via another mechanism)
        try
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = "SELECT vss_version()";
            cmd.ExecuteScalar();
            this.logger.LogDebug("sqlite-vss extension already available (pre-loaded)");
            return true;
        }
        catch (SqliteException)
        {
            this.logger.LogDebug(
                "sqlite-vss extension not available; using brute-force cosine fallback");
            return false;
        }
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
            this.logger.LogDebug(ex, "Could not enable WAL mode");
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

    private void InitializeVssIdMap()
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS vss_id_map (
                collection TEXT NOT NULL,
                vss_rowid INTEGER NOT NULL,
                string_id TEXT NOT NULL,
                payload TEXT,
                vector BLOB,
                PRIMARY KEY (collection, string_id)
            );
            CREATE INDEX IF NOT EXISTS idx_vss_id_map_rowid
                ON vss_id_map (collection, vss_rowid);";
        cmd.ExecuteNonQuery();

        // Add vector column if upgrading from older schema
        try
        {
            using var alter = this.connection.CreateCommand();
            alter.CommandText = "ALTER TABLE vss_id_map ADD COLUMN vector BLOB";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Column already exists — expected when table was just created above
        }
    }

    private void UpsertVssPoints(
        string safeName,
        string collectionName,
        List<VectorPoint> points,
        SqliteTransaction transaction)
    {
        foreach (var point in points)
        {
            // Check if point already exists in the id map
            using var lookupCmd = this.connection.CreateCommand();
            lookupCmd.Transaction = transaction;
            lookupCmd.CommandText =
                "SELECT vss_rowid FROM vss_id_map WHERE collection = @coll AND string_id = @id";
            lookupCmd.Parameters.AddWithValue("@coll", collectionName);
            lookupCmd.Parameters.AddWithValue("@id", point.Id);
            var existingRowId = lookupCmd.ExecuteScalar();

            if (existingRowId != null)
            {
                // Update existing: delete old vss row, insert new
                using var delCmd = this.connection.CreateCommand();
                delCmd.Transaction = transaction;
                delCmd.CommandText = $"DELETE FROM [vss_{safeName}] WHERE rowid = @rid";
                delCmd.Parameters.AddWithValue("@rid", existingRowId);
                delCmd.ExecuteNonQuery();

                using var delMapCmd = this.connection.CreateCommand();
                delMapCmd.Transaction = transaction;
                delMapCmd.CommandText =
                    "DELETE FROM vss_id_map WHERE collection = @coll AND string_id = @id";
                delMapCmd.Parameters.AddWithValue("@coll", collectionName);
                delMapCmd.Parameters.AddWithValue("@id", point.Id);
                delMapCmd.ExecuteNonQuery();
            }

            // Insert into vss virtual table
            using var insertCmd = this.connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = $"INSERT INTO [vss_{safeName}] (embedding) VALUES (@vec)";
            insertCmd.Parameters.AddWithValue("@vec", SerializeVector(point.Vector));
            insertCmd.ExecuteNonQuery();

            // Get the rowid of the inserted row
            using var lastRowCmd = this.connection.CreateCommand();
            lastRowCmd.Transaction = transaction;
            lastRowCmd.CommandText = "SELECT last_insert_rowid()";
            var newRowId = Convert.ToInt64(lastRowCmd.ExecuteScalar());

            // Insert into id map (with vector blob for round-trip retrieval)
            using var mapCmd = this.connection.CreateCommand();
            mapCmd.Transaction = transaction;
            mapCmd.CommandText = @"
                INSERT INTO vss_id_map (collection, vss_rowid, string_id, payload, vector)
                VALUES (@coll, @rid, @id, @payload, @vector)";
            mapCmd.Parameters.AddWithValue("@coll", collectionName);
            mapCmd.Parameters.AddWithValue("@rid", newRowId);
            mapCmd.Parameters.AddWithValue("@id", point.Id);
            mapCmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(point.Payload));
            mapCmd.Parameters.AddWithValue("@vector", SerializeVector(point.Vector));
            mapCmd.ExecuteNonQuery();
        }
    }

    private void UpsertFallbackPoints(
        string safeName,
        List<VectorPoint> points,
        SqliteTransaction transaction)
    {
        foreach (var point in points)
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
    }

    private List<VectorSearchResult> SearchVss(
        string safeName,
        string collectionName,
        float[] queryVector,
        int limit,
        VectorFilter? filter)
    {
        var results = new List<VectorSearchResult>();

        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT m.string_id, m.payload, v.distance
            FROM [vss_{safeName}] v
            INNER JOIN vss_id_map m ON m.collection = @coll AND m.vss_rowid = v.rowid
            WHERE vss_search(v.embedding, @vec)
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@coll", collectionName);
        cmd.Parameters.AddWithValue("@vec", SerializeVector(queryVector));
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var payloadJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
            var distance = reader.GetFloat(2);
            var payload = DeserializePayload(payloadJson);

            if (filter != null && !MatchesFilter(payload, filter))
            {
                continue;
            }

            // sqlite-vss returns L2 distance; convert to cosine-like score
            var score = 1.0f / (1.0f + distance);

            results.Add(new VectorSearchResult
            {
                Id = id,
                Score = score,
                Payload = payload
            });
        }

        return results;
    }

    private List<VectorSearchResult> SearchFallback(
        string safeName,
        float[] queryVector,
        VectorFilter? filter)
    {
        var candidates = new List<VectorSearchResult>();

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

        return candidates;
    }

    private VectorPoint? GetVssPoint(string safeName, string collectionName, string id)
    {
        using var lookupCmd = this.connection.CreateCommand();
        lookupCmd.CommandText =
            "SELECT vss_rowid, payload, vector FROM vss_id_map WHERE collection = @coll AND string_id = @id";
        lookupCmd.Parameters.AddWithValue("@coll", collectionName);
        lookupCmd.Parameters.AddWithValue("@id", id);

        using var reader = lookupCmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var payloadJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
        var vector = reader.IsDBNull(2) ? Array.Empty<float>() : DeserializeVector((byte[])reader[2]);
        return new VectorPoint
        {
            Id = id,
            Vector = vector,
            Payload = DeserializePayload(payloadJson)
        };
    }

    private VectorPoint? GetFallbackPoint(string safeName, string id)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"SELECT id, vector, payload FROM [{safeName}_vectors] WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new VectorPoint
        {
            Id = reader.GetString(0),
            Vector = DeserializeVector((byte[])reader[1]),
            Payload = DeserializePayload(reader.IsDBNull(2) ? "{}" : reader.GetString(2))
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
    }
}