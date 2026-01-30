// -----------------------------------------------------------------------
// <copyright file="GrpcServiceAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect gRPC services and client calls.
/// </summary>
public sealed class GrpcServiceAnalyzer
{
    private static readonly Regex GrpcBaseClassPattern = new(
        @"^(?<ServiceName>\w+)\.(?<ServiceName2>\w+)Base$",
        RegexOptions.Compiled);

    private static readonly Regex GrpcClientPattern = new(
        @"^(?<ServiceName>\w+)\.(?<ServiceName2>\w+)Client$",
        RegexOptions.Compiled);

    /// <summary>
    /// Analyzes a syntax tree for gRPC services and clients.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected gRPC operations.</returns>
    public IReadOnlyList<GrpcOperationInfo> Analyze(SyntaxTree tree, string filePath)
    {
        var operations = new List<GrpcOperationInfo>();
        var root = tree.GetRoot();

        operations.AddRange(this.DetectGrpcServices(root, filePath));
        operations.AddRange(this.DetectGrpcClientCalls(root, filePath));

        return operations;
    }

    private IEnumerable<GrpcOperationInfo> DetectGrpcServices(SyntaxNode root, string filePath)
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

                // Check for pattern: ServiceName.ServiceNameBase
                var match = GrpcBaseClassPattern.Match(typeName);
                if (!match.Success)
                {
                    // Also check for simpler pattern ending in Base
                    if (!typeName.EndsWith("Base"))
                    {
                        continue;
                    }
                }

                var serviceName = match.Success
                    ? match.Groups["ServiceName"].Value
                    : typeName[..^4]; // Remove "Base"

                // Find all public methods (gRPC method implementations)
                var methods = classDecl.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(mod =>
                        mod.IsKind(SyntaxKind.PublicKeyword) || mod.IsKind(SyntaxKind.OverrideKeyword)));

                foreach (var method in methods)
                {
                    var grpcMethodType = this.DetermineGrpcMethodType(method);
                    var requestType = this.GetRequestType(method);
                    var responseType = this.GetResponseType(method);

                    yield return new GrpcOperationInfo
                    {
                        OperationType = GrpcOperationType.ServiceMethod,
                        ServiceName = serviceName,
                        MethodName = method.Identifier.Text,
                        ClassName = classDecl.Identifier.Text,
                        RequestType = requestType,
                        ResponseType = responseType,
                        GrpcMethodType = grpcMethodType,
                        IsAsync = method.ReturnType.ToString().Contains("Task"),
                        FilePath = filePath,
                        LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Confidence = 0.90
                    };
                }

                // Also add the service definition itself
                yield return new GrpcOperationInfo
                {
                    OperationType = GrpcOperationType.ServiceDefinition,
                    ServiceName = serviceName,
                    ClassName = classDecl.Identifier.Text,
                    BaseClassName = typeName,
                    FilePath = filePath,
                    LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Confidence = 0.95
                };
            }
        }
    }

    private IEnumerable<GrpcOperationInfo> DetectGrpcClientCalls(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var receiver = memberAccess.Expression.ToString();
            var methodName = memberAccess.Name.Identifier.Text;

            // Check if receiver looks like a gRPC client
            // Patterns: _ordersClient, _userClient, SomeService.SomeServiceClient, etc.
            var isGrpcClient = receiver.EndsWith("Client") ||
                              (receiver.Contains("Client", StringComparison.OrdinalIgnoreCase) &&
                               !receiver.Contains("HttpClient", StringComparison.OrdinalIgnoreCase));

            if (!isGrpcClient)
            {
                continue;
            }

            // Check if method name ends with Async (common gRPC pattern)
            var isAsync = methodName.EndsWith("Async");
            var baseMethodName = isAsync ? methodName[..^5] : methodName;

            // Skip common non-gRPC methods
            if (baseMethodName is "Dispose" or "ToString" or "GetHashCode" or "Equals" or "Create")
            {
                continue;
            }

            // Determine if it's a streaming call
            var grpcMethodType = this.InferGrpcMethodTypeFromCall(invocation, methodName);

            // Extract request/response types from arguments
            var requestInfo = this.ExtractCallRequestInfo(invocation);

            yield return new GrpcOperationInfo
            {
                OperationType = GrpcOperationType.ClientCall,
                ServiceName = ExtractServiceNameFromClient(receiver),
                MethodName = baseMethodName,
                ClientExpression = receiver,
                RequestType = requestInfo.Type,
                RequestVariable = requestInfo.Variable,
                GrpcMethodType = grpcMethodType,
                IsAsync = isAsync || this.IsAwaited(invocation),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private GrpcMethodType DetermineGrpcMethodType(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        var returnType = method.ReturnType.ToString();

        var hasStreamRequest = parameters.Any(p =>
            p.Type?.ToString().Contains("IAsyncStreamReader") == true);
        var hasStreamResponse = parameters.Any(p =>
            p.Type?.ToString().Contains("IServerStreamWriter") == true);

        if (hasStreamRequest && hasStreamResponse)
        {
            return GrpcMethodType.BidirectionalStreaming;
        }

        if (hasStreamRequest)
        {
            return GrpcMethodType.ClientStreaming;
        }

        if (hasStreamResponse)
        {
            return GrpcMethodType.ServerStreaming;
        }

        return GrpcMethodType.Unary;
    }

    private GrpcMethodType InferGrpcMethodTypeFromCall(InvocationExpressionSyntax invocation, string methodName)
    {
        var text = invocation.ToString();

        // Check for streaming patterns in method names or types
        if (text.Contains("AsyncServerStreamingCall") ||
            methodName.Contains("Stream"))
        {
            return GrpcMethodType.ServerStreaming;
        }

        if (text.Contains("AsyncClientStreamingCall"))
        {
            return GrpcMethodType.ClientStreaming;
        }

        if (text.Contains("AsyncDuplexStreamingCall"))
        {
            return GrpcMethodType.BidirectionalStreaming;
        }

        return GrpcMethodType.Unary;
    }

    private string? GetRequestType(MethodDeclarationSyntax method)
    {
        // First parameter is usually the request (excluding ServerCallContext and stream types)
        var param = method.ParameterList.Parameters
            .FirstOrDefault(p =>
                !p.Type?.ToString().Contains("ServerCallContext") == true &&
                !p.Type?.ToString().Contains("IAsyncStreamReader") == true &&
                !p.Type?.ToString().Contains("IServerStreamWriter") == true);

        return param?.Type?.ToString();
    }

    private string? GetResponseType(MethodDeclarationSyntax method)
    {
        var returnType = method.ReturnType.ToString();

        // Task<SomeResponse> -> SomeResponse
        var match = Regex.Match(returnType, @"Task<(.+)>");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // Check if there's a stream writer parameter
        var streamWriter = method.ParameterList.Parameters
            .FirstOrDefault(p => p.Type?.ToString().Contains("IServerStreamWriter") == true);

        if (streamWriter != null)
        {
            var writerMatch = Regex.Match(streamWriter.Type!.ToString(), @"IServerStreamWriter<(.+)>");
            if (writerMatch.Success)
            {
                return writerMatch.Groups[1].Value;
            }
        }

        return returnType == "Task" ? null : returnType;
    }

    private (string? Type, string? Variable) ExtractCallRequestInfo(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return (null, null);
        }

        var firstArg = args[0].Expression;

        if (firstArg is ObjectCreationExpressionSyntax objectCreation)
        {
            return (objectCreation.Type.ToString(), null);
        }

        if (firstArg is IdentifierNameSyntax identifier)
        {
            return (null, identifier.Identifier.Text);
        }

        return (null, firstArg.ToString());
    }

    private static string ExtractServiceNameFromClient(string clientExpression)
    {
        // Examples:
        // _userClient -> user
        // _someService.SomeServiceClient -> SomeService
        // _ordersClient -> orders
        // _client -> (use surrounding context)
        var parts = clientExpression.Split('.');
        var lastPart = parts[^1];

        // Handle fully qualified: Orders.OrdersClient
        if (parts.Length > 1 && lastPart.EndsWith("Client"))
        {
            // Use the part before Client
            return parts[^2];
        }

        // Remove underscore prefix
        if (lastPart.StartsWith("_"))
        {
            lastPart = lastPart[1..];
        }

        // Handle patterns like "ordersClient" -> "orders"
        if (lastPart.EndsWith("Client", StringComparison.OrdinalIgnoreCase) && lastPart.Length > 6)
        {
            return lastPart[..^6];
        }

        // If just "_client" or "client", try to find type from context
        if (lastPart.Equals("client", StringComparison.OrdinalIgnoreCase))
        {
            // Return the original expression as we can't determine the service
            return clientExpression;
        }

        return lastPart;
    }

    private bool IsAwaited(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is AwaitExpressionSyntax;
    }
}

/// <summary>
/// Information about a gRPC operation.
/// </summary>
public record GrpcOperationInfo
{
    /// <summary>Gets the operation type.</summary>
    public required GrpcOperationType OperationType { get; init; }

    /// <summary>Gets the service name.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Gets the method name.</summary>
    public string? MethodName { get; init; }

    /// <summary>Gets the implementation class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the base class name.</summary>
    public string? BaseClassName { get; init; }

    /// <summary>Gets the client expression (for calls).</summary>
    public string? ClientExpression { get; init; }

    /// <summary>Gets the request type.</summary>
    public string? RequestType { get; init; }

    /// <summary>Gets the request variable name.</summary>
    public string? RequestVariable { get; init; }

    /// <summary>Gets the response type.</summary>
    public string? ResponseType { get; init; }

    /// <summary>Gets the gRPC method type.</summary>
    public GrpcMethodType GrpcMethodType { get; init; }

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
/// Types of gRPC operations.
/// </summary>
public enum GrpcOperationType
{
    /// <summary>Service definition.</summary>
    ServiceDefinition,

    /// <summary>Service method implementation.</summary>
    ServiceMethod,

    /// <summary>Client call.</summary>
    ClientCall
}

/// <summary>
/// Types of gRPC methods.
/// </summary>
public enum GrpcMethodType
{
    /// <summary>Unary (single request, single response).</summary>
    Unary,

    /// <summary>Server streaming.</summary>
    ServerStreaming,

    /// <summary>Client streaming.</summary>
    ClientStreaming,

    /// <summary>Bidirectional streaming.</summary>
    BidirectionalStreaming
}
