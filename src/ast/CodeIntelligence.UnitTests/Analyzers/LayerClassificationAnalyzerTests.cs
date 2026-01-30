// -----------------------------------------------------------------------
// <copyright file="LayerClassificationAnalyzerTests.cs" company="Microsoft">
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
/// Unit tests for <see cref="LayerClassificationAnalyzer"/>.
/// </summary>
[TestClass]
public class LayerClassificationAnalyzerTests
{
    private LayerClassificationAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new LayerClassificationAnalyzer();
    }

    #region Presentation Layer Detection

    [TestMethod]
    public void ClassifyTypes_BlazorComponent_DetectsPresentationLayer()
    {
        var code = @"
namespace MyApp.Components;
public class MyComponent : ComponentBase
{
    public string Title { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "MyComponent.razor.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Presentation);
        results[0].TypeName.Should().Be("MyComponent");
    }

    [TestMethod]
    public void ClassifyTypes_RazorPageModel_DetectsPresentationLayer()
    {
        var code = @"
namespace MyApp.Pages;
public class IndexModel : PageModel
{
    public void OnGet() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "Index.cshtml.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Presentation);
    }

    [TestMethod]
    public void ClassifyTypes_ViewsNamespace_DetectsPresentationLayer()
    {
        var code = @"
namespace MyApp.Views.Home;
public class HomeViewModel
{
    public string Message { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "HomeViewModel.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Presentation);
    }

    #endregion

    #region API Layer Detection

    [TestMethod]
    public void ClassifyTypes_ControllerBase_DetectsApiLayer()
    {
        var code = @"
namespace MyApp.Controllers;
public class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult GetOrders() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "OrdersController.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Api);
        results[0].TypeName.Should().Be("OrdersController");
    }

    [TestMethod]
    public void ClassifyTypes_ApiControllerAttribute_DetectsApiLayer()
    {
        var code = @"
namespace MyApp.Api;
[ApiController]
public class ProductsController
{
    public IActionResult Get() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "ProductsController.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Api);
    }

    [TestMethod]
    public void ClassifyTypes_ControllersNamespace_DetectsApiLayer()
    {
        var code = @"
namespace MyApp.WebApi.Controllers;
public class UsersController : Controller
{
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "UsersController.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Api);
    }

    [TestMethod]
    public void ClassifyTypes_SignalRHub_DetectsApiLayer()
    {
        var code = @"
namespace MyApp.Hubs;
public class ChatHub : Hub
{
    public async Task SendMessage(string message) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "ChatHub.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Api);
    }

    #endregion

    #region Application Layer Detection

    [TestMethod]
    public void ClassifyTypes_ServiceClass_DetectsApplicationLayer()
    {
        var code = @"
namespace MyApp.Services;
public class OrderService
{
    public void ProcessOrder(int orderId) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "OrderService.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Application);
    }

    [TestMethod]
    public void ClassifyTypes_MediatRHandler_DetectsApplicationLayer()
    {
        var code = @"
namespace MyApp.Handlers;
public class CreateOrderHandler : IRequestHandler<CreateOrderCommand, OrderResult>
{
    public Task<OrderResult> Handle(CreateOrderCommand request, CancellationToken ct) => default;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "CreateOrderHandler.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Application);
    }

    [TestMethod]
    public void ClassifyTypes_UseCaseClass_DetectsApplicationLayer()
    {
        var code = @"
namespace MyApp.UseCases;
public class ProcessPaymentUseCase
{
    public void Execute(PaymentRequest request) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "ProcessPaymentUseCase.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Application);
    }

    [TestMethod]
    public void ClassifyTypes_Orchestrator_DetectsApplicationLayer()
    {
        var code = @"
namespace MyApp.Application;
public class OrderOrchestrator
{
    public void Run() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "OrderOrchestrator.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Application);
    }

    #endregion

    #region Domain Layer Detection

    [TestMethod]
    public void ClassifyTypes_EntityBase_DetectsDomainLayer()
    {
        var code = @"
namespace MyApp.Domain;
public class Order : Entity
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "Order.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Domain);
    }

    [TestMethod]
    public void ClassifyTypes_AggregateRoot_DetectsDomainLayer()
    {
        var code = @"
namespace MyApp.Aggregates;
public class Customer : AggregateRoot
{
    public List<Order> Orders { get; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "Customer.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Domain);
    }

    [TestMethod]
    public void ClassifyTypes_ValueObject_DetectsDomainLayer()
    {
        var code = @"
namespace MyApp.Domain.ValueObjects;
public class Address : ValueObject
{
    public string Street { get; }
    public string City { get; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "Address.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Domain);
    }

    [TestMethod]
    public void ClassifyTypes_DomainEvent_DetectsDomainLayer()
    {
        var code = @"
namespace MyApp.DomainEvents;
public class OrderCreatedEvent : IDomainEvent
{
    public int OrderId { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "OrderCreatedEvent.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Domain);
    }

    #endregion

    #region Infrastructure Layer Detection

    [TestMethod]
    public void ClassifyTypes_Repository_DetectsInfrastructureLayer()
    {
        var code = @"
namespace MyApp.Repositories;
public class OrderRepository : IOrderRepository
{
    public Order GetById(int id) => default;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "OrderRepository.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Infrastructure);
    }

    [TestMethod]
    public void ClassifyTypes_DbContext_DetectsInfrastructureLayer()
    {
        var code = @"
namespace MyApp.Data;
public class AppDbContext : DbContext
{
    public DbSet<Order> Orders { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "AppDbContext.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Infrastructure);
    }

    [TestMethod]
    public void ClassifyTypes_ExternalClient_DetectsInfrastructureLayer()
    {
        var code = @"
namespace MyApp.External;
public class PaymentGatewayClient
{
    private readonly HttpClient _httpClient;
    public Task<PaymentResult> ProcessAsync(Payment p) => default;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "PaymentGatewayClient.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Infrastructure);
    }

    [TestMethod]
    public void ClassifyTypes_Gateway_DetectsInfrastructureLayer()
    {
        var code = @"
namespace MyApp.Infrastructure;
public class EmailGateway
{
    public void Send(Email email) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "EmailGateway.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Infrastructure);
    }

    #endregion

    #region CrossCutting Layer Detection

    [TestMethod]
    public void ClassifyTypes_LoggerImplementation_DetectsCrossCuttingLayer()
    {
        var code = @"
namespace MyApp.Common;
public class CustomLogger : ILogger
{
    public void Log(string message) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "CustomLogger.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.CrossCutting);
    }

    [TestMethod]
    public void ClassifyTypes_HelperClass_DetectsCrossCuttingLayer()
    {
        var code = @"
namespace MyApp.Utilities;
public class StringHelper
{
    public static string Sanitize(string input) => input;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "StringHelper.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.CrossCutting);
    }

    [TestMethod]
    public void ClassifyTypes_ExtensionsClass_DetectsCrossCuttingLayer()
    {
        var code = @"
namespace MyApp.Shared;
public static class DateTimeExtensions
{
    public static bool IsWeekend(this DateTime dt) => false;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "DateTimeExtensions.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.CrossCutting);
    }

    #endregion

    #region Namespace Pattern Detection

    [TestMethod]
    public void ClassifyTypes_NestedNamespace_CorrectlyClassifies()
    {
        var code = @"
namespace MyApp.WebApi.Controllers.V2;
public class OrdersV2Controller : ControllerBase
{
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "OrdersV2Controller.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Api);
    }

    [TestMethod]
    public void ClassifyTypes_DomainSubnamespace_CorrectlyClassifies()
    {
        var code = @"
namespace MyApp.Domain.Entities.Orders;
public class OrderLine : Entity
{
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "OrderLine.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Domain);
    }

    #endregion

    #region Confidence Calculation

    [TestMethod]
    public void ClassifyTypes_MultipleSignals_HighConfidence()
    {
        // Controller that inherits ControllerBase AND has ApiController attribute AND is in Controllers namespace
        var code = @"
namespace MyApp.Controllers;
[ApiController]
public class WeatherController : ControllerBase
{
    public IActionResult Get() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "WeatherController.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Api);
        results[0].Confidence.Should().BeGreaterThan(0.5f);
    }

    [TestMethod]
    public void ClassifyTypes_SingleSignal_LowerConfidence()
    {
        // Only naming convention, no inheritance or attributes
        var code = @"
namespace MyApp.SomeNamespace;
public class DataService
{
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "DataService.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Application);
        results[0].Confidence.Should().BeLessThanOrEqualTo(0.5f);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public void ClassifyTypes_Interface_DetectsCorrectLayer()
    {
        var code = @"
namespace MyApp.Domain;
public interface IOrderRepository
{
    Order GetById(int id);
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "IOrderRepository.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Domain);
        results[0].IsInterface.Should().BeTrue();
    }

    [TestMethod]
    public void ClassifyTypes_AbstractClass_DetectsCorrectLayer()
    {
        var code = @"
namespace MyApp.Services;
public abstract class BaseService
{
    protected abstract void Execute();
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "BaseService.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Application);
        results[0].IsAbstract.Should().BeTrue();
    }

    [TestMethod]
    public void ClassifyTypes_UnknownPattern_ReturnsUnknown()
    {
        var code = @"
namespace MyApp.Something;
public class MyClass
{
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "MyClass.cs");

        results.Should().HaveCount(1);
        results[0].Layer.Should().Be(ArchitectureLayer.Unknown);
    }

    [TestMethod]
    public void ClassifyTypes_HostedServiceNotApplicationLayer()
    {
        // BackgroundService/HostedService should NOT be classified as Application layer service
        var code = @"
namespace MyApp.Services;
public class EmailBackgroundService : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var results = this.analyzer.ClassifyTypes(tree, "EmailBackgroundService.cs");

        results.Should().HaveCount(1);
        // Should not be classified as Application layer due to BackgroundService convention
        results[0].Layer.Should().NotBe(ArchitectureLayer.Application);
    }

    #endregion
}
