// -----------------------------------------------------------------------
// <copyright file="LayerInteractionAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using System.Linq;
using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static CodeIntelligence.Analyzers.LayerClassificationAnalyzer;

/// <summary>
/// Unit tests for <see cref="LayerInteractionAnalyzer"/>.
/// </summary>
[TestClass]
public class LayerInteractionAnalyzerTests
{
    private LayerInteractionAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new LayerInteractionAnalyzer();
    }

    #region Constructor Injection Tests

    [TestMethod]
    public void AnalyzeInteractions_ControllerInjectsService_DetectsApiToApplicationInteraction()
    {
        var code = @"
namespace MyApp.Controllers;
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    
    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "OrdersController.cs");

        results.Should().Contain(i => 
            i.SourceLayer == ArchitectureLayer.Api &&
            i.TargetType == "IOrderService" &&
            i.InteractionType == InteractionType.ConstructorInjection);
    }

    [TestMethod]
    public void AnalyzeInteractions_ServiceInjectsRepository_DetectsApplicationToInfrastructureInteraction()
    {
        var code = @"
namespace MyApp.Services;
public class OrderService
{
    private readonly IOrderRepository _repository;
    
    public OrderService(IOrderRepository repository)
    {
        _repository = repository;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "OrderService.cs");

        results.Should().Contain(i => 
            i.SourceLayer == ArchitectureLayer.Application &&
            i.TargetType == "IOrderRepository" &&
            i.InteractionType == InteractionType.ConstructorInjection);
    }

    [TestMethod]
    public void AnalyzeInteractions_MultipleInjections_DetectsAll()
    {
        var code = @"
namespace MyApp.Controllers;
public class OrdersController : ControllerBase
{
    public OrdersController(
        IOrderService orderService,
        ICustomerService customerService,
        ILogger logger)
    {
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "OrdersController.cs");

        results.Where(i => i.InteractionType == InteractionType.ConstructorInjection)
            .Should().HaveCountGreaterOrEqualTo(2);
    }

    #endregion

    #region Field Dependency Tests

    [TestMethod]
    public void AnalyzeInteractions_FieldDependency_DetectsCrossLayerDependency()
    {
        var code = @"
namespace MyApp.Controllers;
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "ProductsController.cs");

        results.Should().Contain(i => 
            i.InteractionType == InteractionType.FieldDependency &&
            i.TargetType == "IProductService");
    }

    #endregion

    #region Property Dependency Tests

    [TestMethod]
    public void AnalyzeInteractions_PropertyDependency_DetectsCrossLayerDependency()
    {
        var code = @"
namespace MyApp.Controllers;
public class SettingsController : ControllerBase
{
    public ISettingsService SettingsService { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "SettingsController.cs");

        results.Should().Contain(i => 
            i.InteractionType == InteractionType.PropertyDependency &&
            i.TargetType == "ISettingsService");
    }

    #endregion

    #region Inheritance Tests

    [TestMethod]
    public void AnalyzeInteractions_InheritanceFromDifferentLayer_DetectsInteraction()
    {
        // API controller injecting and using Repository interface (from Infrastructure via naming)
        var code = @"
namespace MyApp.Controllers;
[ApiController]
public class OrdersController : ControllerBase
{
    private readonly IOrderRepository _repo;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "OrdersController.cs");

        // API controller with field dependency to Infrastructure Repository = cross-layer
        results.Should().Contain(i =>
            i.InteractionType == InteractionType.FieldDependency &&
            i.TargetType == "IOrderRepository" &&
            i.SourceLayer == ArchitectureLayer.Api &&
            i.TargetLayer == ArchitectureLayer.Infrastructure);
    }

    [TestMethod]
    public void AnalyzeInteractions_InheritanceSameLayer_NotDetected()
    {
        // Both are in Infrastructure layer - should not create cross-layer interaction
        var code = @"
namespace MyApp.Repositories;
public class SqlOrderRepository : RepositoryBase
{
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "SqlOrderRepository.cs");

        // Same layer inheritance should not be detected as cross-layer interaction
        results.Where(i =>
            i.SourceLayer == ArchitectureLayer.Infrastructure &&
            i.TargetLayer == ArchitectureLayer.Infrastructure)
            .Should().BeEmpty();
    }

    #endregion

    #region Aggregation Tests

    [TestMethod]
    public void AggregateInteractions_GroupsByLayerPair()
    {
        var interactions = new[]
        {
            new LayerInteractionInfo
            {
                SourceType = "OrdersController",
                SourceLayer = ArchitectureLayer.Api,
                TargetType = "IOrderService",
                TargetLayer = ArchitectureLayer.Application,
                InteractionType = InteractionType.ConstructorInjection,
                FilePath = "test.cs",
                LineNumber = 1,
            },
            new LayerInteractionInfo
            {
                SourceType = "ProductsController",
                SourceLayer = ArchitectureLayer.Api,
                TargetType = "IProductService",
                TargetLayer = ArchitectureLayer.Application,
                InteractionType = InteractionType.ConstructorInjection,
                FilePath = "test.cs",
                LineNumber = 2,
            },
            new LayerInteractionInfo
            {
                SourceType = "OrderService",
                SourceLayer = ArchitectureLayer.Application,
                TargetType = "IOrderRepository",
                TargetLayer = ArchitectureLayer.Infrastructure,
                InteractionType = InteractionType.ConstructorInjection,
                FilePath = "test.cs",
                LineNumber = 3,
            },
        };

        var summaries = this.analyzer.AggregateInteractions(interactions);

        summaries.Should().HaveCount(2);
        summaries.Should().Contain(s => 
            s.SourceLayer == ArchitectureLayer.Api && 
            s.TargetLayer == ArchitectureLayer.Application &&
            s.Count == 2);
        summaries.Should().Contain(s => 
            s.SourceLayer == ArchitectureLayer.Application && 
            s.TargetLayer == ArchitectureLayer.Infrastructure &&
            s.Count == 1);
    }

    [TestMethod]
    public void AggregateInteractions_IncludesExamples()
    {
        var interactions = new[]
        {
            new LayerInteractionInfo
            {
                SourceType = "OrdersController",
                SourceLayer = ArchitectureLayer.Api,
                TargetType = "IOrderService",
                TargetLayer = ArchitectureLayer.Application,
                InteractionType = InteractionType.ConstructorInjection,
                FilePath = "test.cs",
                LineNumber = 1,
            },
        };

        var summaries = this.analyzer.AggregateInteractions(interactions);

        summaries.Should().HaveCount(1);
        summaries[0].Examples.Should().Contain("OrdersController -> IOrderService");
    }

    #endregion

    #region Type Layer Cache Tests

    [TestMethod]
    public void BuildTypeLayerCache_CachesTypeLayerMappings()
    {
        var code1 = @"
namespace MyApp.Controllers;
public class OrdersController : ControllerBase { }";

        var code2 = @"
namespace MyApp.Services;
public class OrderService { }";

        var trees = new[]
        {
            (CSharpSyntaxTree.ParseText(code1), "OrdersController.cs"),
            (CSharpSyntaxTree.ParseText(code2), "OrderService.cs"),
        };

        this.analyzer.BuildTypeLayerCache(trees);

        // Now interactions should be able to resolve types
        var code3 = @"
namespace MyApp.Controllers;
public class TestController : ControllerBase
{
    public TestController(OrderService service) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code3);
        var results = this.analyzer.AnalyzeInteractions(tree, "TestController.cs");

        results.Should().Contain(i => 
            i.TargetType == "OrderService" &&
            i.TargetLayer == ArchitectureLayer.Application);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public void AnalyzeInteractions_SameLayerDependency_NotDetected()
    {
        var code = @"
namespace MyApp.Services;
public class OrderService
{
    private readonly IInventoryService _inventoryService;
    
    public OrderService(IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "OrderService.cs");

        // Same layer dependencies (Application -> Application) should not be in cross-layer interactions
        results.Where(i => 
            i.SourceLayer == ArchitectureLayer.Application &&
            i.TargetLayer == ArchitectureLayer.Application)
            .Should().BeEmpty();
    }

    [TestMethod]
    public void AnalyzeInteractions_UnknownTypeLayer_NotDetected()
    {
        var code = @"
namespace MyApp.Controllers;
public class TestController : ControllerBase
{
    public TestController(ISomeUnknownType unknown) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.AnalyzeInteractions(tree, "TestController.cs");

        // Unknown layer types should not create interactions
        results.Where(i => i.TargetType == "ISomeUnknownType")
            .Should().BeEmpty();
    }

    #endregion
}
