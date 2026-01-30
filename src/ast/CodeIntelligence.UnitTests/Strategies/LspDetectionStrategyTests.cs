// -----------------------------------------------------------------------
// <copyright file="LspDetectionStrategyTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Strategies;

using CodeIntelligence.Models;
using CodeIntelligence.Strategies;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="LspDetectionStrategy"/>.
/// </summary>
[TestClass]
public class LspDetectionStrategyTests
{
    [TestMethod]
    public void Constructor_WithDefaults_CreatesInstance()
    {
        // Arrange & Act
        var strategy = new LspDetectionStrategy();

        // Assert
        strategy.Should().NotBeNull();
        strategy.Name.Should().Be("lsp");
    }

    [TestMethod]
    public void Constructor_WithOptions_CreatesInstance()
    {
        // Arrange & Act
        var options = new LspClientOptions
        {
            ServerCommand = "dotnet",
            ServerArguments = "language-server"
        };
        var strategy = new LspDetectionStrategy(options);

        // Assert
        strategy.Should().NotBeNull();
        strategy.Name.Should().Be("lsp");
    }

    [TestMethod]
    public void CanHandle_LspStrategy_ReturnsTrue()
    {
        // Arrange
        var strategy = new LspDetectionStrategy();
        var pattern = new DetectionPattern
        {
            Strategy = "lsp",
            Patterns = new List<PatternDefinition>
            {
                new PatternDefinition { NodeType = "MethodDeclaration" }
            }
        };

        // Act
        var result = strategy.CanHandle(pattern);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public void CanHandle_AstStrategy_ReturnsFalse()
    {
        // Arrange
        var strategy = new LspDetectionStrategy();
        var pattern = new DetectionPattern
        {
            Strategy = "ast",
            Patterns = new List<PatternDefinition>
            {
                new PatternDefinition { NodeType = "MethodDeclaration" }
            }
        };

        // Act
        var result = strategy.CanHandle(pattern);

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public void SupportedLanguages_IncludesCSharp()
    {
        // Arrange
        var strategy = new LspDetectionStrategy();

        // Act & Assert
        strategy.SupportedLanguages.Should().Contain("csharp");
        strategy.SupportedLanguages.Should().Contain("cs");
    }

    [TestMethod]
    public void SupportedLanguages_IncludesTypeScript()
    {
        // Arrange
        var strategy = new LspDetectionStrategy();

        // Act & Assert
        strategy.SupportedLanguages.Should().Contain("typescript");
        strategy.SupportedLanguages.Should().Contain("ts");
    }

    [TestMethod]
    public void SupportedLanguages_IncludesJavaScript()
    {
        // Arrange
        var strategy = new LspDetectionStrategy();

        // Act & Assert
        strategy.SupportedLanguages.Should().Contain("javascript");
        strategy.SupportedLanguages.Should().Contain("js");
    }

    [TestMethod]
    public async Task DetectAsync_WithNoServer_ReturnsEmpty()
    {
        // Arrange
        var options = new LspClientOptions
        {
            ServerCommand = "nonexistent-server-12345"
        };
        var strategy = new LspDetectionStrategy(options);
        var rule = CreateTestRule("lsp");

        // Act
        var results = await strategy.DetectAsync(rule, new[] { @"C:\test\file.cs" });

        // Assert
        results.Should().BeEmpty();
    }

    [TestMethod]
    public void LspClientOptions_DefaultValues_AreSet()
    {
        // Arrange & Act
        var options = new LspClientOptions();

        // Assert
        options.ServerCommand.Should().BeNull();
        options.ServerArguments.Should().BeNull();
    }

    [TestMethod]
    public void LspClientOptions_CustomValues_ArePreserved()
    {
        // Arrange & Act
        var options = new LspClientOptions
        {
            ServerCommand = "csharp-ls",
            ServerArguments = "--verbose"
        };

        // Assert
        options.ServerCommand.Should().Be("csharp-ls");
        options.ServerArguments.Should().Be("--verbose");
    }

    private static DetectionRule CreateTestRule(string strategy)
    {
        return new DetectionRule
        {
            Id = "test-rule",
            Name = "Test Rule",
            Category = "structural",
            RelationshipType = "test",
            Detection = new List<DetectionPattern>
            {
                new DetectionPattern
                {
                    Strategy = strategy,
                    Patterns = new List<PatternDefinition>
                    {
                        new PatternDefinition { NodeType = "ClassDeclaration" }
                    }
                }
            },
            Output = new OutputSpecification
            {
                NodeType = "Class",
                EdgeType = "defines",
                Properties = new List<PropertySpecification>()
            },
            Confidence = new ConfidenceSpecification
            {
                BaseScore = 0.8f
            }
        };
    }
}
