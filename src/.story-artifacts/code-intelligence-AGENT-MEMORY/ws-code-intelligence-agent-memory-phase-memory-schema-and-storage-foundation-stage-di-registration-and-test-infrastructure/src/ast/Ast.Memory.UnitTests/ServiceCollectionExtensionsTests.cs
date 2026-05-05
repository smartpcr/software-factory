// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Schema;
using Ast.Memory.Storage;
using Microsoft.Extensions.DependencyInjection;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_SqliteBackend_ResolvesAsSqliteGraphStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Sqlite;
            opts.SqlitePath = ":memory:";
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var store = provider.GetRequiredService<IMemoryGraphStore>();

        // Assert
        store.Should().NotBeNull();
        store.Should().BeOfType<SqliteGraphStore>();
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_Neo4jBackend_ResolvesAsNeo4jMemoryGraphStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Neo4j;
            opts.Neo4jUri = "bolt://localhost:7687";
            opts.Neo4jUser = "neo4j";
            opts.Neo4jPassword = "test-password";
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var store = provider.GetRequiredService<IMemoryGraphStore>();

        // Assert
        store.Should().NotBeNull();
        store.Should().BeOfType<Neo4jMemoryGraphStore>();
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_SqliteBackend_IGraphStoreResolvesToSameInstance()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Sqlite;
            opts.SqlitePath = ":memory:";
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var memoryStore = provider.GetRequiredService<IMemoryGraphStore>();
        var graphStore = provider.GetRequiredService<IGraphStore>();

        // Assert
        graphStore.Should().BeSameAs(memoryStore);
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_RegistersAnnotationKeyRegistry()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Sqlite;
            opts.SqlitePath = ":memory:";
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<AnnotationKeyRegistry>();

        // Assert
        registry.Should().NotBeNull();
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_AnnotationKeyRegistryIsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Sqlite;
            opts.SqlitePath = ":memory:";
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<AnnotationKeyRegistry>();
        var second = provider.GetRequiredService<AnnotationKeyRegistry>();

        // Assert
        first.Should().BeSameAs(second);
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_SqliteBackend_DefaultPath_ResolvesSuccessfully()
    {
        // Arrange — no SqlitePath set, should default to memory_graph.db
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Sqlite;
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var store = provider.GetRequiredService<IMemoryGraphStore>();

        // Assert
        store.Should().BeOfType<SqliteGraphStore>();
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_Neo4jBackend_MissingUri_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Neo4j;
            // Neo4jUri deliberately omitted
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<IMemoryGraphStore>();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Neo4jUri*");
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_Neo4jBackend_MissingUser_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Neo4j;
            opts.Neo4jUri = "bolt://localhost:7687";
            // Neo4jUser deliberately omitted
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<IMemoryGraphStore>();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Neo4jUser*");
    }

    [TestMethod]
    [TestCategory("DI")]
    public async Task AddMemoryGraph_Neo4jBackend_MissingPassword_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Neo4j;
            opts.Neo4jUri = "bolt://localhost:7687";
            opts.Neo4jUser = "neo4j";
            // Neo4jPassword deliberately omitted
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<IMemoryGraphStore>();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Neo4jPassword*");
    }

    [TestMethod]
    [TestCategory("DI")]
    public void AddMemoryGraph_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddMemoryGraph(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}