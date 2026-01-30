// -----------------------------------------------------------------------
// <copyright file="DomainModelAnalyzer.cs" company="Microsoft">
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
/// Analyzes domain-driven design patterns including aggregates, entities, events, and CQRS.
/// </summary>
public class DomainModelAnalyzer
{
    private static readonly HashSet<string> AggregateRootPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "AggregateRoot", "Aggregate", "IAggregateRoot", "AggregateRootBase",
    };

    private static readonly HashSet<string> EntityPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Entity", "EntityBase", "IEntity", "BaseEntity",
    };

    private static readonly HashSet<string> DomainEventPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "DomainEvent", "IDomainEvent", "INotification", "IntegrationEvent",
        "IEvent", "EventBase", "DomainEventBase",
    };

    private static readonly HashSet<string> CommandPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "ICommand", "IRequest", "Command", "CommandBase",
    };

    private static readonly HashSet<string> QueryPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "IQuery", "Query", "QueryBase",
    };

    private static readonly HashSet<string> HandlerPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "ICommandHandler", "IRequestHandler", "IQueryHandler", "INotificationHandler",
    };

    /// <summary>
    /// Analyzes domain aggregates and entities in the given syntax tree.
    /// </summary>
    public List<AggregateInfo> AnalyzeAggregates(SyntaxTree tree, string filePath)
    {
        var results = new List<AggregateInfo>();
        var root = tree.GetRoot();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var info = this.AnalyzeAggregateClass(classDecl, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        // Also check record types (value objects)
        foreach (var recordDecl in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
        {
            var info = this.AnalyzeValueObjectRecord(recordDecl, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes domain events in the given syntax tree.
    /// </summary>
    public List<DomainEventInfo> AnalyzeDomainEvents(SyntaxTree tree, string filePath)
    {
        var results = new List<DomainEventInfo>();
        var root = tree.GetRoot();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var info = this.AnalyzeDomainEventClass(classDecl, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        // Also check record types for events
        foreach (var recordDecl in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
        {
            var info = this.AnalyzeDomainEventRecord(recordDecl, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes CQRS patterns (commands, queries, handlers) in the given syntax tree.
    /// </summary>
    public List<CqrsPatternInfo> AnalyzeCqrsPatterns(SyntaxTree tree, string filePath)
    {
        var results = new List<CqrsPatternInfo>();
        var root = tree.GetRoot();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var info = this.AnalyzeCqrsClass(classDecl, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        // Also check record types for commands/queries
        foreach (var recordDecl in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
        {
            var info = this.AnalyzeCqrsRecord(recordDecl, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes event raising patterns within aggregates.
    /// </summary>
    public List<EventRaisingInfo> AnalyzeEventRaising(SyntaxTree tree, string filePath)
    {
        var results = new List<EventRaisingInfo>();
        var root = tree.GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var info = this.AnalyzeEventRaisingInvocation(invocation, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    private AggregateInfo? AnalyzeAggregateClass(ClassDeclarationSyntax classDecl, string filePath)
    {
        if (classDecl.BaseList == null)
        {
            return null;
        }

        var baseTypes = classDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
        DomainEntityType? entityType = null;

        // Check for aggregate root
        if (baseTypes.Any(bt => AggregateRootPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            entityType = DomainEntityType.AggregateRoot;
        }
        // Check for entity
        else if (baseTypes.Any(bt => EntityPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            entityType = DomainEntityType.Entity;
        }

        if (entityType == null)
        {
            return null;
        }

        // Extract ID type if generic
        string? idType = null;
        foreach (var baseType in classDecl.BaseList.Types)
        {
            if (baseType.Type is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count > 0)
            {
                idType = generic.TypeArgumentList.Arguments[0].ToString();
                break;
            }
        }

        // Extract properties
        var properties = classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(p => new DomainProperty
            {
                Name = p.Identifier.Text,
                Type = p.Type.ToString(),
                IsReadOnly = p.AccessorList?.Accessors.All(a => a.Kind() == SyntaxKind.GetAccessorDeclaration) ?? false,
            })
            .ToList();

        // Check for factory methods
        var factoryMethods = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) &&
                        m.ReturnType.ToString() == classDecl.Identifier.Text)
            .Select(m => m.Identifier.Text)
            .ToList();

        return new AggregateInfo
        {
            Name = classDecl.Identifier.Text,
            EntityType = entityType.Value,
            IdType = idType,
            Properties = properties,
            FactoryMethods = factoryMethods,
            FilePath = filePath,
            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private AggregateInfo? AnalyzeValueObjectRecord(RecordDeclarationSyntax recordDecl, string filePath)
    {
        // Records are often used as value objects
        // Check if it has base type indicating value object, or check by convention
        var isValueObject = false;

        if (recordDecl.BaseList != null)
        {
            var baseTypes = recordDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
            isValueObject = baseTypes.Any(bt =>
                bt.Contains("ValueObject", StringComparison.OrdinalIgnoreCase) ||
                bt.Contains("IValueObject", StringComparison.OrdinalIgnoreCase));
        }

        // Also detect by naming convention
        if (!isValueObject)
        {
            var name = recordDecl.Identifier.Text;
            isValueObject = name.EndsWith("Id") || name.EndsWith("Value") ||
                           name.EndsWith("Amount") || name.EndsWith("Address") ||
                           name.EndsWith("Email") || name.EndsWith("Money");
        }

        if (!isValueObject)
        {
            return null;
        }

        // Extract parameters as properties
        var properties = new List<DomainProperty>();
        if (recordDecl.ParameterList != null)
        {
            properties = recordDecl.ParameterList.Parameters
                .Select(p => new DomainProperty
                {
                    Name = p.Identifier.Text,
                    Type = p.Type?.ToString() ?? "object",
                    IsReadOnly = true,
                })
                .ToList();
        }

        return new AggregateInfo
        {
            Name = recordDecl.Identifier.Text,
            EntityType = DomainEntityType.ValueObject,
            Properties = properties,
            FactoryMethods = new List<string>(),
            FilePath = filePath,
            LineNumber = recordDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.80f,
        };
    }

    private DomainEventInfo? AnalyzeDomainEventClass(ClassDeclarationSyntax classDecl, string filePath)
    {
        if (classDecl.BaseList == null)
        {
            return null;
        }

        var baseTypes = classDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
        DomainEventType? eventType = null;

        if (baseTypes.Any(bt => bt.Contains("IntegrationEvent", StringComparison.OrdinalIgnoreCase)))
        {
            eventType = DomainEventType.IntegrationEvent;
        }
        else if (baseTypes.Any(bt => DomainEventPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            eventType = DomainEventType.DomainEvent;
        }

        if (eventType == null)
        {
            return null;
        }

        // Extract event properties
        var properties = classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(p => p.Identifier.Text)
            .ToList();

        return new DomainEventInfo
        {
            Name = classDecl.Identifier.Text,
            EventType = eventType.Value,
            Properties = properties,
            FilePath = filePath,
            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private DomainEventInfo? AnalyzeDomainEventRecord(RecordDeclarationSyntax recordDecl, string filePath)
    {
        if (recordDecl.BaseList == null)
        {
            return null;
        }

        var baseTypes = recordDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
        DomainEventType? eventType = null;

        if (baseTypes.Any(bt => bt.Contains("IntegrationEvent", StringComparison.OrdinalIgnoreCase)))
        {
            eventType = DomainEventType.IntegrationEvent;
        }
        else if (baseTypes.Any(bt => DomainEventPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            eventType = DomainEventType.DomainEvent;
        }

        if (eventType == null)
        {
            return null;
        }

        // Extract parameters as properties
        var properties = new List<string>();
        if (recordDecl.ParameterList != null)
        {
            properties = recordDecl.ParameterList.Parameters
                .Select(p => p.Identifier.Text)
                .ToList();
        }

        return new DomainEventInfo
        {
            Name = recordDecl.Identifier.Text,
            EventType = eventType.Value,
            Properties = properties,
            FilePath = filePath,
            LineNumber = recordDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private CqrsPatternInfo? AnalyzeCqrsClass(ClassDeclarationSyntax classDecl, string filePath)
    {
        if (classDecl.BaseList == null)
        {
            return null;
        }

        var baseTypes = classDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
        CqrsPatternType? patternType = null;
        string? responseType = null;
        string? handledType = null;

        // Check for handler first (IRequestHandler<TRequest, TResponse>)
        foreach (var baseType in classDecl.BaseList.Types)
        {
            var typeStr = baseType.Type.ToString();
            if (HandlerPatterns.Any(p => typeStr.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                patternType = CqrsPatternType.Handler;
                if (baseType.Type is GenericNameSyntax generic)
                {
                    if (generic.TypeArgumentList.Arguments.Count > 0)
                    {
                        handledType = generic.TypeArgumentList.Arguments[0].ToString();
                    }

                    if (generic.TypeArgumentList.Arguments.Count > 1)
                    {
                        responseType = generic.TypeArgumentList.Arguments[1].ToString();
                    }
                }

                break;
            }
        }

        // Check for command
        if (patternType == null && baseTypes.Any(bt => CommandPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            patternType = CqrsPatternType.Command;
            // Extract response type if IRequest<T>
            foreach (var baseType in classDecl.BaseList.Types)
            {
                if (baseType.Type is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count > 0)
                {
                    responseType = generic.TypeArgumentList.Arguments[0].ToString();
                    break;
                }
            }
        }

        // Check for query
        if (patternType == null && baseTypes.Any(bt => QueryPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            patternType = CqrsPatternType.Query;
            foreach (var baseType in classDecl.BaseList.Types)
            {
                if (baseType.Type is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count > 0)
                {
                    responseType = generic.TypeArgumentList.Arguments[0].ToString();
                    break;
                }
            }
        }

        if (patternType == null)
        {
            return null;
        }

        return new CqrsPatternInfo
        {
            Name = classDecl.Identifier.Text,
            PatternType = patternType.Value,
            ResponseType = responseType,
            HandledType = handledType,
            FilePath = filePath,
            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private CqrsPatternInfo? AnalyzeCqrsRecord(RecordDeclarationSyntax recordDecl, string filePath)
    {
        if (recordDecl.BaseList == null)
        {
            return null;
        }

        var baseTypes = recordDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
        CqrsPatternType? patternType = null;
        string? responseType = null;

        // Check for command
        if (baseTypes.Any(bt => CommandPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            patternType = CqrsPatternType.Command;
            foreach (var baseType in recordDecl.BaseList.Types)
            {
                if (baseType.Type is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count > 0)
                {
                    responseType = generic.TypeArgumentList.Arguments[0].ToString();
                    break;
                }
            }
        }

        // Check for query
        if (patternType == null && baseTypes.Any(bt => QueryPatterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            patternType = CqrsPatternType.Query;
            foreach (var baseType in recordDecl.BaseList.Types)
            {
                if (baseType.Type is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count > 0)
                {
                    responseType = generic.TypeArgumentList.Arguments[0].ToString();
                    break;
                }
            }
        }

        if (patternType == null)
        {
            return null;
        }

        return new CqrsPatternInfo
        {
            Name = recordDecl.Identifier.Text,
            PatternType = patternType.Value,
            ResponseType = responseType,
            FilePath = filePath,
            LineNumber = recordDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private EventRaisingInfo? AnalyzeEventRaisingInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        string? methodName = null;

        // Handle both member access (obj.Method()) and simple invocation (Method())
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.Text;
        }
        else if (invocation.Expression is IdentifierNameSyntax identifier)
        {
            methodName = identifier.Identifier.Text;
        }

        if (methodName == null)
        {
            return null;
        }

        // Common event raising patterns
        if (methodName != "AddDomainEvent" && methodName != "RaiseDomainEvent" &&
            methodName != "RaiseEvent" && methodName != "Publish" &&
            methodName != "PublishAsync" && methodName != "Send" && methodName != "SendAsync")
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        string? eventType = null;
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is ObjectCreationExpressionSyntax creation)
            {
                eventType = creation.Type.ToString();
            }
            else if (firstArg is IdentifierNameSyntax ident)
            {
                eventType = ident.Identifier.Text;
            }
        }

        return new EventRaisingInfo
        {
            MethodName = methodName,
            EventType = eventType,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.85f,
        };
    }
}

#region Model Classes

/// <summary>
/// Aggregate/Entity information.
/// </summary>
public class AggregateInfo
{
    public required string Name { get; set; }

    public DomainEntityType EntityType { get; set; }

    public string? IdType { get; set; }

    public List<DomainProperty> Properties { get; set; } = new();

    public List<string> FactoryMethods { get; set; } = new();

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// Domain property.
/// </summary>
public class DomainProperty
{
    public required string Name { get; set; }

    public required string Type { get; set; }

    public bool IsReadOnly { get; set; }
}

/// <summary>
/// Domain event information.
/// </summary>
public class DomainEventInfo
{
    public required string Name { get; set; }

    public DomainEventType EventType { get; set; }

    public List<string> Properties { get; set; } = new();

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// CQRS pattern information.
/// </summary>
public class CqrsPatternInfo
{
    public required string Name { get; set; }

    public CqrsPatternType PatternType { get; set; }

    public string? ResponseType { get; set; }

    public string? HandledType { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// Event raising information.
/// </summary>
public class EventRaisingInfo
{
    public required string MethodName { get; set; }

    public string? EventType { get; set; }

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

#endregion

#region Enums

/// <summary>
/// Domain entity type.
/// </summary>
public enum DomainEntityType
{
    AggregateRoot,
    Entity,
    ValueObject,
}

/// <summary>
/// Domain event type.
/// </summary>
public enum DomainEventType
{
    DomainEvent,
    IntegrationEvent,
}

/// <summary>
/// CQRS pattern type.
/// </summary>
public enum CqrsPatternType
{
    Command,
    Query,
    Handler,
}

#endregion
