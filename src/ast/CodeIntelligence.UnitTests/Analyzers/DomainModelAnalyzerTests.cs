// -----------------------------------------------------------------------
// <copyright file="DomainModelAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Unit tests for DomainModelAnalyzer.
/// </summary>
[TestClass]
public class DomainModelAnalyzerTests
{
    private DomainModelAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new DomainModelAnalyzer();
    }

    #region Aggregate Root Tests

    [TestMethod]
    public void AnalyzeAggregates_AggregateRootClass_DetectsAggregate()
    {
        // Arrange
        var code = @"
public class Order : AggregateRoot<OrderId>
{
    public OrderId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public decimal TotalAmount { get; private set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var aggregates = this.analyzer.AnalyzeAggregates(tree, "Order.cs");

        // Assert
        aggregates.Should().ContainSingle();
        aggregates[0].Name.Should().Be("Order");
        aggregates[0].EntityType.Should().Be(DomainEntityType.AggregateRoot);
        aggregates[0].IdType.Should().Be("OrderId");
    }

    [TestMethod]
    public void AnalyzeAggregates_EntityClass_DetectsEntity()
    {
        // Arrange
        var code = @"
public class OrderItem : Entity<OrderItemId>
{
    public OrderItemId Id { get; private set; }
    public ProductId ProductId { get; private set; }
    public int Quantity { get; private set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var aggregates = this.analyzer.AnalyzeAggregates(tree, "OrderItem.cs");

        // Assert
        aggregates.Should().ContainSingle();
        aggregates[0].Name.Should().Be("OrderItem");
        aggregates[0].EntityType.Should().Be(DomainEntityType.Entity);
    }

    [TestMethod]
    public void AnalyzeAggregates_ValueObjectRecord_DetectsValueObject()
    {
        // Arrange
        var code = @"
public record OrderId(Guid Value) : ValueObject;";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var aggregates = this.analyzer.AnalyzeAggregates(tree, "OrderId.cs");

        // Assert
        aggregates.Should().ContainSingle();
        aggregates[0].Name.Should().Be("OrderId");
        aggregates[0].EntityType.Should().Be(DomainEntityType.ValueObject);
    }

    [TestMethod]
    public void AnalyzeAggregates_ValueObjectByNaming_DetectsValueObject()
    {
        // Arrange
        var code = @"
public record CustomerId(Guid Value);";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var aggregates = this.analyzer.AnalyzeAggregates(tree, "CustomerId.cs");

        // Assert
        aggregates.Should().ContainSingle();
        aggregates[0].EntityType.Should().Be(DomainEntityType.ValueObject);
    }

    [TestMethod]
    public void AnalyzeAggregates_WithFactoryMethod_DetectsFactory()
    {
        // Arrange
        var code = @"
public class Order : AggregateRoot
{
    public static Order Create(CustomerId customerId)
    {
        return new Order { CustomerId = customerId };
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var aggregates = this.analyzer.AnalyzeAggregates(tree, "Order.cs");

        // Assert
        aggregates.Should().ContainSingle();
        aggregates[0].FactoryMethods.Should().Contain("Create");
    }

    [TestMethod]
    public void AnalyzeAggregates_WithProperties_ExtractsProperties()
    {
        // Arrange
        var code = @"
public class Order : AggregateRoot
{
    public OrderId Id { get; private set; }
    public string CustomerName { get; set; }
    public decimal Amount { get; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var aggregates = this.analyzer.AnalyzeAggregates(tree, "Order.cs");

        // Assert
        aggregates.Should().ContainSingle();
        aggregates[0].Properties.Should().HaveCount(3);
        aggregates[0].Properties.Should().Contain(p => p.Name == "Id");
        aggregates[0].Properties.Should().Contain(p => p.Name == "CustomerName");
        aggregates[0].Properties.Should().Contain(p => p.Name == "Amount" && p.IsReadOnly);
    }

    #endregion

    #region Domain Event Tests

    [TestMethod]
    public void AnalyzeDomainEvents_DomainEventClass_DetectsEvent()
    {
        // Arrange
        var code = @"
public class OrderCreatedEvent : DomainEvent
{
    public OrderId OrderId { get; init; }
    public DateTime CreatedAt { get; init; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeDomainEvents(tree, "OrderCreatedEvent.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].Name.Should().Be("OrderCreatedEvent");
        events[0].EventType.Should().Be(DomainEventType.DomainEvent);
    }

    [TestMethod]
    public void AnalyzeDomainEvents_INotificationClass_DetectsEvent()
    {
        // Arrange
        var code = @"
public class OrderShippedNotification : INotification
{
    public OrderId OrderId { get; init; }
    public string TrackingNumber { get; init; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeDomainEvents(tree, "OrderShippedNotification.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].Name.Should().Be("OrderShippedNotification");
    }

    [TestMethod]
    public void AnalyzeDomainEvents_IntegrationEvent_DetectsType()
    {
        // Arrange
        var code = @"
public class OrderCreatedIntegrationEvent : IntegrationEvent
{
    public Guid OrderId { get; init; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeDomainEvents(tree, "OrderCreatedIntegrationEvent.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].EventType.Should().Be(DomainEventType.IntegrationEvent);
    }

    [TestMethod]
    public void AnalyzeDomainEvents_RecordEvent_DetectsEvent()
    {
        // Arrange
        var code = @"
public record OrderCancelledEvent(OrderId OrderId, string Reason) : IDomainEvent;";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var events = this.analyzer.AnalyzeDomainEvents(tree, "OrderCancelledEvent.cs");

        // Assert
        events.Should().ContainSingle();
        events[0].Name.Should().Be("OrderCancelledEvent");
        events[0].Properties.Should().Contain("OrderId");
        events[0].Properties.Should().Contain("Reason");
    }

    #endregion

    #region CQRS Pattern Tests

    [TestMethod]
    public void AnalyzeCqrsPatterns_CommandClass_DetectsCommand()
    {
        // Arrange
        var code = @"
public class CreateOrderCommand : ICommand<OrderId>
{
    public string CustomerName { get; init; }
    public List<OrderItem> Items { get; init; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var patterns = this.analyzer.AnalyzeCqrsPatterns(tree, "CreateOrderCommand.cs");

        // Assert
        patterns.Should().ContainSingle();
        patterns[0].Name.Should().Be("CreateOrderCommand");
        patterns[0].PatternType.Should().Be(CqrsPatternType.Command);
        patterns[0].ResponseType.Should().Be("OrderId");
    }

    [TestMethod]
    public void AnalyzeCqrsPatterns_MediatRRequest_DetectsCommand()
    {
        // Arrange
        var code = @"
public class UpdateOrderCommand : IRequest<bool>
{
    public OrderId OrderId { get; init; }
    public decimal NewAmount { get; init; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var patterns = this.analyzer.AnalyzeCqrsPatterns(tree, "UpdateOrderCommand.cs");

        // Assert
        patterns.Should().ContainSingle();
        patterns[0].PatternType.Should().Be(CqrsPatternType.Command);
        patterns[0].ResponseType.Should().Be("bool");
    }

    [TestMethod]
    public void AnalyzeCqrsPatterns_QueryClass_DetectsQuery()
    {
        // Arrange
        var code = @"
public class GetOrderByIdQuery : IQuery<OrderDto>
{
    public OrderId OrderId { get; init; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var patterns = this.analyzer.AnalyzeCqrsPatterns(tree, "GetOrderByIdQuery.cs");

        // Assert
        patterns.Should().ContainSingle();
        patterns[0].Name.Should().Be("GetOrderByIdQuery");
        patterns[0].PatternType.Should().Be(CqrsPatternType.Query);
        patterns[0].ResponseType.Should().Be("OrderDto");
    }

    [TestMethod]
    public void AnalyzeCqrsPatterns_CommandHandler_DetectsHandler()
    {
        // Arrange
        var code = @"
public class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, OrderId>
{
    public async Task<OrderId> Handle(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        // Handle command
        return new OrderId(Guid.NewGuid());
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var patterns = this.analyzer.AnalyzeCqrsPatterns(tree, "CreateOrderCommandHandler.cs");

        // Assert
        patterns.Should().ContainSingle();
        patterns[0].Name.Should().Be("CreateOrderCommandHandler");
        patterns[0].PatternType.Should().Be(CqrsPatternType.Handler);
        patterns[0].HandledType.Should().Be("CreateOrderCommand");
        patterns[0].ResponseType.Should().Be("OrderId");
    }

    [TestMethod]
    public void AnalyzeCqrsPatterns_RecordCommand_DetectsCommand()
    {
        // Arrange
        var code = @"
public record DeleteOrderCommand(OrderId OrderId) : IRequest<bool>;";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var patterns = this.analyzer.AnalyzeCqrsPatterns(tree, "DeleteOrderCommand.cs");

        // Assert
        patterns.Should().ContainSingle();
        patterns[0].PatternType.Should().Be(CqrsPatternType.Command);
    }

    [TestMethod]
    public void AnalyzeCqrsPatterns_NotificationHandler_DetectsHandler()
    {
        // Arrange
        var code = @"
public class OrderCreatedEventHandler : INotificationHandler<OrderCreatedEvent>
{
    public Task Handle(OrderCreatedEvent notification, CancellationToken cancellationToken)
    {
        // Handle event
        return Task.CompletedTask;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var patterns = this.analyzer.AnalyzeCqrsPatterns(tree, "OrderCreatedEventHandler.cs");

        // Assert
        patterns.Should().ContainSingle();
        patterns[0].PatternType.Should().Be(CqrsPatternType.Handler);
        patterns[0].HandledType.Should().Be("OrderCreatedEvent");
    }

    #endregion

    #region Event Raising Tests

    [TestMethod]
    public void AnalyzeEventRaising_AddDomainEvent_DetectsRaising()
    {
        // Arrange
        var code = @"
public class Order : AggregateRoot
{
    public void Complete()
    {
        Status = OrderStatus.Completed;
        AddDomainEvent(new OrderCompletedEvent(Id));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var raisings = this.analyzer.AnalyzeEventRaising(tree, "Order.cs");

        // Assert
        raisings.Should().ContainSingle();
        raisings[0].MethodName.Should().Be("AddDomainEvent");
        raisings[0].EventType.Should().Be("OrderCompletedEvent");
    }

    [TestMethod]
    public void AnalyzeEventRaising_PublishAsync_DetectsRaising()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly IMediator _mediator;

    public async Task CompleteOrderAsync(Order order)
    {
        await _mediator.PublishAsync(new OrderCompletedEvent(order.Id));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var raisings = this.analyzer.AnalyzeEventRaising(tree, "OrderService.cs");

        // Assert
        raisings.Should().ContainSingle();
        raisings[0].MethodName.Should().Be("PublishAsync");
    }

    [TestMethod]
    public void AnalyzeEventRaising_Send_DetectsRaising()
    {
        // Arrange
        var code = @"
public class OrderController
{
    private readonly IMediator _mediator;

    public async Task<IActionResult> CreateOrder(CreateOrderDto dto)
    {
        var result = await _mediator.Send(new CreateOrderCommand(dto));
        return Ok(result);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var raisings = this.analyzer.AnalyzeEventRaising(tree, "OrderController.cs");

        // Assert
        raisings.Should().ContainSingle();
        raisings[0].MethodName.Should().Be("Send");
    }

    #endregion

    #region Mixed Tests

    [TestMethod]
    public void AnalyzeAll_CompleteAggregate_DetectsAllPatterns()
    {
        // Arrange
        var code = @"
public class Order : AggregateRoot<OrderId>
{
    public OrderId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }

    public static Order Create(CustomerId customerId)
    {
        var order = new Order { Id = new OrderId(Guid.NewGuid()), CustomerId = customerId };
        order.AddDomainEvent(new OrderCreatedEvent(order.Id));
        return order;
    }
}

public record OrderId(Guid Value) : ValueObject;

public record OrderCreatedEvent(OrderId OrderId) : IDomainEvent;";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var aggregates = this.analyzer.AnalyzeAggregates(tree, "Order.cs");
        var events = this.analyzer.AnalyzeDomainEvents(tree, "Order.cs");
        var raisings = this.analyzer.AnalyzeEventRaising(tree, "Order.cs");

        // Assert
        aggregates.Should().HaveCount(2); // Order + OrderId
        aggregates.Should().Contain(a => a.EntityType == DomainEntityType.AggregateRoot);
        aggregates.Should().Contain(a => a.EntityType == DomainEntityType.ValueObject);
        events.Should().ContainSingle();
        raisings.Should().ContainSingle();
    }

    #endregion
}
