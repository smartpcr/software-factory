// -----------------------------------------------------------------------
// <copyright file="HttpEndpointAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect HTTP API endpoints (attribute-based, minimal API, and convention-based).
/// </summary>
public sealed class HttpEndpointAnalyzer
{
    private static readonly HashSet<string> HttpMethodAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions",
        "HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute", "HttpDeleteAttribute",
        "HttpPatchAttribute", "HttpHeadAttribute", "HttpOptionsAttribute"
    };

    private static readonly HashSet<string> MinimalApiMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods"
    };

    private static readonly HashSet<string> ControllerBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Controller", "ControllerBase", "ApiController"
    };

    /// <summary>
    /// Analyzes a syntax tree for HTTP endpoints.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected HTTP endpoints.</returns>
    public IReadOnlyList<HttpEndpointInfo> Analyze(SyntaxTree tree, string filePath)
    {
        var endpoints = new List<HttpEndpointInfo>();
        var root = tree.GetRoot();

        // Detect attribute-based endpoints
        endpoints.AddRange(this.DetectAttributeEndpoints(root, filePath));

        // Detect minimal API endpoints
        endpoints.AddRange(this.DetectMinimalApiEndpoints(root, filePath));

        // Detect convention-based endpoints
        endpoints.AddRange(this.DetectConventionEndpoints(root, filePath));

        return endpoints;
    }

    private IEnumerable<HttpEndpointInfo> DetectAttributeEndpoints(SyntaxNode root, string filePath)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var httpAttributes = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .Where(a => HttpMethodAttributes.Contains(GetAttributeName(a)))
                .ToList();

            if (httpAttributes.Count == 0)
            {
                continue;
            }

            var classDecl = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl == null)
            {
                continue;
            }

            var controllerInfo = this.GetControllerInfo(classDecl);
            var routeInfo = this.GetRouteInfo(method, classDecl);

            foreach (var httpAttr in httpAttributes)
            {
                var httpMethod = GetHttpMethodFromAttribute(httpAttr);
                var actionRoute = GetRouteFromAttribute(httpAttr);
                var fullRoute = this.CombineRoutes(controllerInfo.Route, routeInfo.ActionRoute ?? actionRoute);

                yield return new HttpEndpointInfo
                {
                    EndpointType = HttpEndpointType.AttributeBased,
                    HttpMethod = httpMethod,
                    RouteTemplate = fullRoute,
                    ControllerName = controllerInfo.Name,
                    ActionName = method.Identifier.Text,
                    ReturnType = method.ReturnType.ToString(),
                    Parameters = this.ExtractParameters(method),
                    IsAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                    RequiresAuthorization = this.HasAuthorizeAttribute(method, classDecl),
                    AllowsAnonymous = this.HasAllowAnonymousAttribute(method),
                    ProducesContentTypes = this.GetProducesTypes(method, classDecl),
                    ConsumesContentTypes = this.GetConsumesTypes(method, classDecl),
                    FilePath = filePath,
                    LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Confidence = 0.95
                };
            }
        }
    }

    private IEnumerable<HttpEndpointInfo> DetectMinimalApiEndpoints(SyntaxNode root, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var methodName = GetInvocationMethodName(invocation);
            if (!MinimalApiMethods.Contains(methodName))
            {
                continue;
            }

            var args = invocation.ArgumentList.Arguments;
            if (args.Count < 2)
            {
                continue;
            }

            var routeArg = args[0].Expression.ToString().Trim('"');
            var handlerArg = args[1].Expression;

            var httpMethod = methodName.Replace("Map", "").ToUpperInvariant();
            if (httpMethod == "METHODS")
            {
                httpMethod = "CUSTOM";
            }

            // Extract handler info
            var handlerInfo = this.ExtractMinimalApiHandler(handlerArg);

            yield return new HttpEndpointInfo
            {
                EndpointType = HttpEndpointType.MinimalApi,
                HttpMethod = httpMethod,
                RouteTemplate = routeArg,
                ActionName = handlerInfo.Name,
                ReturnType = handlerInfo.ReturnType,
                Parameters = handlerInfo.Parameters,
                IsAsync = handlerInfo.IsAsync,
                RequiresAuthorization = this.HasAuthorizationChain(invocation),
                FilePath = filePath,
                LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            };
        }
    }

    private IEnumerable<HttpEndpointInfo> DetectConventionEndpoints(SyntaxNode root, string filePath)
    {
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            if (!this.IsControllerClass(classDecl))
            {
                continue;
            }

            // Skip if class has [NonController] attribute
            if (this.HasAttribute(classDecl, "NonController"))
            {
                continue;
            }

            var controllerInfo = this.GetControllerInfo(classDecl);

            // Find public methods that could be actions
            var methods = classDecl.Members
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));

            foreach (var method in methods)
            {
                // Skip if method has [NonAction] attribute
                if (this.HasAttribute(method, "NonAction"))
                {
                    continue;
                }

                // Skip if already has explicit HTTP method attribute
                var hasHttpAttr = method.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Any(a => HttpMethodAttributes.Contains(GetAttributeName(a)));

                if (hasHttpAttr)
                {
                    continue;
                }

                // Convention: method name prefixed with HTTP verb
                var httpMethod = this.InferHttpMethodFromName(method.Identifier.Text);
                var routeInfo = this.GetRouteInfo(method, classDecl);

                yield return new HttpEndpointInfo
                {
                    EndpointType = HttpEndpointType.ConventionBased,
                    HttpMethod = httpMethod,
                    RouteTemplate = routeInfo.FullRoute ?? $"{controllerInfo.Route}/{method.Identifier.Text}",
                    ControllerName = controllerInfo.Name,
                    ActionName = method.Identifier.Text,
                    ReturnType = method.ReturnType.ToString(),
                    Parameters = this.ExtractParameters(method),
                    IsAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                    RequiresAuthorization = this.HasAuthorizeAttribute(method, classDecl),
                    FilePath = filePath,
                    LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Confidence = 0.80
                };
            }
        }
    }

    private bool IsControllerClass(ClassDeclarationSyntax classDecl)
    {
        // Check naming convention
        if (classDecl.Identifier.Text.EndsWith("Controller"))
        {
            return true;
        }

        // Check base type
        var baseList = classDecl.BaseList;
        if (baseList != null)
        {
            foreach (var baseType in baseList.Types)
            {
                var typeName = baseType.Type.ToString();
                if (ControllerBaseTypes.Any(ct => typeName.Contains(ct)))
                {
                    return true;
                }
            }
        }

        // Check [ApiController] attribute
        return this.HasAttribute(classDecl, "ApiController");
    }

    private (string Name, string? Route) GetControllerInfo(ClassDeclarationSyntax classDecl)
    {
        var name = classDecl.Identifier.Text;
        if (name.EndsWith("Controller"))
        {
            name = name[..^10];
        }

        // Get route from [Route] attribute
        var routeAttr = classDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => GetAttributeName(a) == "Route");

        string? route = null;
        if (routeAttr != null)
        {
            route = GetRouteFromAttribute(routeAttr);
        }

        // Replace [controller] placeholder
        route ??= $"api/{name.ToLowerInvariant()}";
        route = route.Replace("[controller]", name.ToLowerInvariant());

        return (name, route);
    }

    private (string? ActionRoute, string? FullRoute) GetRouteInfo(
        MethodDeclarationSyntax method,
        ClassDeclarationSyntax classDecl)
    {
        var routeAttr = method.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => GetAttributeName(a) == "Route");

        string? actionRoute = null;
        if (routeAttr != null)
        {
            actionRoute = GetRouteFromAttribute(routeAttr);
        }

        var controllerInfo = this.GetControllerInfo(classDecl);
        var fullRoute = this.CombineRoutes(controllerInfo.Route, actionRoute);

        // Replace [action] placeholder
        fullRoute = fullRoute?.Replace("[action]", method.Identifier.Text.ToLowerInvariant());

        return (actionRoute, fullRoute);
    }

    private string CombineRoutes(string? controllerRoute, string? actionRoute)
    {
        if (string.IsNullOrEmpty(actionRoute))
        {
            return controllerRoute ?? "";
        }

        if (actionRoute.StartsWith("/") || actionRoute.StartsWith("~/"))
        {
            return actionRoute.TrimStart('~', '/');
        }

        if (string.IsNullOrEmpty(controllerRoute))
        {
            return actionRoute;
        }

        return $"{controllerRoute.TrimEnd('/')}/{actionRoute.TrimStart('/')}";
    }

    private IReadOnlyList<EndpointParameter> ExtractParameters(MethodDeclarationSyntax method)
    {
        var parameters = new List<EndpointParameter>();

        foreach (var param in method.ParameterList.Parameters)
        {
            var source = this.GetParameterSource(param);

            parameters.Add(new EndpointParameter
            {
                Name = param.Identifier.Text,
                Type = param.Type?.ToString() ?? "object",
                Source = source,
                IsOptional = param.Default != null,
                DefaultValue = param.Default?.Value.ToString()
            });
        }

        return parameters;
    }

    private ParameterSource GetParameterSource(ParameterSyntax param)
    {
        var attrs = param.AttributeLists.SelectMany(al => al.Attributes);

        foreach (var attr in attrs)
        {
            var name = GetAttributeName(attr);
            return name switch
            {
                "FromBody" => ParameterSource.Body,
                "FromQuery" => ParameterSource.Query,
                "FromRoute" => ParameterSource.Route,
                "FromHeader" => ParameterSource.Header,
                "FromForm" => ParameterSource.Form,
                "FromServices" => ParameterSource.Services,
                _ => ParameterSource.Route
            };
        }

        // Default: if type is complex, assume Body; otherwise Route
        var typeName = param.Type?.ToString() ?? "";
        if (typeName.StartsWith("string") || typeName.StartsWith("int") ||
            typeName.StartsWith("long") || typeName.StartsWith("Guid") ||
            typeName.StartsWith("bool") || typeName.StartsWith("double"))
        {
            return ParameterSource.Route;
        }

        return ParameterSource.Body;
    }

    private (string Name, string ReturnType, IReadOnlyList<EndpointParameter> Parameters, bool IsAsync)
        ExtractMinimalApiHandler(ExpressionSyntax handler)
    {
        var parameters = new List<EndpointParameter>();
        string name = "Handler";
        string returnType = "IResult";
        bool isAsync = false;

        if (handler is ParenthesizedLambdaExpressionSyntax lambda)
        {
            isAsync = lambda.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));

            foreach (var param in lambda.ParameterList?.Parameters ?? [])
            {
                parameters.Add(new EndpointParameter
                {
                    Name = param.Identifier.Text,
                    Type = param.Type?.ToString() ?? "object",
                    Source = ParameterSource.Auto
                });
            }
        }
        else if (handler is SimpleLambdaExpressionSyntax simpleLambda)
        {
            isAsync = simpleLambda.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            parameters.Add(new EndpointParameter
            {
                Name = simpleLambda.Parameter.Identifier.Text,
                Type = simpleLambda.Parameter.Type?.ToString() ?? "object",
                Source = ParameterSource.Auto
            });
        }
        else if (handler is IdentifierNameSyntax identifier)
        {
            name = identifier.Identifier.Text;
        }
        else if (handler is MemberAccessExpressionSyntax memberAccess)
        {
            name = memberAccess.Name.Identifier.Text;
        }

        return (name, returnType, parameters, isAsync);
    }

    private string InferHttpMethodFromName(string methodName)
    {
        var lowerName = methodName.ToLowerInvariant();

        if (lowerName.StartsWith("get") || lowerName.StartsWith("list") ||
            lowerName.StartsWith("find") || lowerName.StartsWith("fetch"))
        {
            return "GET";
        }

        if (lowerName.StartsWith("post") || lowerName.StartsWith("create") ||
            lowerName.StartsWith("add") || lowerName.StartsWith("insert"))
        {
            return "POST";
        }

        if (lowerName.StartsWith("put") || lowerName.StartsWith("update") ||
            lowerName.StartsWith("replace"))
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

        return "GET";
    }

    private bool HasAttribute(MemberDeclarationSyntax member, string attributeName)
    {
        return member.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => GetAttributeName(a) == attributeName);
    }

    private bool HasAuthorizeAttribute(MethodDeclarationSyntax method, ClassDeclarationSyntax classDecl)
    {
        // Check method first
        if (this.HasAttribute(method, "Authorize") || this.HasAttribute(method, "AuthorizeAttribute"))
        {
            return true;
        }

        // Check class
        return this.HasAttribute(classDecl, "Authorize") || this.HasAttribute(classDecl, "AuthorizeAttribute");
    }

    private bool HasAllowAnonymousAttribute(MethodDeclarationSyntax method)
    {
        return this.HasAttribute(method, "AllowAnonymous") || this.HasAttribute(method, "AllowAnonymousAttribute");
    }

    private bool HasAuthorizationChain(InvocationExpressionSyntax invocation)
    {
        // Check for .RequireAuthorization() in the chain
        var parent = invocation.Parent;
        while (parent != null)
        {
            if (parent is InvocationExpressionSyntax parentInvocation)
            {
                var methodName = GetInvocationMethodName(parentInvocation);
                if (methodName == "RequireAuthorization")
                {
                    return true;
                }
            }

            parent = parent.Parent;
        }

        // Also check in the same statement
        var statement = invocation.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement != null)
        {
            var allInvocations = statement.DescendantNodes().OfType<InvocationExpressionSyntax>();
            return allInvocations.Any(i => GetInvocationMethodName(i) == "RequireAuthorization");
        }

        return false;
    }

    private IReadOnlyList<string> GetProducesTypes(MethodDeclarationSyntax method, ClassDeclarationSyntax classDecl)
    {
        var types = new List<string>();

        var producesAttrs = method.AttributeLists
            .Concat(classDecl.AttributeLists)
            .SelectMany(al => al.Attributes)
            .Where(a => GetAttributeName(a).StartsWith("Produces"));

        foreach (var attr in producesAttrs)
        {
            if (attr.ArgumentList?.Arguments.Count > 0)
            {
                types.Add(attr.ArgumentList.Arguments[0].ToString().Trim('"'));
            }
        }

        return types;
    }

    private IReadOnlyList<string> GetConsumesTypes(MethodDeclarationSyntax method, ClassDeclarationSyntax classDecl)
    {
        var types = new List<string>();

        var consumesAttrs = method.AttributeLists
            .Concat(classDecl.AttributeLists)
            .SelectMany(al => al.Attributes)
            .Where(a => GetAttributeName(a) == "Consumes");

        foreach (var attr in consumesAttrs)
        {
            if (attr.ArgumentList?.Arguments.Count > 0)
            {
                types.Add(attr.ArgumentList.Arguments[0].ToString().Trim('"'));
            }
        }

        return types;
    }

    private static string GetAttributeName(AttributeSyntax attr)
    {
        var name = attr.Name.ToString();
        // Remove Attribute suffix if present
        if (name.EndsWith("Attribute"))
        {
            name = name[..^9];
        }

        // Handle namespace-qualified names
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

        return name;
    }

    private static string GetHttpMethodFromAttribute(AttributeSyntax attr)
    {
        var name = GetAttributeName(attr);
        return name.Replace("Http", "").ToUpperInvariant();
    }

    private static string GetRouteFromAttribute(AttributeSyntax attr)
    {
        if (attr.ArgumentList?.Arguments.Count > 0)
        {
            var firstArg = attr.ArgumentList.Arguments[0];
            return firstArg.ToString().Trim('"');
        }

        return "";
    }

    private static string GetInvocationMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => ""
        };
    }
}

/// <summary>
/// Information about a detected HTTP endpoint.
/// </summary>
public record HttpEndpointInfo
{
    /// <summary>Gets the endpoint type.</summary>
    public required HttpEndpointType EndpointType { get; init; }

    /// <summary>Gets the HTTP method (GET, POST, etc.).</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Gets the route template.</summary>
    public required string RouteTemplate { get; init; }

    /// <summary>Gets the controller name (for attribute/convention endpoints).</summary>
    public string? ControllerName { get; init; }

    /// <summary>Gets the action/handler name.</summary>
    public required string ActionName { get; init; }

    /// <summary>Gets the return type.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Gets the parameters.</summary>
    public IReadOnlyList<EndpointParameter> Parameters { get; init; } = [];

    /// <summary>Gets whether the method is async.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets whether authorization is required.</summary>
    public bool RequiresAuthorization { get; init; }

    /// <summary>Gets whether anonymous access is allowed.</summary>
    public bool AllowsAnonymous { get; init; }

    /// <summary>Gets the content types produced.</summary>
    public IReadOnlyList<string> ProducesContentTypes { get; init; } = [];

    /// <summary>Gets the content types consumed.</summary>
    public IReadOnlyList<string> ConsumesContentTypes { get; init; } = [];

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Types of HTTP endpoints.
/// </summary>
public enum HttpEndpointType
{
    /// <summary>Attribute-based routing ([HttpGet], etc.).</summary>
    AttributeBased,

    /// <summary>Minimal API (MapGet, etc.).</summary>
    MinimalApi,

    /// <summary>Convention-based routing.</summary>
    ConventionBased
}

/// <summary>
/// Information about an endpoint parameter.
/// </summary>
public record EndpointParameter
{
    /// <summary>Gets the parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the parameter type.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the parameter source.</summary>
    public required ParameterSource Source { get; init; }

    /// <summary>Gets whether the parameter is optional.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Gets the default value.</summary>
    public string? DefaultValue { get; init; }
}

/// <summary>
/// Source of parameter binding.
/// </summary>
public enum ParameterSource
{
    /// <summary>Auto-detected.</summary>
    Auto,

    /// <summary>From route.</summary>
    Route,

    /// <summary>From query string.</summary>
    Query,

    /// <summary>From request body.</summary>
    Body,

    /// <summary>From header.</summary>
    Header,

    /// <summary>From form.</summary>
    Form,

    /// <summary>From DI services.</summary>
    Services
}
