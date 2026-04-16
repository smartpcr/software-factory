// -----------------------------------------------------------------------
// <copyright file="InheritanceAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="InheritanceAnalyzer"/>.
/// </summary>
[TestClass]
public class InheritanceAnalyzerTests
{
    private InheritanceAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new InheritanceAnalyzer();
    }

    [TestMethod]
    public void Analyze_SimpleSingleInheritance_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class BaseClass { }
    public class DerivedClass : BaseClass { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.SourceType.Should().Be("Test.DerivedClass");
        rel.TargetType.Should().Be("BaseClass");
        rel.RelationType.Should().Be(InheritanceRelationType.Inherits);
        rel.SourceKind.Should().Be(TypeKind.Class);
        rel.TargetKind.Should().Be(TypeKind.Class);
    }

    [TestMethod]
    public void Analyze_MultiLevelInheritance_DetectsAllLevels()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class GrandParent { }
    public class Parent : GrandParent { }
    public class Child : Parent { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(2);
        relationships.Should().Contain(r => r.SourceType == "Test.Parent" && r.TargetType == "GrandParent");
        relationships.Should().Contain(r => r.SourceType == "Test.Child" && r.TargetType == "Parent");
    }

    [TestMethod]
    public void Analyze_AbstractBaseClass_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public abstract class AbstractBase { }
    public class ConcreteClass : AbstractBase { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.SourceType.Should().Be("Test.ConcreteClass");
        rel.TargetType.Should().Be("AbstractBase");
    }

    [TestMethod]
    public void Analyze_GenericBaseClass_DetectsRelationshipWithArguments()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class GenericBase<T> { }
    public class StringDerived : GenericBase<string> { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.TargetType.Should().Be("GenericBase<string>");
        rel.IsGeneric.Should().BeTrue();
        rel.GenericArguments.Should().Contain("string");
    }

    [TestMethod]
    public void Analyze_PartialClass_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class BaseClass { }
    public partial class PartialDerived : BaseClass { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var hierarchy = this.analyzer.BuildHierarchy();
        var partialType = hierarchy.Types.FirstOrDefault(t => t.Name == "PartialDerived");
        partialType.Should().NotBeNull();
        partialType!.IsPartial.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_RecordInheritance_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public record BaseRecord(string Name);
    public record DerivedRecord(string Name, int Age) : BaseRecord(Name);
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.SourceKind.Should().Be(TypeKind.Record);
        rel.TargetKind.Should().Be(TypeKind.Record);
    }

    [TestMethod]
    public void Analyze_StructWithInterfaces_OnlyDetectsImplements()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IComparable { }
    public struct MyStruct : IComparable { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.RelationType.Should().Be(InheritanceRelationType.Implements);
        rel.SourceKind.Should().Be(TypeKind.Struct);
    }

    [TestMethod]
    public void Analyze_SingleInterfaceImplementation_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public class ServiceImpl : IService { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.RelationType.Should().Be(InheritanceRelationType.Implements);
        rel.SourceKind.Should().Be(TypeKind.Class);
        rel.TargetKind.Should().Be(TypeKind.Interface);
    }

    [TestMethod]
    public void Analyze_MultipleInterfaceImplementation_DetectsAll()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public interface IRepository { }
    public interface IDisposable { }
    public class MultiImpl : IService, IRepository, IDisposable { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var implRelationships = relationships.Where(r => r.SourceType.Contains("MultiImpl")).ToList();
        implRelationships.Should().HaveCount(3);
        implRelationships.Should().OnlyContain(r => r.RelationType == InheritanceRelationType.Implements);
    }

    [TestMethod]
    public void Analyze_GenericInterfaceImplementation_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IRepository<T> { }
    public class UserRepository : IRepository<User> { }
    public class User { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var implRel = relationships.FirstOrDefault(r => r.SourceType.Contains("UserRepository"));
        implRel.Should().NotBeNull();
        implRel!.TargetType.Should().Be("IRepository<User>");
        implRel.IsGeneric.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_InterfaceInheritance_DetectsExtends()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IBase { }
    public interface IDerived : IBase { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.RelationType.Should().Be(InheritanceRelationType.Extends);
        rel.SourceKind.Should().Be(TypeKind.Interface);
        rel.TargetKind.Should().Be(TypeKind.Interface);
    }

    [TestMethod]
    public void BuildHierarchy_ReturnsCompleteGraph()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IBase { }
    public class BaseClass : IBase { }
    public class DerivedClass : BaseClass { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        this.analyzer.Analyze(tree, "test.cs");

        // Act
        var hierarchy = this.analyzer.BuildHierarchy();

        // Assert
        // 3 declared types + 2 referenced types without namespace (IBase, BaseClass)
        hierarchy.Types.Should().HaveCountGreaterThanOrEqualTo(3);

        var derivedNode = hierarchy.Types.First(t => t.Name == "DerivedClass");
        derivedNode.BaseType.Should().NotBeNull();
        derivedNode.BaseType!.Name.Should().Be("BaseClass");
    }

    [TestMethod]
    public void Analyze_ClassWithBaseAndInterfaces_DetectsBoth()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public class BaseClass { }
    public class DerivedService : BaseClass, IService { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var derivedRels = relationships.Where(r => r.SourceType.Contains("DerivedService")).ToList();
        derivedRels.Should().HaveCount(2);
        derivedRels.Should().Contain(r => r.RelationType == InheritanceRelationType.Inherits);
        derivedRels.Should().Contain(r => r.RelationType == InheritanceRelationType.Implements);
    }
}
