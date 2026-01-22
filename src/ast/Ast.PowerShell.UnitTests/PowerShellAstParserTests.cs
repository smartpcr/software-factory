// -----------------------------------------------------------------------
// <copyright file="PowerShellAstParserTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.PowerShell.UnitTests;

[TestClass]
public class PowerShellAstParserTests
{
    private readonly PowerShellAstParser parser = new();

    [TestMethod]
    public void ParseCode_EmptyCode_ReturnsScriptBlock()
    {
        // Arrange
        var code = "";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert - empty code still creates a script block
        nodes.Should().ContainSingle(n => n.NodeType == PsAstNodeType.ScriptBlock);
    }

    [TestMethod]
    public void ParseCode_SimpleFunction_ReturnsFunctionNode()
    {
        // Arrange
        var code = @"
function Get-Greeting {
    param([string]$Name)
    return ""Hello, $Name!""
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == PsAstNodeType.Function);
        var funcNode = nodes.First(n => n.NodeType == PsAstNodeType.Function);
        funcNode.Name.Should().Be("Get-Greeting");
    }

    [TestMethod]
    public void ParseCode_FunctionWithParameters_ExtractsParameters()
    {
        // Arrange
        var code = @"
function Set-Configuration {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Path,

        [int]$Timeout = 30
    )
    # Implementation
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var funcNode = nodes.First(n => n.NodeType == PsAstNodeType.Function);
        funcNode.Parameters.Should().HaveCount(2);

        var pathParam = funcNode.Parameters.First(p => p.Name == "Path");
        pathParam.Type.Should().Be("String");
        pathParam.IsMandatory.Should().BeTrue();

        var timeoutParam = funcNode.Parameters.First(p => p.Name == "Timeout");
        timeoutParam.Type.Should().Be("Int32");
        timeoutParam.IsMandatory.Should().BeFalse();
    }

    [TestMethod]
    public void ParseCode_ClassDefinition_ReturnsClassNode()
    {
        // Arrange
        var code = @"
class Person {
    [string]$Name
    [int]$Age

    [void] SayHello() {
        Write-Host ""Hello, I am $($this.Name)""
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == PsAstNodeType.Class);
        var classNode = nodes.First(n => n.NodeType == PsAstNodeType.Class);
        classNode.Name.Should().Be("Person");
        classNode.Properties.Should().Contain("Name");
        classNode.Properties.Should().Contain("Age");
        classNode.Methods.Should().Contain("SayHello");
    }

    [TestMethod]
    public void ParseCode_ClassWithBaseType_IncludesBaseTypes()
    {
        // Arrange
        var code = @"
class Employee : Person {
    [string]$Department
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var classNode = nodes.First(n => n.NodeType == PsAstNodeType.Class);
        classNode.Name.Should().Be("Employee");
        classNode.BaseTypes.Should().Contain("Person");
    }

    [TestMethod]
    public void ParseCode_EnumDefinition_ReturnsEnumNode()
    {
        // Arrange
        var code = @"
enum Status {
    Pending
    Active
    Completed
    Failed
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == PsAstNodeType.Enum);
        var enumNode = nodes.First(n => n.NodeType == PsAstNodeType.Enum);
        enumNode.Name.Should().Be("Status");
    }

    [TestMethod]
    public void ParseCode_ParamBlock_ReturnsParamBlockNode()
    {
        // Arrange
        var code = @"
param(
    [string]$ConfigPath,
    [switch]$Force
)";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == PsAstNodeType.ParamBlock);
        var paramNode = nodes.First(n => n.NodeType == PsAstNodeType.ParamBlock);
        paramNode.Parameters.Should().HaveCount(2);
    }

    [TestMethod]
    public void ParseCode_InvalidSyntax_ReturnsErrorNode()
    {
        // Arrange
        var code = @"
function Broken {
    $x = (
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == PsAstNodeType.Error);
    }

    [TestMethod]
    public void ParseCode_LineNumbers_AreCorrect()
    {
        // Arrange
        var code = @"function First {
    # Line 2
}

function Second {
    # Line 6
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var first = nodes.First(n => n.NodeType == PsAstNodeType.Function && n.Name == "First");
        first.StartLine.Should().Be(1);

        var second = nodes.First(n => n.NodeType == PsAstNodeType.Function && n.Name == "Second");
        second.StartLine.Should().Be(5);
    }

    [TestMethod]
    public void ParseCode_FilePath_IsPreserved()
    {
        // Arrange
        var code = "function Test { }";
        var filePath = "scripts/Deploy.ps1";

        // Act
        var nodes = parser.ParseCode(code, filePath).ToList();

        // Assert
        nodes.Should().OnlyContain(n => n.FilePath == filePath);
    }

    [TestMethod]
    public void ParseCode_FunctionWithHelpComment_ExtractsSynopsis()
    {
        // Arrange
        var code = @"
<#
.SYNOPSIS
    Gets the current user configuration.
.DESCRIPTION
    Retrieves configuration from the registry.
#>
function Get-UserConfig {
    # Implementation
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var funcNode = nodes.First(n => n.NodeType == PsAstNodeType.Function);
        funcNode.Summary.Should().Contain("Gets the current user configuration");
    }

    [TestMethod]
    public void ParseCode_MultipleFunctions_ReturnsAllFunctions()
    {
        // Arrange
        var code = @"
function Get-Data { }
function Set-Data { }
function Remove-Data { }";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var functions = nodes.Where(n => n.NodeType == PsAstNodeType.Function).ToList();
        functions.Should().HaveCount(3);
        functions.Should().Contain(f => f.Name == "Get-Data");
        functions.Should().Contain(f => f.Name == "Set-Data");
        functions.Should().Contain(f => f.Name == "Remove-Data");
    }

    [TestMethod]
    public void ParseCode_ScriptBlock_ReturnsScriptBlockNode()
    {
        // Arrange
        var code = @"
# Script header
$config = Import-Config
Start-Process -FilePath $config.Path";

        // Act
        var nodes = parser.ParseCode(code, "script.ps1").ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == PsAstNodeType.ScriptBlock);
        var scriptNode = nodes.First(n => n.NodeType == PsAstNodeType.ScriptBlock);
        scriptNode.Name.Should().Be("script.ps1");
    }

    [TestMethod]
    public void ParseCode_AdvancedFunction_ExtractsAllAttributes()
    {
        // Arrange
        var code = @"
function Invoke-Process {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory=$true, Position=0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name
    )
    process {
        [PSCustomObject]@{ Name = $Name }
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var funcNode = nodes.First(n => n.NodeType == PsAstNodeType.Function);
        funcNode.Name.Should().Be("Invoke-Process");
        funcNode.Parameters.Should().HaveCount(1);
        funcNode.Parameters[0].IsMandatory.Should().BeTrue();
    }
}
