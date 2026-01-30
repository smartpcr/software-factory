// -----------------------------------------------------------------------
// <copyright file="ObservabilityAnalyzer.cs" company="Microsoft">
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
/// Analyzes observability patterns including logging, metrics, tracing, ETW, and health checks.
/// </summary>
public class ObservabilityAnalyzer
{
    private static readonly HashSet<string> ILoggerMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical", "Log",
    };

    private static readonly HashSet<string> SerilogMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose", "Debug", "Information", "Warning", "Error", "Fatal", "Write",
    };

    private static readonly HashSet<string> NLogMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trace", "Debug", "Info", "Warn", "Error", "Fatal", "Log",
    };

    private static readonly HashSet<string> OtelMeterMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreateCounter", "CreateHistogram", "CreateUpDownCounter",
        "CreateObservableCounter", "CreateObservableGauge", "CreateObservableUpDownCounter",
    };

    private static readonly HashSet<string> PrometheusMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreateCounter", "CreateGauge", "CreateHistogram", "CreateSummary",
    };

    private static readonly HashSet<string> ActivityMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartActivity", "CreateActivity",
    };

    /// <summary>
    /// Analyzes logging statements in the given syntax tree.
    /// </summary>
    public List<LogStatementInfo> AnalyzeLogging(SyntaxTree tree, string filePath)
    {
        var results = new List<LogStatementInfo>();
        var root = tree.GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var logInfo = this.AnalyzeLogInvocation(invocation, filePath);
            if (logInfo != null)
            {
                results.Add(logInfo);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes metric definitions in the given syntax tree.
    /// </summary>
    public List<MetricDefinitionInfo> AnalyzeMetrics(SyntaxTree tree, string filePath)
    {
        var results = new List<MetricDefinitionInfo>();
        var root = tree.GetRoot();

        // Meter definitions
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var metricInfo = this.AnalyzeMeterCreation(creation, filePath);
            if (metricInfo != null)
            {
                results.Add(metricInfo);
            }
        }

        // Metric creation invocations (CreateCounter, CreateHistogram, etc.)
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var metricInfo = this.AnalyzeMetricInvocation(invocation, filePath);
            if (metricInfo != null)
            {
                results.Add(metricInfo);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes tracing spans in the given syntax tree.
    /// </summary>
    public List<TraceSpanInfo> AnalyzeTracing(SyntaxTree tree, string filePath)
    {
        var results = new List<TraceSpanInfo>();
        var root = tree.GetRoot();

        // ActivitySource definitions
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var spanInfo = this.AnalyzeActivitySourceCreation(creation, filePath);
            if (spanInfo != null)
            {
                results.Add(spanInfo);
            }
        }

        // StartActivity invocations
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var spanInfo = this.AnalyzeActivityInvocation(invocation, filePath);
            if (spanInfo != null)
            {
                results.Add(spanInfo);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes ETW EventSource definitions in the given syntax tree.
    /// </summary>
    public List<EtwEventInfo> AnalyzeEtwEvents(SyntaxTree tree, string filePath)
    {
        var results = new List<EtwEventInfo>();
        var root = tree.GetRoot();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            // Check if class inherits from EventSource
            if (!this.InheritsFromEventSource(classDecl))
            {
                continue;
            }

            var eventSourceName = this.GetEventSourceName(classDecl);

            // Find [Event] attributed methods
            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var eventInfo = this.AnalyzeEventMethod(method, eventSourceName, filePath);
                if (eventInfo != null)
                {
                    results.Add(eventInfo);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes EventCounter definitions in the given syntax tree.
    /// </summary>
    public List<EventCounterInfo> AnalyzeEventCounters(SyntaxTree tree, string filePath)
    {
        var results = new List<EventCounterInfo>();
        var root = tree.GetRoot();

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var counterInfo = this.AnalyzeEventCounterCreation(creation, filePath);
            if (counterInfo != null)
            {
                results.Add(counterInfo);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes health check implementations in the given syntax tree.
    /// </summary>
    public List<HealthCheckInfo> AnalyzeHealthChecks(SyntaxTree tree, string filePath)
    {
        var results = new List<HealthCheckInfo>();
        var root = tree.GetRoot();

        // IHealthCheck implementations
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var healthInfo = this.AnalyzeHealthCheckClass(classDecl, filePath);
            if (healthInfo != null)
            {
                results.Add(healthInfo);
            }
        }

        // Health check registrations
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var healthInfo = this.AnalyzeHealthCheckRegistration(invocation, filePath);
            if (healthInfo != null)
            {
                results.Add(healthInfo);
            }
        }

        return results;
    }

    private LogStatementInfo? AnalyzeLogInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();

        LoggerType? loggerType = null;
        LogLevel? level = null;

        // ILogger detection
        if (ILoggerMethods.Contains(methodName) &&
            (receiver.Contains("logger", StringComparison.OrdinalIgnoreCase) ||
             receiver.Contains("_log", StringComparison.OrdinalIgnoreCase) ||
             receiver.Contains("Logger", StringComparison.OrdinalIgnoreCase)))
        {
            loggerType = LoggerType.ILogger;
            level = this.GetILoggerLevel(methodName);
        }
        // Serilog detection
        else if (SerilogMethods.Contains(methodName) &&
                 (receiver.Equals("Log", StringComparison.OrdinalIgnoreCase) ||
                  receiver.Contains("Serilog", StringComparison.OrdinalIgnoreCase) ||
                  receiver.Contains("_log", StringComparison.OrdinalIgnoreCase)))
        {
            loggerType = LoggerType.Serilog;
            level = this.GetSerilogLevel(methodName);
        }
        // NLog detection
        else if (NLogMethods.Contains(methodName) &&
                 (receiver.Contains("logger", StringComparison.OrdinalIgnoreCase) ||
                  receiver.Contains("NLog", StringComparison.OrdinalIgnoreCase)))
        {
            loggerType = LoggerType.NLog;
            level = this.GetNLogLevel(methodName);
        }

        if (loggerType == null || level == null)
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        // Extract message template
        string? messageTemplate = null;
        var parameters = new List<string>();
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                messageTemplate = literal.Token.ValueText;
            }
            else if (firstArg is InterpolatedStringExpressionSyntax interpolated)
            {
                messageTemplate = interpolated.ToString();
            }

            // Extract parameter names from message template
            if (messageTemplate != null)
            {
                parameters = this.ExtractTemplateParameters(messageTemplate);
            }
        }

        // Check for EventId
        string? eventId = null;
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.NameColon?.Name.Identifier.Text == "eventId" ||
                arg.Expression.ToString().Contains("EventId"))
            {
                eventId = arg.Expression.ToString();
                break;
            }
        }

        return new LogStatementInfo
        {
            LoggerType = loggerType.Value,
            Level = level.Value,
            MessageTemplate = messageTemplate,
            Parameters = parameters,
            EventId = eventId,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private MetricDefinitionInfo? AnalyzeMeterCreation(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();
        if (!typeName.Contains("Meter"))
        {
            return null;
        }

        var containingClass = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        string? meterName = null;
        string? version = null;
        if (creation.ArgumentList?.Arguments.Count > 0)
        {
            var firstArg = creation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                meterName = literal.Token.ValueText;
            }

            if (creation.ArgumentList.Arguments.Count > 1)
            {
                var secondArg = creation.ArgumentList.Arguments[1].Expression;
                if (secondArg is LiteralExpressionSyntax versionLiteral)
                {
                    version = versionLiteral.Token.ValueText;
                }
            }
        }

        return new MetricDefinitionInfo
        {
            MetricType = MetricType.Meter,
            MetricName = meterName ?? containingClass?.Identifier.Text ?? "Unknown",
            Description = null,
            Unit = null,
            Tags = new List<string>(),
            Technology = MetricTechnology.OpenTelemetry,
            ClassName = containingClass?.Identifier.Text,
            FilePath = filePath,
            LineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private MetricDefinitionInfo? AnalyzeMetricInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();

        MetricType? metricType = null;
        MetricTechnology? technology = null;

        // OTEL/System.Diagnostics.Metrics patterns
        if (OtelMeterMethods.Contains(methodName) &&
            (receiver.Contains("meter", StringComparison.OrdinalIgnoreCase) ||
             receiver.Contains("Meter", StringComparison.OrdinalIgnoreCase) ||
             receiver.Contains("_meter", StringComparison.OrdinalIgnoreCase)))
        {
            technology = MetricTechnology.OpenTelemetry;
            metricType = this.GetOtelMetricType(methodName);
        }
        // Prometheus patterns
        else if (PrometheusMethods.Contains(methodName) &&
                 receiver.Equals("Metrics", StringComparison.OrdinalIgnoreCase))
        {
            technology = MetricTechnology.Prometheus;
            metricType = this.GetPrometheusMetricType(methodName);
        }

        if (metricType == null || technology == null)
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        // Extract metric name
        string? metricName = null;
        string? description = null;
        string? unit = null;
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                metricName = literal.Token.ValueText;
            }

            // Look for named arguments
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var argName = arg.NameColon?.Name.Identifier.Text;
                if (argName == "description" && arg.Expression is LiteralExpressionSyntax descLiteral)
                {
                    description = descLiteral.Token.ValueText;
                }
                else if (argName == "unit" && arg.Expression is LiteralExpressionSyntax unitLiteral)
                {
                    unit = unitLiteral.Token.ValueText;
                }
            }
        }

        return new MetricDefinitionInfo
        {
            MetricType = metricType.Value,
            MetricName = metricName ?? "Unknown",
            Description = description,
            Unit = unit,
            Tags = new List<string>(),
            Technology = technology.Value,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private TraceSpanInfo? AnalyzeActivitySourceCreation(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();
        if (!typeName.Contains("ActivitySource"))
        {
            return null;
        }

        var containingClass = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        string? sourceName = null;
        if (creation.ArgumentList?.Arguments.Count > 0)
        {
            var firstArg = creation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                sourceName = literal.Token.ValueText;
            }
        }

        return new TraceSpanInfo
        {
            SpanName = sourceName ?? "Unknown",
            ActivitySourceName = sourceName,
            Kind = SpanKind.Internal,
            IsSourceDefinition = true,
            Tags = new List<string>(),
            ClassName = containingClass?.Identifier.Text,
            FilePath = filePath,
            LineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private TraceSpanInfo? AnalyzeActivityInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();

        if (!ActivityMethods.Contains(methodName))
        {
            return null;
        }

        // Must be called on an ActivitySource
        if (!receiver.Contains("ActivitySource", StringComparison.OrdinalIgnoreCase) &&
            !receiver.Contains("_activitySource", StringComparison.OrdinalIgnoreCase) &&
            !receiver.Contains("activitySource", StringComparison.OrdinalIgnoreCase) &&
            !receiver.Contains("Source", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        string? spanName = null;
        SpanKind kind = SpanKind.Internal;
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                spanName = literal.Token.ValueText;
            }

            // Check for ActivityKind
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var argText = arg.Expression.ToString();
                if (argText.Contains("ActivityKind."))
                {
                    kind = this.ParseActivityKind(argText);
                }
            }
        }

        return new TraceSpanInfo
        {
            SpanName = spanName ?? containingMethod?.Identifier.Text ?? "Unknown",
            ActivitySourceName = receiver,
            Kind = kind,
            IsSourceDefinition = false,
            Tags = new List<string>(),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private bool InheritsFromEventSource(ClassDeclarationSyntax classDecl)
    {
        if (classDecl.BaseList == null)
        {
            return false;
        }

        return classDecl.BaseList.Types
            .Any(t => t.Type.ToString().Contains("EventSource"));
    }

    private string GetEventSourceName(ClassDeclarationSyntax classDecl)
    {
        // Check for [EventSource(Name = "...")] attribute
        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                if (attr.Name.ToString().Contains("EventSource") && attr.ArgumentList != null)
                {
                    foreach (var arg in attr.ArgumentList.Arguments)
                    {
                        if (arg.NameEquals?.Name.Identifier.Text == "Name" &&
                            arg.Expression is LiteralExpressionSyntax literal)
                        {
                            return literal.Token.ValueText;
                        }
                    }
                }
            }
        }

        return classDecl.Identifier.Text;
    }

    private EtwEventInfo? AnalyzeEventMethod(MethodDeclarationSyntax method, string eventSourceName, string filePath)
    {
        // Look for [Event] attribute
        AttributeSyntax? eventAttr = null;
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                if (attr.Name.ToString().Contains("Event"))
                {
                    eventAttr = attr;
                    break;
                }
            }
        }

        if (eventAttr == null)
        {
            return null;
        }

        // Extract event ID
        int? eventId = null;
        string? level = "Informational";
        string? message = null;
        string? keywords = null;

        if (eventAttr.ArgumentList != null && eventAttr.ArgumentList.Arguments.Count > 0)
        {
            // First positional arg is event ID
            var firstArg = eventAttr.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal && int.TryParse(literal.Token.ValueText, out var id))
            {
                eventId = id;
            }

            // Named arguments
            foreach (var arg in eventAttr.ArgumentList.Arguments)
            {
                var argName = arg.NameEquals?.Name.Identifier.Text;
                if (argName == "Level")
                {
                    level = arg.Expression.ToString().Replace("EventLevel.", string.Empty);
                }
                else if (argName == "Message" && arg.Expression is LiteralExpressionSyntax msgLiteral)
                {
                    message = msgLiteral.Token.ValueText;
                }
                else if (argName == "Keywords")
                {
                    keywords = arg.Expression.ToString();
                }
            }
        }

        // Extract parameters
        var parameters = method.ParameterList.Parameters
            .Select(p => new EtwEventParameter
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "object",
            })
            .ToList();

        var containingClass = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        return new EtwEventInfo
        {
            EventSourceName = eventSourceName,
            EventId = eventId ?? 0,
            EventName = method.Identifier.Text,
            Level = level,
            Keywords = keywords,
            Message = message,
            Parameters = parameters,
            ClassName = containingClass?.Identifier.Text,
            FilePath = filePath,
            LineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private EventCounterInfo? AnalyzeEventCounterCreation(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();
        EventCounterType? counterType = null;

        if (typeName == "EventCounter")
        {
            counterType = EventCounterType.EventCounter;
        }
        else if (typeName == "IncrementingEventCounter")
        {
            counterType = EventCounterType.IncrementingEventCounter;
        }
        else if (typeName == "PollingCounter")
        {
            counterType = EventCounterType.PollingCounter;
        }
        else if (typeName == "IncrementingPollingCounter")
        {
            counterType = EventCounterType.IncrementingPollingCounter;
        }

        if (counterType == null)
        {
            return null;
        }

        var containingClass = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        string? counterName = null;
        string? displayName = null;
        string? displayUnits = null;

        if (creation.ArgumentList?.Arguments.Count > 0)
        {
            var firstArg = creation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                counterName = literal.Token.ValueText;
            }
        }

        // Check initializer for DisplayName/DisplayUnits
        if (creation.Initializer != null)
        {
            foreach (var expr in creation.Initializer.Expressions)
            {
                if (expr is AssignmentExpressionSyntax assignment)
                {
                    var propName = assignment.Left.ToString();
                    if (propName == "DisplayName" && assignment.Right is LiteralExpressionSyntax dnLiteral)
                    {
                        displayName = dnLiteral.Token.ValueText;
                    }
                    else if (propName == "DisplayUnits" && assignment.Right is LiteralExpressionSyntax duLiteral)
                    {
                        displayUnits = duLiteral.Token.ValueText;
                    }
                }
            }
        }

        return new EventCounterInfo
        {
            CounterType = counterType.Value,
            CounterName = counterName ?? "Unknown",
            DisplayName = displayName,
            DisplayUnits = displayUnits,
            EventSourceName = containingClass?.Identifier.Text,
            ClassName = containingClass?.Identifier.Text,
            FilePath = filePath,
            LineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private HealthCheckInfo? AnalyzeHealthCheckClass(ClassDeclarationSyntax classDecl, string filePath)
    {
        if (classDecl.BaseList == null)
        {
            return null;
        }

        var implementsIHealthCheck = classDecl.BaseList.Types
            .Any(t => t.Type.ToString().Contains("IHealthCheck"));

        if (!implementsIHealthCheck)
        {
            return null;
        }

        return new HealthCheckInfo
        {
            Name = classDecl.Identifier.Text,
            IsImplementation = true,
            Tags = new List<string>(),
            Timeout = null,
            ClassName = classDecl.Identifier.Text,
            FilePath = filePath,
            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private HealthCheckInfo? AnalyzeHealthCheckRegistration(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;

        if (methodName != "AddCheck" && methodName != "AddDbContextCheck" &&
            methodName != "AddUrlGroup" && methodName != "AddTypeActivatedCheck")
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        string? checkName = null;
        var tags = new List<string>();
        string? timeout = null;

        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
            {
                checkName = literal.Token.ValueText;
            }

            // Look for tags and timeout
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var argName = arg.NameColon?.Name.Identifier.Text;
                if (argName == "tags")
                {
                    // Extract tags from array
                    if (arg.Expression is ImplicitArrayCreationExpressionSyntax array)
                    {
                        foreach (var elem in array.Initializer.Expressions)
                        {
                            if (elem is LiteralExpressionSyntax tagLiteral)
                            {
                                tags.Add(tagLiteral.Token.ValueText);
                            }
                        }
                    }
                    else if (arg.Expression is ArrayCreationExpressionSyntax explicitArray &&
                             explicitArray.Initializer != null)
                    {
                        foreach (var elem in explicitArray.Initializer.Expressions)
                        {
                            if (elem is LiteralExpressionSyntax tagLiteral)
                            {
                                tags.Add(tagLiteral.Token.ValueText);
                            }
                        }
                    }
                }
                else if (argName == "timeout")
                {
                    timeout = arg.Expression.ToString();
                }
            }
        }

        return new HealthCheckInfo
        {
            Name = checkName ?? methodName,
            IsImplementation = false,
            Tags = tags,
            Timeout = timeout,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private LogLevel? GetILoggerLevel(string methodName) => methodName.ToLowerInvariant() switch
    {
        "logtrace" => LogLevel.Trace,
        "logdebug" => LogLevel.Debug,
        "loginformation" => LogLevel.Information,
        "logwarning" => LogLevel.Warning,
        "logerror" => LogLevel.Error,
        "logcritical" => LogLevel.Critical,
        _ => null,
    };

    private LogLevel? GetSerilogLevel(string methodName) => methodName.ToLowerInvariant() switch
    {
        "verbose" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "information" => LogLevel.Information,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "fatal" => LogLevel.Critical,
        _ => null,
    };

    private LogLevel? GetNLogLevel(string methodName) => methodName.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "fatal" => LogLevel.Critical,
        _ => null,
    };

    private MetricType GetOtelMetricType(string methodName) => methodName switch
    {
        "CreateCounter" => MetricType.Counter,
        "CreateHistogram" => MetricType.Histogram,
        "CreateUpDownCounter" => MetricType.UpDownCounter,
        "CreateObservableCounter" => MetricType.ObservableCounter,
        "CreateObservableGauge" => MetricType.ObservableGauge,
        "CreateObservableUpDownCounter" => MetricType.ObservableUpDownCounter,
        _ => MetricType.Counter,
    };

    private MetricType GetPrometheusMetricType(string methodName) => methodName switch
    {
        "CreateCounter" => MetricType.Counter,
        "CreateGauge" => MetricType.Gauge,
        "CreateHistogram" => MetricType.Histogram,
        "CreateSummary" => MetricType.Summary,
        _ => MetricType.Counter,
    };

    private SpanKind ParseActivityKind(string kindStr)
    {
        if (kindStr.Contains("Server"))
        {
            return SpanKind.Server;
        }

        if (kindStr.Contains("Client"))
        {
            return SpanKind.Client;
        }

        if (kindStr.Contains("Producer"))
        {
            return SpanKind.Producer;
        }

        if (kindStr.Contains("Consumer"))
        {
            return SpanKind.Consumer;
        }

        return SpanKind.Internal;
    }

    private List<string> ExtractTemplateParameters(string template)
    {
        var parameters = new List<string>();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i);
                if (end > i)
                {
                    var param = template.Substring(i + 1, end - i - 1);
                    // Handle format specifiers like {value:N2}
                    var colonIndex = param.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        param = param.Substring(0, colonIndex);
                    }

                    parameters.Add(param);
                    i = end + 1;
                    continue;
                }
            }

            i++;
        }

        return parameters;
    }
}

#region Model Classes

/// <summary>
/// Log statement information.
/// </summary>
public class LogStatementInfo
{
    public LoggerType LoggerType { get; set; }

    public LogLevel Level { get; set; }

    public string? MessageTemplate { get; set; }

    public List<string> Parameters { get; set; } = new();

    public string? EventId { get; set; }

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// Metric definition information.
/// </summary>
public class MetricDefinitionInfo
{
    public MetricType MetricType { get; set; }

    public required string MetricName { get; set; }

    public string? Description { get; set; }

    public string? Unit { get; set; }

    public List<string> Tags { get; set; } = new();

    public MetricTechnology Technology { get; set; }

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// Trace span information.
/// </summary>
public class TraceSpanInfo
{
    public required string SpanName { get; set; }

    public string? ActivitySourceName { get; set; }

    public SpanKind Kind { get; set; }

    public bool IsSourceDefinition { get; set; }

    public List<string> Tags { get; set; } = new();

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// ETW event information.
/// </summary>
public class EtwEventInfo
{
    public required string EventSourceName { get; set; }

    public int EventId { get; set; }

    public required string EventName { get; set; }

    public string? Level { get; set; }

    public string? Keywords { get; set; }

    public string? Message { get; set; }

    public List<EtwEventParameter> Parameters { get; set; } = new();

    public string? ClassName { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// ETW event parameter.
/// </summary>
public class EtwEventParameter
{
    public required string Name { get; set; }

    public required string Type { get; set; }
}

/// <summary>
/// Event counter information.
/// </summary>
public class EventCounterInfo
{
    public EventCounterType CounterType { get; set; }

    public required string CounterName { get; set; }

    public string? DisplayName { get; set; }

    public string? DisplayUnits { get; set; }

    public string? EventSourceName { get; set; }

    public string? ClassName { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// Health check information.
/// </summary>
public class HealthCheckInfo
{
    public required string Name { get; set; }

    public bool IsImplementation { get; set; }

    public List<string> Tags { get; set; } = new();

    public string? Timeout { get; set; }

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

#endregion

#region Enums

/// <summary>
/// Logger type.
/// </summary>
public enum LoggerType
{
    ILogger,
    Serilog,
    NLog,
}

/// <summary>
/// Log level.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// Metric type.
/// </summary>
public enum MetricType
{
    Meter,
    Counter,
    Histogram,
    Gauge,
    UpDownCounter,
    ObservableCounter,
    ObservableGauge,
    ObservableUpDownCounter,
    Summary,
}

/// <summary>
/// Metric technology.
/// </summary>
public enum MetricTechnology
{
    OpenTelemetry,
    Prometheus,
    AppInsights,
    EventCounter,
}

/// <summary>
/// Span kind.
/// </summary>
public enum SpanKind
{
    Internal,
    Server,
    Client,
    Producer,
    Consumer,
}

/// <summary>
/// Event counter type.
/// </summary>
public enum EventCounterType
{
    EventCounter,
    IncrementingEventCounter,
    PollingCounter,
    IncrementingPollingCounter,
}

#endregion
