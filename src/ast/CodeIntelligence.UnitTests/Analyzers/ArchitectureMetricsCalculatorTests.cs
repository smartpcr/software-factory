// -----------------------------------------------------------------------
// <copyright file="ArchitectureMetricsCalculatorTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using System.Collections.Generic;
using System.Linq;
using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static CodeIntelligence.Analyzers.LayerClassificationAnalyzer;

/// <summary>
/// Unit tests for <see cref="ArchitectureMetricsCalculator"/>.
/// </summary>
[TestClass]
public class ArchitectureMetricsCalculatorTests
{
    private ArchitectureMetricsCalculator calculator = null!;

    [TestInitialize]
    public void Setup()
    {
        this.calculator = new ArchitectureMetricsCalculator();
    }

    #region Layer Summaries

    [TestMethod]
    public void CalculateLayerSummaries_CalculatesComponentCounts()
    {
        var classifications = new[]
        {
            CreateClassification("OrdersController", ArchitectureLayer.Api),
            CreateClassification("ProductsController", ArchitectureLayer.Api),
            CreateClassification("OrderService", ArchitectureLayer.Application),
            CreateClassification("Order", ArchitectureLayer.Domain),
            CreateClassification("OrderRepository", ArchitectureLayer.Infrastructure),
        };

        var interactions = Enumerable.Empty<LayerInteractionInfo>();
        var summaries = this.calculator.CalculateLayerSummaries(classifications, interactions);

        summaries.Should().ContainKey(ArchitectureLayer.Api);
        summaries[ArchitectureLayer.Api].ComponentCount.Should().Be(2);
        summaries[ArchitectureLayer.Application].ComponentCount.Should().Be(1);
        summaries[ArchitectureLayer.Domain].ComponentCount.Should().Be(1);
        summaries[ArchitectureLayer.Infrastructure].ComponentCount.Should().Be(1);
    }

    [TestMethod]
    public void CalculateLayerSummaries_CalculatesClassAndInterfaceCounts()
    {
        var classifications = new[]
        {
            CreateClassification("IOrderService", ArchitectureLayer.Application, isInterface: true),
            CreateClassification("OrderService", ArchitectureLayer.Application),
            CreateClassification("BaseService", ArchitectureLayer.Application, isAbstract: true),
        };

        var interactions = Enumerable.Empty<LayerInteractionInfo>();
        var summaries = this.calculator.CalculateLayerSummaries(classifications, interactions);

        summaries[ArchitectureLayer.Application].InterfaceCount.Should().Be(1);
        summaries[ArchitectureLayer.Application].ClassCount.Should().Be(1); // Only non-interface, non-abstract
        summaries[ArchitectureLayer.Application].AbstractCount.Should().Be(1);
    }

    [TestMethod]
    public void CalculateLayerSummaries_CollectsNamespaces()
    {
        var classifications = new[]
        {
            CreateClassification("OrdersController", ArchitectureLayer.Api, "MyApp.Controllers"),
            CreateClassification("ProductsController", ArchitectureLayer.Api, "MyApp.Controllers"),
            CreateClassification("UsersController", ArchitectureLayer.Api, "MyApp.Controllers.V2"),
        };

        var interactions = Enumerable.Empty<LayerInteractionInfo>();
        var summaries = this.calculator.CalculateLayerSummaries(classifications, interactions);

        summaries[ArchitectureLayer.Api].Namespaces.Should().HaveCount(2);
        summaries[ArchitectureLayer.Api].Namespaces.Should().Contain("MyApp.Controllers");
        summaries[ArchitectureLayer.Api].Namespaces.Should().Contain("MyApp.Controllers.V2");
    }

    [TestMethod]
    public void CalculateLayerSummaries_CalculatesDependencyCounts()
    {
        var classifications = new[]
        {
            CreateClassification("OrdersController", ArchitectureLayer.Api),
            CreateClassification("OrderService", ArchitectureLayer.Application),
        };

        var interactions = new[]
        {
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
        };

        var summaries = this.calculator.CalculateLayerSummaries(classifications, interactions);

        summaries[ArchitectureLayer.Api].OutgoingDependencies.Should().Be(2);
        summaries[ArchitectureLayer.Application].IncomingDependencies.Should().Be(2);
    }

    #endregion

    #region Coupling Score

    [TestMethod]
    public void CalculateCouplingScore_NoDependencies_ReturnsZero()
    {
        var classifications = new[]
        {
            CreateClassification("Class1", ArchitectureLayer.Api),
            CreateClassification("Class2", ArchitectureLayer.Application),
        };

        var interactions = Enumerable.Empty<LayerInteractionInfo>();
        var score = this.calculator.CalculateCouplingScore(interactions, classifications);

        score.Should().Be(0f);
    }

    [TestMethod]
    public void CalculateCouplingScore_SomeDependencies_ReturnsLowScore()
    {
        var classifications = new[]
        {
            CreateClassification("Controller1", ArchitectureLayer.Api),
            CreateClassification("Controller2", ArchitectureLayer.Api),
            CreateClassification("Service1", ArchitectureLayer.Application),
            CreateClassification("Service2", ArchitectureLayer.Application),
        };

        var interactions = new[]
        {
            new LayerInteractionInfo
            {
                SourceType = "Controller1",
                SourceLayer = ArchitectureLayer.Api,
                TargetType = "Service1",
                TargetLayer = ArchitectureLayer.Application,
                InteractionType = InteractionType.ConstructorInjection,
                FilePath = "test.cs",
                LineNumber = 1,
            },
        };

        var score = this.calculator.CalculateCouplingScore(interactions, classifications);

        score.Should().BeGreaterThan(0f);
        score.Should().BeLessThan(0.5f); // Low coupling
    }

    #endregion

    #region Cohesion Score

    [TestMethod]
    public void CalculateCohesionScore_NoInteractions_ReturnsPerfect()
    {
        var classifications = new[]
        {
            CreateClassification("Class1", ArchitectureLayer.Api),
        };

        var interactions = Enumerable.Empty<LayerInteractionInfo>();
        var score = this.calculator.CalculateCohesionScore(classifications, interactions);

        score.Should().Be(1f);
    }

    [TestMethod]
    public void CalculateCohesionScore_ManyCrossLayerInteractions_ReturnsLowerScore()
    {
        var classifications = new[]
        {
            CreateClassification("Controller1", ArchitectureLayer.Api),
            CreateClassification("Service1", ArchitectureLayer.Application),
        };

        var interactions = new[]
        {
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
        };

        var score = this.calculator.CalculateCohesionScore(classifications, interactions);

        score.Should().BeLessThan(1f);
    }

    #endregion

    #region Layer Adherence

    [TestMethod]
    public void CalculateLayerAdherence_NoViolations_Returns100Percent()
    {
        var interactions = new[]
        {
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Application, ArchitectureLayer.Domain),
        };

        var violations = Enumerable.Empty<LayerViolation>();
        var adherence = this.calculator.CalculateLayerAdherence(interactions, violations);

        adherence.Should().Be(1f);
    }

    [TestMethod]
    public void CalculateLayerAdherence_SomeViolations_ReturnsCorrectPercentage()
    {
        var interactions = new[]
        {
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Infrastructure), // violation
            CreateInteraction(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure), // violation
        };

        var violations = new[]
        {
            CreateViolation(ArchitectureLayer.Api, ArchitectureLayer.Infrastructure),
            CreateViolation(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure),
        };

        var adherence = this.calculator.CalculateLayerAdherence(interactions, violations);

        adherence.Should().Be(0.5f); // 2 valid out of 4
    }

    [TestMethod]
    public void CalculateLayerAdherence_NoInteractions_Returns100Percent()
    {
        var interactions = Enumerable.Empty<LayerInteractionInfo>();
        var violations = Enumerable.Empty<LayerViolation>();
        var adherence = this.calculator.CalculateLayerAdherence(interactions, violations);

        adherence.Should().Be(1f);
    }

    #endregion

    #region Interaction Matrix

    [TestMethod]
    public void BuildInteractionMatrix_CountsInteractions()
    {
        var interactions = new[]
        {
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Application, ArchitectureLayer.Domain),
            CreateInteraction(ArchitectureLayer.Application, ArchitectureLayer.Infrastructure),
        };

        var matrix = this.calculator.BuildInteractionMatrix(interactions);

        matrix[(ArchitectureLayer.Api, ArchitectureLayer.Application)].Should().Be(2);
        matrix[(ArchitectureLayer.Application, ArchitectureLayer.Domain)].Should().Be(1);
        matrix[(ArchitectureLayer.Application, ArchitectureLayer.Infrastructure)].Should().Be(1);
    }

    [TestMethod]
    public void FormatInteractionMatrix_ProducesTextOutput()
    {
        var matrix = new Dictionary<(ArchitectureLayer, ArchitectureLayer), int>
        {
            [(ArchitectureLayer.Api, ArchitectureLayer.Application)] = 5,
            [(ArchitectureLayer.Application, ArchitectureLayer.Domain)] = 10,
        };

        var output = this.calculator.FormatInteractionMatrix(matrix);

        output.Should().NotBeNullOrEmpty();
        output.Should().Contain("5");
        output.Should().Contain("10");
    }

    #endregion

    #region Full Metrics Calculation

    [TestMethod]
    public void CalculateMetrics_ReturnsComprehensiveMetrics()
    {
        var classifications = new[]
        {
            CreateClassification("OrdersController", ArchitectureLayer.Api),
            CreateClassification("ProductsController", ArchitectureLayer.Api),
            CreateClassification("OrderService", ArchitectureLayer.Application),
            CreateClassification("Order", ArchitectureLayer.Domain),
            CreateClassification("OrderRepository", ArchitectureLayer.Infrastructure),
        };

        var interactions = new[]
        {
            CreateInteraction(ArchitectureLayer.Api, ArchitectureLayer.Application),
            CreateInteraction(ArchitectureLayer.Application, ArchitectureLayer.Domain),
            CreateInteraction(ArchitectureLayer.Application, ArchitectureLayer.Infrastructure),
        };

        var violations = Enumerable.Empty<LayerViolation>();

        var metrics = this.calculator.CalculateMetrics(classifications, interactions, violations);

        metrics.TotalLayers.Should().Be(4);
        metrics.TotalComponents.Should().Be(5);
        metrics.TotalInteractions.Should().Be(3);
        metrics.ViolationCount.Should().Be(0);
        metrics.LayerAdherence.Should().Be(1f);
        metrics.LayerSummaries.Should().HaveCount(4);
        metrics.InteractionMatrix.Should().HaveCount(3);
    }

    [TestMethod]
    public void CalculateMetrics_WithViolations_ReportsCorrectly()
    {
        var classifications = new[]
        {
            CreateClassification("Order", ArchitectureLayer.Domain),
            CreateClassification("DbContext", ArchitectureLayer.Infrastructure),
        };

        var interactions = new[]
        {
            CreateInteraction(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure),
        };

        var violations = new[]
        {
            CreateViolation(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure),
        };

        var metrics = this.calculator.CalculateMetrics(classifications, interactions, violations);

        metrics.ViolationCount.Should().Be(1);
        metrics.LayerAdherence.Should().Be(0f);
    }

    #endregion

    #region Helper Methods

    private static LayerClassification CreateClassification(
        string typeName,
        ArchitectureLayer layer,
        string? ns = null,
        bool isInterface = false,
        bool isAbstract = false)
    {
        return new LayerClassification
        {
            TypeName = typeName,
            Namespace = ns ?? "MyApp",
            Layer = layer,
            FilePath = $"{typeName}.cs",
            LineNumber = 1,
            Confidence = 0.8f,
            ClassificationReason = "Test",
            IsInterface = isInterface,
            IsAbstract = isAbstract,
        };
    }

    private static LayerInteractionInfo CreateInteraction(
        ArchitectureLayer sourceLayer,
        ArchitectureLayer targetLayer)
    {
        return new LayerInteractionInfo
        {
            SourceType = $"{sourceLayer}Type",
            SourceLayer = sourceLayer,
            TargetType = $"{targetLayer}Type",
            TargetLayer = targetLayer,
            InteractionType = InteractionType.ConstructorInjection,
            FilePath = "test.cs",
            LineNumber = 1,
        };
    }

    private static LayerViolation CreateViolation(
        ArchitectureLayer sourceLayer,
        ArchitectureLayer targetLayer)
    {
        return new LayerViolation
        {
            SourceLayer = sourceLayer,
            TargetLayer = targetLayer,
            SourceComponent = $"{sourceLayer}Component",
            TargetComponent = $"{targetLayer}Component",
            FilePath = "test.cs",
            LineNumber = 1,
            Severity = ViolationSeverity.High,
            Message = "Test violation",
            Recommendation = "Test recommendation",
            InteractionType = InteractionType.FieldDependency,
        };
    }

    #endregion
}
