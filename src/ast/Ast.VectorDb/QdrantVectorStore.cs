// -----------------------------------------------------------------------
// <copyright file="QdrantVectorStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

/// <summary>
/// Qdrant implementation of the vector store.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly ILogger<QdrantVectorStore> logger;
    private readonly VectorStoreOptions options;
    private readonly QdrantClient client;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QdrantVectorStore"/> class.
    /// </summary>
    /// <param name="options">The vector store options.</param>
    /// <param name="logger">The logger.</param>
    public QdrantVectorStore(
        IOptions<VectorStoreOptions> options,
        ILogger<QdrantVectorStore> logger)
    {
        this.options = options.Value;
        this.logger = logger;

        this.client = new QdrantClient(
            host: this.options.Host,
            port: this.options.UseGrpc ? this.options.GrpcPort : this.options.HttpPort,
            https: this.options.UseTls,
            apiKey: this.options.ApiKey);

        this.logger.LogInformation(
            "QdrantVectorStore initialized: {Host}:{Port} (gRPC: {UseGrpc}, TLS: {UseTls})",
            this.options.Host,
            this.options.UseGrpc ? this.options.GrpcPort : this.options.HttpPort,
            this.options.UseGrpc,
            this.options.UseTls);
    }

    /// <inheritdoc/>
    public string Name => "Qdrant";

    /// <inheritdoc/>
    public async Task CreateCollectionAsync(
        string collectionName,
        int vectorSize,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var distance = this.options.Distance switch
        {
            DistanceMetric.Cosine => Distance.Cosine,
            DistanceMetric.Euclidean => Distance.Euclid,
            DistanceMetric.DotProduct => Distance.Dot,
            _ => Distance.Cosine
        };

        this.logger.LogInformation(
            "Creating collection {Collection} with vector size {VectorSize} and distance {Distance}",
            collectionName, vectorSize, distance);

        await this.client.CreateCollectionAsync(
            collectionName: collectionName,
            vectorsConfig: new VectorParams
            {
                Size = (ulong)vectorSize,
                Distance = distance
            },
            cancellationToken: cancellationToken);

        this.logger.LogInformation("Collection {Collection} created successfully", collectionName);
    }

    /// <inheritdoc/>
    public async Task<bool> CollectionExistsAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        try
        {
            return await this.client.CollectionExistsAsync(collectionName, cancellationToken);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Error checking if collection {Collection} exists", collectionName);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task DeleteCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.logger.LogInformation("Deleting collection {Collection}", collectionName);
        await this.client.DeleteCollectionAsync(
            collectionName,
            timeout: TimeSpan.FromSeconds(this.options.TimeoutSeconds),
            cancellationToken: cancellationToken);
        this.logger.LogInformation("Collection {Collection} deleted", collectionName);
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(
        string collectionName,
        IEnumerable<VectorPoint> points,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var pointsList = points.ToList();
        if (pointsList.Count == 0)
        {
            return;
        }

        var qdrantPoints = pointsList.Select(p => new PointStruct
        {
            Id = new PointId { Uuid = p.Id },
            Vectors = p.Vector,
            Payload = { ConvertPayload(p.Payload) }
        }).ToList();

        this.logger.LogDebug("Upserting {Count} points to collection {Collection}", pointsList.Count, collectionName);

        await this.client.UpsertAsync(
            collectionName: collectionName,
            points: qdrantPoints,
            cancellationToken: cancellationToken);

        this.logger.LogDebug("Upserted {Count} points to collection {Collection}", pointsList.Count, collectionName);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        int limit = 10,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var qdrantFilter = filter != null ? BuildFilter(filter) : null;

        var results = await this.client.SearchAsync(
            collectionName: collectionName,
            vector: queryVector,
            limit: (ulong)limit,
            filter: qdrantFilter,
            cancellationToken: cancellationToken);

        return results.Select(r => new VectorSearchResult
        {
            Id = r.Id.Uuid,
            Score = r.Score,
            Payload = ConvertPayloadFromQdrant(r.Payload)
        }).ToList();
    }

    /// <inheritdoc/>
    public async Task<VectorPoint?> GetAsync(
        string collectionName,
        string id,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        try
        {
            var results = await this.client.RetrieveAsync(
                collectionName: collectionName,
                ids: new[] { new PointId { Uuid = id } },
                withPayload: true,
                withVectors: true,
                cancellationToken: cancellationToken);

            var point = results.FirstOrDefault();
            if (point == null)
            {
                return null;
            }

            return new VectorPoint
            {
                Id = point.Id.Uuid,
                Vector = point.Vectors.Vector.Data.ToArray(),
                Payload = ConvertPayloadFromQdrant(point.Payload)
            };
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Error retrieving point {Id} from collection {Collection}", id, collectionName);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        string collectionName,
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var idsList = ids.ToList();
        if (idsList.Count == 0)
        {
            return;
        }

        this.logger.LogDebug("Deleting {Count} points from collection {Collection}", idsList.Count, collectionName);

        // Delete using filter with IDs
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    HasId = new HasIdCondition
                    {
                        HasId = { idsList.Select(id => new PointId { Uuid = id }) }
                    }
                }
            }
        };

        await this.client.DeleteAsync(
            collectionName: collectionName,
            filter: filter,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        try
        {
            var info = await this.client.GetCollectionInfoAsync(collectionName, cancellationToken);
            return (long)info.PointsCount;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Error getting count for collection {Collection}", collectionName);
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (this.disposed)
        {
            return false;
        }

        try
        {
            await this.client.ListCollectionsAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Qdrant is not available at {Host}:{Port}", this.options.Host, this.options.GrpcPort);
            return false;
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
        this.client?.Dispose();
        this.logger.LogInformation("QdrantVectorStore disposed");
    }

    private static Dictionary<string, Value> ConvertPayload(Dictionary<string, object> payload)
    {
        var result = new Dictionary<string, Value>();

        foreach (var (key, value) in payload)
        {
            result[key] = ConvertToValue(value);
        }

        return result;
    }

    private static Value ConvertToValue(object? value)
    {
        return value switch
        {
            null => new Value { NullValue = NullValue.NullValue },
            string s => new Value { StringValue = s },
            int i => new Value { IntegerValue = i },
            long l => new Value { IntegerValue = l },
            float f => new Value { DoubleValue = f },
            double d => new Value { DoubleValue = d },
            bool b => new Value { BoolValue = b },
            IEnumerable<string> list => new Value
            {
                ListValue = new ListValue
                {
                    Values = { list.Select(s => new Value { StringValue = s }) }
                }
            },
            _ => new Value { StringValue = value.ToString() ?? string.Empty }
        };
    }

    private static Dictionary<string, object> ConvertPayloadFromQdrant(
        Google.Protobuf.Collections.MapField<string, Value> payload)
    {
        var result = new Dictionary<string, object>();

        foreach (var (key, value) in payload)
        {
            result[key] = ConvertFromValue(value);
        }

        return result;
    }

    private static object ConvertFromValue(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.IntegerValue => value.IntegerValue,
            Value.KindOneofCase.DoubleValue => value.DoubleValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ConvertFromValue).ToList(),
            Value.KindOneofCase.NullValue => null!,
            _ => value.StringValue
        };
    }

    private static Filter? BuildFilter(VectorFilter filter)
    {
        var conditions = new List<Condition>();

        foreach (var must in filter.Must)
        {
            conditions.Add(BuildCondition(must));
        }

        if (conditions.Count == 0)
        {
            return null;
        }

        return new Filter
        {
            Must = { conditions }
        };
    }

    private static Condition BuildCondition(FilterCondition condition)
    {
        return condition.Operator switch
        {
            FilterOperator.Equals => new Condition
            {
                Field = new FieldCondition
                {
                    Key = condition.Field,
                    Match = new Match { Keyword = condition.Value.ToString() }
                }
            },
            FilterOperator.In when condition.Value is IEnumerable<string> values => new Condition
            {
                Field = new FieldCondition
                {
                    Key = condition.Field,
                    Match = new Match
                    {
                        Keywords = new RepeatedStrings { Strings = { values } }
                    }
                }
            },
            FilterOperator.GreaterThan when condition.Value is int intVal => new Condition
            {
                Field = new FieldCondition
                {
                    Key = condition.Field,
                    Range = new Range { Gt = intVal }
                }
            },
            FilterOperator.LessThan when condition.Value is int intVal => new Condition
            {
                Field = new FieldCondition
                {
                    Key = condition.Field,
                    Range = new Range { Lt = intVal }
                }
            },
            _ => new Condition
            {
                Field = new FieldCondition
                {
                    Key = condition.Field,
                    Match = new Match { Keyword = condition.Value.ToString() }
                }
            }
        };
    }
}
