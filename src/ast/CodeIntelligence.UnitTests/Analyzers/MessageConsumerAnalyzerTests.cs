// -----------------------------------------------------------------------
// <copyright file="MessageConsumerAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class MessageConsumerAnalyzerTests
{
    private MessageConsumerAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new MessageConsumerAnalyzer();
    }

    #region MassTransit Consumers

    [TestMethod]
    public void Analyze_MassTransitIConsumer_DetectsConsumer()
    {
        // Arrange
        var code = @"
public class OrderConsumer : IConsumer<OrderCreatedEvent>
{
    public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
    {
        var order = context.Message;
        // Process order
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "OrderConsumer.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.MassTransit);
        consumers[0].ConsumerType.Should().Be(ConsumerType.Consumer);
        consumers[0].ClassName.Should().Be("OrderConsumer");
        consumers[0].MessageType.Should().Be("OrderCreatedEvent");
    }

    [TestMethod]
    public void Analyze_MassTransitSaga_DetectsSaga()
    {
        // Arrange
        var code = @"
public class OrderSaga : ISaga<OrderSagaState>
{
    public Guid CorrelationId { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "OrderSaga.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.MassTransit);
        consumers[0].ConsumerType.Should().Be(ConsumerType.Saga);
    }

    #endregion

    #region MediatR Handlers

    [TestMethod]
    public void Analyze_MediatRRequestHandler_DetectsHandler()
    {
        // Arrange
        var code = @"
public class CreateOrderHandler : IRequestHandler<CreateOrderCommand, OrderResult>
{
    public async Task<OrderResult> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        return new OrderResult();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "CreateOrderHandler.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.MediatR);
        consumers[0].ConsumerType.Should().Be(ConsumerType.RequestHandler);
        consumers[0].MessageType.Should().Be("CreateOrderCommand");
        consumers[0].ResponseType.Should().Be("OrderResult");
    }

    [TestMethod]
    public void Analyze_MediatRNotificationHandler_DetectsHandler()
    {
        // Arrange
        var code = @"
public class OrderCreatedHandler : INotificationHandler<OrderCreatedNotification>
{
    public async Task Handle(OrderCreatedNotification notification, CancellationToken cancellationToken)
    {
        // Handle notification
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "OrderCreatedHandler.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.MediatR);
        consumers[0].ConsumerType.Should().Be(ConsumerType.NotificationHandler);
        consumers[0].MessageType.Should().Be("OrderCreatedNotification");
    }

    [TestMethod]
    public void Analyze_MediatRStreamHandler_DetectsStreamHandler()
    {
        // Arrange
        var code = @"
public class GetOrdersHandler : IStreamRequestHandler<GetOrdersQuery, Order>
{
    public async IAsyncEnumerable<Order> Handle(GetOrdersQuery request, CancellationToken cancellationToken)
    {
        yield return new Order();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "GetOrdersHandler.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].ConsumerType.Should().Be(ConsumerType.StreamHandler);
        consumers[0].IsStreaming.Should().BeTrue();
    }

    #endregion

    #region Azure Function Triggers

    [TestMethod]
    public void Analyze_ServiceBusTrigger_DetectsConsumer()
    {
        // Arrange
        var code = @"
public class OrderFunctions
{
    [Function(""ProcessOrder"")]
    public async Task ProcessOrder(
        [ServiceBusTrigger(""orders"")] OrderMessage message,
        FunctionContext context)
    {
        // Process order
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "OrderFunctions.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.AzureServiceBus);
        consumers[0].ConsumerType.Should().Be(ConsumerType.AzureFunctionTrigger);
        consumers[0].QueueName.Should().Be("orders");
        consumers[0].HandlerMethod.Should().Be("ProcessOrder");
    }

    [TestMethod]
    public void Analyze_ServiceBusTriggerWithTopic_ExtractsTopicAndSubscription()
    {
        // Arrange
        var code = @"
public class EventFunctions
{
    [FunctionName(""ProcessEvent"")]
    public async Task ProcessEvent(
        [ServiceBusTrigger(""events"", ""my-subscription"")] EventMessage message)
    {
        // Process event
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "EventFunctions.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].TopicName.Should().Be("events");
        consumers[0].SubscriptionName.Should().Be("my-subscription");
    }

    [TestMethod]
    public void Analyze_QueueTrigger_DetectsStorageQueue()
    {
        // Arrange
        var code = @"
public class QueueFunctions
{
    [Function(""ProcessQueueMessage"")]
    public void ProcessMessage(
        [QueueTrigger(""myqueue"")] string message)
    {
        // Process message
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "QueueFunctions.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.AzureStorageQueue);
        consumers[0].QueueName.Should().Be("myqueue");
    }

    [TestMethod]
    public void Analyze_BatchServiceBusTrigger_DetectsBatchConsumer()
    {
        // Arrange
        var code = @"
public class BatchFunctions
{
    [Function(""ProcessBatch"")]
    public async Task ProcessBatch(
        [ServiceBusTrigger(""batch-queue"")] OrderMessage[] messages)
    {
        // Process batch
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "BatchFunctions.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].IsBatchConsumer.Should().BeTrue();
    }

    #endregion

    #region RabbitMQ

    [TestMethod]
    public void Analyze_RabbitMQConsumer_DetectsConsumer()
    {
        // Arrange
        var code = @"
public class MyConsumer : EventingBasicConsumer
{
    public MyConsumer(IModel model) : base(model) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "MyConsumer.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.RabbitMQ);
    }

    [TestMethod]
    public void Analyze_RabbitMQAsyncConsumer_DetectsAsyncConsumer()
    {
        // Arrange
        var code = @"
public class MyAsyncConsumer : AsyncEventingBasicConsumer
{
    public MyAsyncConsumer(IModel model) : base(model) { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "MyAsyncConsumer.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].IsAsync.Should().BeTrue();
    }

    #endregion

    #region Kafka

    [TestMethod]
    public void Analyze_KafkaConsume_DetectsKafkaConsumer()
    {
        // Arrange
        var code = @"
public class KafkaProcessor
{
    private readonly IConsumer<string, string> _consumer;

    public async Task ProcessAsync()
    {
        while (true)
        {
            var result = _consumer.Consume();
            // Process message
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "KafkaProcessor.cs");

        // Assert
        consumers.Should().ContainSingle();
        consumers[0].Broker.Should().Be(MessageBroker.Kafka);
    }

    #endregion

    [TestMethod]
    public void Analyze_NoConsumers_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class Calculator
{
    public int Add(int a, int b) => a + b;
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var consumers = this.analyzer.Analyze(tree, "Calculator.cs");

        // Assert
        consumers.Should().BeEmpty();
    }
}
