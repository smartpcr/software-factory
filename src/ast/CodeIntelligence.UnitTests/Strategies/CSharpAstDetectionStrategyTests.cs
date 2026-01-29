// -----------------------------------------------------------------------
// <copyright file="CSharpAstDetectionStrategyTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Strategies;

using CodeIntelligence.Models;
using CodeIntelligence.Strategies;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="CSharpAstDetectionStrategy"/>.
/// </summary>
[TestClass]
public class CSharpAstDetectionStrategyTests
{
    private CSharpAstDetectionStrategy strategy = null!;
    private string testFilePath = null!;

    [TestInitialize]
    public void Setup()
    {
        this.strategy = new CSharpAstDetectionStrategy();
        this.testFilePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.cs");
    }

    [TestCleanup]
    public void Cleanup()
    {
        this.strategy.Dispose();
        if (File.Exists(this.testFilePath))
        {
            File.Delete(this.testFilePath);
        }
    }

    [TestMethod]
    public void Name_ReturnsAst()
    {
        this.strategy.Name.Should().Be("ast");
    }

    [TestMethod]
    public void SupportedLanguages_IncludesCSharp()
    {
        this.strategy.SupportedLanguages.Should().Contain("csharp");
        this.strategy.SupportedLanguages.Should().Contain("cs");
    }

    [TestMethod]
    public void CanHandle_AstPattern_ReturnsTrue()
    {
        var pattern = new DetectionPattern
        {
            Strategy = "ast",
            Language = "csharp",
            Patterns = []
        };

        this.strategy.CanHandle(pattern).Should().BeTrue();
    }

    [TestMethod]
    public void CanHandle_NonAstPattern_ReturnsFalse()
    {
        var pattern = new DetectionPattern
        {
            Strategy = "regex",
            Language = "csharp",
            Patterns = []
        };

        this.strategy.CanHandle(pattern).Should().BeFalse();
    }

    [TestMethod]
    public async Task DetectAsync_ClassDeclaration_FindsClass()
    {
        // Arrange
        var sourceCode = @"
namespace TestNamespace
{
    public class MyTestClass
    {
    }
}";
        await File.WriteAllTextAsync(this.testFilePath, sourceCode);

        var rule = CreateRule("ClassDeclaration");

        // Act
        var results = await this.strategy.DetectAsync(rule, [this.testFilePath]);

        // Assert
        results.Should().HaveCount(1);
        results[0].Source.Name.Should().Be("MyTestClass");
        results[0].Category.Should().Be("test");
        results[0].RelationshipType.Should().Be("TestRelation");
    }

    [TestMethod]
    public async Task DetectAsync_MultipleClasses_FindsAll()
    {
        // Arrange
        var sourceCode = @"
namespace TestNamespace
{
    public class ClassOne { }
    public class ClassTwo { }
    public class ClassThree { }
}";
        await File.WriteAllTextAsync(this.testFilePath, sourceCode);

        var rule = CreateRule("ClassDeclaration");

        // Act
        var results = await this.strategy.DetectAsync(rule, [this.testFilePath]);

        // Assert
        results.Should().HaveCount(3);
        results.Select(r => r.Source.Name).Should().Contain("ClassOne", "ClassTwo", "ClassThree");
    }

    [TestMethod]
    public async Task DetectAsync_MethodDeclaration_FindsMethods()
    {
        // Arrange
        var sourceCode = @"
namespace TestNamespace
{
    public class MyClass
    {
        public void Method1() { }
        public int Method2() { return 0; }
    }
}";
        await File.WriteAllTextAsync(this.testFilePath, sourceCode);

        var rule = CreateRule("MethodDeclaration");

        // Act
        var results = await this.strategy.DetectAsync(rule, [this.testFilePath]);

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Source.Name).Should().Contain("Method1", "Method2");
    }

    [TestMethod]
    public async Task DetectAsync_InheritanceCondition_FiltersCorrectly()
    {
        // Arrange
        var sourceCode = @"
namespace TestNamespace
{
    public class BaseClass { }
    public class DerivedClass : BaseClass { }
    public class StandaloneClass { }
}";
        await File.WriteAllTextAsync(this.testFilePath, sourceCode);

        var rule = CreateRuleWithCondition("ClassDeclaration", "inheritsFrom('BaseClass')");

        // Act
        var results = await this.strategy.DetectAsync(rule, [this.testFilePath]);

        // Assert
        results.Should().HaveCount(1);
        results[0].Source.Name.Should().Be("DerivedClass");
    }

    [TestMethod]
    public async Task DetectAsync_AttributeCondition_FiltersCorrectly()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    [Obsolete]
    public class ObsoleteClass { }
    
    public class NormalClass { }
}";
        await File.WriteAllTextAsync(this.testFilePath, sourceCode);

        var rule = CreateRuleWithCondition("ClassDeclaration", "hasAttribute('Obsolete')");

        // Act
        var results = await this.strategy.DetectAsync(rule, [this.testFilePath]);

        // Assert
        results.Should().HaveCount(1);
        results[0].Source.Name.Should().Be("ObsoleteClass");
    }

    [TestMethod]
    public async Task DetectAsync_NonCSharpFile_SkipsFile()
    {
        // Arrange
        var txtPath = Path.ChangeExtension(this.testFilePath, ".txt");
        await File.WriteAllTextAsync(txtPath, "public class Test { }");

        var rule = CreateRule("ClassDeclaration");

        try
        {
            // Act
            var results = await this.strategy.DetectAsync(rule, [txtPath]);

            // Assert
            results.Should().BeEmpty();
        }
        finally
        {
            File.Delete(txtPath);
        }
    }

    [TestMethod]
    public async Task DetectAsync_EmptyFile_ReturnsEmpty()
    {
        // Arrange
        await File.WriteAllTextAsync(this.testFilePath, string.Empty);

        var rule = CreateRule("ClassDeclaration");

        // Act
        var results = await this.strategy.DetectAsync(rule, [this.testFilePath]);

        // Assert
        results.Should().BeEmpty();
    }

    private static DetectionRule CreateRule(string nodeType)
    {
        return new DetectionRule
        {
            Id = "test-rule",
            Name = "Test Rule",
            Category = "test",
            RelationshipType = "TestRelation",
            Detection =
            [
                new DetectionPattern
                {
                    Strategy = "ast",
                    Language = "csharp",
                    Patterns =
                    [
                        new PatternDefinition
                        {
                            NodeType = nodeType
                        }
                    ]
                }
            ],
            Output = new OutputSpecification
            {
                NodeType = "TestNode",
                Properties = []
            },
            Confidence = new ConfidenceSpecification
            {
                BaseScore = 0.9f,
                MinimumThreshold = 0.7f
            }
        };
    }

    private static DetectionRule CreateRuleWithCondition(string nodeType, string condition)
    {
        return new DetectionRule
        {
            Id = "test-rule",
            Name = "Test Rule",
            Category = "test",
            RelationshipType = "TestRelation",
            Detection =
            [
                new DetectionPattern
                {
                    Strategy = "ast",
                    Language = "csharp",
                    Patterns =
                    [
                        new PatternDefinition
                        {
                            NodeType = nodeType,
                            Condition = condition
                        }
                    ]
                }
            ],
            Output = new OutputSpecification
            {
                NodeType = "TestNode",
                Properties = []
            },
            Confidence = new ConfidenceSpecification
            {
                BaseScore = 0.9f,
                MinimumThreshold = 0.7f
            }
        };
    }
}
