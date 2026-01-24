// -----------------------------------------------------------------------
// <copyright file="HypergraphNodeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core.UnitTests;

[TestClass]
public class HypergraphNodeTests
{
    [TestMethod]
    public void Constructor_WithRequiredProperties_CreatesValidNode()
    {
        // Arrange & Act
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "MyClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "src/MyClass.cs"
        };

        // Assert
        node.Id.Should().Be("node_001");
        node.Name.Should().Be("MyClass");
        node.NodeType.Should().Be("class");
        node.Language.Should().Be("csharp");
        node.FilePath.Should().Be("src/MyClass.cs");
    }

    [TestMethod]
    public void Constructor_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "Test",
            NodeType = "method",
            Language = "csharp",
            FilePath = "test.cs"
        };

        // Assert
        node.Scope.Should().BeEmpty();
        node.LineStart.Should().Be(0);
        node.LineEnd.Should().Be(0);
        node.Modifiers.Should().BeEmpty();
        node.Metadata.Should().BeEmpty();
    }

    [TestMethod]
    public void Equals_SameId_ReturnsTrue()
    {
        // Arrange
        var node1 = new HypergraphNode
        {
            Id = "node_001",
            Name = "ClassA",
            NodeType = "class",
            Language = "csharp",
            FilePath = "a.cs"
        };

        var node2 = new HypergraphNode
        {
            Id = "node_001",
            Name = "ClassB", // Different name, same ID
            NodeType = "interface",
            Language = "powershell",
            FilePath = "b.ps1"
        };

        // Act & Assert
        node1.Equals(node2).Should().BeTrue();
    }

    [TestMethod]
    public void Equals_DifferentId_ReturnsFalse()
    {
        // Arrange
        var node1 = new HypergraphNode
        {
            Id = "node_001",
            Name = "MyClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "a.cs"
        };

        var node2 = new HypergraphNode
        {
            Id = "node_002",
            Name = "MyClass", // Same name, different ID
            NodeType = "class",
            Language = "csharp",
            FilePath = "a.cs"
        };

        // Act & Assert
        node1.Equals(node2).Should().BeFalse();
    }

    [TestMethod]
    public void GetHashCode_SameId_ReturnsSameHash()
    {
        // Arrange
        var node1 = new HypergraphNode
        {
            Id = "node_001",
            Name = "A",
            NodeType = "class",
            Language = "csharp",
            FilePath = "a.cs"
        };

        var node2 = new HypergraphNode
        {
            Id = "node_001",
            Name = "B",
            NodeType = "method",
            Language = "powershell",
            FilePath = "b.ps1"
        };

        // Act & Assert
        node1.GetHashCode().Should().Be(node2.GetHashCode());
    }

    [TestMethod]
    public void GetEmbeddingText_MinimalNode_ReturnsBasicInfo()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "ProcessData",
            NodeType = "method",
            Language = "csharp",
            FilePath = "src/Processor.cs",
            LineStart = 42
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("method: ProcessData");
        embeddingText.Should().Contain("Language: csharp");
        embeddingText.Should().Contain("File: src/Processor.cs:42");
    }

    [TestMethod]
    public void GetEmbeddingText_WithScope_IncludesScope()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "Calculate",
            NodeType = "method",
            Language = "csharp",
            FilePath = "Calculator.cs",
            Scope = "MyApp.Services.Calculator"
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Scope: MyApp.Services.Calculator");
    }

    [TestMethod]
    public void GetEmbeddingText_WithModifiers_IncludesModifiers()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "MyClass",
            NodeType = "class",
            Language = "csharp",
            FilePath = "MyClass.cs",
            Modifiers = ["public", "abstract"]
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Modifiers: public abstract");
    }

    [TestMethod]
    public void GetEmbeddingText_EmptyScope_DoesNotIncludeScope()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "Test",
            NodeType = "method",
            Language = "csharp",
            FilePath = "test.cs",
            Scope = ""
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().NotContain("Scope:");
    }

    [TestMethod]
    public void Metadata_CanStoreArbitraryData()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node_001",
            Name = "Test",
            NodeType = "class",
            Language = "csharp",
            FilePath = "test.cs",
            Metadata = new Dictionary<string, object?>
            {
                ["return_type"] = "void",
                ["parameter_count"] = 3,
                ["is_async"] = true,
                ["description"] = null
            }
        };

        // Act & Assert
        node.Metadata["return_type"].Should().Be("void");
        node.Metadata["parameter_count"].Should().Be(3);
        node.Metadata["is_async"].Should().Be(true);
        node.Metadata["description"].Should().BeNull();
    }
}