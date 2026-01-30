// -----------------------------------------------------------------------
// <copyright file="LegacyApiAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect legacy API patterns (OWIN, WCF, Web API 2, Nancy, ServiceStack, GraphQL).
/// </summary>
public sealed class LegacyApiAnalyzer
{
    #region OWIN Detection

    /// <summary>
    /// Detects OWIN startup configuration and middleware.
    /// </summary>
    public IReadOnlyList<OwinStartupInfo> DetectOwinStartup(SyntaxTree tree, string filePath)
    {
        var results = new List<OwinStartupInfo>();
        var root = tree.GetRoot();

        // Find OWIN Startup classes
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            // Check for OwinStartup attribute
            var hasOwinAttr = classDecl.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => GetAttributeName(a).Contains("OwinStartup") ||
                         GetAttributeName(a) == "assembly");

            // Check for Configuration(IAppBuilder) method
            var configMethod = classDecl.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m =>
                    m.Identifier.Text == "Configuration" &&
                    m.ParameterList.Parameters.Any(p =>
                        p.Type?.ToString().Contains("IAppBuilder") == true));

            if (configMethod != null || hasOwinAttr)
            {
                var middlewareRegistrations = this.ExtractOwinMiddleware(configMethod, classDecl);

                results.Add(new OwinStartupInfo
                {
                    ClassName = classDecl.Identifier.Text,
                    ConfigurationMethodName = configMethod?.Identifier.Text ?? "Configuration",
                    MiddlewareRegistrations = middlewareRegistrations,
                    FilePath = filePath,
                    LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Confidence = 0.90
                });
            }
        }

        return results;
    }

    private IReadOnlyList<OwinMiddlewareInfo> ExtractOwinMiddleware(
        MethodDeclarationSyntax? configMethod,
        ClassDeclarationSyntax classDecl)
    {
        var middleware = new List<OwinMiddlewareInfo>();

        if (configMethod == null)
        {
            return middleware;
        }

        var invocations = configMethod.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var methodName = GetInvocationMethodName(invocation);

            // Common OWIN middleware patterns
            if (methodName.StartsWith("Use") || methodName.StartsWith("Map"))
            {
                var middlewareType = this.InferOwinMiddlewareType(methodName, invocation);

                middleware.Add(new OwinMiddlewareInfo
                {
                    MethodName = methodName,
                    MiddlewareType = middlewareType,
                    Route = this.ExtractRouteFromOwinCall(invocation),
                    LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                });
            }
        }

        return middleware;
    }

    private OwinMiddlewareType InferOwinMiddlewareType(string methodName, InvocationExpressionSyntax invocation)
    {
        return methodName switch
        {
            "UseWebApi" => OwinMiddlewareType.WebApi,
            "UseSignalR" or "MapSignalR" => OwinMiddlewareType.SignalR,
            "UseCors" => OwinMiddlewareType.Cors,
            "UseOAuthBearerAuthentication" or "UseOAuthAuthorizationServer" => OwinMiddlewareType.OAuth,
            "UseIdentityServer" => OwinMiddlewareType.IdentityServer,
            "UseNancy" => OwinMiddlewareType.Nancy,
            "UseServiceStack" => OwinMiddlewareType.ServiceStack,
            "UseStaticFiles" => OwinMiddlewareType.StaticFiles,
            "UseSwagger" or "UseSwaggerUi" => OwinMiddlewareType.Swagger,
            _ when methodName.StartsWith("Map") => OwinMiddlewareType.RouteMapping,
            _ => OwinMiddlewareType.Custom
        };
    }

    private string? ExtractRouteFromOwinCall(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count > 0)
        {
            var firstArg = args[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText;
            }
        }

        return null;
    }

    #endregion

    #region WCF Detection

    /// <summary>
    /// Detects WCF service definitions.
    /// </summary>
    public IReadOnlyList<WcfServiceInfo> DetectWcfServices(SyntaxTree tree, string filePath)
    {
        var results = new List<WcfServiceInfo>();
        var root = tree.GetRoot();

        // Find interfaces with [ServiceContract]
        var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();

        foreach (var interfaceDecl in interfaces)
        {
            var hasServiceContract = interfaceDecl.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => GetAttributeName(a) == "ServiceContract");

            if (!hasServiceContract)
            {
                continue;
            }

            var operations = this.ExtractWcfOperations(interfaceDecl);

            results.Add(new WcfServiceInfo
            {
                InterfaceName = interfaceDecl.Identifier.Text,
                Operations = operations,
                FilePath = filePath,
                LineNumber = interfaceDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.95
            });
        }

        // Also find classes implementing WCF services
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var hasServiceBehavior = classDecl.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => GetAttributeName(a) == "ServiceBehavior");

            if (!hasServiceBehavior)
            {
                continue;
            }

            results.Add(new WcfServiceInfo
            {
                ClassName = classDecl.Identifier.Text,
                InterfaceName = classDecl.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
                FilePath = filePath,
                LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            });
        }

        return results;
    }

    private IReadOnlyList<WcfOperationInfo> ExtractWcfOperations(InterfaceDeclarationSyntax interfaceDecl)
    {
        var operations = new List<WcfOperationInfo>();

        var methods = interfaceDecl.Members.OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var operationAttr = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a => GetAttributeName(a) == "OperationContract");

            if (operationAttr == null)
            {
                continue;
            }

            // Check for WebGet/WebInvoke (REST-style WCF)
            var webGetAttr = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a => GetAttributeName(a) == "WebGet");

            var webInvokeAttr = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a => GetAttributeName(a) == "WebInvoke");

            string? httpMethod = null;
            string? uriTemplate = null;

            if (webGetAttr != null)
            {
                httpMethod = "GET";
                uriTemplate = this.ExtractUriTemplate(webGetAttr);
            }
            else if (webInvokeAttr != null)
            {
                httpMethod = this.ExtractWebInvokeMethod(webInvokeAttr);
                uriTemplate = this.ExtractUriTemplate(webInvokeAttr);
            }

            operations.Add(new WcfOperationInfo
            {
                MethodName = method.Identifier.Text,
                ReturnType = method.ReturnType.ToString(),
                HttpMethod = httpMethod,
                UriTemplate = uriTemplate,
                IsRestStyle = webGetAttr != null || webInvokeAttr != null,
                LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }

        return operations;
    }

    private string? ExtractUriTemplate(AttributeSyntax attr)
    {
        var args = attr.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
        {
            return null;
        }

        var uriArg = args.Value.FirstOrDefault(a =>
            a.NameEquals?.Name.Identifier.Text == "UriTemplate");

        return uriArg?.Expression.ToString().Trim('"');
    }

    private string ExtractWebInvokeMethod(AttributeSyntax attr)
    {
        var args = attr.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
        {
            return "POST";
        }

        var methodArg = args.Value.FirstOrDefault(a =>
            a.NameEquals?.Name.Identifier.Text == "Method");

        var method = methodArg?.Expression.ToString().Trim('"');
        return method ?? "POST";
    }

    #endregion

    #region Web API 2 (Pre-Core) Detection

    /// <summary>
    /// Detects ASP.NET Web API 2 endpoints (with RoutePrefix, AcceptVerbs, etc.).
    /// </summary>
    public IReadOnlyList<WebApi2EndpointInfo> DetectWebApi2Endpoints(SyntaxTree tree, string filePath)
    {
        var results = new List<WebApi2EndpointInfo>();
        var root = tree.GetRoot();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            // Check for ApiController base or RoutePrefix attribute
            var isApiController = classDecl.BaseList?.Types
                .Any(t => t.Type.ToString() == "ApiController") == true;

            var routePrefixAttr = classDecl.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a => GetAttributeName(a) == "RoutePrefix");

            if (!isApiController && routePrefixAttr == null)
            {
                continue;
            }

            var routePrefix = routePrefixAttr?.ArgumentList?.Arguments
                .FirstOrDefault()?.Expression.ToString().Trim('"');

            var methods = classDecl.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));

            foreach (var method in methods)
            {
                var endpoint = this.ExtractWebApi2Endpoint(method, classDecl, routePrefix, filePath);
                if (endpoint != null)
                {
                    results.Add(endpoint);
                }
            }
        }

        return results;
    }

    private WebApi2EndpointInfo? ExtractWebApi2Endpoint(
        MethodDeclarationSyntax method,
        ClassDeclarationSyntax classDecl,
        string? routePrefix,
        string filePath)
    {
        var attrs = method.AttributeLists.SelectMany(al => al.Attributes).ToList();

        // Check for Route attribute
        var routeAttr = attrs.FirstOrDefault(a => GetAttributeName(a) == "Route");
        var route = routeAttr?.ArgumentList?.Arguments
            .FirstOrDefault()?.Expression.ToString().Trim('"');

        // Check for AcceptVerbs
        var acceptVerbsAttr = attrs.FirstOrDefault(a => GetAttributeName(a) == "AcceptVerbs");
        string? httpMethod = null;

        if (acceptVerbsAttr != null)
        {
            httpMethod = acceptVerbsAttr.ArgumentList?.Arguments
                .FirstOrDefault()?.Expression.ToString().Trim('"').ToUpperInvariant();
        }

        // Check for HttpGet, HttpPost, etc. (same as Core)
        var httpAttr = attrs.FirstOrDefault(a =>
            GetAttributeName(a).StartsWith("Http"));

        if (httpAttr != null)
        {
            httpMethod = GetAttributeName(httpAttr).Replace("Http", "").ToUpperInvariant();
        }

        // Infer from method name if no explicit attribute
        if (httpMethod == null)
        {
            httpMethod = this.InferHttpMethodFromName(method.Identifier.Text);
        }

        // Combine route prefix and action route
        var fullRoute = string.IsNullOrEmpty(routePrefix)
            ? route
            : string.IsNullOrEmpty(route)
                ? routePrefix
                : $"{routePrefix}/{route}";

        return new WebApi2EndpointInfo
        {
            ControllerName = classDecl.Identifier.Text.Replace("Controller", ""),
            ActionName = method.Identifier.Text,
            HttpMethod = httpMethod ?? "GET",
            RoutePrefix = routePrefix,
            RouteTemplate = fullRoute,
            ReturnType = method.ReturnType.ToString(),
            FilePath = filePath,
            LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90
        };
    }

    private string InferHttpMethodFromName(string methodName)
    {
        var lowerName = methodName.ToLowerInvariant();

        if (lowerName.StartsWith("get") || lowerName.StartsWith("list") || lowerName.StartsWith("find"))
        {
            return "GET";
        }

        if (lowerName.StartsWith("post") || lowerName.StartsWith("create") || lowerName.StartsWith("add"))
        {
            return "POST";
        }

        if (lowerName.StartsWith("put") || lowerName.StartsWith("update"))
        {
            return "PUT";
        }

        if (lowerName.StartsWith("delete") || lowerName.StartsWith("remove"))
        {
            return "DELETE";
        }

        return "GET";
    }

    #endregion

    #region GraphQL Detection

    /// <summary>
    /// Detects GraphQL schema definitions (HotChocolate, GraphQL.NET).
    /// </summary>
    public IReadOnlyList<GraphQLInfo> DetectGraphQL(SyntaxTree tree, string filePath)
    {
        var results = new List<GraphQLInfo>();
        var root = tree.GetRoot();

        // HotChocolate patterns
        results.AddRange(this.DetectHotChocolate(root, filePath));

        // GraphQL.NET patterns
        results.AddRange(this.DetectGraphQLNet(root, filePath));

        return results;
    }

    private IEnumerable<GraphQLInfo> DetectHotChocolate(SyntaxNode root, string filePath)
    {
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            // Check for ObjectType<T>, QueryType, MutationType, SubscriptionType
            var baseList = classDecl.BaseList;
            if (baseList == null)
            {
                continue;
            }

            var baseTypes = baseList.Types.Select(t => t.Type.ToString()).ToList();

            GraphQLOperationType? opType = null;

            if (baseTypes.Any(t => t.StartsWith("ObjectType<") || t == "ObjectType"))
            {
                opType = GraphQLOperationType.Type;
            }
            else if (baseTypes.Any(t => t.Contains("QueryType") || t == "Query"))
            {
                opType = GraphQLOperationType.Query;
            }
            else if (baseTypes.Any(t => t.Contains("MutationType") || t == "Mutation"))
            {
                opType = GraphQLOperationType.Mutation;
            }
            else if (baseTypes.Any(t => t.Contains("SubscriptionType") || t == "Subscription"))
            {
                opType = GraphQLOperationType.Subscription;
            }

            if (opType == null)
            {
                continue;
            }

            // Extract resolvers
            var resolvers = this.ExtractGraphQLResolvers(classDecl);

            yield return new GraphQLInfo
            {
                Framework = GraphQLFramework.HotChocolate,
                OperationType = opType.Value,
                ClassName = classDecl.Identifier.Text,
                Resolvers = resolvers,
                FilePath = filePath,
                LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            };
        }
    }

    private IEnumerable<GraphQLInfo> DetectGraphQLNet(SyntaxNode root, string filePath)
    {
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var baseList = classDecl.BaseList;
            if (baseList == null)
            {
                continue;
            }

            var baseTypes = baseList.Types.Select(t => t.Type.ToString()).ToList();

            // GraphQL.NET patterns: ObjectGraphType<T>, EfObjectGraphType, etc.
            if (!baseTypes.Any(t => t.Contains("GraphType")))
            {
                continue;
            }

            var opType = GraphQLOperationType.Type;
            if (classDecl.Identifier.Text.Contains("Query"))
            {
                opType = GraphQLOperationType.Query;
            }
            else if (classDecl.Identifier.Text.Contains("Mutation"))
            {
                opType = GraphQLOperationType.Mutation;
            }

            var resolvers = this.ExtractGraphQLResolvers(classDecl);

            yield return new GraphQLInfo
            {
                Framework = GraphQLFramework.GraphQLNet,
                OperationType = opType,
                ClassName = classDecl.Identifier.Text,
                Resolvers = resolvers,
                FilePath = filePath,
                LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.85
            };
        }
    }

    private IReadOnlyList<GraphQLResolverInfo> ExtractGraphQLResolvers(ClassDeclarationSyntax classDecl)
    {
        var resolvers = new List<GraphQLResolverInfo>();

        // Find methods with resolver attributes or public methods
        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));

        foreach (var method in methods)
        {
            // Skip constructor and common methods
            if (method.Identifier.Text is "Configure" or "ConfigureAsync")
            {
                continue;
            }

            resolvers.Add(new GraphQLResolverInfo
            {
                Name = method.Identifier.Text,
                ReturnType = method.ReturnType.ToString(),
                IsAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }

        // Also check for Field<T> calls in constructor
        var constructors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
        foreach (var ctor in constructors)
        {
            var fieldCalls = ctor.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => GetInvocationMethodName(i).StartsWith("Field"));

            foreach (var fieldCall in fieldCalls)
            {
                var fieldName = fieldCall.ArgumentList.Arguments
                    .FirstOrDefault()?.Expression.ToString().Trim('"');

                if (fieldName != null)
                {
                    resolvers.Add(new GraphQLResolverInfo
                    {
                        Name = fieldName,
                        IsFieldDefinition = true,
                        LineNumber = fieldCall.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                    });
                }
            }
        }

        return resolvers;
    }

    #endregion

    #region OData Detection

    /// <summary>
    /// Detects OData controllers and entity sets.
    /// </summary>
    public IReadOnlyList<ODataInfo> DetectOData(SyntaxTree tree, string filePath)
    {
        var results = new List<ODataInfo>();
        var root = tree.GetRoot();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var baseList = classDecl.BaseList;
            if (baseList == null)
            {
                continue;
            }

            var baseTypes = baseList.Types.Select(t => t.Type.ToString()).ToList();

            // OData controller patterns
            var isODataController = baseTypes.Any(t =>
                t.Contains("ODataController") ||
                t.Contains("EntitySetController") ||
                t.Contains("AsyncEntitySetController"));

            if (!isODataController)
            {
                continue;
            }

            var entityType = baseTypes
                .Where(t => t.Contains("<"))
                .Select(t => Regex.Match(t, @"<(.+?)(?:,|>)").Groups[1].Value)
                .FirstOrDefault();

            var actions = this.ExtractODataActions(classDecl);

            results.Add(new ODataInfo
            {
                ControllerName = classDecl.Identifier.Text.Replace("Controller", ""),
                EntityType = entityType,
                BaseType = baseTypes.FirstOrDefault(),
                Actions = actions,
                FilePath = filePath,
                LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90
            });
        }

        return results;
    }

    private IReadOnlyList<ODataActionInfo> ExtractODataActions(ClassDeclarationSyntax classDecl)
    {
        var actions = new List<ODataActionInfo>();

        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));

        foreach (var method in methods)
        {
            var methodName = method.Identifier.Text;

            // Standard OData operations
            var odataOp = methodName switch
            {
                "Get" or "GetAll" => ODataOperation.Get,
                "GetById" or "GetEntity" => ODataOperation.GetById,
                "Post" or "Create" => ODataOperation.Post,
                "Put" or "Update" => ODataOperation.Put,
                "Patch" => ODataOperation.Patch,
                "Delete" => ODataOperation.Delete,
                _ when method.AttributeLists.Any(al => al.Attributes.Any(a =>
                    GetAttributeName(a) == "ODataRoute" || GetAttributeName(a) == "EnableQuery"))
                    => ODataOperation.Custom,
                _ => (ODataOperation?)null
            };

            if (odataOp == null)
            {
                continue;
            }

            var hasEnableQuery = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => GetAttributeName(a) == "EnableQuery");

            actions.Add(new ODataActionInfo
            {
                MethodName = methodName,
                Operation = odataOp.Value,
                HasEnableQuery = hasEnableQuery,
                ReturnType = method.ReturnType.ToString(),
                LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }

        return actions;
    }

    #endregion

    #region Helper Methods

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

    private static string GetInvocationMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => ""
        };
    }

    #endregion
}

#region OWIN Models

/// <summary>
/// Information about OWIN startup configuration.
/// </summary>
public record OwinStartupInfo
{
    /// <summary>Gets the startup class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Gets the configuration method name.</summary>
    public required string ConfigurationMethodName { get; init; }

    /// <summary>Gets the middleware registrations.</summary>
    public IReadOnlyList<OwinMiddlewareInfo> MiddlewareRegistrations { get; init; } = [];

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Information about OWIN middleware registration.
/// </summary>
public record OwinMiddlewareInfo
{
    /// <summary>Gets the registration method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the middleware type.</summary>
    public required OwinMiddlewareType MiddlewareType { get; init; }

    /// <summary>Gets the route (if applicable).</summary>
    public string? Route { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Types of OWIN middleware.
/// </summary>
public enum OwinMiddlewareType
{
    /// <summary>ASP.NET Web API.</summary>
    WebApi,

    /// <summary>SignalR.</summary>
    SignalR,

    /// <summary>CORS.</summary>
    Cors,

    /// <summary>OAuth authentication.</summary>
    OAuth,

    /// <summary>IdentityServer.</summary>
    IdentityServer,

    /// <summary>Nancy framework.</summary>
    Nancy,

    /// <summary>ServiceStack.</summary>
    ServiceStack,

    /// <summary>Static files.</summary>
    StaticFiles,

    /// <summary>Swagger.</summary>
    Swagger,

    /// <summary>Route mapping.</summary>
    RouteMapping,

    /// <summary>Custom middleware.</summary>
    Custom
}

#endregion

#region WCF Models

/// <summary>
/// Information about a WCF service.
/// </summary>
public record WcfServiceInfo
{
    /// <summary>Gets the service interface name.</summary>
    public string? InterfaceName { get; init; }

    /// <summary>Gets the implementation class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the operations.</summary>
    public IReadOnlyList<WcfOperationInfo> Operations { get; init; } = [];

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Information about a WCF operation.
/// </summary>
public record WcfOperationInfo
{
    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the return type.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Gets the HTTP method (for REST-style WCF).</summary>
    public string? HttpMethod { get; init; }

    /// <summary>Gets the URI template (for REST-style WCF).</summary>
    public string? UriTemplate { get; init; }

    /// <summary>Gets whether this is REST-style WCF.</summary>
    public bool IsRestStyle { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

#endregion

#region Web API 2 Models

/// <summary>
/// Information about a Web API 2 endpoint.
/// </summary>
public record WebApi2EndpointInfo
{
    /// <summary>Gets the controller name.</summary>
    public required string ControllerName { get; init; }

    /// <summary>Gets the action name.</summary>
    public required string ActionName { get; init; }

    /// <summary>Gets the HTTP method.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Gets the route prefix.</summary>
    public string? RoutePrefix { get; init; }

    /// <summary>Gets the full route template.</summary>
    public string? RouteTemplate { get; init; }

    /// <summary>Gets the return type.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

#endregion

#region GraphQL Models

/// <summary>
/// Information about GraphQL schema.
/// </summary>
public record GraphQLInfo
{
    /// <summary>Gets the framework.</summary>
    public required GraphQLFramework Framework { get; init; }

    /// <summary>Gets the operation type.</summary>
    public required GraphQLOperationType OperationType { get; init; }

    /// <summary>Gets the class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Gets the resolvers.</summary>
    public IReadOnlyList<GraphQLResolverInfo> Resolvers { get; init; } = [];

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Information about a GraphQL resolver.
/// </summary>
public record GraphQLResolverInfo
{
    /// <summary>Gets the resolver name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the return type.</summary>
    public string? ReturnType { get; init; }

    /// <summary>Gets whether the resolver is async.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets whether this is a field definition.</summary>
    public bool IsFieldDefinition { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// GraphQL frameworks.
/// </summary>
public enum GraphQLFramework
{
    /// <summary>HotChocolate.</summary>
    HotChocolate,

    /// <summary>GraphQL.NET.</summary>
    GraphQLNet,

    /// <summary>Unknown framework.</summary>
    Unknown
}

/// <summary>
/// GraphQL operation types.
/// </summary>
public enum GraphQLOperationType
{
    /// <summary>Query operation.</summary>
    Query,

    /// <summary>Mutation operation.</summary>
    Mutation,

    /// <summary>Subscription operation.</summary>
    Subscription,

    /// <summary>Type definition.</summary>
    Type
}

#endregion

#region OData Models

/// <summary>
/// Information about an OData controller.
/// </summary>
public record ODataInfo
{
    /// <summary>Gets the controller name.</summary>
    public required string ControllerName { get; init; }

    /// <summary>Gets the entity type.</summary>
    public string? EntityType { get; init; }

    /// <summary>Gets the base type.</summary>
    public string? BaseType { get; init; }

    /// <summary>Gets the actions.</summary>
    public IReadOnlyList<ODataActionInfo> Actions { get; init; } = [];

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Information about an OData action.
/// </summary>
public record ODataActionInfo
{
    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the OData operation.</summary>
    public required ODataOperation Operation { get; init; }

    /// <summary>Gets whether EnableQuery is applied.</summary>
    public bool HasEnableQuery { get; init; }

    /// <summary>Gets the return type.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// OData operations.
/// </summary>
public enum ODataOperation
{
    /// <summary>Get collection.</summary>
    Get,

    /// <summary>Get by ID.</summary>
    GetById,

    /// <summary>Create.</summary>
    Post,

    /// <summary>Full update.</summary>
    Put,

    /// <summary>Partial update.</summary>
    Patch,

    /// <summary>Delete.</summary>
    Delete,

    /// <summary>Custom action/function.</summary>
    Custom
}

#endregion
