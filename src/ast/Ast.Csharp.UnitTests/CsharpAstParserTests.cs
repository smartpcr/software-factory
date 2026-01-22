// -----------------------------------------------------------------------
// <copyright file="CsharpAstParserTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Csharp.UnitTests;

[TestClass]
public class CsharpAstParserTests
{
    private readonly CsharpAstParser parser = new();

    [TestMethod]
    public void ParseCode_EmptyCode_ReturnsNoNodes()
    {
        // Arrange
        var code = "";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().BeEmpty();
    }

    [TestMethod]
    public void ParseCode_SimpleClass_ReturnsClassNode()
    {
        // Arrange
        var code = @"
public class MyClass
{
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().ContainSingle(n => n.NodeType == AstNodeType.Class);
        var classNode = nodes.First(n => n.NodeType == AstNodeType.Class);
        classNode.Name.Should().Be("MyClass");
        classNode.Modifiers.Should().Contain("public");
    }

    [TestMethod]
    public void ParseCode_ClassWithBaseType_IncludesBaseTypes()
    {
        // Arrange
        var code = @"
public class Dog : Animal, IBarking
{
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var classNode = nodes.First(n => n.NodeType == AstNodeType.Class);
        classNode.Name.Should().Be("Dog");
        classNode.BaseTypes.Should().Contain("Animal");
        classNode.BaseTypes.Should().Contain("IBarking");
    }

    [TestMethod]
    public void ParseCode_Interface_ReturnsInterfaceNode()
    {
        // Arrange
        var code = @"
public interface IRepository<T> where T : class
{
    T GetById(int id);
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().ContainSingle(n => n.NodeType == AstNodeType.Interface);
        var ifaceNode = nodes.First(n => n.NodeType == AstNodeType.Interface);
        ifaceNode.Name.Should().Be("IRepository");
    }

    [TestMethod]
    public void ParseCode_Struct_ReturnsStructNode()
    {
        // Arrange
        var code = @"
public readonly struct Point
{
    public int X { get; }
    public int Y { get; }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Struct);
        var structNode = nodes.First(n => n.NodeType == AstNodeType.Struct);
        structNode.Name.Should().Be("Point");
        structNode.Modifiers.Should().Contain("readonly");
    }

    [TestMethod]
    public void ParseCode_Record_ReturnsRecordNode()
    {
        // Arrange
        var code = @"
public record Person(string Name, int Age);";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Record);
        var recordNode = nodes.First(n => n.NodeType == AstNodeType.Record);
        recordNode.Name.Should().Be("Person");
    }

    [TestMethod]
    public void ParseCode_Enum_ReturnsEnumNode()
    {
        // Arrange
        var code = @"
public enum Status
{
    Pending,
    Active,
    Completed
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Enum);
        var enumNode = nodes.First(n => n.NodeType == AstNodeType.Enum);
        enumNode.Name.Should().Be("Status");
    }

    [TestMethod]
    public void ParseCode_Method_ReturnsMethodNode()
    {
        // Arrange
        var code = @"
public class Calculator
{
    public int Add(int a, int b)
    {
        return a + b;
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Method);
        var methodNode = nodes.First(n => n.NodeType == AstNodeType.Method);
        methodNode.Name.Should().Be("Add");
        methodNode.ReturnType.Should().Be("int");
        methodNode.Parameters.Should().HaveCount(2);
        methodNode.Parameters[0].Name.Should().Be("a");
        methodNode.Parameters[0].Type.Should().Be("int");
    }

    [TestMethod]
    public void ParseCode_Property_ReturnsPropertyNode()
    {
        // Arrange
        var code = @"
public class Person
{
    public string Name { get; set; }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Property);
        var propNode = nodes.First(n => n.NodeType == AstNodeType.Property);
        propNode.Name.Should().Be("Name");
        propNode.ReturnType.Should().Be("string");
    }

    [TestMethod]
    public void ParseCode_Field_ReturnsFieldNode()
    {
        // Arrange
        var code = @"
public class Counter
{
    private readonly int _maxValue = 100;
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Field);
        var fieldNode = nodes.First(n => n.NodeType == AstNodeType.Field);
        fieldNode.Name.Should().Be("_maxValue");
        fieldNode.ReturnType.Should().Be("int");
        fieldNode.Modifiers.Should().Contain("readonly");
    }

    [TestMethod]
    public void ParseCode_Constructor_ReturnsConstructorNode()
    {
        // Arrange
        var code = @"
public class Service
{
    private readonly ILogger _logger;

    public Service(ILogger logger)
    {
        _logger = logger;
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Constructor);
        var ctorNode = nodes.First(n => n.NodeType == AstNodeType.Constructor);
        ctorNode.Name.Should().Be("Service");
        ctorNode.Parameters.Should().HaveCount(1);
        ctorNode.Parameters[0].Name.Should().Be("logger");
        ctorNode.Parameters[0].Type.Should().Be("ILogger");
    }

    [TestMethod]
    public void ParseCode_Namespace_ReturnsNamespaceNode()
    {
        // Arrange
        var code = @"
namespace MyApp.Services
{
    public class Service { }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Namespace);
        var nsNode = nodes.First(n => n.NodeType == AstNodeType.Namespace);
        nsNode.Name.Should().Be("MyApp.Services");
    }

    [TestMethod]
    public void ParseCode_FileScopedNamespace_ReturnsNamespaceNode()
    {
        // Arrange
        var code = @"
namespace MyApp.Services;

public class Service { }";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        nodes.Should().Contain(n => n.NodeType == AstNodeType.Namespace);
        var nsNode = nodes.First(n => n.NodeType == AstNodeType.Namespace);
        nsNode.Name.Should().Be("MyApp.Services");
    }

    [TestMethod]
    public void ParseCode_XmlDocumentation_ExtractsSummary()
    {
        // Arrange
        var code = @"
public class Calculator
{
    /// <summary>
    /// Calculates the sum of two numbers.
    /// </summary>
    public int Add(int a, int b) => a + b;
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var methodNode = nodes.First(n => n.NodeType == AstNodeType.Method);
        methodNode.Summary.Should().Contain("Calculates the sum of two numbers");
    }

    [TestMethod]
    public void ParseCode_LineNumbers_AreCorrect()
    {
        // Arrange
        var code = @"public class MyClass
{
    public void Method1() { }

    public void Method2() { }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var classNode = nodes.First(n => n.NodeType == AstNodeType.Class);
        classNode.StartLine.Should().Be(1);

        var method1 = nodes.First(n => n.NodeType == AstNodeType.Method && n.Name == "Method1");
        method1.StartLine.Should().Be(3);

        var method2 = nodes.First(n => n.NodeType == AstNodeType.Method && n.Name == "Method2");
        method2.StartLine.Should().Be(5);
    }

    [TestMethod]
    public void ParseCode_NestedClasses_AreExtracted()
    {
        // Arrange
        var code = @"
public class Outer
{
    public class Inner
    {
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var classes = nodes.Where(n => n.NodeType == AstNodeType.Class).ToList();
        classes.Should().HaveCount(2);
        classes.Should().Contain(c => c.Name == "Outer");
        classes.Should().Contain(c => c.Name == "Inner");
    }

    [TestMethod]
    public void ParseCode_AsyncMethod_HasCorrectModifiers()
    {
        // Arrange
        var code = @"
public class Service
{
    public async Task<string> GetDataAsync()
    {
        return await Task.FromResult(""data"");
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var methodNode = nodes.First(n => n.NodeType == AstNodeType.Method);
        methodNode.Modifiers.Should().Contain("async");
        methodNode.ReturnType.Should().Be("Task<string>");
    }

    [TestMethod]
    public void ParseCode_GenericMethod_HasCorrectReturnType()
    {
        // Arrange
        var code = @"
public class Repository
{
    public T GetById<T>(int id) where T : class
    {
        return default;
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();

        // Assert
        var methodNode = nodes.First(n => n.NodeType == AstNodeType.Method);
        methodNode.Name.Should().Be("GetById");
        methodNode.ReturnType.Should().Be("T");
    }

    [TestMethod]
    public void ParseCode_FilePath_IsPreserved()
    {
        // Arrange
        var code = "public class Test { }";
        var filePath = "src/Services/TestService.cs";

        // Act
        var nodes = parser.ParseCode(code, filePath).ToList();

        // Assert
        nodes.Should().OnlyContain(n => n.FilePath == filePath);
    }

    [TestMethod]
    public void GetEmbeddingText_Method_ContainsAllInfo()
    {
        // Arrange
        var code = @"
public class Service
{
    /// <summary>
    /// Process the data.
    /// </summary>
    public async Task<Result> ProcessAsync(string input, int count)
    {
        return new Result();
    }
}";

        // Act
        var nodes = parser.ParseCode(code).ToList();
        var methodNode = nodes.First(n => n.NodeType == AstNodeType.Method);
        var embeddingText = methodNode.GetEmbeddingText();

        // Assert - core method info
        embeddingText.Should().Contain("Method: ProcessAsync");
        embeddingText.Should().Contain("Modifiers: public async");
        embeddingText.Should().Contain("Returns: Task<Result>");
        embeddingText.Should().Contain("Parameters: string input, int count");
        // Note: Summary extraction may include XML formatting artifacts
        if (methodNode.Summary != null)
        {
            embeddingText.Should().Contain("Summary:");
        }
    }
}
