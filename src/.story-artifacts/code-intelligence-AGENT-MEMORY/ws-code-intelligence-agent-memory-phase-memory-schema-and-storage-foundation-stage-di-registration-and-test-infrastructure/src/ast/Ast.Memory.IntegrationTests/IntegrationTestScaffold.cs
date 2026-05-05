// -----------------------------------------------------------------------
// <copyright file="IntegrationTestScaffold.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.IntegrationTests;

using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Schema;
using Ast.Memory.Storage;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Integration tests verifying the full DI wiring with SQLite backend
/// and cross-cutting interactions between AnnotationKeyRegistry and stores.
/// </summary>
[TestClass]
public class IntegrationTestScaffold
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task FullDiWiring_Sqlite_ResolvesAllServices()
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
        var graphStore = provider.GetRequiredService<IGraphStore>();
        var registry = provider.GetRequiredService<AnnotationKeyRegistry>();

        // Assert
        store.Should().NotBeNull();
        store.Should().BeOfType<SqliteGraphStore>();
        graphStore.Should().BeSameAs(store);
        registry.Should().NotBeNull();
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task AnnotationKeyRegistry_ValidatesTypedMetadata_EndToEnd()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Sqlite;
            opts.SqlitePath = ":memory:";
        });
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<AnnotationKeyRegistry>();

        var validMetadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.Confidence] = 0.9,
            [MemoryMetadataKeys.MemberCount] = 5,
            [MemoryMetadataKeys.Stale] = false,
            ["ext:team-owner"] = "platform"
        };

        var invalidMetadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.Confidence] = "not-a-double",
            ["unknown-key"] = "value"
        };

        // Act
        var validResult = registry.Validate(validMetadata);
        var invalidResult = registry.Validate(invalidMetadata);

        // Assert
        validResult.IsValid.Should().BeTrue();
        invalidResult.IsValid.Should().BeFalse();
        invalidResult.Warnings.Should().HaveCount(2);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task AnnotationKeyRegistry_Singleton_SharedAcrossResolutions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMemoryGraph(opts =>
        {
            opts.Backend = MemoryBackend.Sqlite;
            opts.SqlitePath = ":memory:";
        });
        await using var provider = services.BuildServiceProvider();

        // Act — register a custom key via one resolution, verify via another
        var registry1 = provider.GetRequiredService<AnnotationKeyRegistry>();
        registry1.Register("custom-integration-key", typeof(int));

        var registry2 = provider.GetRequiredService<AnnotationKeyRegistry>();

        // Assert
        registry2.IsRegistered("custom-integration-key").Should().BeTrue();
        registry2.GetDescriptor("custom-integration-key")!.ValueType.Should().Be(typeof(int));
    }
}