// -----------------------------------------------------------------------
// <copyright file="ApiEndpointDetector.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Detectors;

using CodeIntelligence.Analyzers;
using CodeIntelligence.Models;
using CodeIntelligence.Routes;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Detects API endpoints and communication patterns in C# code.
/// </summary>
public sealed class ApiEndpointDetector
{
    private static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private readonly HttpEndpointAnalyzer httpEndpointAnalyzer = new();
    private readonly HttpClientAnalyzer httpClientAnalyzer = new();
    private readonly MessagePublisherAnalyzer messagePublisherAnalyzer = new();
    private readonly MessageConsumerAnalyzer messageConsumerAnalyzer = new();
    private readonly GrpcServiceAnalyzer grpcServiceAnalyzer = new();
    private readonly RouteTemplateResolver routeResolver = new();

    /// <summary>
    /// Analyzes a directory for API endpoints and communication patterns.
    /// </summary>
    /// <param name="directory">The directory to analyze.</param>
    /// <returns>API analysis result.</returns>
    public async Task<ApiAnalysisResult> AnalyzeAsync(string directory)
    {
        var result = new ApiAnalysisResult();

        var csFiles = Directory.GetFiles(directory, "*.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                       !f.Contains("/obj/") && !f.Contains("/bin/"));

        foreach (var file in csFiles)
        {
            try
            {
                await this.AnalyzeFileAsync(file, result);
                result.FilesAnalyzed++;
            }
            catch (Exception ex)
            {
                result.Errors.Add(new AnalysisError
                {
                    FilePath = file,
                    Message = ex.Message,
                    ErrorType = "ParseError"
                });
            }
        }

        // Build relationships
        this.BuildRelationships(result);

        return result;
    }

    /// <summary>
    /// Analyzes a single file for API endpoints.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="result">The result to populate.</param>
    private async Task AnalyzeFileAsync(string filePath, ApiAnalysisResult result)
    {
        var content = await File.ReadAllTextAsync(filePath);
        var tree = CSharpSyntaxTree.ParseText(content, path: filePath);

        // HTTP Endpoints
        var endpoints = this.httpEndpointAnalyzer.Analyze(tree, filePath);
        result.HttpEndpoints.AddRange(endpoints);

        // HTTP Client Calls
        var clientCalls = this.httpClientAnalyzer.Analyze(tree, filePath);
        result.HttpClientCalls.AddRange(clientCalls);

        // Message Publishers
        var publishers = this.messagePublisherAnalyzer.Analyze(tree, filePath);
        result.MessagePublishers.AddRange(publishers);

        // Message Consumers
        var consumers = this.messageConsumerAnalyzer.Analyze(tree, filePath);
        result.MessageConsumers.AddRange(consumers);

        // gRPC Operations
        var grpcOps = this.grpcServiceAnalyzer.Analyze(tree, filePath);
        result.GrpcOperations.AddRange(grpcOps);
    }

    private void BuildRelationships(ApiAnalysisResult result)
    {
        // HTTP Endpoint relationships
        foreach (var endpoint in result.HttpEndpoints)
        {
            var sourceNode = new RelationshipNode
            {
                FullyQualifiedName = $"{endpoint.ControllerName ?? "MinimalApi"}.{endpoint.ActionName}",
                Name = endpoint.ControllerName ?? "MinimalApi",
                NodeType = "Controller",
                FilePath = endpoint.FilePath,
                LineNumber = endpoint.LineNumber
            };

            var targetNode = new RelationshipNode
            {
                FullyQualifiedName = $"{endpoint.HttpMethod}:{endpoint.RouteTemplate}",
                Name = $"{endpoint.HttpMethod} {endpoint.RouteTemplate}",
                NodeType = "HttpEndpoint",
                FilePath = endpoint.FilePath,
                LineNumber = endpoint.LineNumber
            };

            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"api-endpoint-{endpoint.FilePath}:{endpoint.LineNumber}",
                Category = "api",
                RelationshipType = "ExposesEndpoint",
                Source = sourceNode,
                Target = targetNode,
                Confidence = (float)endpoint.Confidence,
                DetectionStrategy = "ast",
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "ast",
                        Description = $"HTTP {endpoint.HttpMethod} endpoint at {endpoint.RouteTemplate}",
                        Weight = 0.9f,
                        FilePath = endpoint.FilePath,
                        LineNumber = endpoint.LineNumber
                    }
                },
                Properties = new Dictionary<string, object>
                {
                    ["httpMethod"] = endpoint.HttpMethod,
                    ["route"] = endpoint.RouteTemplate,
                    ["requiresAuth"] = endpoint.RequiresAuthorization
                }
            });
        }

        // HTTP Client Call relationships
        foreach (var call in result.HttpClientCalls)
        {
            var sourceNode = new RelationshipNode
            {
                FullyQualifiedName = call.TypedClientClass ?? call.ReceiverExpression,
                Name = call.TypedClientClass ?? "HttpClient",
                NodeType = "Class",
                FilePath = call.FilePath,
                LineNumber = call.LineNumber
            };

            var targetNode = new RelationshipNode
            {
                FullyQualifiedName = call.TargetUrl ?? "external-service",
                Name = call.TargetUrl ?? "External Service",
                NodeType = "ExternalService",
                FilePath = call.FilePath,
                LineNumber = call.LineNumber
            };

            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"api-client-{call.FilePath}:{call.LineNumber}",
                Category = "api",
                RelationshipType = "CallsExternal",
                Source = sourceNode,
                Target = targetNode,
                Confidence = (float)call.Confidence,
                DetectionStrategy = "ast",
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "ast",
                        Description = $"HTTP {call.HttpMethod} call via {call.ClientType}",
                        Weight = 0.85f,
                        FilePath = call.FilePath,
                        LineNumber = call.LineNumber
                    }
                },
                Properties = new Dictionary<string, object>
                {
                    ["httpMethod"] = call.HttpMethod,
                    ["clientType"] = call.ClientType.ToString()
                }
            });
        }

        // Message Publisher relationships
        foreach (var publisher in result.MessagePublishers)
        {
            var sourceNode = new RelationshipNode
            {
                FullyQualifiedName = $"{publisher.ReceiverExpression}.{publisher.MethodName}",
                Name = publisher.ReceiverExpression,
                NodeType = "MessagePublisher",
                FilePath = publisher.FilePath,
                LineNumber = publisher.LineNumber
            };

            var targetNode = new RelationshipNode
            {
                FullyQualifiedName = $"{publisher.Broker}:{publisher.DestinationName ?? "unknown"}",
                Name = publisher.DestinationName ?? publisher.Broker.ToString(),
                NodeType = "MessageDestination",
                FilePath = publisher.FilePath,
                LineNumber = publisher.LineNumber
            };

            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"msg-publish-{publisher.FilePath}:{publisher.LineNumber}",
                Category = "api",
                RelationshipType = "PublishesTo",
                Source = sourceNode,
                Target = targetNode,
                Confidence = (float)publisher.Confidence,
                DetectionStrategy = "ast",
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "ast",
                        Description = $"Message publish to {publisher.Broker}",
                        Weight = 0.85f,
                        FilePath = publisher.FilePath,
                        LineNumber = publisher.LineNumber
                    }
                },
                Properties = new Dictionary<string, object>
                {
                    ["broker"] = publisher.Broker.ToString(),
                    ["messageType"] = publisher.MessageType ?? "unknown",
                    ["operation"] = publisher.OperationType.ToString()
                }
            });
        }

        // Message Consumer relationships
        foreach (var consumer in result.MessageConsumers)
        {
            var sourceNode = new RelationshipNode
            {
                FullyQualifiedName = $"{consumer.Broker}:{consumer.QueueName ?? consumer.TopicName ?? "unknown"}",
                Name = consumer.QueueName ?? consumer.TopicName ?? consumer.Broker.ToString(),
                NodeType = "MessageSource",
                FilePath = consumer.FilePath,
                LineNumber = consumer.LineNumber
            };

            var targetNode = new RelationshipNode
            {
                FullyQualifiedName = $"{consumer.ClassName ?? "Handler"}.{consumer.HandlerMethod ?? "Handle"}",
                Name = consumer.ClassName ?? consumer.HandlerMethod ?? "Handler",
                NodeType = "MessageConsumer",
                FilePath = consumer.FilePath,
                LineNumber = consumer.LineNumber
            };

            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"msg-consume-{consumer.FilePath}:{consumer.LineNumber}",
                Category = "api",
                RelationshipType = "ConsumesFrom",
                Source = sourceNode,
                Target = targetNode,
                Confidence = (float)consumer.Confidence,
                DetectionStrategy = "ast",
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "ast",
                        Description = $"Message consumer from {consumer.Broker}",
                        Weight = 0.85f,
                        FilePath = consumer.FilePath,
                        LineNumber = consumer.LineNumber
                    }
                },
                Properties = new Dictionary<string, object>
                {
                    ["broker"] = consumer.Broker.ToString(),
                    ["messageType"] = consumer.MessageType ?? "unknown",
                    ["consumerType"] = consumer.ConsumerType.ToString()
                }
            });
        }

        // gRPC Service relationships
        foreach (var grpc in result.GrpcOperations.Where(g => g.OperationType == GrpcOperationType.ServiceDefinition))
        {
            var sourceNode = new RelationshipNode
            {
                FullyQualifiedName = $"grpc.{grpc.ServiceName}",
                Name = grpc.ServiceName ?? "Unknown",
                NodeType = "GrpcService",
                FilePath = grpc.FilePath,
                LineNumber = grpc.LineNumber
            };

            var targetNode = new RelationshipNode
            {
                FullyQualifiedName = grpc.ClassName ?? "Unknown",
                Name = grpc.ClassName ?? "Unknown",
                NodeType = "GrpcImplementation",
                FilePath = grpc.FilePath,
                LineNumber = grpc.LineNumber
            };

            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"grpc-service-{grpc.FilePath}:{grpc.LineNumber}",
                Category = "api",
                RelationshipType = "ImplementsGrpc",
                Source = sourceNode,
                Target = targetNode,
                Confidence = (float)grpc.Confidence,
                DetectionStrategy = "ast",
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "ast",
                        Description = $"gRPC service implementation: {grpc.ServiceName}",
                        Weight = 0.9f,
                        FilePath = grpc.FilePath,
                        LineNumber = grpc.LineNumber
                    }
                },
                Properties = new Dictionary<string, object>()
            });
        }

        // gRPC Client Call relationships
        foreach (var grpc in result.GrpcOperations.Where(g => g.OperationType == GrpcOperationType.ClientCall))
        {
            var sourceNode = new RelationshipNode
            {
                FullyQualifiedName = grpc.ClientExpression ?? "Unknown",
                Name = grpc.ClientExpression ?? "Unknown",
                NodeType = "GrpcCaller",
                FilePath = grpc.FilePath,
                LineNumber = grpc.LineNumber
            };

            var targetNode = new RelationshipNode
            {
                FullyQualifiedName = $"grpc.{grpc.ServiceName}.{grpc.MethodName}",
                Name = $"{grpc.ServiceName}.{grpc.MethodName}",
                NodeType = "GrpcMethod",
                FilePath = grpc.FilePath,
                LineNumber = grpc.LineNumber
            };

            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"grpc-call-{grpc.FilePath}:{grpc.LineNumber}",
                Category = "api",
                RelationshipType = "CallsGrpc",
                Source = sourceNode,
                Target = targetNode,
                Confidence = (float)grpc.Confidence,
                DetectionStrategy = "ast",
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "ast",
                        Description = $"gRPC client call: {grpc.ServiceName}.{grpc.MethodName}",
                        Weight = 0.85f,
                        FilePath = grpc.FilePath,
                        LineNumber = grpc.LineNumber
                    }
                },
                Properties = new Dictionary<string, object>
                {
                    ["methodType"] = grpc.GrpcMethodType.ToString(),
                    ["isAsync"] = grpc.IsAsync
                }
            });
        }
    }
}

/// <summary>
/// Result of API analysis.
/// </summary>
public class ApiAnalysisResult
{
    /// <summary>Gets the number of files analyzed.</summary>
    public int FilesAnalyzed { get; set; }

    /// <summary>Gets the detected HTTP endpoints.</summary>
    public List<HttpEndpointInfo> HttpEndpoints { get; } = [];

    /// <summary>Gets the detected HTTP client calls.</summary>
    public List<HttpClientCallInfo> HttpClientCalls { get; } = [];

    /// <summary>Gets the detected message publishers.</summary>
    public List<MessagePublishInfo> MessagePublishers { get; } = [];

    /// <summary>Gets the detected message consumers.</summary>
    public List<MessageConsumerInfo> MessageConsumers { get; } = [];

    /// <summary>Gets the detected gRPC operations.</summary>
    public List<GrpcOperationInfo> GrpcOperations { get; } = [];

    /// <summary>Gets the detected relationships.</summary>
    public List<DetectedRelationship> DetectedRelationships { get; } = [];

    /// <summary>Gets the errors encountered.</summary>
    public List<AnalysisError> Errors { get; } = [];
}
