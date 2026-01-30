// -----------------------------------------------------------------------
// <copyright file="LayerClassificationAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes and classifies code components into architectural layers.
/// </summary>
public class LayerClassificationAnalyzer
{
    /// <summary>
    /// Layer types used in classification.
    /// </summary>
    public enum ArchitectureLayer
    {
        /// <summary>Unknown or unclassified layer.</summary>
        Unknown,

        /// <summary>Presentation layer (UI, Views, Pages).</summary>
        Presentation,

        /// <summary>API layer (Controllers, Endpoints).</summary>
        Api,

        /// <summary>Application layer (Services, Handlers, Use Cases).</summary>
        Application,

        /// <summary>Domain layer (Entities, Aggregates, Value Objects).</summary>
        Domain,

        /// <summary>Infrastructure layer (Repositories, DbContext, External Clients).</summary>
        Infrastructure,

        /// <summary>Cross-cutting concerns (Logging, Auth, Caching).</summary>
        CrossCutting,
    }

    private static readonly Dictionary<string, ArchitectureLayer> NamespaceLayerPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        // Presentation patterns
        { "Views", ArchitectureLayer.Presentation },
        { "Pages", ArchitectureLayer.Presentation },
        { "Components", ArchitectureLayer.Presentation },
        { "UI", ArchitectureLayer.Presentation },
        { "Presentation", ArchitectureLayer.Presentation },
        { "Web.UI", ArchitectureLayer.Presentation },
        { "Blazor", ArchitectureLayer.Presentation },

        // API patterns
        { "Controllers", ArchitectureLayer.Api },
        { "Api", ArchitectureLayer.Api },
        { "Endpoints", ArchitectureLayer.Api },
        { "WebApi", ArchitectureLayer.Api },
        { "Rest", ArchitectureLayer.Api },

        // Application patterns
        { "Application", ArchitectureLayer.Application },
        { "Services", ArchitectureLayer.Application },
        { "UseCases", ArchitectureLayer.Application },
        { "Handlers", ArchitectureLayer.Application },
        { "Commands", ArchitectureLayer.Application },
        { "Queries", ArchitectureLayer.Application },

        // Domain patterns
        { "Domain", ArchitectureLayer.Domain },
        { "Entities", ArchitectureLayer.Domain },
        { "Aggregates", ArchitectureLayer.Domain },
        { "ValueObjects", ArchitectureLayer.Domain },
        { "Model", ArchitectureLayer.Domain },
        { "DomainEvents", ArchitectureLayer.Domain },

        // Infrastructure patterns
        { "Infrastructure", ArchitectureLayer.Infrastructure },
        { "Persistence", ArchitectureLayer.Infrastructure },
        { "Data", ArchitectureLayer.Infrastructure },
        { "Repositories", ArchitectureLayer.Infrastructure },
        { "External", ArchitectureLayer.Infrastructure },
        { "Adapters", ArchitectureLayer.Infrastructure },

        // Cross-cutting patterns
        { "Common", ArchitectureLayer.CrossCutting },
        { "Shared", ArchitectureLayer.CrossCutting },
        { "CrossCutting", ArchitectureLayer.CrossCutting },
        { "Utilities", ArchitectureLayer.CrossCutting },
        { "Helpers", ArchitectureLayer.CrossCutting },
        { "Extensions", ArchitectureLayer.CrossCutting },
    };

    private static readonly HashSet<string> PresentationBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ComponentBase", "LayoutComponentBase", "OwningComponentBase", "Page", "PageModel",
        "ViewComponent", "RazorPage", "PartialViewResult",
    };

    private static readonly HashSet<string> ApiBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Controller", "ControllerBase", "ApiController", "ODataController",
        "Hub", "Hub<T>", // SignalR
    };

    // Base types that are infrastructure/lifecycle, not application services
    private static readonly HashSet<string> InfrastructureLifecycleBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BackgroundService", "IHostedService", "ServiceBase", // Hosted/background services
        "StatelessService", "StatefulService", // Service Fabric
    };

    private static readonly HashSet<string> ApiAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiController", "Route", "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch",
    };

    private static readonly HashSet<string> ApplicationInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "IRequestHandler", "INotificationHandler", "ICommandHandler", "IQueryHandler",
        "IEventHandler", "IMediator",
    };

    private static readonly HashSet<string> DomainBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Entity", "EntityBase", "AggregateRoot", "AggregateRootBase", "ValueObject",
        "DomainEvent", "IDomainEvent", "IEntity", "IAggregateRoot",
    };

    private static readonly HashSet<string> InfrastructureBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DbContext", "IdentityDbContext", "Repository", "RepositoryBase",
    };

    private static readonly HashSet<string> InfrastructureInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "IRepository", "IUnitOfWork", "IDbContextFactory",
    };

    private static readonly HashSet<string> CrossCuttingInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "ILogger", "ILoggerProvider", "ILoggerFactory",
        "IDistributedCache", "IMemoryCache",
        "IAuthenticationHandler", "IAuthorizationHandler",
        "IConfiguration", "IOptions",
    };

    /// <summary>
    /// Classifies all types in a syntax tree into architectural layers.
    /// </summary>
    public List<LayerClassification> ClassifyTypes(SyntaxTree tree, string filePath)
    {
        var results = new List<LayerClassification>();
        var root = tree.GetRoot();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var classification = this.ClassifyType(typeDecl, filePath);
            if (classification != null)
            {
                results.Add(classification);
            }
        }

        return results;
    }

    /// <summary>
    /// Classifies a single type declaration into an architectural layer.
    /// </summary>
    public LayerClassification? ClassifyType(TypeDeclarationSyntax typeDecl, string filePath)
    {
        var typeName = typeDecl.Identifier.Text;
        var namespaceName = this.GetNamespace(typeDecl);
        var lineNumber = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Determine layer using multiple signals
        var layer = this.DetermineLayer(typeDecl, namespaceName, filePath);
        var confidence = this.CalculateConfidence(typeDecl, layer, namespaceName);

        return new LayerClassification
        {
            TypeName = typeName,
            Namespace = namespaceName,
            Layer = layer,
            FilePath = filePath,
            LineNumber = lineNumber,
            Confidence = confidence,
            ClassificationReason = this.GetClassificationReason(typeDecl, layer, namespaceName),
            IsInterface = typeDecl is InterfaceDeclarationSyntax,
            IsAbstract = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)),
        };
    }

    /// <summary>
    /// Determines the architectural layer for a type based on multiple signals.
    /// </summary>
    private ArchitectureLayer DetermineLayer(TypeDeclarationSyntax typeDecl, string? namespaceName, string filePath)
    {
        var typeName = typeDecl.Identifier.Text;

        // 1. Check base types and interfaces first (strongest signal)
        var layerFromInheritance = this.DetermineLayerFromInheritance(typeDecl);
        if (layerFromInheritance != ArchitectureLayer.Unknown)
        {
            return layerFromInheritance;
        }

        // 2. Check attributes
        var layerFromAttributes = this.DetermineLayerFromAttributes(typeDecl);
        if (layerFromAttributes != ArchitectureLayer.Unknown)
        {
            return layerFromAttributes;
        }

        // 3. Check namespace patterns
        if (!string.IsNullOrEmpty(namespaceName))
        {
            var layerFromNamespace = this.DetermineLayerFromNamespace(namespaceName);
            if (layerFromNamespace != ArchitectureLayer.Unknown)
            {
                return layerFromNamespace;
            }
        }

        // 4. Check file path patterns (for Razor/Blazor)
        var layerFromPath = this.DetermineLayerFromFilePath(filePath);
        if (layerFromPath != ArchitectureLayer.Unknown)
        {
            return layerFromPath;
        }

        // 5. Check naming conventions (weakest signal)
        var layerFromNaming = this.DetermineLayerFromNaming(typeName);
        if (layerFromNaming != ArchitectureLayer.Unknown)
        {
            return layerFromNaming;
        }

        return ArchitectureLayer.Unknown;
    }

    private ArchitectureLayer DetermineLayerFromInheritance(TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl.BaseList == null)
        {
            return ArchitectureLayer.Unknown;
        }

        foreach (var baseType in typeDecl.BaseList.Types)
        {
            var baseTypeName = this.GetSimpleTypeName(baseType.Type);

            // Presentation
            if (PresentationBaseTypes.Contains(baseTypeName))
            {
                return ArchitectureLayer.Presentation;
            }

            // API
            if (ApiBaseTypes.Contains(baseTypeName))
            {
                return ArchitectureLayer.Api;
            }

            // Infrastructure lifecycle (before Application to take precedence)
            if (InfrastructureLifecycleBaseTypes.Contains(baseTypeName))
            {
                return ArchitectureLayer.Infrastructure;
            }

            // Application
            if (ApplicationInterfaces.Any(i => baseTypeName.StartsWith(i, StringComparison.OrdinalIgnoreCase)))
            {
                return ArchitectureLayer.Application;
            }

            // Domain
            if (DomainBaseTypes.Contains(baseTypeName))
            {
                return ArchitectureLayer.Domain;
            }

            // Infrastructure
            if (InfrastructureBaseTypes.Contains(baseTypeName) ||
                InfrastructureInterfaces.Any(i => baseTypeName.StartsWith(i, StringComparison.OrdinalIgnoreCase)))
            {
                return ArchitectureLayer.Infrastructure;
            }

            // Cross-cutting
            if (CrossCuttingInterfaces.Any(i => baseTypeName.StartsWith(i, StringComparison.OrdinalIgnoreCase)))
            {
                return ArchitectureLayer.CrossCutting;
            }
        }

        return ArchitectureLayer.Unknown;
    }

    private ArchitectureLayer DetermineLayerFromAttributes(TypeDeclarationSyntax typeDecl)
    {
        foreach (var attrList in typeDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();

                if (ApiAttributes.Contains(attrName) || ApiAttributes.Contains(attrName + "Attribute"))
                {
                    return ArchitectureLayer.Api;
                }
            }
        }

        return ArchitectureLayer.Unknown;
    }

    private ArchitectureLayer DetermineLayerFromNamespace(string namespaceName)
    {
        var parts = namespaceName.Split('.');

        // Check each namespace segment from right to left (more specific first)
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (NamespaceLayerPatterns.TryGetValue(parts[i], out var layer))
            {
                return layer;
            }
        }

        return ArchitectureLayer.Unknown;
    }

    private ArchitectureLayer DetermineLayerFromFilePath(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/').ToLowerInvariant();

        // Razor/Blazor files
        if (normalizedPath.EndsWith(".razor") || normalizedPath.EndsWith(".cshtml"))
        {
            return ArchitectureLayer.Presentation;
        }

        // Views folder
        if (normalizedPath.Contains("/views/") || normalizedPath.Contains("/pages/"))
        {
            return ArchitectureLayer.Presentation;
        }

        // Controllers folder
        if (normalizedPath.Contains("/controllers/") || normalizedPath.Contains("/api/"))
        {
            return ArchitectureLayer.Api;
        }

        return ArchitectureLayer.Unknown;
    }

    private ArchitectureLayer DetermineLayerFromNaming(string typeName)
    {
        // Controller naming
        if (typeName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureLayer.Api;
        }

        // Service naming (application layer)
        if (typeName.EndsWith("Service", StringComparison.OrdinalIgnoreCase) &&
            !typeName.EndsWith("HostedService", StringComparison.OrdinalIgnoreCase) &&
            !typeName.EndsWith("BackgroundService", StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureLayer.Application;
        }

        // Handler naming
        if (typeName.EndsWith("Handler", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("UseCase", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Orchestrator", StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureLayer.Application;
        }

        // Repository naming
        if (typeName.EndsWith("Repository", StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureLayer.Infrastructure;
        }

        // Client naming (usually infrastructure)
        if (typeName.EndsWith("Client", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Gateway", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Adapter", StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureLayer.Infrastructure;
        }

        // Helper/Utility naming
        if (typeName.EndsWith("Helper", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Utility", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Extensions", StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureLayer.CrossCutting;
        }

        return ArchitectureLayer.Unknown;
    }

    private float CalculateConfidence(TypeDeclarationSyntax typeDecl, ArchitectureLayer layer, string? namespaceName)
    {
        if (layer == ArchitectureLayer.Unknown)
        {
            return 0.0f;
        }

        var signals = 0;
        var maxSignals = 4; // inheritance, attributes, namespace, naming

        // Check inheritance
        if (this.DetermineLayerFromInheritance(typeDecl) == layer)
        {
            signals += 2; // Stronger weight
        }

        // Check attributes
        if (this.DetermineLayerFromAttributes(typeDecl) == layer)
        {
            signals++;
        }

        // Check namespace
        if (!string.IsNullOrEmpty(namespaceName) && this.DetermineLayerFromNamespace(namespaceName) == layer)
        {
            signals++;
        }

        // Check naming
        if (this.DetermineLayerFromNaming(typeDecl.Identifier.Text) == layer)
        {
            signals++;
        }

        return Math.Min(1.0f, signals / (float)maxSignals);
    }

    private string GetClassificationReason(TypeDeclarationSyntax typeDecl, ArchitectureLayer layer, string? namespaceName)
    {
        if (layer == ArchitectureLayer.Unknown)
        {
            return "No classification signals detected";
        }

        var reasons = new List<string>();

        if (this.DetermineLayerFromInheritance(typeDecl) == layer)
        {
            var baseTypes = typeDecl.BaseList?.Types.Select(t => t.Type.ToString()) ?? [];
            reasons.Add($"Inherits from: {string.Join(", ", baseTypes)}");
        }

        if (this.DetermineLayerFromAttributes(typeDecl) == layer)
        {
            var attrs = typeDecl.AttributeLists.SelectMany(al => al.Attributes).Select(a => a.Name.ToString());
            reasons.Add($"Has attributes: {string.Join(", ", attrs)}");
        }

        if (!string.IsNullOrEmpty(namespaceName) && this.DetermineLayerFromNamespace(namespaceName) == layer)
        {
            reasons.Add($"Namespace pattern: {namespaceName}");
        }

        if (this.DetermineLayerFromNaming(typeDecl.Identifier.Text) == layer)
        {
            reasons.Add($"Name convention: {typeDecl.Identifier.Text}");
        }

        return reasons.Count > 0 ? string.Join("; ", reasons) : "Unknown";
    }

    private string? GetNamespace(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is BaseNamespaceDeclarationSyntax ns)
            {
                return ns.Name.ToString();
            }

            current = current.Parent;
        }

        return null;
    }

    private string GetSimpleTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => this.GetSimpleTypeName(qualified.Right),
            _ => type.ToString(),
        };
    }
}

/// <summary>
/// Represents the classification of a type into an architectural layer.
/// </summary>
public record LayerClassification
{
    /// <summary>Gets the type name.</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets the namespace.</summary>
    public string? Namespace { get; init; }

    /// <summary>Gets the classified layer.</summary>
    public required LayerClassificationAnalyzer.ArchitectureLayer Layer { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the confidence score (0-1).</summary>
    public required float Confidence { get; init; }

    /// <summary>Gets the reason for classification.</summary>
    public required string ClassificationReason { get; init; }

    /// <summary>Gets whether this is an interface.</summary>
    public bool IsInterface { get; init; }

    /// <summary>Gets whether this is abstract.</summary>
    public bool IsAbstract { get; init; }
}
