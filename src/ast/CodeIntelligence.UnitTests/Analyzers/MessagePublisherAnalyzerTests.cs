// -----------------------------------------------------------------------
// <copyright file="MessagePublisherAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class MessagePublisherAnalyzerTests
{
    private MessagePublisherAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new MessagePublisherAnalyzer();
    }

    #region Azure Service Bus

    [TestMethod]
    public void Analyze_ServiceBusSendMessageAsync_DetectsPublish()
    {
        // Arrange
        var code = @"
public class OrderPublisher
{
    private readonly ServiceBusSender _sender;

    public async Task PublishOrderAsync(Order order)
    {
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(order));
        await _sender.SendMessageAsync(message);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "OrderPublisher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.AzureServiceBus);
        publishers[0].OperationType.Should().Be(MessageOperationType.Send);
    }

    [TestMethod]
    public void Analyze_ServiceBusSendMessagesAsync_DetectsBatchPublish()
    {
        // Arrange
        var code = @"
public class BatchPublisher
{
    private readonly ServiceBusSender _sender;

    public async Task PublishBatchAsync(IEnumerable<Order> orders)
    {
        var messages = orders.Select(o => new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(o)));
        await _sender.SendMessagesAsync(messages);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "BatchPublisher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.AzureServiceBus);
    }

    [TestMethod]
    public void Analyze_ServiceBusScheduleMessageAsync_DetectsSchedule()
    {
        // Arrange
        var code = @"
public class ScheduledPublisher
{
    private readonly ServiceBusSender _sender;

    public async Task ScheduleOrderAsync(Order order, DateTimeOffset scheduledTime)
    {
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(order));
        await _sender.ScheduleMessageAsync(message, scheduledTime);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "ScheduledPublisher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].OperationType.Should().Be(MessageOperationType.Schedule);
    }

    #endregion

    #region MassTransit

    [TestMethod]
    public void Analyze_MassTransitPublish_DetectsPublish()
    {
        // Arrange
        var code = @"
public class EventPublisher
{
    private readonly IBus _bus;

    public async Task PublishEventAsync(OrderCreatedEvent evt)
    {
        await _bus.Publish(evt);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "EventPublisher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.MassTransit);
        publishers[0].OperationType.Should().Be(MessageOperationType.Publish);
    }

    [TestMethod]
    public void Analyze_MassTransitSend_DetectsSend()
    {
        // Arrange
        var code = @"
public class CommandSender
{
    private readonly IBus _bus;

    public async Task SendCommandAsync(ProcessOrderCommand command)
    {
        await _bus.Send(command);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "CommandSender.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.MassTransit);
        publishers[0].OperationType.Should().Be(MessageOperationType.Send);
    }

    [TestMethod]
    public void Analyze_PublishEndpoint_DetectsMassTransit()
    {
        // Arrange
        var code = @"
public class NotificationService
{
    private readonly IPublishEndpoint _publishEndpoint;

    public async Task NotifyAsync(UserNotification notification)
    {
        await _publishEndpoint.Publish(notification);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "NotificationService.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.MassTransit);
    }

    #endregion

    #region MediatR

    [TestMethod]
    public void Analyze_MediatRSend_DetectsMediatR()
    {
        // Arrange
        var code = @"
public class OrderHandler
{
    private readonly IMediator _mediator;

    public async Task<OrderResult> HandleAsync(CreateOrderCommand command)
    {
        return await _mediator.Send(command);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "OrderHandler.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.MediatR);
        publishers[0].OperationType.Should().Be(MessageOperationType.Send);
    }

    [TestMethod]
    public void Analyze_MediatRPublish_DetectsNotification()
    {
        // Arrange
        var code = @"
public class EventDispatcher
{
    private readonly IMediator _mediator;

    public async Task DispatchAsync(OrderCreatedNotification notification)
    {
        await _mediator.Publish(notification);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "EventDispatcher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.MediatR);
        publishers[0].OperationType.Should().Be(MessageOperationType.Publish);
    }

    #endregion

    #region RabbitMQ

    [TestMethod]
    public void Analyze_RabbitMQBasicPublish_DetectsRabbitMQ()
    {
        // Arrange
        var code = @"
public class RabbitPublisher
{
    private readonly IModel _channel;

    public void PublishMessage(string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        _channel.BasicPublish(exchange: ""orders"", routingKey: ""order.created"", body: body);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "RabbitPublisher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.RabbitMQ);
    }

    #endregion

    #region Event Grid

    [TestMethod]
    public void Analyze_EventGridSendEventAsync_DetectsEventGrid()
    {
        // Arrange
        var code = @"
public class EventGridPublisher
{
    private readonly EventGridPublisherClient _eventGridClient;

    public async Task PublishAsync(EventGridEvent evt)
    {
        await _eventGridClient.SendEventAsync(evt);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "EventGridPublisher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.EventGrid);
    }

    #endregion

    #region Kafka

    [TestMethod]
    public void Analyze_KafkaProduceAsync_DetectsKafka()
    {
        // Arrange
        var code = @"
public class KafkaPublisher
{
    private readonly IProducer<string, string> _producer;

    public async Task PublishAsync(string topic, string message)
    {
        await _producer.ProduceAsync(topic, new Message<string, string> { Value = message });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "KafkaPublisher.cs");

        // Assert
        publishers.Should().ContainSingle();
        publishers[0].Broker.Should().Be(MessageBroker.Kafka);
    }

    #endregion

    [TestMethod]
    public void Analyze_NoPublishers_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class Calculator
{
    public int Add(int a, int b) => a + b;
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var publishers = this.analyzer.Analyze(tree, "Calculator.cs");

        // Assert
        publishers.Should().BeEmpty();
    }
}
