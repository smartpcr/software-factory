// -----------------------------------------------------------------------
// <copyright file="LayerViolationDetectorTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using System.Linq;
using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static CodeIntelligence.Analyzers.LayerClassificationAnalyzer;

/// <summary>
/// Unit tests for <see cref="LayerViolationDetector"/>.
/// </summary>
[TestClass]
public class LayerViolationDetectorTests
{
    private LayerViolationDetector detector = null!;

    [TestInitialize]
    public void Setup()
    {
        this.detector = new LayerViolationDetector();
    }

    #region Critical Violations

    [TestMethod]
    public void IsViolation_DomainToInfrastructure_ReturnsTrue()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure);
        result.Should().BeTrue();
    }

    [TestMethod]
    public void IsViolation_DomainToApplication_ReturnsTrue()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Domain, ArchitectureLayer.Application);
        result.Should().BeTrue();
    }

    [TestMethod]
    public void IsViolation_DomainToApi_ReturnsTrue()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Domain, ArchitectureLayer.Api);
        result.Should().BeTrue();
    }

    [TestMethod]
    public void IsViolation_DomainToPresentation_ReturnsTrue()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Domain, ArchitectureLayer.Presentation);
        result.Should().BeTrue();
    }

    [TestMethod]
    public void GetSeverity_DomainToInfrastructure_ReturnsCritical()
    {
        var severity = this.detector.GetSeverity(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure);
        severity.Should().Be(ViolationSeverity.Critical);
    }

    #endregion

    #region High Severity Violations

    [TestMethod]
    public void IsViolation_PresentationToInfrastructure_ReturnsTrue()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Presentation, ArchitectureLayer.Infrastructure);
        result.Should().BeTrue();
    }

    [TestMethod]
    public void IsViolation_PresentationToDomain_ReturnsTrue()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Presentation, ArchitectureLayer.Domain);
        result.Should().BeTrue();
    }

    [TestMethod]
    public void GetSeverity_PresentationToInfrastructure_ReturnsHigh()
    {
        var severity = this.detector.GetSeverity(ArchitectureLayer.Presentation, ArchitectureLayer.Infrastructure);
        severity.Should().Be(ViolationSeverity.High);
    }

    #endregion

    #region Medium Severity Violations

    [TestMethod]
    public void IsViolation_ApiToInfrastructure_ReturnsTrue()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Api, ArchitectureLayer.Infrastructure);
        result.Should().BeTrue();
    }

    [TestMethod]
    public void GetSeverity_ApiToInfrastructure_ReturnsMedium()
    {
        var severity = this.detector.GetSeverity(ArchitectureLayer.Api, ArchitectureLayer.Infrastructure);
        severity.Should().Be(ViolationSeverity.Medium);
    }

    #endregion

    #region Allowed Interactions

    [TestMethod]
    public void IsViolation_PresentationToApi_ReturnsFalse()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Presentation, ArchitectureLayer.Api);
        result.Should().BeFalse();
    }

    [TestMethod]
    public void IsViolation_ApiToApplication_ReturnsFalse()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Api, ArchitectureLayer.Application);
        result.Should().BeFalse();
    }

    [TestMethod]
    public void IsViolation_ApplicationToDomain_ReturnsFalse()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Application, ArchitectureLayer.Domain);
        result.Should().BeFalse();
    }

    [TestMethod]
    public void IsViolation_ApplicationToInfrastructure_ReturnsFalse()
    {
        var result = this.detector.IsViolation(ArchitectureLayer.Application, ArchitectureLayer.Infrastructure);
        result.Should().BeFalse();
    }

    [TestMethod]
    public void IsViolation_InfrastructureToDomain_ReturnsFalse()
    {
        // Infrastructure implementing domain interfaces
        var result = this.detector.IsViolation(ArchitectureLayer.Infrastructure, ArchitectureLayer.Domain);
        result.Should().BeFalse();
    }

    [TestMethod]
    public void IsViolation_AnyToCrossCutting_ReturnsFalse()
    {
        this.detector.IsViolation(ArchitectureLayer.Presentation, ArchitectureLayer.CrossCutting).Should().BeFalse();
        this.detector.IsViolation(ArchitectureLayer.Api, ArchitectureLayer.CrossCutting).Should().BeFalse();
        this.detector.IsViolation(ArchitectureLayer.Application, ArchitectureLayer.CrossCutting).Should().BeFalse();
        this.detector.IsViolation(ArchitectureLayer.Domain, ArchitectureLayer.CrossCutting).Should().BeFalse();
        this.detector.IsViolation(ArchitectureLayer.Infrastructure, ArchitectureLayer.CrossCutting).Should().BeFalse();
    }

    [TestMethod]
    public void IsViolation_SameLayer_ReturnsFalse()
    {
        this.detector.IsViolation(ArchitectureLayer.Api, ArchitectureLayer.Api).Should().BeFalse();
        this.detector.IsViolation(ArchitectureLayer.Domain, ArchitectureLayer.Domain).Should().BeFalse();
    }

    [TestMethod]
    public void IsViolation_UnknownLayer_ReturnsFalse()
    {
        this.detector.IsViolation(ArchitectureLayer.Unknown, ArchitectureLayer.Api).Should().BeFalse();
        this.detector.IsViolation(ArchitectureLayer.Api, ArchitectureLayer.Unknown).Should().BeFalse();
    }

    #endregion

    #region Violation Detection

    [TestMethod]
    public void DetectViolations_DetectsViolationsInInteractions()
    {
        var interactions = new[]
        {
            new LayerInteractionInfo
            {
                SourceType = "OrderEntity",
                SourceLayer = ArchitectureLayer.Domain,
                TargetType = "SqlConnection",
                TargetLayer = ArchitectureLayer.Infrastructure,
                InteractionType = InteractionType.FieldDependency,
                FilePath = "Order.cs",
                LineNumber = 10,
            },
            new LayerInteractionInfo
            {
                SourceType = "OrderService",
                SourceLayer = ArchitectureLayer.Application,
                TargetType = "IOrderRepository",
                TargetLayer = ArchitectureLayer.Infrastructure,
                InteractionType = InteractionType.ConstructorInjection,
                FilePath = "OrderService.cs",
                LineNumber = 5,
            },
        };

        var violations = this.detector.DetectViolations(interactions);

        violations.Should().HaveCount(1);
        violations[0].SourceComponent.Should().Be("OrderEntity");
        violations[0].Severity.Should().Be(ViolationSeverity.Critical);
    }

    [TestMethod]
    public void DetectViolations_IncludesMessageAndRecommendation()
    {
        var interactions = new[]
        {
            new LayerInteractionInfo
            {
                SourceType = "Order",
                SourceLayer = ArchitectureLayer.Domain,
                TargetType = "DbContext",
                TargetLayer = ArchitectureLayer.Infrastructure,
                InteractionType = InteractionType.FieldDependency,
                FilePath = "Order.cs",
                LineNumber = 10,
            },
        };

        var violations = this.detector.DetectViolations(interactions);

        violations.Should().HaveCount(1);
        violations[0].Message.Should().Contain("Dependency Inversion");
        violations[0].Recommendation.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Violation Messages

    [TestMethod]
    public void GetViolationDetails_DomainToInfrastructure_ReturnsCorrectMessage()
    {
        var (message, recommendation) = this.detector.GetViolationDetails(
            ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure);

        message.Should().Contain("Dependency Inversion");
        recommendation.Should().Contain("interface");
    }

    [TestMethod]
    public void GetViolationDetails_PresentationToInfrastructure_ReturnsCorrectMessage()
    {
        var (message, recommendation) = this.detector.GetViolationDetails(
            ArchitectureLayer.Presentation, ArchitectureLayer.Infrastructure);

        message.Should().Contain("data layer");
        recommendation.Should().Contain("service");
    }

    [TestMethod]
    public void GetViolationDetails_ApiToInfrastructure_ReturnsCorrectMessage()
    {
        var (message, recommendation) = this.detector.GetViolationDetails(
            ArchitectureLayer.Api, ArchitectureLayer.Infrastructure);

        message.Should().Contain("Controllers");
        recommendation.Should().Contain("services");
    }

    #endregion

    #region Summary

    [TestMethod]
    public void GetSummary_CalculatesCorrectCounts()
    {
        var violations = new[]
        {
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Domain,
                TargetLayer = ArchitectureLayer.Infrastructure,
                SourceComponent = "Order",
                TargetComponent = "DbContext",
                FilePath = "Order.cs",
                LineNumber = 10,
                Severity = ViolationSeverity.Critical,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.FieldDependency,
            },
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Presentation,
                TargetLayer = ArchitectureLayer.Infrastructure,
                SourceComponent = "HomeView",
                TargetComponent = "SqlConnection",
                FilePath = "HomeView.cs",
                LineNumber = 20,
                Severity = ViolationSeverity.High,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.FieldDependency,
            },
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Api,
                TargetLayer = ArchitectureLayer.Infrastructure,
                SourceComponent = "OrdersController",
                TargetComponent = "OrderRepository",
                FilePath = "OrdersController.cs",
                LineNumber = 30,
                Severity = ViolationSeverity.Medium,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.ConstructorInjection,
            },
        };

        var summary = this.detector.GetSummary(violations);

        summary.TotalCount.Should().Be(3);
        summary.CriticalCount.Should().Be(1);
        summary.HighCount.Should().Be(1);
        summary.MediumCount.Should().Be(1);
        summary.LowCount.Should().Be(0);
    }

    [TestMethod]
    public void GetSummary_IdentifiesTopViolatingTypes()
    {
        var violations = new[]
        {
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Domain,
                TargetLayer = ArchitectureLayer.Infrastructure,
                SourceComponent = "Order",
                TargetComponent = "DbContext",
                FilePath = "Order.cs",
                LineNumber = 10,
                Severity = ViolationSeverity.Critical,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.FieldDependency,
            },
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Domain,
                TargetLayer = ArchitectureLayer.Application,
                SourceComponent = "Order",
                TargetComponent = "OrderService",
                FilePath = "Order.cs",
                LineNumber = 15,
                Severity = ViolationSeverity.Critical,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.FieldDependency,
            },
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Domain,
                TargetLayer = ArchitectureLayer.Infrastructure,
                SourceComponent = "Customer",
                TargetComponent = "DbContext",
                FilePath = "Customer.cs",
                LineNumber = 10,
                Severity = ViolationSeverity.Critical,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.FieldDependency,
            },
        };

        var summary = this.detector.GetSummary(violations);

        summary.TopViolatingTypes.Should().HaveCount(2);
        summary.TopViolatingTypes[0].TypeName.Should().Be("Order");
        summary.TopViolatingTypes[0].Count.Should().Be(2);
    }

    [TestMethod]
    public void GroupBySeverity_GroupsCorrectly()
    {
        var violations = new[]
        {
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Domain,
                TargetLayer = ArchitectureLayer.Infrastructure,
                SourceComponent = "Order",
                TargetComponent = "DbContext",
                FilePath = "Order.cs",
                LineNumber = 10,
                Severity = ViolationSeverity.Critical,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.FieldDependency,
            },
            new LayerViolation
            {
                SourceLayer = ArchitectureLayer.Api,
                TargetLayer = ArchitectureLayer.Infrastructure,
                SourceComponent = "OrdersController",
                TargetComponent = "Repository",
                FilePath = "OrdersController.cs",
                LineNumber = 20,
                Severity = ViolationSeverity.Medium,
                Message = "test",
                Recommendation = "test",
                InteractionType = InteractionType.ConstructorInjection,
            },
        };

        var grouped = this.detector.GroupBySeverity(violations);

        grouped.Should().ContainKey(ViolationSeverity.Critical);
        grouped.Should().ContainKey(ViolationSeverity.Medium);
        grouped[ViolationSeverity.Critical].Should().HaveCount(1);
        grouped[ViolationSeverity.Medium].Should().HaveCount(1);
    }

    #endregion
}
