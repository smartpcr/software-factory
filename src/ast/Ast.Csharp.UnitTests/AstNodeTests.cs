// -----------------------------------------------------------------------
// <copyright file="AstNodeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Csharp.UnitTests;

[TestClass]
public class AstNodeTests
{
    [TestMethod]
    public void Constructor_WithRequiredProperties_CreatesValidNode()
    {
        // Arrange & Act
        var node = new AstNode
        {
            NodeType = AstNodeType.Class,
            Name = "TestClass",
            FilePath = "src/Test.cs"
        };

        // Assert
        node.NodeType.Should().Be(AstNodeType.Class);
        node.Name.Should().Be("TestClass");
        node.FilePath.Should().Be("src/Test.cs");
    }

    [TestMethod]
    public void Constructor_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var node = new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = "Test",
            FilePath = "test.cs"
        };

        // Assert
        node.StartLine.Should().Be(0);
        node.EndLine.Should().Be(0);
        node.Code.Should().BeNull();
        node.Summary.Should().BeNull();
        node.Modifiers.Should().BeNull();
        node.ReturnType.Should().BeNull();
        node.BaseTypes.Should().BeEmpty();
        node.Parameters.Should().BeEmpty();
    }

    [TestMethod]
    public void GetEmbeddingText_MinimalNode_ReturnsBasicInfo()
    {
        // Arrange
        var node = new AstNode
        {
            NodeType = AstNodeType.Class,
            Name = "MyService",
            FilePath = "src/MyService.cs",
            StartLine = 10
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Class: MyService");
        embeddingText.Should().Contain("File: src/MyService.cs:10");
    }

    [TestMethod]
    public void GetEmbeddingText_WithSummary_IncludesSummary()
    {
        // Arrange
        var node = new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = "Calculate",
            FilePath = "calc.cs",
            Summary = "Performs a complex calculation."
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Summary: Performs a complex calculation.");
    }

    [TestMethod]
    public void GetEmbeddingText_WithModifiers_IncludesModifiers()
    {
        // Arrange
        var node = new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = "ProcessAsync",
            FilePath = "processor.cs",
            Modifiers = "public async virtual"
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Modifiers: public async virtual");
    }

    [TestMethod]
    public void GetEmbeddingText_WithReturnType_IncludesReturnType()
    {
        // Arrange
        var node = new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = "GetData",
            FilePath = "data.cs",
            ReturnType = "Task<IEnumerable<string>>"
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Returns: Task<IEnumerable<string>>");
    }

    [TestMethod]
    public void GetEmbeddingText_WithBaseTypes_IncludesInheritance()
    {
        // Arrange
        var node = new AstNode
        {
            NodeType = AstNodeType.Class,
            Name = "UserService",
            FilePath = "service.cs",
            BaseTypes = new List<string> { "BaseService", "IUserService", "IDisposable" }
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Inherits: BaseService, IUserService, IDisposable");
    }

    [TestMethod]
    public void GetEmbeddingText_WithParameters_IncludesParameters()
    {
        // Arrange
        var node = new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = "CreateUser",
            FilePath = "user.cs",
            Parameters = new List<ParameterInfo>
            {
                new() { Name = "name", Type = "string" },
                new() { Name = "age", Type = "int" },
                new() { Name = "email", Type = "string?" }
            }
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Parameters: string name, int age, string? email");
    }

    [TestMethod]
    public void GetEmbeddingText_WithCode_IncludesCode()
    {
        // Arrange
        var code = "public int Add(int a, int b) => a + b;";
        var node = new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = "Add",
            FilePath = "math.cs",
            Code = code
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain($"Code:\n{code}");
    }

    [TestMethod]
    public void GetEmbeddingText_FullNode_ContainsAllSections()
    {
        // Arrange
        var node = new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = "ProcessOrder",
            FilePath = "src/Services/OrderService.cs",
            StartLine = 42,
            Summary = "Processes an order and returns the result.",
            Modifiers = "public async",
            ReturnType = "Task<OrderResult>",
            BaseTypes = new List<string>(), // Empty, should not appear
            Parameters = new List<ParameterInfo>
            {
                new() { Name = "order", Type = "Order" },
                new() { Name = "cancellationToken", Type = "CancellationToken" }
            },
            Code = "public async Task<OrderResult> ProcessOrder(Order order, CancellationToken ct) { }"
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Method: ProcessOrder");
        embeddingText.Should().Contain("File: src/Services/OrderService.cs:42");
        embeddingText.Should().Contain("Summary: Processes an order");
        embeddingText.Should().Contain("Modifiers: public async");
        embeddingText.Should().Contain("Returns: Task<OrderResult>");
        embeddingText.Should().Contain("Parameters: Order order, CancellationToken cancellationToken");
        embeddingText.Should().Contain("Code:");
        embeddingText.Should().NotContain("Inherits:"); // Empty list should not appear
    }

    [TestMethod]
    [DataRow(AstNodeType.Namespace)]
    [DataRow(AstNodeType.Class)]
    [DataRow(AstNodeType.Record)]
    [DataRow(AstNodeType.Struct)]
    [DataRow(AstNodeType.Interface)]
    [DataRow(AstNodeType.Enum)]
    [DataRow(AstNodeType.Method)]
    [DataRow(AstNodeType.Property)]
    [DataRow(AstNodeType.Field)]
    [DataRow(AstNodeType.Constructor)]
    public void NodeType_AllEnumValues_CanBeUsed(AstNodeType nodeType)
    {
        // Arrange & Act
        var node = new AstNode
        {
            NodeType = nodeType,
            Name = "Test",
            FilePath = "test.cs"
        };

        // Assert
        node.NodeType.Should().Be(nodeType);
    }
}

[TestClass]
public class ParameterInfoTests
{
    [TestMethod]
    public void Constructor_SetsProperties()
    {
        // Arrange & Act
        var param = new ParameterInfo
        {
            Name = "userId",
            Type = "Guid"
        };

        // Assert
        param.Name.Should().Be("userId");
        param.Type.Should().Be("Guid");
    }
}
