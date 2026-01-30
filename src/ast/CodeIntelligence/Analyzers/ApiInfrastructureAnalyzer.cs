// -----------------------------------------------------------------------
// <copyright file="ApiInfrastructureAnalyzer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes API infrastructure setup - how routes are registered, auth is configured,
/// and controllers are wired up. Works by tracing from bootstrap/startup code.
/// </summary>
public class ApiInfrastructureAnalyzer
{
    #region Public Methods

    /// <summary>
    /// Analyzes a startup/configuration file for API infrastructure setup.
    /// </summary>
    public ApiInfrastructureInfo Analyze(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var info = new ApiInfrastructureInfo { FilePath = filePath };

        // Detect HttpConfiguration setup
        this.DetectHttpConfiguration(root, info);

        // Detect route registration
        this.DetectRouteRegistration(root, info);

        // Detect message handlers (auth, tracing, etc.)
        this.DetectMessageHandlers(root, info);

        // Detect DI container registrations
        this.DetectDependencyRegistrations(root, info);

        // Detect OWIN middleware
        this.DetectOwinMiddleware(root, info);

        return info;
    }

    /// <summary>
    /// Analyzes a DI configuration file to extract controller registrations.
    /// </summary>
    public IReadOnlyList<ControllerRegistration> AnalyzeControllerRegistrations(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var registrations = new List<ControllerRegistration>();

        // Find container.RegisterType<TInterface, TImplementation>() calls
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                if (methodName == "RegisterType" && memberAccess.Name is GenericNameSyntax genericName)
                {
                    var typeArgs = genericName.TypeArgumentList.Arguments;
                    if (typeArgs.Count == 2)
                    {
                        var interfaceType = typeArgs[0].ToString();
                        var implementationType = typeArgs[1].ToString();

                        // Check if it's a controller registration
                        if (interfaceType.EndsWith("Controller") || implementationType.EndsWith("Controller"))
                        {
                            var lineSpan = tree.GetLineSpan(invocation.Span);

                            registrations.Add(new ControllerRegistration
                            {
                                InterfaceType = interfaceType,
                                ImplementationType = implementationType,
                                FilePath = filePath,
                                LineNumber = lineSpan.StartLinePosition.Line + 1,
                                Lifetime = this.ExtractLifetime(invocation)
                            });
                        }
                    }
                }
            }
        }

        return registrations;
    }

    /// <summary>
    /// Analyzes HTTP client factory registrations.
    /// </summary>
    public IReadOnlyList<HttpClientRegistration> AnalyzeHttpClientRegistrations(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var registrations = new List<HttpClientRegistration>();

        // Find HttpClient singleton patterns
        this.DetectHttpClientSingleton(root, filePath, registrations);

        // Find factory patterns
        this.DetectHttpClientFactory(root, filePath, registrations);

        // Find IHttpClientFactory registrations
        this.DetectHttpClientFactoryDI(root, filePath, registrations);

        return registrations;
    }

    /// <summary>
    /// Analyzes authentication handler registrations.
    /// </summary>
    public IReadOnlyList<AuthHandlerInfo> AnalyzeAuthHandlers(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var handlers = new List<AuthHandlerInfo>();

        // Build a map of variable declarations for resolution (use last occurrence if duplicates)
        var variableDeclarations = new Dictionary<string, string>();
        foreach (var decl in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            foreach (var variable in decl.Variables)
            {
                var name = variable.Identifier.Text;
                var init = variable.Initializer?.Value?.ToString() ?? "";
                variableDeclarations[name] = init; // Overwrites duplicates
            }
        }

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                // MessageHandlers.Insert or Add
                if (methodName is "Insert" or "Add" &&
                    memberAccess.Expression.ToString().Contains("MessageHandlers"))
                {
                    var handlerArg = invocation.ArgumentList.Arguments.LastOrDefault();
                    if (handlerArg != null)
                    {
                        var handlerInfo = this.ExtractAuthHandlerInfo(
                            handlerArg.Expression, tree, filePath, variableDeclarations);
                        if (handlerInfo != null)
                        {
                            handlers.Add(handlerInfo);
                        }
                    }
                }
            }
        }

        return handlers;
    }

    #endregion

    #region Private Methods - HttpConfiguration Detection

    private void DetectHttpConfiguration(SyntaxNode root, ApiInfrastructureInfo info)
    {
        // Find methods with HttpConfiguration parameter
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.ParameterList.Parameters
                .Any(p => p.Type?.ToString().Contains("HttpConfiguration") == true));

        foreach (var method in methods)
        {
            info.HttpConfigurationMethods.Add(new HttpConfigurationMethod
            {
                MethodName = method.Identifier.Text,
                ClassName = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "",
                Parameters = method.ParameterList.Parameters.Select(p => p.ToString()).ToList()
            });
        }
    }

    private void DetectRouteRegistration(SyntaxNode root, ApiInfrastructureInfo info)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                // MapHttpAttributeRoutes
                if (methodName == "MapHttpAttributeRoutes")
                {
                    var routeProvider = invocation.ArgumentList.Arguments.FirstOrDefault()?.ToString();
                    info.RouteRegistrations.Add(new RouteRegistration
                    {
                        RegistrationType = "AttributeRouting",
                        RouteProvider = routeProvider,
                        RawExpression = invocation.ToString()
                    });
                }
                // MapRoute
                else if (methodName == "MapRoute" || methodName == "MapHttpRoute")
                {
                    info.RouteRegistrations.Add(new RouteRegistration
                    {
                        RegistrationType = "ConventionalRouting",
                        RawExpression = invocation.ToString()
                    });
                }
            }
        }
    }

    private void DetectMessageHandlers(SyntaxNode root, ApiInfrastructureInfo info)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var expr = memberAccess.Expression.ToString();
                var methodName = memberAccess.Name.Identifier.Text;

                if (expr.Contains("MessageHandlers") && methodName is "Insert" or "Add")
                {
                    var args = invocation.ArgumentList.Arguments;
                    var handlerExpr = args.Count > 1 ? args[1] : args.FirstOrDefault();

                    if (handlerExpr != null)
                    {
                        info.MessageHandlers.Add(new MessageHandlerRegistration
                        {
                            HandlerExpression = handlerExpr.ToString(),
                            InsertIndex = args.Count > 1 ? args[0].ToString() : null,
                            RawExpression = invocation.ToString()
                        });
                    }
                }
            }
        }
    }

    private void DetectDependencyRegistrations(SyntaxNode root, ApiInfrastructureInfo info)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                // Services.Replace
                if (methodName == "Replace" && memberAccess.Expression.ToString().Contains("Services"))
                {
                    var args = invocation.ArgumentList.Arguments;
                    if (args.Count >= 2)
                    {
                        info.ServiceReplacements.Add(new ServiceReplacement
                        {
                            ServiceType = args[0].ToString(),
                            Implementation = args[1].ToString(),
                            RawExpression = invocation.ToString()
                        });
                    }
                }

                // DependencyResolver assignment
                if (methodName == "set_DependencyResolver" ||
                    (invocation.Parent is AssignmentExpressionSyntax assign &&
                     assign.Left.ToString().Contains("DependencyResolver")))
                {
                    info.DependencyResolverSetup = invocation.ToString();
                }
            }
        }

        // Also check for DependencyResolver assignments
        var assignments = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().Contains("DependencyResolver"));

        foreach (var assign in assignments)
        {
            info.DependencyResolverSetup = assign.Right.ToString();
        }
    }

    private void DetectOwinMiddleware(SyntaxNode root, ApiInfrastructureInfo info)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                // Use* methods on IAppBuilder
                if (methodName.StartsWith("Use") || methodName.StartsWith("Map"))
                {
                    var receiverType = memberAccess.Expression.ToString();
                    if (receiverType.Contains("app") || receiverType.Contains("builder"))
                    {
                        info.OwinMiddleware.Add(new OwinMiddlewareRegistration
                        {
                            MiddlewareName = methodName,
                            Arguments = invocation.ArgumentList.Arguments.Select(a => a.ToString()).ToList(),
                            RawExpression = invocation.ToString()
                        });
                    }
                }
            }
        }
    }

    #endregion

    #region Private Methods - HttpClient Detection

    private void DetectHttpClientSingleton(SyntaxNode root, string filePath, List<HttpClientRegistration> registrations)
    {
        // Find static HttpClient fields
        var fields = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) &&
                       f.Declaration.Type.ToString().Contains("HttpClient"));

        foreach (var field in fields)
        {
            var className = field.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;

            registrations.Add(new HttpClientRegistration
            {
                RegistrationType = "Singleton",
                ClassName = className ?? "",
                FieldName = field.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
                FilePath = filePath,
                HandlerConfiguration = this.FindHttpClientHandlerConfig(root, field)
            });
        }
    }

    private void DetectHttpClientFactory(SyntaxNode root, string filePath, List<HttpClientRegistration> registrations)
    {
        // Find classes with "Factory" in name that create HttpClient
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.Text.Contains("Factory") || c.Identifier.Text.Contains("Client"));

        foreach (var cls in classes)
        {
            // Look for HttpClient creation
            var creations = cls.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(o => o.Type.ToString().Contains("HttpClient"));

            foreach (var creation in creations)
            {
                var method = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                registrations.Add(new HttpClientRegistration
                {
                    RegistrationType = "Factory",
                    ClassName = cls.Identifier.Text,
                    MethodName = method?.Identifier.Text,
                    FilePath = filePath,
                    HandlerConfiguration = this.ExtractHandlerFromCreation(creation)
                });
            }
        }
    }

    private void DetectHttpClientFactoryDI(SyntaxNode root, string filePath, List<HttpClientRegistration> registrations)
    {
        // Find AddHttpClient registrations
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().Contains("AddHttpClient"));

        foreach (var invocation in invocations)
        {
            var genericArgs = (invocation.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax;

            registrations.Add(new HttpClientRegistration
            {
                RegistrationType = "IHttpClientFactory",
                TypedClientType = genericArgs?.TypeArgumentList.Arguments.FirstOrDefault()?.ToString(),
                FilePath = filePath,
                RawExpression = invocation.ToString()
            });
        }
    }

    private string? FindHttpClientHandlerConfig(SyntaxNode root, FieldDeclarationSyntax field)
    {
        // Look for handler configuration in the same class
        var className = field.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (className == null)
        {
            return null;
        }

        var creations = className.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString().Contains("HttpClientHandler") ||
                       o.Type.ToString().Contains("DelegatingHandler"));

        return creations.FirstOrDefault()?.ToString();
    }

    private string? ExtractHandlerFromCreation(ObjectCreationExpressionSyntax creation)
    {
        var args = creation.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
        {
            return null;
        }

        return args.Value.FirstOrDefault()?.ToString();
    }

    private string? ExtractLifetime(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        foreach (var arg in args)
        {
            var text = arg.ToString();
            if (text.Contains("Lifetime") || text.Contains("Singleton") ||
                text.Contains("Transient") || text.Contains("Scoped"))
            {
                return text;
            }
        }

        return null;
    }

    private AuthHandlerInfo? ExtractAuthHandlerInfo(
        ExpressionSyntax expression,
        SyntaxTree tree,
        string filePath,
        Dictionary<string, string> variableDeclarations)
    {
        var lineSpan = tree.GetLineSpan(expression.Span);
        var exprText = expression.ToString();

        // If this is a variable reference, look up its initialization
        if (expression is IdentifierNameSyntax identifier &&
            variableDeclarations.TryGetValue(identifier.Identifier.Text, out var initializer))
        {
            exprText = initializer;
        }

        // Determine handler type
        string handlerType = "Unknown";
        string? authProvider = null;

        if (exprText.Contains("MultiAuthMessageHandler"))
        {
            handlerType = "MultiAuth";
            // Extract providers from constructor
            if (expression is ObjectCreationExpressionSyntax creation)
            {
                authProvider = creation.ArgumentList?.ToString();
            }
        }
        else if (exprText.Contains("NoAuthMessageHandler"))
        {
            handlerType = "NoAuth";
        }
        else if (exprText.Contains("CertificateRequest") || exprText.Contains("Mtls"))
        {
            handlerType = "CertificateBased";
        }
        else if (exprText.Contains("OAuth") || exprText.Contains("Bearer"))
        {
            handlerType = "OAuth";
        }
        else if (exprText.Contains("Tracing") || exprText.Contains("TracingHandler"))
        {
            handlerType = "Tracing";
        }

        return new AuthHandlerInfo
        {
            HandlerType = handlerType,
            Expression = exprText,
            AuthProvider = authProvider,
            FilePath = filePath,
            LineNumber = lineSpan.StartLinePosition.Line + 1
        };
    }

    #endregion
}

#region Model Records

/// <summary>
/// Complete API infrastructure information extracted from startup/config code.
/// </summary>
public record ApiInfrastructureInfo
{
    public required string FilePath { get; init; }
    public List<HttpConfigurationMethod> HttpConfigurationMethods { get; init; } = [];
    public List<RouteRegistration> RouteRegistrations { get; init; } = [];
    public List<MessageHandlerRegistration> MessageHandlers { get; init; } = [];
    public List<ServiceReplacement> ServiceReplacements { get; init; } = [];
    public string? DependencyResolverSetup { get; set; }
    public List<OwinMiddlewareRegistration> OwinMiddleware { get; init; } = [];
}

public record HttpConfigurationMethod
{
    public required string MethodName { get; init; }
    public required string ClassName { get; init; }
    public List<string> Parameters { get; init; } = [];
}

public record RouteRegistration
{
    public required string RegistrationType { get; init; }
    public string? RouteProvider { get; init; }
    public string? RawExpression { get; init; }
}

public record MessageHandlerRegistration
{
    public required string HandlerExpression { get; init; }
    public string? InsertIndex { get; init; }
    public string? RawExpression { get; init; }
}

public record ServiceReplacement
{
    public required string ServiceType { get; init; }
    public required string Implementation { get; init; }
    public string? RawExpression { get; init; }
}

public record OwinMiddlewareRegistration
{
    public required string MiddlewareName { get; init; }
    public List<string> Arguments { get; init; } = [];
    public string? RawExpression { get; init; }
}

public record ControllerRegistration
{
    public required string InterfaceType { get; init; }
    public required string ImplementationType { get; init; }
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public string? Lifetime { get; init; }
}

public record HttpClientRegistration
{
    public required string RegistrationType { get; init; }
    public string? ClassName { get; init; }
    public string? FieldName { get; init; }
    public string? MethodName { get; init; }
    public string? TypedClientType { get; init; }
    public string? FilePath { get; init; }
    public string? HandlerConfiguration { get; init; }
    public string? RawExpression { get; init; }
}

public record AuthHandlerInfo
{
    public required string HandlerType { get; init; }
    public required string Expression { get; init; }
    public string? AuthProvider { get; init; }
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
}

#endregion
