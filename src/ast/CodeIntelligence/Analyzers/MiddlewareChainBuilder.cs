// -----------------------------------------------------------------------
// <copyright file="MiddlewareChainBuilder.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Builds ordered middleware pipeline graphs from ASP.NET Core applications.
/// </summary>
public sealed class MiddlewareChainBuilder
{
    private static readonly HashSet<string> MiddlewareRegistrationMethods = new(StringComparer.Ordinal)
    {
        "UseMiddleware",
        "Use",
        "UseRouting",
        "UseEndpoints",
        "UseAuthentication",
        "UseAuthorization",
        "UseCors",
        "UseHttpsRedirection",
        "UseStaticFiles",
        "UseSession",
        "UseResponseCaching",
        "UseResponseCompression",
        "UseExceptionHandler",
        "UseDeveloperExceptionPage",
        "UseHsts",
        "UseSwagger",
        "UseSwaggerUI",
        "UseHealthChecks",
        "MapControllers",
        "MapRazorPages",
        "MapBlazorHub",
        "MapHub",
        "MapGet",
        "MapPost",
        "MapPut",
        "MapDelete",
        "MapWhen",
        "UseWhen",
        "Map",
        "Run"
    };

    /// <summary>
    /// Analyzes a syntax tree for middleware registrations.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of middleware registrations in order.</returns>
    public IReadOnlyList<MiddlewareRegistration> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var registrations = new List<MiddlewareRegistration>();
        var order = 0;

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var registration = this.AnalyzeInvocation(invocation, filePath, ref order);
            if (registration != null)
            {
                registrations.Add(registration);
            }
        }

        return registrations;
    }

    /// <summary>
    /// Builds a middleware chain from registrations.
    /// </summary>
    /// <param name="registrations">The middleware registrations.</param>
    /// <returns>The middleware chain.</returns>
    public MiddlewareChain BuildChain(IReadOnlyList<MiddlewareRegistration> registrations)
    {
        var nodes = registrations.Select(r => new MiddlewareNode
        {
            Name = r.MiddlewareName ?? r.MethodName,
            MiddlewareType = r.MiddlewareType,
            Order = r.Order,
            IsTerminal = r.IsTerminal,
            IsBranching = r.IsBranching,
            FilePath = r.FilePath,
            LineNumber = r.LineNumber
        }).ToList();

        // Link nodes
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            nodes[i].Next = nodes[i + 1];
        }

        return new MiddlewareChain
        {
            Nodes = nodes,
            First = nodes.FirstOrDefault(),
            TerminalMiddleware = nodes.LastOrDefault(n => n.IsTerminal)
        };
    }

    private MiddlewareRegistration? AnalyzeInvocation(
        InvocationExpressionSyntax invocation,
        string filePath,
        ref int order)
    {
        var methodName = GetMethodName(invocation);
        if (methodName == null || !MiddlewareRegistrationMethods.Contains(methodName))
        {
            return null;
        }

        var lineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var middlewareName = ExtractMiddlewareName(invocation, methodName);
        var middlewareType = DetermineMiddlewareType(methodName);
        var isTerminal = methodName == "Run" || methodName.StartsWith("Map");
        var isBranching = methodName == "MapWhen" || methodName == "UseWhen" || methodName == "Map";

        return new MiddlewareRegistration
        {
            MethodName = methodName,
            MiddlewareName = middlewareName,
            MiddlewareType = middlewareType,
            Order = order++,
            IsTerminal = isTerminal,
            IsBranching = isBranching,
            Arguments = ExtractArguments(invocation),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name switch
            {
                GenericNameSyntax generic => generic.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => null
            },
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null
        };
    }

    private static string? ExtractMiddlewareName(InvocationExpressionSyntax invocation, string methodName)
    {
        // For UseMiddleware<T>, extract T
        if (methodName == "UseMiddleware")
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name is GenericNameSyntax genericName &&
                genericName.TypeArgumentList.Arguments.Count > 0)
            {
                return genericName.TypeArgumentList.Arguments[0].ToString();
            }
        }

        // For MapHub<T>, extract T
        if (methodName == "MapHub")
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name is GenericNameSyntax genericName &&
                genericName.TypeArgumentList.Arguments.Count > 0)
            {
                return genericName.TypeArgumentList.Arguments[0].ToString();
            }
        }

        // For methods like MapGet, MapPost, extract the route
        if (methodName.StartsWith("Map") && invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                return $"{methodName}:{literal.Token.ValueText}";
            }
        }

        // For Use with type argument
        if (methodName == "Use" && invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is TypeOfExpressionSyntax typeOf)
            {
                return typeOf.Type.ToString();
            }
        }

        return null;
    }

    private static MiddlewareCategory DetermineMiddlewareType(string methodName)
    {
        return methodName switch
        {
            "UseAuthentication" => MiddlewareCategory.Authentication,
            "UseAuthorization" => MiddlewareCategory.Authorization,
            "UseCors" => MiddlewareCategory.Cors,
            "UseHttpsRedirection" => MiddlewareCategory.Security,
            "UseHsts" => MiddlewareCategory.Security,
            "UseStaticFiles" => MiddlewareCategory.StaticFiles,
            "UseSession" => MiddlewareCategory.Session,
            "UseResponseCaching" => MiddlewareCategory.Caching,
            "UseResponseCompression" => MiddlewareCategory.Compression,
            "UseExceptionHandler" => MiddlewareCategory.ErrorHandling,
            "UseDeveloperExceptionPage" => MiddlewareCategory.ErrorHandling,
            "UseRouting" => MiddlewareCategory.Routing,
            "UseEndpoints" => MiddlewareCategory.Endpoints,
            "MapControllers" => MiddlewareCategory.Endpoints,
            "MapRazorPages" => MiddlewareCategory.Endpoints,
            "MapBlazorHub" => MiddlewareCategory.Endpoints,
            "MapHub" => MiddlewareCategory.Endpoints,
            "MapGet" or "MapPost" or "MapPut" or "MapDelete" => MiddlewareCategory.Endpoints,
            "UseHealthChecks" => MiddlewareCategory.HealthCheck,
            "UseSwagger" or "UseSwaggerUI" => MiddlewareCategory.Documentation,
            "Run" => MiddlewareCategory.Terminal,
            "MapWhen" or "UseWhen" or "Map" => MiddlewareCategory.Branching,
            _ => MiddlewareCategory.Custom
        };
    }

    private static List<string> ExtractArguments(InvocationExpressionSyntax invocation)
    {
        return invocation.ArgumentList.Arguments
            .Select(a => a.Expression.ToString())
            .ToList();
    }
}

/// <summary>
/// Represents a middleware registration.
/// </summary>
public record MiddlewareRegistration
{
    /// <summary>Gets the registration method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the middleware name (if determinable).</summary>
    public string? MiddlewareName { get; init; }

    /// <summary>Gets the middleware type category.</summary>
    public required MiddlewareCategory MiddlewareType { get; init; }

    /// <summary>Gets the order in the pipeline.</summary>
    public required int Order { get; init; }

    /// <summary>Gets whether this is a terminal middleware.</summary>
    public bool IsTerminal { get; init; }

    /// <summary>Gets whether this middleware branches the pipeline.</summary>
    public bool IsBranching { get; init; }

    /// <summary>Gets the arguments passed to the registration.</summary>
    public List<string> Arguments { get; init; } = new();

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// A node in the middleware chain.
/// </summary>
public class MiddlewareNode
{
    /// <summary>Gets or sets the middleware name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the middleware type.</summary>
    public required MiddlewareCategory MiddlewareType { get; set; }

    /// <summary>Gets or sets the order in the pipeline.</summary>
    public required int Order { get; set; }

    /// <summary>Gets or sets whether this is a terminal middleware.</summary>
    public bool IsTerminal { get; set; }

    /// <summary>Gets or sets whether this middleware branches.</summary>
    public bool IsBranching { get; set; }

    /// <summary>Gets or sets the next middleware in the chain.</summary>
    public MiddlewareNode? Next { get; set; }

    /// <summary>Gets or sets the branch pipeline (for branching middleware).</summary>
    public MiddlewareNode? Branch { get; set; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; set; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; set; }
}

/// <summary>
/// The complete middleware chain.
/// </summary>
public record MiddlewareChain
{
    /// <summary>Gets all nodes in the chain.</summary>
    public required IReadOnlyList<MiddlewareNode> Nodes { get; init; }

    /// <summary>Gets the first middleware in the chain.</summary>
    public MiddlewareNode? First { get; init; }

    /// <summary>Gets the terminal middleware.</summary>
    public MiddlewareNode? TerminalMiddleware { get; init; }
}

/// <summary>
/// Categories of middleware.
/// </summary>
public enum MiddlewareCategory
{
    /// <summary>Custom middleware.</summary>
    Custom,

    /// <summary>Authentication middleware.</summary>
    Authentication,

    /// <summary>Authorization middleware.</summary>
    Authorization,

    /// <summary>CORS middleware.</summary>
    Cors,

    /// <summary>Security middleware.</summary>
    Security,

    /// <summary>Static files middleware.</summary>
    StaticFiles,

    /// <summary>Session middleware.</summary>
    Session,

    /// <summary>Caching middleware.</summary>
    Caching,

    /// <summary>Compression middleware.</summary>
    Compression,

    /// <summary>Error handling middleware.</summary>
    ErrorHandling,

    /// <summary>Routing middleware.</summary>
    Routing,

    /// <summary>Endpoints middleware.</summary>
    Endpoints,

    /// <summary>Health check middleware.</summary>
    HealthCheck,

    /// <summary>Documentation middleware (Swagger, etc.).</summary>
    Documentation,

    /// <summary>Terminal middleware (Run).</summary>
    Terminal,

    /// <summary>Branching middleware (MapWhen, UseWhen).</summary>
    Branching
}
