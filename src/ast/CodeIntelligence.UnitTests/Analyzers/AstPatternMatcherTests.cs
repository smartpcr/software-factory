// -----------------------------------------------------------------------
// <copyright file="AstPatternMatcherTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using CodeIntelligence.Models;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="AstPatternMatcher"/>.
/// </summary>
[TestClass]
public class AstPatternMatcherTests
{
    private AstPatternMatcher matcher = null!;

    [TestInitialize]
    public void Setup()
    {
        this.matcher = new AstPatternMatcher();
    }

    [TestMethod]
    public void Match_ClassDeclaration_FindsAllClasses()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class ClassA { }
    public class ClassB { }
    internal class ClassC { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition { NodeType = "ClassDeclaration" };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(3);
        results.Select(r => r.Properties["className"]).Should().Contain("ClassA", "ClassB", "ClassC");
    }

    [TestMethod]
    public void Match_WithInheritsFromCondition_FiltersCorrectly()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class BaseClass { }
    public class DerivedClass : BaseClass { }
    public class OtherClass { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "inheritsFrom('BaseClass')"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties["className"].Should().Be("DerivedClass");
    }

    [TestMethod]
    public void Match_WithImplementsCondition_FiltersCorrectly()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public class ServiceImpl : IService { }
    public class NotAService { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "implements('IService')"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties["className"].Should().Be("ServiceImpl");
    }

    [TestMethod]
    public void Match_WithHasAttributeCondition_FiltersCorrectly()
    {
        // Arrange
        var code = @"
using System;

namespace Test
{
    [Obsolete]
    public class DeprecatedClass { }
    
    public class NormalClass { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "hasAttribute('Obsolete')"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties["className"].Should().Be("DeprecatedClass");
    }

    [TestMethod]
    public void Match_WithHasMethodCondition_FiltersCorrectly()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class WithDispose : System.IDisposable
    {
        public void Dispose() { }
    }
    
    public class WithoutDispose { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "hasMethod('Dispose')"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties["className"].Should().Be("WithDispose");
    }

    [TestMethod]
    public void Match_MethodDeclaration_ExtractsMethodInfo()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyClass
    {
        public int Calculate(int a, int b) { return a + b; }
        public async Task ProcessAsync() { }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition { NodeType = "MethodDeclaration" };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(2);
        var calculateMethod = results.First(r => r.Properties["methodName"].Equals("Calculate"));
        calculateMethod.Properties["returnType"].Should().Be("int");
        calculateMethod.Properties["containingType"].Should().Be("MyClass");
    }

    [TestMethod]
    public void Match_ConstructorDeclaration_ExtractsParameters()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyService
    {
        private readonly ILogger _logger;
        private readonly IRepository _repo;
        
        public MyService(ILogger logger, IRepository repo)
        {
            _logger = logger;
            _repo = repo;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition { NodeType = "ConstructorDeclaration" };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties["className"].Should().Be("MyService");
        var parameters = results[0].Properties["parameters"] as IEnumerable<object>;
        parameters.Should().HaveCount(2);
    }

    [TestMethod]
    public void Match_InvocationExpression_ExtractsCallInfo()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyClass
    {
        public void Method()
        {
            Console.WriteLine(""Hello"");
            _logger.LogInformation(""Test"");
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition { NodeType = "InvocationExpression" };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Properties["expression"]).Should()
            .Contain("Console.WriteLine", "_logger.LogInformation");
    }

    [TestMethod]
    public void Match_RecordDeclaration_ExtractsRecordInfo()
    {
        // Arrange
        var code = @"
namespace Test
{
    public record Person(string Name, int Age);
    public record Employee(string Name, int Age, string Department) : Person(Name, Age);
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition { NodeType = "RecordDeclaration" };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Properties["recordName"]).Should().Contain("Person", "Employee");
    }

    [TestMethod]
    public void EvaluateCondition_AndOperator_WorksCorrectly()
    {
        // Arrange
        var code = @"
namespace Test
{
    public static class Helper { }
    public class NonStatic { }
    internal static class InternalHelper { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "isStatic && isPublic"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties["className"].Should().Be("Helper");
    }

    [TestMethod]
    public void EvaluateCondition_OrOperator_WorksCorrectly()
    {
        // Arrange
        var code = @"
namespace Test
{
    public abstract class AbstractBase { }
    public static class StaticHelper { }
    public class Regular { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "isAbstract || isStatic"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Properties["className"]).Should().Contain("AbstractBase", "StaticHelper");
    }

    [TestMethod]
    public void EvaluateCondition_NotOperator_WorksCorrectly()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class PublicClass { }
    internal class InternalClass { }
    private class PrivateClass { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "!isPublic"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Properties["className"]).Should().NotContain("PublicClass");
    }

    [TestMethod]
    public void Match_WithExtractExpressions_ExtractsCustomProperties()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyClass : BaseClass
    {
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Extract = new Dictionary<string, string>
            {
                ["name"] = "node.Identifier.Text",
                ["bases"] = "node.BaseList.Types"
            }
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties.Should().ContainKey("name");
        results[0].Properties["name"].Should().Be("MyClass");
    }

    [TestMethod]
    public void Match_InterfaceDeclaration_FindsInterfaces()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public interface IRepository { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition { NodeType = "InterfaceDeclaration" };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Properties["interfaceName"]).Should().Contain("IService", "IRepository");
    }

    [TestMethod]
    public void Match_EnumDeclaration_ExtractsMembers()
    {
        // Arrange
        var code = @"
namespace Test
{
    public enum Status
    {
        Pending,
        Active,
        Completed
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition { NodeType = "EnumDeclaration" };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Properties["enumName"].Should().Be("Status");
        var members = results[0].Properties["members"] as IEnumerable<string>;
        members.Should().Contain("Pending", "Active", "Completed");
    }

    [TestMethod]
    public void Match_NameMatchesPattern_FiltersCorrectly()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class OrderService { }
    public class ProductService { }
    public class OrderRepository { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var pattern = new PatternDefinition
        {
            NodeType = "ClassDeclaration",
            Condition = "className matches '.*Service$'"
        };

        // Act
        var results = this.matcher.Match(root, pattern).ToList();

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Properties["className"]).Should().Contain("OrderService", "ProductService");
    }
}
