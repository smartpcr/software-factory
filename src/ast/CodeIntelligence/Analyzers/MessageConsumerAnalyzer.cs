// -----------------------------------------------------------------------
// <copyright file="MessageConsumerAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect message consumer patterns (IConsumer, handlers, triggers).
/// </summary>
public sealed class MessageConsumerAnalyzer
{
    private static readonly Regex GenericInterfacePattern = new(
        @"^I(Consumer|RequestHandler|NotificationHandler)<(.+)>$",
        RegexOptions.Compiled);

    /// <summary>
    /// Analyzes a syntax tree for message consumer patterns.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected message consumers.</returns>
    public IReadOnlyList<MessageConsumerInfo> Analyze(SyntaxTree tree, string filePath)
    {
        var consumers = new List<MessageConsumerInfo>();
        var root = tree.GetRoot();

        consumers.AddRange(this.DetectMassTransitConsumers(root, filePath));
        consumers.AddRange(this.DetectMediatRHandlers(root, filePath));
        consumers.AddRange(this.DetectAzureFunctionTriggers(root, filePath));
        consumers.AddRange(this.DetectRabbitMqConsumers(root, filePath));
        consumers.AddRange(this.DetectKafkaConsumers(root, filePath));

        return consumers;
    }

    private IEnumerable<MessageConsumerInfo> DetectMassTransitConsumers(SyntaxNode root, string filePath)
    {
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            if (classDecl.BaseList == null)
            {
                continue;
            }

            foreach (var baseType in classDecl.BaseList.Types)
            {
                var typeName = baseType.Type.ToString();

                // IConsumer<TMessage>
                if (typeName.StartsWith("IConsumer<"))
                {
                    var messageType = ExtractGenericArgument(typeName);

                    yield return new MessageConsumerInfo
                    {
                        Broker = MessageBroker.MassTransit,
                        ConsumerType = ConsumerType.Consumer,
                        ClassName = classDecl.Identifier.Text,
                        MessageType = messageType,
                        HandlerMethod = "Consume",
                        IsAsync = true,
                        FilePath = filePath,
                        LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Confidence = 0.95
                    };
                }

                // ISaga<TState>
                if (typeName.StartsWith("ISaga<"))
                {
                    var stateType = ExtractGenericArgument(typeName);

                    yield return new MessageConsumerInfo
                    {
                        Broker = MessageBroker.MassTransit,
                        ConsumerType = ConsumerType.Saga,
                        ClassName = classDecl.Identifier.Text,
                        MessageType = stateType,
                        FilePath = filePath,
                        LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Confidence = 0.90
                    };
                }
            }
        }
    }

    private IEnumerable<MessageConsumerInfo> DetectMediatRHandlers(SyntaxNode root, string filePath)
    {
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            if (classDecl.BaseList == null)
            {
                continue;
            }

            foreach (var baseType in classDecl.BaseList.Types)
            {
                var typeName = baseType.Type.ToString();

                // IRequestHandler<TRequest, TResponse> or IRequestHandler<TRequest>
                if (typeName.StartsWith("IRequestHandler<"))
                {
                    var args = ExtractGenericArguments(typeName);
                    var requestType = args.Length > 0 ? args[0] : null;
                    var responseType = args.Length > 1 ? args[1] : "Unit";

                    yield return new MessageConsumerInfo
                    {
                        Broker = MessageBroker.MediatR,
                        ConsumerType = ConsumerType.RequestHandler,
                        ClassName = classDecl.Identifier.Text,
                        MessageType = requestType,
                        ResponseType = responseType,
                        HandlerMethod = "Handle",
                        IsAsync = true,
                        FilePath = filePath,
                        LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Confidence = 0.95
                    };
                }

                // INotificationHandler<TNotification>
                if (typeName.StartsWith("INotificationHandler<"))
                {
                    var notificationType = ExtractGenericArgument(typeName);

                    yield return new MessageConsumerInfo
                    {
                        Broker = MessageBroker.MediatR,
                        ConsumerType = ConsumerType.NotificationHandler,
                        ClassName = classDecl.Identifier.Text,
                        MessageType = notificationType,
                        HandlerMethod = "Handle",
                        IsAsync = true,
                        FilePath = filePath,
                        LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Confidence = 0.95
                    };
                }

                // IStreamRequestHandler<TRequest, TResponse>
                if (typeName.StartsWith("IStreamRequestHandler<"))
                {
                    var args = ExtractGenericArguments(typeName);
                    var requestType = args.Length > 0 ? args[0] : null;
                    var responseType = args.Length > 1 ? args[1] : null;

                    yield return new MessageConsumerInfo
                    {
                        Broker = MessageBroker.MediatR,
                        ConsumerType = ConsumerType.StreamHandler,
                        ClassName = classDecl.Identifier.Text,
                        MessageType = requestType,
                        ResponseType = responseType,
                        HandlerMethod = "Handle",
                        IsStreaming = true,
                        IsAsync = true,
                        FilePath = filePath,
                        LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Confidence = 0.90
                    };
                }
            }
        }
    }

    private IEnumerable<MessageConsumerInfo> DetectAzureFunctionTriggers(SyntaxNode root, string filePath)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            // Check for Function attribute
            var hasFunctionAttr = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => GetAttributeName(a) == "Function" || GetAttributeName(a) == "FunctionName");

            if (!hasFunctionAttr)
            {
                continue;
            }

            // Look for trigger parameters
            foreach (var param in method.ParameterList.Parameters)
            {
                var triggerAttr = param.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .FirstOrDefault(a =>
                        GetAttributeName(a).EndsWith("Trigger") ||
                        GetAttributeName(a) == "ServiceBusTrigger" ||
                        GetAttributeName(a) == "QueueTrigger" ||
                        GetAttributeName(a) == "EventHubTrigger" ||
                        GetAttributeName(a) == "BlobTrigger" ||
                        GetAttributeName(a) == "CosmosDBTrigger" ||
                        GetAttributeName(a) == "KafkaTrigger");

                if (triggerAttr == null)
                {
                    continue;
                }

                var triggerType = GetAttributeName(triggerAttr);
                var broker = GetBrokerFromTrigger(triggerType);
                var triggerInfo = this.ExtractTriggerInfo(triggerAttr);

                yield return new MessageConsumerInfo
                {
                    Broker = broker,
                    ConsumerType = ConsumerType.AzureFunctionTrigger,
                    ClassName = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text,
                    HandlerMethod = method.Identifier.Text,
                    MessageType = param.Type?.ToString(),
                    QueueName = triggerInfo.QueueName,
                    TopicName = triggerInfo.TopicName,
                    SubscriptionName = triggerInfo.SubscriptionName,
                    TriggerType = triggerType,
                    IsAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                    IsBatchConsumer = param.Type?.ToString().Contains("[]") == true ||
                                     param.Type?.ToString().Contains("List<") == true,
                    FilePath = filePath,
                    LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Confidence = 0.95
                };
            }
        }
    }

    private IEnumerable<MessageConsumerInfo> DetectRabbitMqConsumers(SyntaxNode root, string filePath)
    {
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            // Check for IBasicConsumer or AsyncEventingBasicConsumer
            var baseList = classDecl.BaseList;
            if (baseList == null)
            {
                continue;
            }

            var isRabbitConsumer = baseList.Types.Any(t =>
                t.Type.ToString().Contains("BasicConsumer") ||
                t.Type.ToString().Contains("AsyncEventingBasicConsumer") ||
                t.Type.ToString().Contains("EventingBasicConsumer"));

            if (!isRabbitConsumer)
            {
                continue;
            }

            yield return new MessageConsumerInfo
            {
                Broker = MessageBroker.RabbitMQ,
                ConsumerType = ConsumerType.Consumer,
                ClassName = classDecl.Identifier.Text,
                HandlerMethod = "HandleBasicDeliver",
                IsAsync = classDecl.BaseList.Types.Any(t => t.Type.ToString().Contains("Async")),
                FilePath = filePath,
                LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }

        // Also check for Received event handlers
        var invocations = root.DescendantNodes().OfType<AssignmentExpressionSyntax>();
        foreach (var assignment in invocations)
        {
            if (!assignment.Left.ToString().Contains("Received"))
            {
                continue;
            }

            if (!assignment.ToString().Contains("consumer") &&
                !assignment.ToString().Contains("Consumer"))
            {
                continue;
            }

            yield return new MessageConsumerInfo
            {
                Broker = MessageBroker.RabbitMQ,
                ConsumerType = ConsumerType.EventHandler,
                HandlerMethod = assignment.Right.ToString(),
                FilePath = filePath,
                LineNumber = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.80
            };
        }
    }

    private IEnumerable<MessageConsumerInfo> DetectKafkaConsumers(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            if (methodName != "Consume" && methodName != "ConsumeAsync")
            {
                continue;
            }

            var receiver = memberAccess.Expression.ToString();
            if (!receiver.Contains("consumer", StringComparison.OrdinalIgnoreCase) &&
                !receiver.Contains("Consumer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Find containing method
            var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            yield return new MessageConsumerInfo
            {
                Broker = MessageBroker.Kafka,
                ConsumerType = ConsumerType.Consumer,
                ClassName = containingClass?.Identifier.Text,
                HandlerMethod = containingMethod?.Identifier.Text,
                IsAsync = methodName.EndsWith("Async"),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private (string? QueueName, string? TopicName, string? SubscriptionName) ExtractTriggerInfo(
        AttributeSyntax trigger)
    {
        var args = trigger.ArgumentList?.Arguments.ToList() ?? [];
        string? queueName = null;
        string? topicName = null;
        string? subscriptionName = null;

        // ServiceBusTrigger(queueName) or ServiceBusTrigger(topicName, subscriptionName)
        // QueueTrigger(queueName)
        foreach (var arg in args)
        {
            var value = arg.Expression.ToString().Trim('"');
            var name = arg.NameColon?.Name.Identifier.Text ??
                       arg.NameEquals?.Name.Identifier.Text;

            if (name != null)
            {
                switch (name.ToLowerInvariant())
                {
                    case "queuename":
                        queueName = value;
                        break;
                    case "topicname":
                        topicName = value;
                        break;
                    case "subscriptionname":
                        subscriptionName = value;
                        break;
                }
            }
            else if (args.IndexOf(arg) == 0)
            {
                // First positional argument
                queueName = value;
            }
            else if (args.IndexOf(arg) == 1)
            {
                // Second positional = subscription for ServiceBus
                subscriptionName = value;
                topicName = queueName;
                queueName = null;
            }
        }

        return (queueName, topicName, subscriptionName);
    }

    private static MessageBroker GetBrokerFromTrigger(string triggerType)
    {
        return triggerType switch
        {
            "ServiceBusTrigger" => MessageBroker.AzureServiceBus,
            "QueueTrigger" => MessageBroker.AzureStorageQueue,
            "EventHubTrigger" => MessageBroker.EventGrid,
            "KafkaTrigger" => MessageBroker.Kafka,
            _ => MessageBroker.Unknown
        };
    }

    private static string GetAttributeName(AttributeSyntax attr)
    {
        var name = attr.Name.ToString();
        if (name.EndsWith("Attribute"))
        {
            name = name[..^9];
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

        return name;
    }

    private static string? ExtractGenericArgument(string typeName)
    {
        var match = Regex.Match(typeName, @"<(.+)>");
        if (match.Success)
        {
            var args = match.Groups[1].Value;
            // Handle nested generics - just return the first argument
            var depth = 0;
            var start = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == '<')
                {
                    depth++;
                }
                else if (args[i] == '>')
                {
                    depth--;
                }
                else if (args[i] == ',' && depth == 0)
                {
                    return args[start..i].Trim();
                }
            }

            return args.Trim();
        }

        return null;
    }

    private static string[] ExtractGenericArguments(string typeName)
    {
        var match = Regex.Match(typeName, @"<(.+)>");
        if (!match.Success)
        {
            return [];
        }

        var args = match.Groups[1].Value;
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == '<')
            {
                depth++;
            }
            else if (args[i] == '>')
            {
                depth--;
            }
            else if (args[i] == ',' && depth == 0)
            {
                result.Add(args[start..i].Trim());
                start = i + 1;
            }
        }

        result.Add(args[start..].Trim());
        return result.ToArray();
    }
}

/// <summary>
/// Information about a message consumer.
/// </summary>
public record MessageConsumerInfo
{
    /// <summary>Gets the message broker.</summary>
    public required MessageBroker Broker { get; init; }

    /// <summary>Gets the consumer type.</summary>
    public required ConsumerType ConsumerType { get; init; }

    /// <summary>Gets the consumer class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the handler method name.</summary>
    public string? HandlerMethod { get; init; }

    /// <summary>Gets the message type being consumed.</summary>
    public string? MessageType { get; init; }

    /// <summary>Gets the response type (for request handlers).</summary>
    public string? ResponseType { get; init; }

    /// <summary>Gets the queue name.</summary>
    public string? QueueName { get; init; }

    /// <summary>Gets the topic name.</summary>
    public string? TopicName { get; init; }

    /// <summary>Gets the subscription name.</summary>
    public string? SubscriptionName { get; init; }

    /// <summary>Gets the trigger type for Azure Functions.</summary>
    public string? TriggerType { get; init; }

    /// <summary>Gets whether the consumer is async.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets whether this is a batch consumer.</summary>
    public bool IsBatchConsumer { get; init; }

    /// <summary>Gets whether this is a streaming handler.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Types of consumers.
/// </summary>
public enum ConsumerType
{
    /// <summary>Generic consumer (IConsumer).</summary>
    Consumer,

    /// <summary>MediatR request handler.</summary>
    RequestHandler,

    /// <summary>MediatR notification handler.</summary>
    NotificationHandler,

    /// <summary>MediatR stream handler.</summary>
    StreamHandler,

    /// <summary>MassTransit saga.</summary>
    Saga,

    /// <summary>Azure Function trigger.</summary>
    AzureFunctionTrigger,

    /// <summary>Event handler.</summary>
    EventHandler
}
