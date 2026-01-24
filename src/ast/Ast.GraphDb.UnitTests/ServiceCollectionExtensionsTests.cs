// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb.UnitTests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddNeo4jGraphStore_WithConfigureOptions_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddNeo4jGraphStore(options =>
        {
            options.Uri = "bolt://custom:7687";
            options.Username = "admin";
            options.Password = "secret";
        });

        // Assert
        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IGraphStore));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(Neo4jGraphStore));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [TestMethod]
    public void AddNeo4jGraphStore_WithConnectionParams_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddNeo4jGraphStore(
            "bolt://myserver:7687",
            "myuser",
            "mypassword");

        // Assert
        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IGraphStore));
        descriptor.Should().NotBeNull();
    }

    [TestMethod]
    public void AddNeo4jGraphStore_ConfiguresOptions()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddNeo4jGraphStore(options =>
        {
            options.Uri = "bolt://test:7687";
            options.Username = "testuser";
            options.Password = "testpass";
            options.Database = "testdb";
            options.BatchSize = 500;
        });

        // Assert - verify options configuration was registered
        var optionsDescriptor = services.FirstOrDefault(s =>
            s.ServiceType.IsGenericType &&
            s.ServiceType.GetGenericTypeDefinition() == typeof(IConfigureOptions<>) &&
            s.ServiceType.GetGenericArguments()[0] == typeof(GraphStoreOptions));

        optionsDescriptor.Should().NotBeNull();
    }

    [TestMethod]
    public void AddNeo4jGraphStore_ReturnsSameServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddNeo4jGraphStore(options => { });

        // Assert
        result.Should().BeSameAs(services);
    }

    [TestMethod]
    public void AddNeo4jGraphStore_DoesNotReplaceExistingRegistration()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act - register twice
        services.AddNeo4jGraphStore(o => o.Uri = "bolt://first:7687");
        services.AddNeo4jGraphStore(o => o.Uri = "bolt://second:7687");

        // Assert - should only have one IGraphStore registration
        var descriptors = services.Where(s => s.ServiceType == typeof(IGraphStore)).ToList();
        descriptors.Should().HaveCount(1);
    }
}