// -----------------------------------------------------------------------
// <copyright file="HypergraphNodeEnhancedTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.UnitTests;

using Ast.Core;

[TestClass]
public class HypergraphNodeEnhancedTests
{
    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeBasicInfo()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "ProcessData",
            NodeType = "Method",
            Language = "C#",
            FilePath = @"C:\project\Service.cs",
            Scope = "MyNamespace.MyService",
            LineStart = 10,
            LineEnd = 20,
            Modifiers = ["public", "async"]
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Method: ProcessData");
        text.Should().Contain("Scope: MyNamespace.MyService");
        text.Should().Contain("Modifiers: public async");
        text.Should().Contain("Location: ");
        text.Should().Contain(":10");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeSignature()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "Calculate",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["Signature"] = "int Calculate(int a, int b)"
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Signature: int Calculate(int a, int b)");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeReturnType()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "GetData",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["ReturnType"] = "Task<IEnumerable<Data>>"
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Returns: Task<IEnumerable<Data>>");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeParameters()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "ProcessOrder",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["Parameters"] = "order: Order, token: CancellationToken"
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Parameters: order: Order, token: CancellationToken");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeDocumentation()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "Validate",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["Documentation"] = "Validates the input data according to business rules."
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Validates the input data according to business rules.");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeCallers()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "SaveData",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["Callers"] = "OrderService, CustomerService, BatchProcessor"
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Called by: OrderService, CustomerService, BatchProcessor");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeCallees()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "ProcessAll",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["Callees"] = "ValidateInput, SaveToDatabase, SendNotification"
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Calls: ValidateInput, SaveToDatabase, SendNotification");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeSourceCode()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "Add",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = "public int Add(int a, int b) => a + b;"
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Code:\npublic int Add(int a, int b) => a + b;");
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldTruncateLongSourceCode()
    {
        // Arrange
        var longCode = new string('x', 3000);
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "LongMethod",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["SourceCode"] = longCode
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().NotContain(longCode); // Full code should not be present
        text.Length.Should().BeLessThan(longCode.Length + 500); // Should be truncated
    }

    [TestMethod]
    public void GetEnhancedEmbeddingText_ShouldIncludeAllEnrichmentData()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "CompleteMethod",
            NodeType = "Method",
            Language = "C#",
            FilePath = @"C:\project\Service.cs",
            Scope = "MyNamespace.MyService",
            LineStart = 10,
            LineEnd = 30,
            Modifiers = ["public", "virtual"],
            Metadata = new Dictionary<string, object?>
            {
                ["Signature"] = "Task<Result> CompleteMethod(Input input, CancellationToken ct)",
                ["ReturnType"] = "Task<Result>",
                ["Parameters"] = "input: Input, ct: CancellationToken",
                ["Documentation"] = "Completes the operation with full validation.",
                ["Callers"] = "Controller, Service",
                ["Callees"] = "Validate, Save"
            }
        };

        // Act
        var text = node.GetEnhancedEmbeddingText();

        // Assert
        text.Should().Contain("Method: CompleteMethod");
        text.Should().Contain("Completes the operation");
        text.Should().Contain("Signature:");
        text.Should().Contain("Returns: Task<Result>");
        text.Should().Contain("Parameters:");
        text.Should().Contain("Called by: Controller, Service");
        text.Should().Contain("Calls: Validate, Save");
        text.Should().Contain("Scope: MyNamespace.MyService");
        text.Should().Contain("Modifiers: public virtual");
    }

    [TestMethod]
    public void IsLspEnriched_ShouldReturnTrue_WhenEnriched()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "Test",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["LspEnriched"] = true
            }
        };

        // Act & Assert
        node.IsLspEnriched.Should().BeTrue();
    }

    [TestMethod]
    public void IsLspEnriched_ShouldReturnFalse_WhenNotEnriched()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "Test",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1
        };

        // Act & Assert
        node.IsLspEnriched.Should().BeFalse();
    }

    [TestMethod]
    public void IsLspEnriched_ShouldReturnFalse_WhenMetadataIsFalse()
    {
        // Arrange
        var node = new HypergraphNode
        {
            Id = "node1",
            Name = "Test",
            NodeType = "Method",
            Language = "C#",
            FilePath = "test.cs",
            LineStart = 1,
            Metadata = new Dictionary<string, object?>
            {
                ["LspEnriched"] = false
            }
        };

        // Act & Assert
        node.IsLspEnriched.Should().BeFalse();
    }
}
