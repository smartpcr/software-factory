// -----------------------------------------------------------------------
// <copyright file="ObservabilityAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Unit tests for ObservabilityAnalyzer.
/// </summary>
[TestClass]
public class ObservabilityAnalyzerTests
{
    private ObservabilityAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new ObservabilityAnalyzer();
    }

    #region ILogger Logging Tests

    [TestMethod]
    public void AnalyzeLogging_ILoggerLogInformation_DetectsLog()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger<OrderService> _logger;

    public void ProcessOrder(int orderId)
    {
        _logger.LogInformation(""Processing order {OrderId}"", orderId);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].LoggerType.Should().Be(LoggerType.ILogger);
        logs[0].Level.Should().Be(LogLevel.Information);
        logs[0].MessageTemplate.Should().Be("Processing order {OrderId}");
        logs[0].Parameters.Should().Contain("OrderId");
    }

    [TestMethod]
    public void AnalyzeLogging_ILoggerLogError_DetectsError()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger _logger;

    public void ProcessOrder()
    {
        _logger.LogError(""Order processing failed"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be(LogLevel.Error);
    }

    [TestMethod]
    public void AnalyzeLogging_ILoggerLogWarning_DetectsWarning()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger _logger;

    public void ProcessOrder()
    {
        _logger.LogWarning(""Low stock warning for product {ProductId}"", productId);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be(LogLevel.Warning);
    }

    [TestMethod]
    public void AnalyzeLogging_ILoggerLogDebug_DetectsDebug()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger _logger;

    public void ProcessOrder()
    {
        _logger.LogDebug(""Debug info: {Details}"", details);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be(LogLevel.Debug);
    }

    [TestMethod]
    public void AnalyzeLogging_ILoggerLogCritical_DetectsCritical()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger _logger;

    public void ProcessOrder()
    {
        _logger.LogCritical(""System failure!"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be(LogLevel.Critical);
    }

    [TestMethod]
    public void AnalyzeLogging_ILoggerLogTrace_DetectsTrace()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger _logger;

    public void ProcessOrder()
    {
        _logger.LogTrace(""Entering method"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be(LogLevel.Trace);
    }

    [TestMethod]
    public void AnalyzeLogging_MultipleParameters_ExtractsAll()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger _logger;

    public void ProcessOrder()
    {
        _logger.LogInformation(""Order {OrderId} for customer {CustomerId} completed in {Duration}ms"", orderId, customerId, duration);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Parameters.Should().HaveCount(3);
        logs[0].Parameters.Should().Contain("OrderId");
        logs[0].Parameters.Should().Contain("CustomerId");
        logs[0].Parameters.Should().Contain("Duration");
    }

    #endregion

    #region Serilog Logging Tests

    [TestMethod]
    public void AnalyzeLogging_SerilogInformation_DetectsLog()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        Log.Information(""Processing order {OrderId}"", orderId);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].LoggerType.Should().Be(LoggerType.Serilog);
        logs[0].Level.Should().Be(LogLevel.Information);
    }

    [TestMethod]
    public void AnalyzeLogging_SerilogError_DetectsError()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        Log.Error(""Order failed: {Message}"", ex.Message);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].LoggerType.Should().Be(LoggerType.Serilog);
        logs[0].Level.Should().Be(LogLevel.Error);
    }

    [TestMethod]
    public void AnalyzeLogging_SerilogWarning_DetectsWarning()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        Log.Warning(""Slow response: {Duration}ms"", duration);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be(LogLevel.Warning);
    }

    [TestMethod]
    public void AnalyzeLogging_SerilogFatal_DetectsCritical()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        Log.Fatal(""System crash!"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be(LogLevel.Critical);
    }

    #endregion

    #region NLog Logging Tests

    [TestMethod]
    public void AnalyzeLogging_NLogInfo_DetectsLog()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    public void ProcessOrder()
    {
        _logger.Info(""Processing order"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].LoggerType.Should().Be(LoggerType.NLog);
        logs[0].Level.Should().Be(LogLevel.Information);
    }

    [TestMethod]
    public void AnalyzeLogging_NLogError_DetectsError()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

    public void ProcessOrder()
    {
        logger.Error(""Order failed"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");

        // Assert
        logs.Should().ContainSingle();
        logs[0].LoggerType.Should().Be(LoggerType.NLog);
        logs[0].Level.Should().Be(LogLevel.Error);
    }

    #endregion

    #region OTEL Metrics Tests

    [TestMethod]
    public void AnalyzeMetrics_MeterCreation_DetectsMeter()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Meter _meter = new Meter(""MyApp.Orders"", ""1.0.0"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().ContainSingle();
        metrics[0].MetricType.Should().Be(MetricType.Meter);
        metrics[0].MetricName.Should().Be("MyApp.Orders");
    }

    [TestMethod]
    public void AnalyzeMetrics_CreateCounter_DetectsCounter()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Meter _meter = new Meter(""MyApp.Orders"");
    private static readonly Counter<long> _ordersCounter = _meter.CreateCounter<long>(""orders.processed"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().Contain(m => m.MetricType == MetricType.Counter);
        var counter = metrics.First(m => m.MetricType == MetricType.Counter);
        counter.MetricName.Should().Be("orders.processed");
        counter.Technology.Should().Be(MetricTechnology.OpenTelemetry);
    }

    [TestMethod]
    public void AnalyzeMetrics_CreateHistogram_DetectsHistogram()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Meter _meter = new Meter(""MyApp.Orders"");
    private static readonly Histogram<double> _durationHistogram = _meter.CreateHistogram<double>(""order.duration"", unit: ""ms"", description: ""Order processing duration"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().Contain(m => m.MetricType == MetricType.Histogram);
        var histogram = metrics.First(m => m.MetricType == MetricType.Histogram);
        histogram.MetricName.Should().Be("order.duration");
        histogram.Unit.Should().Be("ms");
        histogram.Description.Should().Be("Order processing duration");
    }

    [TestMethod]
    public void AnalyzeMetrics_CreateUpDownCounter_DetectsUpDownCounter()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Meter _meter = new Meter(""MyApp.Orders"");
    private static readonly UpDownCounter<int> _pendingOrders = _meter.CreateUpDownCounter<int>(""orders.pending"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().Contain(m => m.MetricType == MetricType.UpDownCounter);
    }

    [TestMethod]
    public void AnalyzeMetrics_CreateObservableGauge_DetectsObservable()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Meter _meter = new Meter(""MyApp.Orders"");
    
    public void Setup()
    {
        _meter.CreateObservableGauge(""memory.usage"", () => GetMemoryUsage());
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().Contain(m => m.MetricType == MetricType.ObservableGauge);
    }

    #endregion

    #region Prometheus Metrics Tests

    [TestMethod]
    public void AnalyzeMetrics_PrometheusCounter_DetectsCounter()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Counter _ordersCounter = Metrics.CreateCounter(""orders_total"", ""Total orders processed"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().ContainSingle();
        metrics[0].MetricType.Should().Be(MetricType.Counter);
        metrics[0].Technology.Should().Be(MetricTechnology.Prometheus);
        metrics[0].MetricName.Should().Be("orders_total");
    }

    [TestMethod]
    public void AnalyzeMetrics_PrometheusGauge_DetectsGauge()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Gauge _pendingOrders = Metrics.CreateGauge(""orders_pending"", ""Pending orders"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().ContainSingle();
        metrics[0].MetricType.Should().Be(MetricType.Gauge);
        metrics[0].Technology.Should().Be(MetricTechnology.Prometheus);
    }

    [TestMethod]
    public void AnalyzeMetrics_PrometheusHistogram_DetectsHistogram()
    {
        // Arrange
        var code = @"
public class OrderMetrics
{
    private static readonly Histogram _duration = Metrics.CreateHistogram(""order_duration_seconds"", ""Order duration"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderMetrics.cs");

        // Assert
        metrics.Should().ContainSingle();
        metrics[0].MetricType.Should().Be(MetricType.Histogram);
    }

    #endregion

    #region Activity Tracing Tests

    [TestMethod]
    public void AnalyzeTracing_ActivitySourceCreation_DetectsSource()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private static readonly ActivitySource _activitySource = new ActivitySource(""MyApp.Orders"");
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var spans = this.analyzer.AnalyzeTracing(tree, "OrderService.cs");

        // Assert
        spans.Should().ContainSingle();
        spans[0].SpanName.Should().Be("MyApp.Orders");
        spans[0].IsSourceDefinition.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeTracing_StartActivity_DetectsSpan()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private static readonly ActivitySource _activitySource = new ActivitySource(""MyApp.Orders"");

    public void ProcessOrder()
    {
        using var activity = _activitySource.StartActivity(""ProcessOrder"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var spans = this.analyzer.AnalyzeTracing(tree, "OrderService.cs");

        // Assert
        spans.Should().Contain(s => s.SpanName == "ProcessOrder" && !s.IsSourceDefinition);
    }

    [TestMethod]
    public void AnalyzeTracing_StartActivityWithKind_DetectsKind()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private static readonly ActivitySource _activitySource = new ActivitySource(""MyApp.Orders"");

    public void ProcessOrder()
    {
        using var activity = _activitySource.StartActivity(""ProcessOrder"", ActivityKind.Server);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var spans = this.analyzer.AnalyzeTracing(tree, "OrderService.cs");

        // Assert
        spans.Should().Contain(s => s.SpanName == "ProcessOrder" && s.Kind == SpanKind.Server);
    }

    [TestMethod]
    public void AnalyzeTracing_StartActivityClient_DetectsClientKind()
    {
        // Arrange
        var code = @"
public class HttpClientWrapper
{
    private static readonly ActivitySource Source = new ActivitySource(""MyApp.Http"");

    public async Task<HttpResponseMessage> SendAsync()
    {
        using var activity = Source.StartActivity(""HttpRequest"", ActivityKind.Client);
        return await client.SendAsync(request);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var spans = this.analyzer.AnalyzeTracing(tree, "HttpClientWrapper.cs");

        // Assert
        spans.Should().Contain(s => s.Kind == SpanKind.Client);
    }

    #endregion

    #region ETW EventSource Tests

    [TestMethod]
    public void AnalyzeEtwEvents_EventSourceClass_DetectsEvents()
    {
        // Arrange
        var code = @"
[EventSource(Name = ""MyApp-OrderService"")]
public sealed class OrderEventSource : EventSource
{
    [Event(1, Level = EventLevel.Informational, Message = ""Order {0} received"")]
    public void OrderReceived(int orderId)
    {
        WriteEvent(1, orderId);
    }

    [Event(2, Level = EventLevel.Error, Message = ""Order {0} failed: {1}"")]
    public void OrderFailed(int orderId, string error)
    {
        WriteEvent(2, orderId, error);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeEtwEvents(tree, "OrderEventSource.cs");

        // Assert
        events.Should().HaveCount(2);
        events.Should().Contain(e => e.EventName == "OrderReceived" && e.EventId == 1);
        events.Should().Contain(e => e.EventName == "OrderFailed" && e.EventId == 2);
    }

    [TestMethod]
    public void AnalyzeEtwEvents_EventWithLevel_DetectsLevel()
    {
        // Arrange
        var code = @"
[EventSource(Name = ""MyApp-OrderService"")]
public class OrderEventSource : EventSource
{
    [Event(1, Level = EventLevel.Warning)]
    public void LowStock(string productId)
    {
        WriteEvent(1, productId);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeEtwEvents(tree, "OrderEventSource.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].Level.Should().Be("Warning");
    }

    [TestMethod]
    public void AnalyzeEtwEvents_EventWithMessage_DetectsMessage()
    {
        // Arrange
        var code = @"
[EventSource(Name = ""MyApp-OrderService"")]
public class OrderEventSource : EventSource
{
    [Event(1, Message = ""Processing order {0}"")]
    public void ProcessingOrder(int orderId)
    {
        WriteEvent(1, orderId);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeEtwEvents(tree, "OrderEventSource.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].Message.Should().Be("Processing order {0}");
    }

    [TestMethod]
    public void AnalyzeEtwEvents_EventParameters_ExtractsParameters()
    {
        // Arrange
        var code = @"
[EventSource(Name = ""MyApp"")]
public class MyEventSource : EventSource
{
    [Event(1)]
    public void OrderProcessed(int orderId, string customerId, double amount)
    {
        WriteEvent(1, orderId, customerId, amount);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeEtwEvents(tree, "MyEventSource.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].Parameters.Should().HaveCount(3);
        events[0].Parameters.Should().Contain(p => p.Name == "orderId" && p.Type == "int");
        events[0].Parameters.Should().Contain(p => p.Name == "customerId" && p.Type == "string");
        events[0].Parameters.Should().Contain(p => p.Name == "amount" && p.Type == "double");
    }

    [TestMethod]
    public void AnalyzeEtwEvents_CustomEventSourceName_UsesAttribute()
    {
        // Arrange
        var code = @"
[EventSource(Name = ""Custom-EventSource-Name"")]
public class MyEventSource : EventSource
{
    [Event(1)]
    public void TestEvent()
    {
        WriteEvent(1);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeEtwEvents(tree, "MyEventSource.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].EventSourceName.Should().Be("Custom-EventSource-Name");
    }

    #endregion

    #region EventCounter Tests

    [TestMethod]
    public void AnalyzeEventCounters_EventCounter_DetectsCounter()
    {
        // Arrange
        var code = @"
public class MyEventSource : EventSource
{
    private EventCounter _requestCounter;

    public MyEventSource()
    {
        _requestCounter = new EventCounter(""request-count"", this)
        {
            DisplayName = ""Request Count""
        };
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var counters = this.analyzer.AnalyzeEventCounters(tree, "MyEventSource.cs");

        // Assert
        counters.Should().ContainSingle();
        counters[0].CounterType.Should().Be(EventCounterType.EventCounter);
        counters[0].CounterName.Should().Be("request-count");
        counters[0].DisplayName.Should().Be("Request Count");
    }

    [TestMethod]
    public void AnalyzeEventCounters_IncrementingEventCounter_DetectsType()
    {
        // Arrange
        var code = @"
public class MyEventSource : EventSource
{
    private IncrementingEventCounter _totalRequests;

    public MyEventSource()
    {
        _totalRequests = new IncrementingEventCounter(""total-requests"", this)
        {
            DisplayName = ""Total Requests"",
            DisplayUnits = ""requests""
        };
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var counters = this.analyzer.AnalyzeEventCounters(tree, "MyEventSource.cs");

        // Assert
        counters.Should().ContainSingle();
        counters[0].CounterType.Should().Be(EventCounterType.IncrementingEventCounter);
        counters[0].DisplayUnits.Should().Be("requests");
    }

    [TestMethod]
    public void AnalyzeEventCounters_PollingCounter_DetectsType()
    {
        // Arrange
        var code = @"
public class MyEventSource : EventSource
{
    private PollingCounter _memoryUsage;

    public MyEventSource()
    {
        _memoryUsage = new PollingCounter(""memory-usage"", this, () => GC.GetTotalMemory(false));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var counters = this.analyzer.AnalyzeEventCounters(tree, "MyEventSource.cs");

        // Assert
        counters.Should().ContainSingle();
        counters[0].CounterType.Should().Be(EventCounterType.PollingCounter);
        counters[0].CounterName.Should().Be("memory-usage");
    }

    [TestMethod]
    public void AnalyzeEventCounters_IncrementingPollingCounter_DetectsType()
    {
        // Arrange
        var code = @"
public class MyEventSource : EventSource
{
    private IncrementingPollingCounter _gcCollections;

    public MyEventSource()
    {
        _gcCollections = new IncrementingPollingCounter(""gc-collections"", this, () => GC.CollectionCount(0));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var counters = this.analyzer.AnalyzeEventCounters(tree, "MyEventSource.cs");

        // Assert
        counters.Should().ContainSingle();
        counters[0].CounterType.Should().Be(EventCounterType.IncrementingPollingCounter);
    }

    #endregion

    #region Health Check Tests

    [TestMethod]
    public void AnalyzeHealthChecks_IHealthCheckImplementation_DetectsClass()
    {
        // Arrange
        var code = @"
public class DatabaseHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        return HealthCheckResult.Healthy();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var checks = this.analyzer.AnalyzeHealthChecks(tree, "DatabaseHealthCheck.cs");

        // Assert
        checks.Should().ContainSingle();
        checks[0].Name.Should().Be("DatabaseHealthCheck");
        checks[0].IsImplementation.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeHealthChecks_AddCheck_DetectsRegistration()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck(""database"", new DatabaseHealthCheck());
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var checks = this.analyzer.AnalyzeHealthChecks(tree, "Startup.cs");

        // Assert
        checks.Should().ContainSingle();
        checks[0].Name.Should().Be("database");
        checks[0].IsImplementation.Should().BeFalse();
    }

    [TestMethod]
    public void AnalyzeHealthChecks_AddCheckWithTags_ExtractsTags()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck(""database"", new DatabaseHealthCheck(), tags: new[] { ""ready"", ""db"" });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var checks = this.analyzer.AnalyzeHealthChecks(tree, "Startup.cs");

        // Assert
        checks.Should().ContainSingle();
        checks[0].Tags.Should().Contain("ready");
        checks[0].Tags.Should().Contain("db");
    }

    [TestMethod]
    public void AnalyzeHealthChecks_AddDbContextCheck_DetectsRegistration()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var checks = this.analyzer.AnalyzeHealthChecks(tree, "Startup.cs");

        // Assert
        checks.Should().ContainSingle();
        checks[0].Name.Should().Be("AddDbContextCheck");
    }

    [TestMethod]
    public void AnalyzeHealthChecks_AddUrlGroup_DetectsRegistration()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddUrlGroup(new Uri(""https://api.example.com/health""), ""external-api"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var checks = this.analyzer.AnalyzeHealthChecks(tree, "Startup.cs");

        // Assert
        checks.Should().ContainSingle();
    }

    #endregion

    #region Mixed Observability Tests

    [TestMethod]
    public void AnalyzeMultiple_MixedObservability_DetectsAll()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger<OrderService> _logger;
    private static readonly Meter _meter = new Meter(""MyApp.Orders"");
    private static readonly Counter<long> _counter = _meter.CreateCounter<long>(""orders"");
    private static readonly ActivitySource _activitySource = new ActivitySource(""MyApp.Orders"");

    public void ProcessOrder(int orderId)
    {
        using var activity = _activitySource.StartActivity(""ProcessOrder"");
        _logger.LogInformation(""Processing order {OrderId}"", orderId);
        _counter.Add(1);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var logs = this.analyzer.AnalyzeLogging(tree, "OrderService.cs");
        var metrics = this.analyzer.AnalyzeMetrics(tree, "OrderService.cs");
        var spans = this.analyzer.AnalyzeTracing(tree, "OrderService.cs");

        // Assert
        logs.Should().HaveCount(1);
        metrics.Should().HaveCountGreaterThan(0);
        spans.Should().HaveCountGreaterThan(0);
    }

    #endregion
}
