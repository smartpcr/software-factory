// -----------------------------------------------------------------------
// <copyright file="PsAstNodeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.PowerShell.UnitTests;

[TestClass]
public class PsAstNodeTests
{
    [TestMethod]
    public void Constructor_WithRequiredProperties_CreatesValidNode()
    {
        // Arrange & Act
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Function,
            Name = "Get-Data",
            FilePath = "scripts/data.ps1"
        };

        // Assert
        node.NodeType.Should().Be(PsAstNodeType.Function);
        node.Name.Should().Be("Get-Data");
        node.FilePath.Should().Be("scripts/data.ps1");
    }

    [TestMethod]
    public void Constructor_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Function,
            Name = "Test",
            FilePath = "test.ps1"
        };

        // Assert
        node.StartLine.Should().Be(0);
        node.EndLine.Should().Be(0);
        node.Code.Should().BeNull();
        node.Summary.Should().BeNull();
        node.BaseTypes.Should().BeEmpty();
        node.Parameters.Should().BeEmpty();
        node.Methods.Should().BeEmpty();
        node.Properties.Should().BeEmpty();
    }

    [TestMethod]
    public void GetEmbeddingText_MinimalNode_ReturnsBasicInfo()
    {
        // Arrange
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Function,
            Name = "Get-Configuration",
            FilePath = "scripts/config.ps1",
            StartLine = 10
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Function: Get-Configuration");
        embeddingText.Should().Contain("File: scripts/config.ps1:10");
    }

    [TestMethod]
    public void GetEmbeddingText_WithSynopsis_IncludesSynopsis()
    {
        // Arrange
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Function,
            Name = "Set-Config",
            FilePath = "config.ps1",
            Summary = "Sets the application configuration."
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Synopsis: Sets the application configuration.");
    }

    [TestMethod]
    public void GetEmbeddingText_WithBaseTypes_IncludesInheritance()
    {
        // Arrange
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Class,
            Name = "Employee",
            FilePath = "employee.ps1",
            BaseTypes = ["Person", "IEmployee"]
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Inherits: Person, IEmployee");
    }

    [TestMethod]
    public void GetEmbeddingText_WithParameters_FormatsCorrectly()
    {
        // Arrange
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Function,
            Name = "New-User",
            FilePath = "user.ps1",
            Parameters =
            [
                new() { Name = "Name", Type = "String", IsMandatory = true },
                new() { Name = "Age", Type = "Int32", IsMandatory = false },
                new() { Name = "Email", Type = "String", IsMandatory = true }
            ]
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Parameters:");
        embeddingText.Should().Contain("[Mandatory]String $Name");
        embeddingText.Should().Contain("Int32 $Age");
        embeddingText.Should().Contain("[Mandatory]String $Email");
    }

    [TestMethod]
    public void GetEmbeddingText_WithMethods_IncludesMethods()
    {
        // Arrange
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Class,
            Name = "Calculator",
            FilePath = "calc.ps1",
            Methods = ["Add", "Subtract", "Multiply", "Divide"]
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Methods: Add, Subtract, Multiply, Divide");
    }

    [TestMethod]
    public void GetEmbeddingText_WithProperties_IncludesProperties()
    {
        // Arrange
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Class,
            Name = "Person",
            FilePath = "person.ps1",
            Properties = ["Name", "Age", "Email"]
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Properties: Name, Age, Email");
    }

    [TestMethod]
    public void GetEmbeddingText_WithCode_IncludesCode()
    {
        // Arrange
        var code = "function Get-Date { Get-Date }";
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Function,
            Name = "Get-Date",
            FilePath = "date.ps1",
            Code = code
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain($"Code:\n{code}");
    }

    [TestMethod]
    public void GetEmbeddingText_FullClassNode_ContainsAllSections()
    {
        // Arrange
        var node = new PsAstNode
        {
            NodeType = PsAstNodeType.Class,
            Name = "ServiceClient",
            FilePath = "src/ServiceClient.ps1",
            StartLine = 5,
            Summary = "A client for interacting with the service API.",
            BaseTypes = ["BaseClient"],
            Methods = ["Connect", "Disconnect", "SendRequest"],
            Properties = ["Endpoint", "Timeout", "IsConnected"],
            Code = "class ServiceClient : BaseClient { }"
        };

        // Act
        var embeddingText = node.GetEmbeddingText();

        // Assert
        embeddingText.Should().Contain("Class: ServiceClient");
        embeddingText.Should().Contain("File: src/ServiceClient.ps1:5");
        embeddingText.Should().Contain("Synopsis: A client for interacting with the service API.");
        embeddingText.Should().Contain("Inherits: BaseClient");
        embeddingText.Should().Contain("Methods: Connect, Disconnect, SendRequest");
        embeddingText.Should().Contain("Properties: Endpoint, Timeout, IsConnected");
        embeddingText.Should().Contain("Code:");
    }

    [TestMethod]
    [DataRow(PsAstNodeType.Function)]
    [DataRow(PsAstNodeType.Workflow)]
    [DataRow(PsAstNodeType.Class)]
    [DataRow(PsAstNodeType.Enum)]
    [DataRow(PsAstNodeType.ScriptBlock)]
    [DataRow(PsAstNodeType.ParamBlock)]
    [DataRow(PsAstNodeType.Error)]
    public void NodeType_AllEnumValues_CanBeUsed(PsAstNodeType nodeType)
    {
        // Arrange & Act
        var node = new PsAstNode
        {
            NodeType = nodeType,
            Name = "Test",
            FilePath = "test.ps1"
        };

        // Assert
        node.NodeType.Should().Be(nodeType);
    }
}

[TestClass]
public class PsParameterInfoTests
{
    [TestMethod]
    public void Constructor_SetsProperties()
    {
        // Arrange & Act
        var param = new PsParameterInfo
        {
            Name = "ConfigPath",
            Type = "String",
            IsMandatory = true
        };

        // Assert
        param.Name.Should().Be("ConfigPath");
        param.Type.Should().Be("String");
        param.IsMandatory.Should().BeTrue();
    }

    [TestMethod]
    public void IsMandatory_DefaultValue_IsFalse()
    {
        // Arrange & Act
        var param = new PsParameterInfo
        {
            Name = "Optional",
            Type = "Object"
        };

        // Assert
        param.IsMandatory.Should().BeFalse();
    }
}