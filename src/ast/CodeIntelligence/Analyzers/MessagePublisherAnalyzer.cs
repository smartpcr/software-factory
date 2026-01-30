// -----------------------------------------------------------------------
// <copyright file="MessagePublisherAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect message publishing patterns (ServiceBus, MassTransit, MediatR, etc.).
/// </summary>
public sealed class MessagePublisherAnalyzer
{
    private static readonly Dictionary<string, (string Broker, string[] Methods)> PublisherPatterns = new()
    {
        {
            "AzureServiceBus", (
                "AzureServiceBus",
                new[] { "SendMessageAsync", "SendMessagesAsync", "SendAsync", "ScheduleMessageAsync" })
        },
        {
            "MassTransit", (
                "MassTransit",
                new[] { "Publish", "PublishAsync", "Send", "SendAsync" })
        },
        {
            "MediatR", (
                "MediatR",
                new[] { "Send", "Publish" })
        },
        {
            "RabbitMQ", (
                "RabbitMQ",
                new[] { "BasicPublish", "BasicPublishAsync" })
        },
        {
            "EventGrid", (
                "EventGrid",
                new[] { "SendEventAsync", "SendEventsAsync" })
        },
        {
            "Kafka", (
                "Kafka",
                new[] { "ProduceAsync", "Produce" })
        }
    };

    private static readonly HashSet<string> ServiceBusReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sender", "_sender", "serviceBusSender", "_serviceBusSender", "topicSender", "_topicSender",
        "queueSender", "_queueSender", "ServiceBusSender"
    };

    private static readonly HashSet<string> MassTransitReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "_bus", "bus", "_publishEndpoint", "publishEndpoint", "_sendEndpointProvider",
        "sendEndpointProvider", "IBus", "IPublishEndpoint"
    };

    private static readonly HashSet<string> MediatRReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "_mediator", "mediator", "_sender", "sender", "_publisher", "publisher",
        "IMediator", "ISender", "IPublisher"
    };

    /// <summary>
    /// Analyzes a syntax tree for message publishing patterns.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected message publish operations.</returns>
    public IReadOnlyList<MessagePublishInfo> Analyze(SyntaxTree tree, string filePath)
    {
        var publishes = new List<MessagePublishInfo>();
        var root = tree.GetRoot();

        publishes.AddRange(this.DetectServiceBusPublish(root, filePath));
        publishes.AddRange(this.DetectMassTransitPublish(root, filePath));
        publishes.AddRange(this.DetectMediatRPublish(root, filePath));
        publishes.AddRange(this.DetectRabbitMqPublish(root, filePath));
        publishes.AddRange(this.DetectEventGridPublish(root, filePath));
        publishes.AddRange(this.DetectKafkaPublish(root, filePath));

        return publishes;
    }

    private IEnumerable<MessagePublishInfo> DetectServiceBusPublish(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            var receiver = memberAccess.Expression.ToString();

            if (!ServiceBusReceivers.Any(r => receiver.Contains(r, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!methodName.Contains("Send") && !methodName.Contains("Schedule"))
            {
                continue;
            }

            var messageInfo = this.ExtractMessageInfo(invocation);

            yield return new MessagePublishInfo
            {
                Broker = MessageBroker.AzureServiceBus,
                OperationType = methodName.Contains("Schedule")
                    ? MessageOperationType.Schedule
                    : MessageOperationType.Send,
                MessageType = messageInfo.Type,
                MessageVariable = messageInfo.Variable,
                DestinationName = this.ExtractDestination(invocation, receiver),
                CorrelationIdExpression = messageInfo.CorrelationId,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = methodName.EndsWith("Async"),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            };
        }
    }

    private IEnumerable<MessagePublishInfo> DetectMassTransitPublish(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            var receiver = memberAccess.Expression.ToString();

            if (!MassTransitReceivers.Any(r => receiver.Contains(r, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (methodName != "Publish" && methodName != "PublishAsync" &&
                methodName != "Send" && methodName != "SendAsync")
            {
                continue;
            }

            var messageInfo = this.ExtractMessageInfo(invocation);
            var isPublish = methodName.Contains("Publish");

            yield return new MessagePublishInfo
            {
                Broker = MessageBroker.MassTransit,
                OperationType = isPublish ? MessageOperationType.Publish : MessageOperationType.Send,
                MessageType = messageInfo.Type,
                MessageVariable = messageInfo.Variable,
                CorrelationIdExpression = messageInfo.CorrelationId,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = methodName.EndsWith("Async") || this.IsAwaited(invocation),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            };
        }
    }

    private IEnumerable<MessagePublishInfo> DetectMediatRPublish(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            var receiver = memberAccess.Expression.ToString();

            if (!MediatRReceivers.Any(r => receiver.Contains(r, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (methodName != "Send" && methodName != "Publish")
            {
                continue;
            }

            var messageInfo = this.ExtractMessageInfo(invocation);

            yield return new MessagePublishInfo
            {
                Broker = MessageBroker.MediatR,
                OperationType = methodName == "Publish"
                    ? MessageOperationType.Publish
                    : MessageOperationType.Send,
                MessageType = messageInfo.Type,
                MessageVariable = messageInfo.Variable,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = this.IsAwaited(invocation),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            };
        }
    }

    private IEnumerable<MessagePublishInfo> DetectRabbitMqPublish(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            if (methodName != "BasicPublish" && methodName != "BasicPublishAsync")
            {
                continue;
            }

            var receiver = memberAccess.Expression.ToString();
            if (!receiver.Contains("channel", StringComparison.OrdinalIgnoreCase) &&
                !receiver.Contains("model", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Extract exchange and routing key
            var args = invocation.ArgumentList.Arguments;
            string? exchange = null;
            string? routingKey = null;

            if (args.Count >= 2)
            {
                exchange = args[0].ToString().Trim('"');
                routingKey = args[1].ToString().Trim('"');
            }

            yield return new MessagePublishInfo
            {
                Broker = MessageBroker.RabbitMQ,
                OperationType = MessageOperationType.Publish,
                DestinationName = !string.IsNullOrEmpty(exchange) ? $"{exchange}/{routingKey}" : routingKey,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = methodName.EndsWith("Async"),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private IEnumerable<MessagePublishInfo> DetectEventGridPublish(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            if (!methodName.Equals("SendEventAsync", StringComparison.OrdinalIgnoreCase) &&
                !methodName.Equals("SendEventsAsync", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var receiver = memberAccess.Expression.ToString();
            if (!receiver.Contains("EventGrid", StringComparison.OrdinalIgnoreCase) &&
                !receiver.Contains("eventClient", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var messageInfo = this.ExtractMessageInfo(invocation);

            yield return new MessagePublishInfo
            {
                Broker = MessageBroker.EventGrid,
                OperationType = MessageOperationType.Publish,
                MessageType = messageInfo.Type,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = true,
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private IEnumerable<MessagePublishInfo> DetectKafkaPublish(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            if (methodName != "ProduceAsync" && methodName != "Produce")
            {
                continue;
            }

            var receiver = memberAccess.Expression.ToString();
            if (!receiver.Contains("producer", StringComparison.OrdinalIgnoreCase) &&
                !receiver.Contains("Producer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Extract topic name
            var args = invocation.ArgumentList.Arguments;
            string? topicName = null;
            if (args.Count > 0)
            {
                topicName = args[0].ToString().Trim('"');
            }

            yield return new MessagePublishInfo
            {
                Broker = MessageBroker.Kafka,
                OperationType = MessageOperationType.Publish,
                DestinationName = topicName,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = methodName.EndsWith("Async"),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private (string? Type, string? Variable, string? CorrelationId) ExtractMessageInfo(
        InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return (null, null, null);
        }

        var firstArg = args[0].Expression;
        string? type = null;
        string? variable = null;

        if (firstArg is ObjectCreationExpressionSyntax objectCreation)
        {
            type = objectCreation.Type.ToString();
        }
        else if (firstArg is IdentifierNameSyntax identifier)
        {
            variable = identifier.Identifier.Text;
        }
        else if (firstArg is ImplicitObjectCreationExpressionSyntax)
        {
            // new() { ... } - type is inferred
            type = "InferredType";
        }
        else
        {
            variable = firstArg.ToString();
        }

        // Look for correlation ID in named arguments
        string? correlationId = null;
        var correlationArg = args.FirstOrDefault(a =>
            a.NameColon?.Name.Identifier.Text.Contains("correlation", StringComparison.OrdinalIgnoreCase) == true ||
            a.NameColon?.Name.Identifier.Text.Contains("Correlation", StringComparison.OrdinalIgnoreCase) == true);

        if (correlationArg != null)
        {
            correlationId = correlationArg.Expression.ToString();
        }

        return (type, variable, correlationId);
    }

    private string? ExtractDestination(InvocationExpressionSyntax invocation, string receiver)
    {
        // Try to find where the sender was created to get queue/topic name
        var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method == null)
        {
            return null;
        }

        // Look for CreateSender calls
        var senderCreations = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.ToString().Contains("CreateSender"));

        foreach (var creation in senderCreations)
        {
            var args = creation.ArgumentList.Arguments;
            if (args.Count > 0)
            {
                return args[0].ToString().Trim('"');
            }
        }

        return null;
    }

    private bool IsAwaited(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is AwaitExpressionSyntax;
    }
}

/// <summary>
/// Information about a message publish operation.
/// </summary>
public record MessagePublishInfo
{
    /// <summary>Gets the message broker.</summary>
    public required MessageBroker Broker { get; init; }

    /// <summary>Gets the operation type.</summary>
    public required MessageOperationType OperationType { get; init; }

    /// <summary>Gets the message type.</summary>
    public string? MessageType { get; init; }

    /// <summary>Gets the message variable name.</summary>
    public string? MessageVariable { get; init; }

    /// <summary>Gets the destination name (queue, topic, exchange).</summary>
    public string? DestinationName { get; init; }

    /// <summary>Gets the correlation ID expression.</summary>
    public string? CorrelationIdExpression { get; init; }

    /// <summary>Gets the receiver expression.</summary>
    public required string ReceiverExpression { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets whether the operation is async.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Types of message brokers.
/// </summary>
public enum MessageBroker
{
    /// <summary>Azure Service Bus.</summary>
    AzureServiceBus,

    /// <summary>MassTransit.</summary>
    MassTransit,

    /// <summary>MediatR (in-process).</summary>
    MediatR,

    /// <summary>RabbitMQ.</summary>
    RabbitMQ,

    /// <summary>Azure Event Grid.</summary>
    EventGrid,

    /// <summary>Apache Kafka.</summary>
    Kafka,

    /// <summary>Azure Storage Queues.</summary>
    AzureStorageQueue,

    /// <summary>Unknown broker.</summary>
    Unknown
}

/// <summary>
/// Types of message operations.
/// </summary>
public enum MessageOperationType
{
    /// <summary>Send (point-to-point).</summary>
    Send,

    /// <summary>Publish (broadcast).</summary>
    Publish,

    /// <summary>Schedule for later.</summary>
    Schedule,

    /// <summary>Request-response.</summary>
    Request
}
