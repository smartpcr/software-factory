// -----------------------------------------------------------------------
// <copyright file="AstConfigurationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core.UnitTests;

[TestClass]
public class AstConfigurationTests
{
    [TestMethod]
    public void DefaultConfiguration_HasSensibleDefaults()
    {
        // Arrange & Act
        var config = new AstConfiguration();

        // Assert
        config.ExcludedDirectories.Should().Contain("bin");
        config.ExcludedDirectories.Should().Contain("obj");
        config.ExcludedDirectories.Should().Contain(".git");
        config.ExcludedDirectories.Should().Contain("node_modules");

        config.CSharpFilePatterns.Should().Contain("*.cs");
        config.PowerShellFilePatterns.Should().Contain("*.ps1");
        config.PowerShellFilePatterns.Should().Contain("*.psm1");
        config.PowerShellFilePatterns.Should().Contain("*.psd1");

        config.MaxFileSizeBytes.Should().Be(1_000_000);
        config.EnableDataFlowAnalysis.Should().BeTrue();
        config.EnableCrossLanguageDetection.Should().BeTrue();
        config.IncludeSourceCode.Should().BeTrue();
        config.ExtractDocumentation.Should().BeTrue();
    }

    [TestMethod]
    public void Configuration_CanBeCustomized()
    {
        // Arrange & Act
        var config = new AstConfiguration
        {
            ExcludedDirectories = ["custom_exclude"],
            CSharpFilePatterns = ["*.cs", "*.csx"],
            MaxFileSizeBytes = 5_000_000,
            EnableDataFlowAnalysis = false,
            EnableCrossLanguageDetection = false
        };

        // Assert
        config.ExcludedDirectories.Should().ContainSingle().Which.Should().Be("custom_exclude");
        config.CSharpFilePatterns.Should().HaveCount(2);
        config.MaxFileSizeBytes.Should().Be(5_000_000);
        config.EnableDataFlowAnalysis.Should().BeFalse();
        config.EnableCrossLanguageDetection.Should().BeFalse();
    }
}

[TestClass]
public class EmbeddingConfigurationTests
{
    [TestMethod]
    public void DefaultConfiguration_HasCorrectDefaults()
    {
        // Arrange & Act
        var config = new EmbeddingConfiguration();

        // Assert
        config.Endpoint.Should().Be("http://localhost:11434");
        config.ModelName.Should().Be("nomic-embed-text");
        config.Dimensions.Should().Be(768);
        config.BatchSize.Should().Be(32);
        config.TimeoutSeconds.Should().Be(60);
    }

    [TestMethod]
    public void Configuration_CanBeCustomized()
    {
        // Arrange & Act
        var config = new EmbeddingConfiguration
        {
            Endpoint = "http://gpu-server:8080",
            ModelName = "qwen2.5-coder",
            Dimensions = 896,
            BatchSize = 64,
            TimeoutSeconds = 120
        };

        // Assert
        config.Endpoint.Should().Be("http://gpu-server:8080");
        config.ModelName.Should().Be("qwen2.5-coder");
        config.Dimensions.Should().Be(896);
        config.BatchSize.Should().Be(64);
        config.TimeoutSeconds.Should().Be(120);
    }
}

[TestClass]
public class GraphDbConfigurationTests
{
    [TestMethod]
    public void DefaultConfiguration_HasCorrectDefaults()
    {
        // Arrange & Act
        var config = new GraphDbConfiguration();

        // Assert
        config.Uri.Should().Be("bolt://localhost:7687");
        config.Username.Should().Be("neo4j");
        config.Password.Should().Be("password");
        config.Database.Should().Be("neo4j");
        config.MaxConnectionPoolSize.Should().Be(50);
    }

    [TestMethod]
    public void Configuration_CanBeCustomized()
    {
        // Arrange & Act
        var config = new GraphDbConfiguration
        {
            Uri = "bolt://neo4j.example.com:7687",
            Username = "admin",
            Password = "secret123",
            Database = "codebase",
            MaxConnectionPoolSize = 100
        };

        // Assert
        config.Uri.Should().Be("bolt://neo4j.example.com:7687");
        config.Username.Should().Be("admin");
        config.Password.Should().Be("secret123");
        config.Database.Should().Be("codebase");
        config.MaxConnectionPoolSize.Should().Be(100);
    }
}

[TestClass]
public class VectorDbConfigurationTests
{
    [TestMethod]
    public void DefaultConfiguration_HasCorrectDefaults()
    {
        // Arrange & Act
        var config = new VectorDbConfiguration();

        // Assert
        config.Host.Should().Be("localhost");
        config.Port.Should().Be(6334);
        config.NodesCollection.Should().Be("code_nodes");
        config.EdgesCollection.Should().Be("code_edges");
        config.UseTls.Should().BeFalse();
        config.ApiKey.Should().BeNull();
    }

    [TestMethod]
    public void Configuration_CanBeCustomized()
    {
        // Arrange & Act
        var config = new VectorDbConfiguration
        {
            Host = "qdrant.example.com",
            Port = 6335,
            NodesCollection = "my_nodes",
            EdgesCollection = "my_edges",
            UseTls = true,
            ApiKey = "secret-api-key"
        };

        // Assert
        config.Host.Should().Be("qdrant.example.com");
        config.Port.Should().Be(6335);
        config.NodesCollection.Should().Be("my_nodes");
        config.EdgesCollection.Should().Be("my_edges");
        config.UseTls.Should().BeTrue();
        config.ApiKey.Should().Be("secret-api-key");
    }
}

[TestClass]
public class NestedConfigurationTests
{
    [TestMethod]
    public void AstConfiguration_ContainsNestedConfigurations()
    {
        // Arrange & Act
        var config = new AstConfiguration();

        // Assert
        config.Embedding.Should().NotBeNull();
        config.GraphDb.Should().NotBeNull();
        config.VectorDb.Should().NotBeNull();
    }

    [TestMethod]
    public void AstConfiguration_CanCustomizeNestedConfigurations()
    {
        // Arrange & Act
        var config = new AstConfiguration
        {
            Embedding = new EmbeddingConfiguration
            {
                ModelName = "custom-model"
            },
            GraphDb = new GraphDbConfiguration
            {
                Uri = "bolt://custom:7687"
            },
            VectorDb = new VectorDbConfiguration
            {
                Host = "custom-qdrant"
            }
        };

        // Assert
        config.Embedding.ModelName.Should().Be("custom-model");
        config.GraphDb.Uri.Should().Be("bolt://custom:7687");
        config.VectorDb.Host.Should().Be("custom-qdrant");
    }
}