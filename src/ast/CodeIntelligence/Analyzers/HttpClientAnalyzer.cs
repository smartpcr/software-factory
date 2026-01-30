// -----------------------------------------------------------------------
// <copyright file="HttpClientAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect HTTP client calls (HttpClient, Refit, RestSharp, etc.).
/// </summary>
public sealed class HttpClientAnalyzer
{
    private static readonly Dictionary<string, string> HttpClientMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        { "GetAsync", "GET" },
        { "GetStringAsync", "GET" },
        { "GetStreamAsync", "GET" },
        { "GetByteArrayAsync", "GET" },
        { "GetFromJsonAsync", "GET" },
        { "PostAsync", "POST" },
        { "PostAsJsonAsync", "POST" },
        { "PutAsync", "PUT" },
        { "PutAsJsonAsync", "PUT" },
        { "PatchAsync", "PATCH" },
        { "DeleteAsync", "DELETE" },
        { "DeleteFromJsonAsync", "DELETE" },
        { "SendAsync", "CUSTOM" }
    };

    private static readonly HashSet<string> HttpClientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpClient", "_httpClient", "httpClient", "_client", "client"
    };

    private static readonly Regex RefitClientPattern = new(@"^_?\w+Client$", RegexOptions.Compiled);

    /// <summary>
    /// Analyzes a syntax tree for HTTP client calls.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected HTTP client calls.</returns>
    public IReadOnlyList<HttpClientCallInfo> Analyze(SyntaxTree tree, string filePath)
    {
        var calls = new List<HttpClientCallInfo>();
        var root = tree.GetRoot();

        // Detect HttpClient calls
        calls.AddRange(this.DetectHttpClientCalls(root, filePath));

        // Detect Refit client calls
        calls.AddRange(this.DetectRefitCalls(root, filePath));

        // Detect RestSharp calls
        calls.AddRange(this.DetectRestSharpCalls(root, filePath));

        // Detect typed HttpClient (IHttpClientFactory)
        calls.AddRange(this.DetectTypedClientCalls(root, filePath));

        return calls;
    }

    private IEnumerable<HttpClientCallInfo> DetectHttpClientCalls(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;
            if (!HttpClientMethods.TryGetValue(methodName, out var httpMethod))
            {
                continue;
            }

            // Check if receiver looks like HttpClient
            var receiver = memberAccess.Expression.ToString();
            var isHttpClient = HttpClientTypes.Any(t => receiver.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                              receiver.EndsWith("Client", StringComparison.OrdinalIgnoreCase);

            if (!isHttpClient)
            {
                continue;
            }

            var urlInfo = this.ExtractUrl(invocation);
            var bodyInfo = this.ExtractRequestBody(invocation, methodName);
            var headerInfo = this.ExtractHeaders(invocation);

            yield return new HttpClientCallInfo
            {
                ClientType = HttpClientType.HttpClient,
                HttpMethod = httpMethod == "CUSTOM" ? this.InferMethodFromSendAsync(invocation) : httpMethod,
                TargetUrl = urlInfo.Url,
                UrlIsLiteral = urlInfo.IsLiteral,
                RequestBodyType = bodyInfo.Type,
                RequestBodyVariable = bodyInfo.Variable,
                Headers = headerInfo,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = true,
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            };
        }
    }

    private IEnumerable<HttpClientCallInfo> DetectRefitCalls(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var receiver = memberAccess.Expression.ToString();

            // Check if receiver matches Refit client pattern (e.g., _userClient, _apiClient)
            if (!RefitClientPattern.IsMatch(receiver) ||
                receiver.Equals("HttpClient", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip if it's a known HttpClient method
            var methodName = memberAccess.Name.Identifier.Text;
            if (HttpClientMethods.ContainsKey(methodName))
            {
                continue;
            }

            // Check if it's awaited (Refit calls are typically async)
            var isAwaited = invocation.Parent is AwaitExpressionSyntax;
            if (!isAwaited)
            {
                continue;
            }

            // Infer HTTP method from method name
            var httpMethod = this.InferHttpMethodFromMethodName(methodName);

            yield return new HttpClientCallInfo
            {
                ClientType = HttpClientType.Refit,
                HttpMethod = httpMethod,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = true,
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private IEnumerable<HttpClientCallInfo> DetectRestSharpCalls(SyntaxNode root, string filePath)
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

            // RestSharp patterns
            if (!methodName.Equals("ExecuteAsync", StringComparison.OrdinalIgnoreCase) &&
                !methodName.Equals("ExecuteGetAsync", StringComparison.OrdinalIgnoreCase) &&
                !methodName.Equals("ExecutePostAsync", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!receiver.Contains("RestClient", StringComparison.OrdinalIgnoreCase) &&
                !receiver.Contains("restClient", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var httpMethod = methodName switch
            {
                "ExecuteGetAsync" => "GET",
                "ExecutePostAsync" => "POST",
                _ => "CUSTOM"
            };

            yield return new HttpClientCallInfo
            {
                ClientType = HttpClientType.RestSharp,
                HttpMethod = httpMethod,
                ReceiverExpression = receiver,
                MethodName = methodName,
                IsAsync = methodName.EndsWith("Async"),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private IEnumerable<HttpClientCallInfo> DetectTypedClientCalls(SyntaxNode root, string filePath)
    {
        // Find classes that have HttpClient injected
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            // Check constructor for HttpClient parameter
            var constructors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
            var hasHttpClientParam = constructors.Any(c =>
                c.ParameterList.Parameters.Any(p =>
                    p.Type?.ToString().Contains("HttpClient") == true ||
                    p.Type?.ToString().Contains("IHttpClientFactory") == true));

            if (!hasHttpClientParam)
            {
                continue;
            }

            // This is a typed client, find all HttpClient method calls
            var invocations = classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                {
                    continue;
                }

                var methodName = memberAccess.Name.Identifier.Text;
                if (!HttpClientMethods.TryGetValue(methodName, out var httpMethod))
                {
                    continue;
                }

                var receiver = memberAccess.Expression.ToString();
                var urlInfo = this.ExtractUrl(invocation);

                yield return new HttpClientCallInfo
                {
                    ClientType = HttpClientType.TypedClient,
                    TypedClientClass = classDecl.Identifier.Text,
                    HttpMethod = httpMethod == "CUSTOM" ? this.InferMethodFromSendAsync(invocation) : httpMethod,
                    TargetUrl = urlInfo.Url,
                    UrlIsLiteral = urlInfo.IsLiteral,
                    ReceiverExpression = receiver,
                    MethodName = methodName,
                    IsAsync = true,
                    FilePath = filePath,
                    LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Confidence = 0.90
                };
            }
        }
    }

    private (string? Url, bool IsLiteral) ExtractUrl(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return (null, false);
        }

        var firstArg = args[0].Expression;

        // String literal
        if (firstArg is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return (literal.Token.ValueText, true);
        }

        // Interpolated string
        if (firstArg is InterpolatedStringExpressionSyntax interpolated)
        {
            return (interpolated.ToString(), false);
        }

        // Variable or expression
        return (firstArg.ToString(), false);
    }

    private (string? Type, string? Variable) ExtractRequestBody(InvocationExpressionSyntax invocation, string methodName)
    {
        // POST, PUT, PATCH typically have body as second argument
        if (!methodName.StartsWith("Post") && !methodName.StartsWith("Put") && !methodName.StartsWith("Patch"))
        {
            return (null, null);
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 2)
        {
            return (null, null);
        }

        var bodyArg = args[1].Expression;

        if (bodyArg is ObjectCreationExpressionSyntax objectCreation)
        {
            return (objectCreation.Type.ToString(), null);
        }

        if (bodyArg is IdentifierNameSyntax identifier)
        {
            return (null, identifier.Identifier.Text);
        }

        return (bodyArg.ToString(), null);
    }

    private IReadOnlyList<HttpHeaderInfo> ExtractHeaders(InvocationExpressionSyntax invocation)
    {
        var headers = new List<HttpHeaderInfo>();

        // Look for DefaultRequestHeaders assignments in the same method
        var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method == null)
        {
            return headers;
        }

        var assignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>();

        foreach (var assignment in assignments)
        {
            var left = assignment.Left.ToString();
            if (!left.Contains("Headers") && !left.Contains("DefaultRequestHeaders"))
            {
                continue;
            }

            // Try to extract header name and value
            // e.g., client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (left.Contains("Authorization"))
            {
                headers.Add(new HttpHeaderInfo
                {
                    Name = "Authorization",
                    ValueExpression = assignment.Right.ToString(),
                    IsAuthHeader = true
                });
            }
        }

        // Also check for Add calls on Headers
        var headerAdds = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().Contains("Headers") &&
                       i.Expression.ToString().Contains("Add"));

        foreach (var add in headerAdds)
        {
            var args = add.ArgumentList.Arguments;
            if (args.Count >= 2)
            {
                var headerName = args[0].ToString().Trim('"');
                var headerValue = args[1].ToString();

                headers.Add(new HttpHeaderInfo
                {
                    Name = headerName,
                    ValueExpression = headerValue,
                    IsAuthHeader = headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        return headers;
    }

    private string InferMethodFromSendAsync(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return "CUSTOM";
        }

        var firstArg = args[0].Expression.ToString();

        // Look for HttpMethod in the request message creation
        if (firstArg.Contains("HttpMethod.Get"))
        {
            return "GET";
        }

        if (firstArg.Contains("HttpMethod.Post"))
        {
            return "POST";
        }

        if (firstArg.Contains("HttpMethod.Put"))
        {
            return "PUT";
        }

        if (firstArg.Contains("HttpMethod.Delete"))
        {
            return "DELETE";
        }

        if (firstArg.Contains("HttpMethod.Patch"))
        {
            return "PATCH";
        }

        return "CUSTOM";
    }

    private string InferHttpMethodFromMethodName(string methodName)
    {
        var lowerName = methodName.ToLowerInvariant();

        if (lowerName.StartsWith("get") || lowerName.StartsWith("fetch") || lowerName.StartsWith("list"))
        {
            return "GET";
        }

        if (lowerName.StartsWith("post") || lowerName.StartsWith("create") || lowerName.StartsWith("add"))
        {
            return "POST";
        }

        if (lowerName.StartsWith("put") || lowerName.StartsWith("update") || lowerName.StartsWith("replace"))
        {
            return "PUT";
        }

        if (lowerName.StartsWith("delete") || lowerName.StartsWith("remove"))
        {
            return "DELETE";
        }

        if (lowerName.StartsWith("patch") || lowerName.StartsWith("modify"))
        {
            return "PATCH";
        }

        return "UNKNOWN";
    }
}

/// <summary>
/// Information about a detected HTTP client call.
/// </summary>
public record HttpClientCallInfo
{
    /// <summary>Gets the client type.</summary>
    public required HttpClientType ClientType { get; init; }

    /// <summary>Gets the HTTP method.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Gets the target URL.</summary>
    public string? TargetUrl { get; init; }

    /// <summary>Gets whether the URL is a literal string.</summary>
    public bool UrlIsLiteral { get; init; }

    /// <summary>Gets the request body type.</summary>
    public string? RequestBodyType { get; init; }

    /// <summary>Gets the request body variable name.</summary>
    public string? RequestBodyVariable { get; init; }

    /// <summary>Gets the headers set.</summary>
    public IReadOnlyList<HttpHeaderInfo> Headers { get; init; } = [];

    /// <summary>Gets the receiver expression.</summary>
    public required string ReceiverExpression { get; init; }

    /// <summary>Gets the method name called.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the typed client class name (if applicable).</summary>
    public string? TypedClientClass { get; init; }

    /// <summary>Gets whether the call is async.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Types of HTTP clients.
/// </summary>
public enum HttpClientType
{
    /// <summary>Standard HttpClient.</summary>
    HttpClient,

    /// <summary>Typed HttpClient via IHttpClientFactory.</summary>
    TypedClient,

    /// <summary>Refit interface client.</summary>
    Refit,

    /// <summary>RestSharp client.</summary>
    RestSharp
}

/// <summary>
/// Information about an HTTP header.
/// </summary>
public record HttpHeaderInfo
{
    /// <summary>Gets the header name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the value expression.</summary>
    public required string ValueExpression { get; init; }

    /// <summary>Gets whether this is an authentication header.</summary>
    public bool IsAuthHeader { get; init; }
}
