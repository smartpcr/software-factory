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
    /// Analyzes a DI configuration file to extract all type registrations.
    /// Returns all RegisterType calls - use ValidateControllerRegistrations to filter to controllers only.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path.</param>
    public IReadOnlyList<TypeRegistration> AnalyzeTypeRegistrations(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var registrations = new List<TypeRegistration>();

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
                        var lineSpan = tree.GetLineSpan(invocation.Span);

                        registrations.Add(new TypeRegistration
                        {
                            InterfaceType = interfaceType,
                            ImplementationType = implementationType,
                            FilePath = filePath,
                            LineNumber = lineSpan.StartLinePosition.Line + 1,
                            Lifetime = this.ExtractLifetime(invocation),
                        });
                    }
                }
            }
        }

        return registrations;
    }

    /// <summary>
    /// Analyzes a DI configuration file to extract controller registrations.
    /// Uses base type checking to identify controllers instead of pattern matching.
    /// </summary>
    /// <param name="tree">The syntax tree of the DI config file.</param>
    /// <param name="filePath">The file path.</param>
    /// <param name="controllerBaseTypes">Set of known controller base types (discovered via DiscoverControllerBaseTypes).</param>
    /// <param name="typeBaseTypeMap">Optional map of type name to its base type for validation.</param>
    public IReadOnlyList<ControllerRegistration> AnalyzeControllerRegistrations(
        SyntaxTree tree,
        string filePath,
        IReadOnlySet<string>? controllerBaseTypes = null,
        IReadOnlyDictionary<string, string>? typeBaseTypeMap = null)
    {
        var allRegistrations = this.AnalyzeTypeRegistrations(tree, filePath);
        var controllerRegistrations = new List<ControllerRegistration>();

        // Default known controller base types from common frameworks
        var knownControllerBaseTypes = new HashSet<string>(controllerBaseTypes ?? Enumerable.Empty<string>())
        {
            // ASP.NET Web API
            "ApiController",
            "AsyncApiController",
            // ASP.NET MVC
            "Controller",
            "ControllerBase",
            // ASP.NET Core
            "Microsoft.AspNetCore.Mvc.Controller",
            "Microsoft.AspNetCore.Mvc.ControllerBase",
            // Common custom base types
            "ResourceControllerBase",
            "UnauthorizedResourceControllerBase",
        };

        foreach (var reg in allRegistrations)
        {
            bool isController = false;

            // Method 1: Check if we have base type info from the type map
            if (typeBaseTypeMap != null)
            {
                // Check implementation type's base type
                if (typeBaseTypeMap.TryGetValue(reg.ImplementationType, out var implBaseType))
                {
                    isController = knownControllerBaseTypes.Any(bt => implBaseType.Contains(bt));
                }

                // Also check interface type's base type
                if (!isController && typeBaseTypeMap.TryGetValue(reg.InterfaceType, out var intfBaseType))
                {
                    isController = knownControllerBaseTypes.Any(bt => intfBaseType.Contains(bt));
                }
            }

            // Method 2: Check if the type names reference known controller base types
            if (!isController)
            {
                foreach (var baseType in knownControllerBaseTypes)
                {
                    if (reg.InterfaceType.Contains(baseType) || reg.ImplementationType.Contains(baseType))
                    {
                        isController = true;
                        break;
                    }
                }
            }

            if (isController)
            {
                controllerRegistrations.Add(new ControllerRegistration
                {
                    InterfaceType = reg.InterfaceType,
                    ImplementationType = reg.ImplementationType,
                    FilePath = reg.FilePath,
                    LineNumber = reg.LineNumber,
                    Lifetime = reg.Lifetime,
                });
            }
        }

        return controllerRegistrations;
    }

    /// <summary>
    /// Analyzes a DI configuration file to extract controller registrations.
    /// Uses base type checking to identify controllers instead of pattern matching.
    /// </summary>
    public IReadOnlyList<ControllerRegistration> AnalyzeControllerRegistrations(SyntaxTree tree, string filePath)
    {
        return this.AnalyzeControllerRegistrations(tree, filePath, null, null);
    }

    /// <summary>
    /// Builds a map of type names to their base types by scanning source files.
    /// Use this to enable base-type validation in AnalyzeControllerRegistrations.
    /// </summary>
    public Dictionary<string, string> BuildTypeBaseTypeMap(SyntaxTree tree)
    {
        var map = new Dictionary<string, string>();
        var root = tree.GetRoot();

        var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        foreach (var classDecl in classDecls)
        {
            var className = classDecl.Identifier.Text;
            if (classDecl.BaseList != null && classDecl.BaseList.Types.Count > 0)
            {
                var baseType = classDecl.BaseList.Types[0].Type.ToString();
                map[className] = baseType;
            }
        }

        var interfaceDecls = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
        foreach (var interfaceDecl in interfaceDecls)
        {
            var interfaceName = interfaceDecl.Identifier.Text;
            if (interfaceDecl.BaseList != null && interfaceDecl.BaseList.Types.Count > 0)
            {
                var baseType = interfaceDecl.BaseList.Types[0].Type.ToString();
                map[interfaceName] = baseType;
            }
        }

        return map;
    }

    /// <summary>
    /// Discovers controller base types in a repository by finding classes that:
    /// 1. Inherit from ApiController, ControllerBase, etc.
    /// 2. Have [ApiController] attribute
    /// 3. Have methods with [Route], [HttpGet], [HttpPost] etc. attributes
    /// </summary>
    public HashSet<string> DiscoverControllerBaseTypes(SyntaxTree tree)
    {
        var baseTypes = new HashSet<string>();
        var root = tree.GetRoot();

        var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classDecls)
        {
            // Check if class inherits from known controller types
            if (classDecl.BaseList != null)
            {
                foreach (var baseType in classDecl.BaseList.Types)
                {
                    var baseTypeName = baseType.Type.ToString();
                    if (baseTypeName.Contains("ApiController") ||
                        baseTypeName.Contains("ControllerBase") ||
                        baseTypeName.Contains("Controller"))
                    {
                        // This class is a controller base type
                        baseTypes.Add(classDecl.Identifier.Text);
                        break;
                    }
                }
            }

            // Check for [ApiController] attribute
            var hasApiControllerAttr = classDecl.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => a.Name.ToString().Contains("ApiController"));

            if (hasApiControllerAttr)
            {
                baseTypes.Add(classDecl.Identifier.Text);
            }

            // Check for route attributes on methods
            var methods = classDecl.Members.OfType<MethodDeclarationSyntax>();
            var hasRouteAttributes = methods.Any(m =>
                m.AttributeLists.SelectMany(al => al.Attributes)
                    .Any(a =>
                    {
                        var name = a.Name.ToString();
                        return name.Contains("Route") || name.Contains("HttpGet") ||
                               name.Contains("HttpPost") || name.Contains("HttpPut") ||
                               name.Contains("HttpDelete");
                    }));

            if (hasRouteAttributes)
            {
                baseTypes.Add(classDecl.Identifier.Text);
            }
        }

        return baseTypes;
    }

    /// <summary>
    /// Finds all classes that inherit from a given base type.
    /// </summary>
    public List<string> FindDerivedTypes(SyntaxTree tree, string baseTypeName)
    {
        var derivedTypes = new List<string>();
        var root = tree.GetRoot();

        var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classDecls)
        {
            if (classDecl.BaseList != null)
            {
                var inheritsFromBase = classDecl.BaseList.Types
                    .Any(t => t.Type.ToString().Contains(baseTypeName));

                if (inheritsFromBase)
                {
                    derivedTypes.Add(classDecl.Identifier.Text);
                }
            }
        }

        return derivedTypes;
    }

    /// <summary>
    /// Finds route registration patterns in startup/configuration code.
    /// Detects both attribute routing (MapHttpAttributeRoutes) and conventional routing.
    /// </summary>
    public RouteRegistrationInfo AnalyzeRouteRegistration(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var info = new RouteRegistrationInfo { FilePath = filePath };

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.Text;

            // Attribute routing: config.MapHttpAttributeRoutes()
            if (methodName == "MapHttpAttributeRoutes")
            {
                info.UsesAttributeRouting = true;
                info.AttributeRoutingLocation = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
            }

            // Conventional routing: config.Routes.MapHttpRoute(...)
            if (methodName == "MapHttpRoute")
            {
                var routeInfo = this.ExtractConventionalRoute(invocation);
                if (routeInfo != null)
                {
                    info.ConventionalRoutes.Add(routeInfo);
                }
            }

            // ASP.NET Core: app.MapControllers(), app.UseEndpoints(...)
            if (methodName == "MapControllers" || methodName == "UseEndpoints")
            {
                info.UsesAttributeRouting = true;
                info.AttributeRoutingLocation = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
            }

            // Custom route registration (e.g., config.Routes.Add(...))
            if (methodName == "Add" && memberAccess.Expression.ToString().Contains("Routes"))
            {
                info.HasCustomRouteRegistration = true;
            }
        }

        return info;
    }

    /// <summary>
    /// Extracts route information from a MapHttpRoute call.
    /// </summary>
    private ConventionalRouteInfo? ExtractConventionalRoute(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 2)
        {
            return null;
        }

        var routeInfo = new ConventionalRouteInfo();

        foreach (var arg in args)
        {
            var argName = arg.NameColon?.Name.Identifier.Text ?? string.Empty;
            var argValue = arg.Expression.ToString().Trim('"');

            if (argName == "name" || args.IndexOf(arg) == 0)
            {
                routeInfo.Name = argValue;
            }
            else if (argName == "routeTemplate" || args.IndexOf(arg) == 1)
            {
                routeInfo.Template = argValue;
            }
            else if (argName == "defaults")
            {
                routeInfo.Defaults = argValue;
            }
        }

        return routeInfo;
    }

    /// <summary>
    /// Analyzes a controller class to extract all API endpoints.
    /// Detects route attributes including custom ones like [GetRoute], [PutRoute], [OperationRoute].
    /// </summary>
    public ControllerEndpointInfo AnalyzeControllerEndpoints(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var result = new ControllerEndpointInfo { FilePath = filePath };

        var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classDecls)
        {
            var className = classDecl.Identifier.Text;

            // Get class-level attributes
            var classRoutePrefix = this.ExtractRoutePrefix(classDecl);
            var resourceRouting = this.ExtractResourceRoutingInfo(classDecl);

            // Get base type
            string? baseType = null;
            if (classDecl.BaseList != null)
            {
                baseType = classDecl.BaseList.Types.FirstOrDefault()?.Type.ToString();
            }

            var controllerInfo = new ControllerInfo
            {
                ClassName = className,
                BaseType = baseType,
                RoutePrefix = classRoutePrefix,
                ResourceRouting = resourceRouting,
                FilePath = filePath,
            };

            // Analyze methods for endpoints
            var methods = classDecl.Members.OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                var endpoint = this.AnalyzeMethodEndpoint(method, classRoutePrefix);
                if (endpoint != null)
                {
                    endpoint.ControllerClass = className;
                    controllerInfo.Endpoints.Add(endpoint);
                }
            }

            if (controllerInfo.Endpoints.Count > 0 || baseType?.Contains("Controller") == true)
            {
                result.Controllers.Add(controllerInfo);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts [RoutePrefix] attribute value from a class.
    /// </summary>
    private string? ExtractRoutePrefix(ClassDeclarationSyntax classDecl)
    {
        var routePrefixAttr = classDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString().Contains("RoutePrefix") ||
                                  a.Name.ToString() == "Route");

        if (routePrefixAttr?.ArgumentList?.Arguments.Count > 0)
        {
            return routePrefixAttr.ArgumentList.Arguments[0].Expression.ToString().Trim('"');
        }

        return null;
    }

    /// <summary>
    /// Extracts [ResourceRouting] attribute information from a class.
    /// </summary>
    private ResourceRoutingInfo? ExtractResourceRoutingInfo(ClassDeclarationSyntax classDecl)
    {
        var resourceRoutingAttr = classDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString().Contains("ResourceRouting"));

        if (resourceRoutingAttr == null)
        {
            return null;
        }

        var info = new ResourceRoutingInfo();

        if (resourceRoutingAttr.ArgumentList != null)
        {
            var args = resourceRoutingAttr.ArgumentList.Arguments.ToList();

            // First positional argument is typically the namespace
            if (args.Count > 0 && args[0].NameEquals == null && args[0].NameColon == null)
            {
                info.Namespace = args[0].Expression.ToString().Trim('"');
                info.NamespaceExpression = args[0].Expression.ToString();
            }

            foreach (var arg in args)
            {
                var argName = arg.NameEquals?.Name.Identifier.Text ??
                              arg.NameColon?.Name.Identifier.Text ?? string.Empty;
                var argValue = arg.Expression.ToString();

                if (argName == "resourceProviderNameSpace" || argName == "ResourceProviderNamespace")
                {
                    info.Namespace = argValue.Trim('"');
                    info.NamespaceExpression = argValue;
                }
                else if (argName == "parentControllerType")
                {
                    // Extract type from typeof(ControllerName)
                    if (argValue.StartsWith("typeof(") && argValue.EndsWith(")"))
                    {
                        info.ParentControllerType = argValue.Substring(7, argValue.Length - 8);
                    }
                    else
                    {
                        info.ParentControllerType = argValue;
                    }
                }
                else if (argName == "scope")
                {
                    info.Scope = argValue;
                }
            }
        }

        return info;
    }

    /// <summary>
    /// Builds a map of constant names to their values.
    /// Scans for public const string fields.
    /// </summary>
    /// <param name="constantFiles">Source files to scan for constants.</param>
    /// <returns>Map of fully qualified constant name to value.</returns>
    public Dictionary<string, string> BuildConstantMap(IEnumerable<(SyntaxTree tree, string filePath)> constantFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tree, filePath) in constantFiles)
        {
            var root = tree.GetRoot();
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classes)
            {
                var className = classDecl.Identifier.Text;

                foreach (var member in classDecl.Members)
                {
                    if (member is FieldDeclarationSyntax field &&
                        field.Modifiers.Any(m => m.Text == "const") &&
                        field.Declaration.Type.ToString() == "string")
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            var constName = variable.Identifier.Text;
                            if (variable.Initializer?.Value is LiteralExpressionSyntax literal)
                            {
                                var value = literal.Token.ValueText;
                                // Store with both short and qualified names
                                result[constName] = value;
                                result[$"{className}.{constName}"] = value;
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a namespace expression to its actual value.
    /// Handles constant references like UrpConstants.UpdateResourceProviderAdminNamespace.
    /// </summary>
    /// <param name="namespaceExpression">The expression from the attribute.</param>
    /// <param name="constantMap">Map of constant names to values.</param>
    /// <returns>The resolved namespace value.</returns>
    public string ResolveNamespace(string? namespaceExpression, IReadOnlyDictionary<string, string>? constantMap)
    {
        if (string.IsNullOrEmpty(namespaceExpression))
        {
            return string.Empty;
        }

        // If it's a string literal, return as-is
        if (namespaceExpression.StartsWith("\"") && namespaceExpression.EndsWith("\""))
        {
            return namespaceExpression.Trim('"');
        }

        // Try to resolve from constant map
        if (constantMap != null && constantMap.TryGetValue(namespaceExpression, out var resolved))
        {
            return resolved;
        }

        // Return as-is if can't resolve
        return namespaceExpression;
    }

    /// <summary>
    /// Resolves constant references in a route string.
    /// Replaces patterns like UrpConstants.CacheRouteName with their actual values.
    /// </summary>
    /// <param name="route">The route string that may contain constant references.</param>
    /// <param name="constantMap">Map of constant names to values.</param>
    /// <returns>The route with constants resolved.</returns>
    public string ResolveRouteConstants(string? route, IReadOnlyDictionary<string, string>? constantMap)
    {
        if (string.IsNullOrEmpty(route) || constantMap == null)
        {
            return route ?? string.Empty;
        }

        var result = route;

        // Sort keys by length descending to replace longer (more specific) keys first
        // This prevents "CacheRouteName" from matching before "UrpConstants.CacheRouteName"
        var sortedKeys = constantMap.Keys.OrderByDescending(k => k.Length);

        foreach (var key in sortedKeys)
        {
            if (result.Contains(key))
            {
                result = result.Replace(key, constantMap[key]);
            }
        }

        // Clean up any remaining quotes and plus signs from string concatenation
        result = result.Replace("\" + \"", string.Empty);
        result = result.Replace("\"", string.Empty);
        result = result.Trim();

        return result;
    }

    /// <summary>
    /// Builds an ARM-style route prefix for a controller based on its ResourceRouting attribute.
    /// Follows the pattern: providers/{namespace}/{parentResources}/{resourceType}
    /// </summary>
    /// <param name="controllerInfo">The controller info with ResourceRouting.</param>
    /// <param name="allControllers">All controllers for parent resolution.</param>
    /// <param name="resourceTypeMap">Map of controller name to resource type name.</param>
    /// <returns>The ARM route prefix.</returns>
    public string? BuildArmRoutePrefix(
        ControllerInfo controllerInfo,
        IReadOnlyList<ControllerInfo> allControllers,
        IReadOnlyDictionary<string, string>? resourceTypeMap = null)
    {
        var routing = controllerInfo.ResourceRouting;
        if (routing == null || string.IsNullOrEmpty(routing.Namespace))
        {
            return null;
        }

        var segments = new List<string>();
        segments.Add($"providers/{routing.Namespace}");

        // Build parent chain
        var parentChain = new Stack<(string ControllerName, string ResourceType)>();
        var currentParent = routing.ParentControllerType;

        while (!string.IsNullOrEmpty(currentParent))
        {
            var parentController = allControllers.FirstOrDefault(c => c.ClassName == currentParent);
            if (parentController == null)
            {
                break;
            }

            // Get resource type for parent
            var parentResourceType = this.GetResourceTypeName(parentController, resourceTypeMap);
            if (!string.IsNullOrEmpty(parentResourceType))
            {
                parentChain.Push((currentParent, parentResourceType));
            }

            currentParent = parentController.ResourceRouting?.ParentControllerType;
        }

        // Add parent segments in order (grandparent first)
        while (parentChain.Count > 0)
        {
            var (_, resourceType) = parentChain.Pop();
            segments.Add($"{resourceType}/{{name}}");
        }

        // Add this controller's resource type
        var thisResourceType = this.GetResourceTypeName(controllerInfo, resourceTypeMap);
        if (!string.IsNullOrEmpty(thisResourceType))
        {
            segments.Add(thisResourceType);
        }

        return string.Join("/", segments);
    }

    /// <summary>
    /// Gets the resource type name for a controller.
    /// Extracts from the generic type argument of the base class.
    /// </summary>
    private string? GetResourceTypeName(ControllerInfo controller, IReadOnlyDictionary<string, string>? resourceTypeMap)
    {
        // Extract model type name from base type generic argument
        // e.g., UnauthorizedResourceControllerBase<Update> -> Update
        var baseType = controller.BaseType;
        string? modelTypeName = null;

        if (!string.IsNullOrEmpty(baseType))
        {
            var genericStart = baseType.IndexOf('<');
            var genericEnd = baseType.LastIndexOf('>');
            if (genericStart > 0 && genericEnd > genericStart)
            {
                modelTypeName = baseType.Substring(genericStart + 1, genericEnd - genericStart - 1);
            }
        }

        // Check if we have an explicit mapping for the model type
        if (!string.IsNullOrEmpty(modelTypeName) && resourceTypeMap != null &&
            resourceTypeMap.TryGetValue(modelTypeName, out var mappedType))
        {
            return mappedType;
        }

        // Fall back to heuristic: Convert PascalCase to lowercase plural
        if (!string.IsNullOrEmpty(modelTypeName))
        {
            return this.ToResourceTypeName(modelTypeName);
        }

        return null;
    }

    /// <summary>
    /// Converts a model type name to a resource type name (lowercase, pluralized).
    /// </summary>
    private string ToResourceTypeName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return typeName;
        }

        // Handle common patterns
        var name = typeName;

        // Convert to camelCase
        if (char.IsUpper(name[0]))
        {
            name = char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // Simple pluralization (add 's' if doesn't end in 's')
        if (!name.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            name += "s";
        }

        return name;
    }

    /// <summary>
    /// Builds a map from model class names to their ResourceTypeName constants.
    /// Scans files for classes with ResourceTypeName or Type property that returns a constant.
    /// </summary>
    /// <param name="modelFiles">Source files to scan for model classes.</param>
    /// <returns>Map of class name to resource type name.</returns>
    public Dictionary<string, string> BuildResourceTypeNameMap(IEnumerable<(SyntaxTree tree, string filePath)> modelFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tree, filePath) in modelFiles)
        {
            var root = tree.GetRoot();
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classes)
            {
                var className = classDecl.Identifier.Text;

                // Look for ResourceTypeName constant or Type property
                foreach (var member in classDecl.Members)
                {
                    // Check for const string ResourceTypeName = "...";
                    if (member is FieldDeclarationSyntax field &&
                        field.Modifiers.Any(m => m.Text == "const"))
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            if (variable.Identifier.Text == "ResourceTypeName" &&
                                variable.Initializer?.Value is LiteralExpressionSyntax literal)
                            {
                                var value = literal.Token.ValueText;
                                if (!string.IsNullOrEmpty(value))
                                {
                                    result[className] = value;
                                }
                            }
                        }
                    }

                    // Check for Type property that returns a constant
                    if (member is PropertyDeclarationSyntax prop && prop.Identifier.Text == "Type")
                    {
                        // Check arrow expression: public override string Type => ResourceTypeName;
                        if (prop.ExpressionBody != null)
                        {
                            var exprText = prop.ExpressionBody.Expression.ToString();
                            if (exprText == "ResourceTypeName" && result.ContainsKey(className))
                            {
                                // Already captured from the constant
                            }
                            else if (prop.ExpressionBody.Expression is LiteralExpressionSyntax typeLiteral)
                            {
                                result[className] = typeLiteral.Token.ValueText;
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the full ARM route for an endpoint based on controller's ResourceRouting and method route attributes.
    /// Follows the pattern used by ResourceProviderDirectRouteProvider.
    /// </summary>
    /// <param name="controllerInfo">The controller info.</param>
    /// <param name="endpoint">The endpoint info.</param>
    /// <param name="allControllers">All controllers for parent resolution.</param>
    /// <param name="resourceTypeMap">Map of model type name to resource type name.</param>
    /// <returns>The full ARM route or null if not applicable.</returns>
    public string? BuildArmEndpointRoute(
        ControllerInfo controllerInfo,
        EndpointInfo endpoint,
        IReadOnlyList<ControllerInfo> allControllers,
        IReadOnlyDictionary<string, string>? resourceTypeMap = null)
    {
        // Get the ARM route prefix (e.g., providers/Microsoft.Update.Admin/updateLocations/{name}/updates)
        var routePrefix = this.BuildArmRoutePrefix(controllerInfo, allControllers, resourceTypeMap);

        // If no ARM route prefix, fall back to RoutePrefix or RouteTemplate
        if (string.IsNullOrEmpty(routePrefix))
        {
            // Use [RoutePrefix] if available
            if (!string.IsNullOrEmpty(controllerInfo.RoutePrefix))
            {
                routePrefix = controllerInfo.RoutePrefix;
            }
            else
            {
                // Use just the method's route template
                return endpoint.RouteTemplate;
            }
        }

        // Build the full route based on the method's route attribute type
        var actionRoute = this.GetActionRouteTemplate(endpoint);

        if (string.IsNullOrEmpty(actionRoute))
        {
            return routePrefix;
        }

        // Combine prefix with action route
        return $"{routePrefix}/{actionRoute}".TrimEnd('/');
    }

    /// <summary>
    /// Gets the route template suffix for a method based on its route attribute.
    /// Follows the pattern from GetRoute, GetAllRoute, PutRoute, DeleteRoute, OperationRoute.
    /// </summary>
    private string? GetActionRouteTemplate(EndpointInfo endpoint)
    {
        var methodName = endpoint.MethodName;
        var httpMethod = endpoint.HttpMethod;
        var routeTemplate = endpoint.RouteTemplate;

        // If there's an explicit route template (from standard [Route] or method template), use it
        if (!string.IsNullOrEmpty(routeTemplate))
        {
            return routeTemplate;
        }

        // For custom ARM route attributes, generate the template based on conventions
        // [GetRoute] -> {resourceName} (or {name})
        // [GetAllRoute] -> (empty, uses prefix only)
        // [PutRoute] -> {resourceName}
        // [DeleteRoute] -> {resourceName}
        // [OperationRoute("operationName")] -> {resourceName}/operationName
        if (endpoint.CustomRouteAttribute != null)
        {
            switch (endpoint.CustomRouteAttribute)
            {
                case "GetRoute":
                    return "{name}";
                case "GetAllRoute":
                    return null; // Uses prefix only
                case "PutRoute":
                    return "{name}";
                case "DeleteRoute":
                    return "{name}";
                case "OperationRoute":
                    // Operation route adds the operation name after {resourceName}
                    // e.g., [OperationRoute("checkHealth")] -> {name}/checkHealth
                    return endpoint.OperationName != null ? $"{{name}}/{endpoint.OperationName}" : "{name}";
            }
        }

        // Standard HTTP attribute without template
        // Infer from method name conventions
        if (httpMethod == "GET")
        {
            if (methodName.StartsWith("GetAll") || methodName == "GetAllResources")
            {
                return null; // Uses prefix only
            }
            else if (methodName.StartsWith("Get") || methodName == "GetResource")
            {
                return "{name}";
            }
        }
        else if (httpMethod == "POST")
        {
            // Operations typically have a name suffix
            return "{name}";
        }
        else if (httpMethod is "PUT" or "DELETE")
        {
            return "{name}";
        }

        return null;
    }

    /// <summary>
    /// Analyzes a method for endpoint information based on route attributes.
    /// </summary>
    private EndpointInfo? AnalyzeMethodEndpoint(MethodDeclarationSyntax method, string? classRoutePrefix)
    {
        var methodName = method.Identifier.Text;
        var attributes = method.AttributeLists.SelectMany(al => al.Attributes).ToList();

        if (attributes.Count == 0)
        {
            return null;
        }

        var endpoint = new EndpointInfo
        {
            MethodName = methodName,
            ReturnType = method.ReturnType.ToString(),
        };

        foreach (var attr in attributes)
        {
            var attrName = attr.Name.ToString();
            var attrArgs = attr.ArgumentList?.Arguments.ToList() ?? new List<AttributeArgumentSyntax>();

            // Standard HTTP method attributes
            if (attrName is "HttpGet" or "HttpGetAttribute")
            {
                endpoint.HttpMethod = "GET";
                if (attrArgs.Count > 0)
                {
                    endpoint.RouteTemplate = attrArgs[0].Expression.ToString().Trim('"');
                }
            }
            else if (attrName is "HttpPost" or "HttpPostAttribute")
            {
                endpoint.HttpMethod = "POST";
                if (attrArgs.Count > 0)
                {
                    endpoint.RouteTemplate = attrArgs[0].Expression.ToString().Trim('"');
                }
            }
            else if (attrName is "HttpPut" or "HttpPutAttribute")
            {
                endpoint.HttpMethod = "PUT";
                if (attrArgs.Count > 0)
                {
                    endpoint.RouteTemplate = attrArgs[0].Expression.ToString().Trim('"');
                }
            }
            else if (attrName is "HttpDelete" or "HttpDeleteAttribute")
            {
                endpoint.HttpMethod = "DELETE";
                if (attrArgs.Count > 0)
                {
                    endpoint.RouteTemplate = attrArgs[0].Expression.ToString().Trim('"');
                }
            }
            // Custom route attributes (resource provider pattern)
            else if (attrName.Contains("GetRoute") || attrName.Contains("GetAllRoute"))
            {
                endpoint.HttpMethod = "GET";
                endpoint.CustomRouteAttribute = attrName;
            }
            else if (attrName.Contains("PutRoute"))
            {
                endpoint.HttpMethod = "PUT";
                endpoint.CustomRouteAttribute = attrName;
            }
            else if (attrName.Contains("DeleteRoute"))
            {
                endpoint.HttpMethod = "DELETE";
                endpoint.CustomRouteAttribute = attrName;
            }
            else if (attrName.Contains("OperationRoute"))
            {
                endpoint.HttpMethod = "POST"; // Operations are typically POST
                endpoint.CustomRouteAttribute = attrName;
                if (attrArgs.Count > 0)
                {
                    endpoint.OperationName = attrArgs[0].Expression.ToString().Trim('"');
                }
            }
            else if (attrName is "Route" or "RouteAttribute")
            {
                if (attrArgs.Count > 0)
                {
                    endpoint.RouteTemplate = attrArgs[0].Expression.ToString().Trim('"');
                }
            }
        }

        // Only return if we found HTTP method info
        if (!string.IsNullOrEmpty(endpoint.HttpMethod) || !string.IsNullOrEmpty(endpoint.RouteTemplate))
        {
            // Combine with class route prefix if present
            if (!string.IsNullOrEmpty(classRoutePrefix) && !string.IsNullOrEmpty(endpoint.RouteTemplate))
            {
                endpoint.FullRoute = $"{classRoutePrefix}/{endpoint.RouteTemplate}";
            }
            else if (!string.IsNullOrEmpty(classRoutePrefix))
            {
                endpoint.FullRoute = classRoutePrefix;
            }
            else
            {
                endpoint.FullRoute = endpoint.RouteTemplate;
            }

            return endpoint;
        }

        return null;
    }

    /// <summary>
    /// Detects MapHttpAttributeRoutes calls and extracts custom route provider information.
    /// </summary>
    public AttributeRoutingSetup? AnalyzeAttributeRoutingSetup(SyntaxTree tree, string filePath)
    {
        var root = tree.GetRoot();
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "MapHttpAttributeRoutes")
            {
                var setup = new AttributeRoutingSetup
                {
                    FilePath = filePath,
                    LineNumber = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1,
                };

                // Check for custom route provider argument
                if (invocation.ArgumentList.Arguments.Count > 0)
                {
                    var arg = invocation.ArgumentList.Arguments[0];

                    // e.g., new ResourceProviderDirectRouteProvider(typeof(UnauthorizedResourceControllerBase<>))
                    if (arg.Expression is ObjectCreationExpressionSyntax objectCreation)
                    {
                        setup.CustomRouteProviderType = objectCreation.Type.ToString();

                        // Extract base controller type from typeof() argument
                        if (objectCreation.ArgumentList?.Arguments.Count > 0)
                        {
                            var typeofArg = objectCreation.ArgumentList.Arguments[0].Expression;
                            if (typeofArg is TypeOfExpressionSyntax typeofExpr)
                            {
                                setup.BaseControllerType = typeofExpr.Type.ToString();
                            }
                        }
                    }
                }

                return setup;
            }
        }

        return null;
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

/// <summary>
/// Represents a generic type registration from DI container.
/// </summary>
public record TypeRegistration
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

/// <summary>
/// Information about route registration in startup code.
/// </summary>
public record RouteRegistrationInfo
{
    public string FilePath { get; init; } = string.Empty;
    public bool UsesAttributeRouting { get; set; }
    public int? AttributeRoutingLocation { get; set; }
    public bool HasCustomRouteRegistration { get; set; }
    public List<ConventionalRouteInfo> ConventionalRoutes { get; } = new();
}

/// <summary>
/// Information about a conventional route registration.
/// </summary>
public record ConventionalRouteInfo
{
    public string Name { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Defaults { get; set; } = string.Empty;
}

/// <summary>
/// Information about all controllers and their endpoints in a file.
/// </summary>
public record ControllerEndpointInfo
{
    public string FilePath { get; init; } = string.Empty;
    public List<ControllerInfo> Controllers { get; } = new();
}

/// <summary>
/// Information about a controller class.
/// </summary>
public record ControllerInfo
{
    public string ClassName { get; init; } = string.Empty;
    public string? BaseType { get; init; }
    public string? RoutePrefix { get; set; }
    public ResourceRoutingInfo? ResourceRouting { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public List<EndpointInfo> Endpoints { get; } = new();
}

/// <summary>
/// Information about a [ResourceRouting] attribute.
/// </summary>
public record ResourceRoutingInfo
{
    public string? ResourceType { get; set; }
    public string? ParentResourceType { get; set; }
    public string? Namespace { get; set; }
    public string? NamespaceExpression { get; set; }
    public string? ParentControllerType { get; set; }
    public string? Scope { get; set; }
}

/// <summary>
/// Information about an API endpoint.
/// </summary>
public record EndpointInfo
{
    public string? ControllerClass { get; set; }
    public string MethodName { get; init; } = string.Empty;
    public string? HttpMethod { get; set; }
    public string? RouteTemplate { get; set; }
    public string? FullRoute { get; set; }
    public string? ReturnType { get; init; }
    public string? CustomRouteAttribute { get; set; }
    public string? OperationName { get; set; }
}

/// <summary>
/// Information about attribute routing setup (MapHttpAttributeRoutes).
/// </summary>
public record AttributeRoutingSetup
{
    public string FilePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string? CustomRouteProviderType { get; set; }
    public string? BaseControllerType { get; set; }
}

#endregion
