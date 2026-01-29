// -----------------------------------------------------------------------
// <copyright file="ConfidenceCalculatorTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Scoring;

using CodeIntelligence.Models;
using CodeIntelligence.Scoring;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ConfidenceCalculatorTests
{
    private ConfidenceCalculator calculator = null!;

    [TestInitialize]
    public void Setup()
    {
        this.calculator = new ConfidenceCalculator();
    }

    [TestMethod]
    public void Calculate_WithNoEvidence_ReturnsBaseScore()
    {
        // Arrange
        var baseScore = 0.8f;
        var evidence = new List<Evidence>();

        // Act
        var result = this.calculator.Calculate(baseScore, evidence);

        // Assert
        result.Should().Be(baseScore);
    }

    [TestMethod]
    public void Calculate_WithSingleEvidence_CombinesWithBase()
    {
        // Arrange
        var baseScore = 0.6f;
        var evidence = new List<Evidence>
        {
            new Evidence
            {
                Source = "ast",
                Description = "Found Main method",
                Weight = 0.9f
            }
        };

        // Act
        var result = this.calculator.Calculate(baseScore, evidence);

        // Assert
        result.Should().BeGreaterThan(baseScore);
        result.Should().BeLessThanOrEqualTo(1.0f);
    }

    [TestMethod]
    public void Calculate_WithMultipleEvidence_AggregatesWeights()
    {
        // Arrange
        var baseScore = 0.5f;
        var evidence = new List<Evidence>
        {
            new Evidence { Source = "ast", Description = "Found class", Weight = 0.8f },
            new Evidence { Source = "config", Description = "Found in manifest", Weight = 0.9f },
            new Evidence { Source = "inheritance", Description = "Inherits from ServiceBase", Weight = 0.85f }
        };

        // Act
        var result = this.calculator.Calculate(baseScore, evidence);

        // Assert
        result.Should().BeGreaterThan(baseScore);
        result.Should().BeGreaterThan(0.7f);
    }

    [TestMethod]
    public void CalculateWithBoost_AddsBoostToFinal()
    {
        // Arrange
        var baseScore = 0.7f;
        var evidence = new List<Evidence>
        {
            new Evidence { Source = "ast", Description = "Test", Weight = 0.8f }
        };
        var boost = 0.05f;

        // Act
        var withoutBoost = this.calculator.Calculate(baseScore, evidence);
        var withBoost = this.calculator.CalculateWithBoost(baseScore, evidence, boost);

        // Assert
        withBoost.Should().Be(Math.Min(1.0f, withoutBoost + boost));
    }

    [TestMethod]
    public void CreateAstEvidence_CreatesCorrectEvidence()
    {
        // Act
        var evidence = this.calculator.CreateAstEvidence("Found static Main", "Program.cs", 10);

        // Assert
        evidence.Source.Should().Be("ast");
        evidence.Description.Should().Be("Found static Main");
        evidence.FilePath.Should().Be("Program.cs");
        evidence.LineNumber.Should().Be(10);
        evidence.Weight.Should().Be(ConfidenceCalculator.DefaultWeights["ast"]);
    }

    [TestMethod]
    public void CreateInheritanceEvidence_IncludesTypeName()
    {
        // Act
        var evidence = this.calculator.CreateInheritanceEvidence("BackgroundService", "Worker.cs", 5);

        // Assert
        evidence.Source.Should().Be("inheritance");
        evidence.Description.Should().Contain("BackgroundService");
        evidence.Weight.Should().Be(ConfidenceCalculator.DefaultWeights["inheritance"]);
    }

    [TestMethod]
    public void CreateAttributeEvidence_IncludesAttributeName()
    {
        // Act
        var evidence = this.calculator.CreateAttributeEvidence("HttpGet", "Controller.cs", 20);

        // Assert
        evidence.Source.Should().Be("attribute");
        evidence.Description.Should().Contain("[HttpGet]");
    }

    [TestMethod]
    public void Correlate_WithMultipleStrategies_CalculatesCombinedConfidence()
    {
        // Arrange
        var results = new List<DetectionResult>
        {
            new DetectionResult
            {
                Strategy = "ast",
                BaseScore = 0.8f,
                EvidenceChain = new List<Evidence>
                {
                    new Evidence { Source = "ast", Description = "AST match", Weight = 0.85f }
                }
            },
            new DetectionResult
            {
                Strategy = "config",
                BaseScore = 0.7f,
                EvidenceChain = new List<Evidence>
                {
                    new Evidence { Source = "config", Description = "Config match", Weight = 0.9f }
                }
            }
        };

        // Act
        var correlated = this.calculator.Correlate(results);

        // Assert
        correlated.FinalConfidence.Should().BeGreaterThan(0.7f);
        correlated.StrategiesUsed.Should().HaveCount(2);
        correlated.CombinedEvidence.Should().HaveCount(2);
    }

    [TestMethod]
    public void Correlate_MultiStrategyBonus_IncreasesConfidence()
    {
        // Arrange - single strategy
        var singleResult = new List<DetectionResult>
        {
            new DetectionResult
            {
                Strategy = "ast",
                BaseScore = 0.8f,
                EvidenceChain = new List<Evidence>
                {
                    new Evidence { Source = "ast", Description = "Match", Weight = 0.9f }
                }
            }
        };

        // Arrange - multiple strategies with same evidence
        var multiResult = new List<DetectionResult>
        {
            new DetectionResult
            {
                Strategy = "ast",
                BaseScore = 0.8f,
                EvidenceChain = new List<Evidence>
                {
                    new Evidence { Source = "ast", Description = "Match", Weight = 0.9f }
                }
            },
            new DetectionResult
            {
                Strategy = "lsp",
                BaseScore = 0.8f,
                EvidenceChain = new List<Evidence>()
            }
        };

        // Act
        var singleCorrelated = this.calculator.Correlate(singleResult);
        var multiCorrelated = this.calculator.Correlate(multiResult);

        // Assert
        multiCorrelated.FinalConfidence.Should().BeGreaterThan(singleCorrelated.FinalConfidence);
    }

    [TestMethod]
    public void MeetsThreshold_AboveThreshold_ReturnsTrue()
    {
        // Act & Assert
        ConfidenceCalculator.MeetsThreshold(0.8f, 0.7f).Should().BeTrue();
        ConfidenceCalculator.MeetsThreshold(0.7f, 0.7f).Should().BeTrue();
    }

    [TestMethod]
    public void MeetsThreshold_BelowThreshold_ReturnsFalse()
    {
        // Act & Assert
        ConfidenceCalculator.MeetsThreshold(0.6f, 0.7f).Should().BeFalse();
    }

    [TestMethod]
    public void GetLevel_ReturnsCorrectCategory()
    {
        // Act & Assert
        ConfidenceCalculator.GetLevel(0.95f).Should().Be(ConfidenceLevel.VeryHigh);
        ConfidenceCalculator.GetLevel(0.85f).Should().Be(ConfidenceLevel.High);
        ConfidenceCalculator.GetLevel(0.75f).Should().Be(ConfidenceLevel.Medium);
        ConfidenceCalculator.GetLevel(0.6f).Should().Be(ConfidenceLevel.Low);
        ConfidenceCalculator.GetLevel(0.3f).Should().Be(ConfidenceLevel.VeryLow);
    }

    [TestMethod]
    public void Calculate_ClampsToMaximum()
    {
        // Arrange
        var baseScore = 0.9f;
        var evidence = new List<Evidence>
        {
            new Evidence { Source = "ast", Description = "High", Weight = 1.0f },
            new Evidence { Source = "config", Description = "High", Weight = 1.0f },
            new Evidence { Source = "manifest", Description = "High", Weight = 1.0f },
            new Evidence { Source = "lsp", Description = "High", Weight = 1.0f }
        };

        // Act
        var result = this.calculator.Calculate(baseScore, evidence);

        // Assert
        result.Should().BeLessThanOrEqualTo(1.0f);
    }

    [TestMethod]
    public void Calculate_ClampsToMinimum()
    {
        // Arrange
        var baseScore = 0.0f;
        var evidence = new List<Evidence>();

        // Act
        var result = this.calculator.Calculate(baseScore, evidence);

        // Assert
        result.Should().BeGreaterThanOrEqualTo(0.0f);
    }
}
