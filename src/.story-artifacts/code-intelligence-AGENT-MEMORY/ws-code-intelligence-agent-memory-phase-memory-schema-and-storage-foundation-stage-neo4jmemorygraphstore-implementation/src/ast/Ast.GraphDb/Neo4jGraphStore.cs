// -----------------------------------------------------------------------
// <copyright file="Neo4jGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

using Neo4j.Driver;

/// <summary>
/// Neo4j-backed implementation of <see cref="IGraphStore"/>.
/// </summary>
public class Neo4jGraphStore : IGraphStore
{
    private readonly IDriver _driver;
    private readonly bool _ownsDriver;
    private bool _disposed;

    /// <summary>
    /// Creates a <see cref="Neo4jGraphStore"/> wrapping an externally-managed driver.
    /// The driver is NOT disposed when this store is disposed.
    /// </summary>
    public Neo4jGraphStore(IDriver driver)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _ownsDriver = false;
    }

    /// <summary>
    /// Creates a <see cref="Neo4jGraphStore"/> from connection parameters.
    /// The driver IS disposed when this store is disposed.
    /// </summary>
    public Neo4jGraphStore(string uri, string username, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(username, password));
        _ownsDriver = true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_ownsDriver)
        {
            _driver.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
