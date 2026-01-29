// -----------------------------------------------------------------------
// <copyright file="LifecycleDetector.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Detectors;

using CodeIntelligence.Analyzers;
using CodeIntelligence.Models;
using CodeIntelligence.Parsers;
using CodeIntelligence.Scoring;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Orchestrates entry point and lifecycle hook detection across all file types.
/// </summary>
public sealed class LifecycleDetector
{
    private readonly EntryPointAnalyzer entryPointAnalyzer;
    private readonly AzureFunctionsAnalyzer azureFunctionsAnalyzer;
    private readonly MiddlewareChainBuilder middlewareChainBuilder;
    private readonly AspNetCoreStartupAnalyzer aspNetCoreAnalyzer;
    private readonly ScheduledJobAnalyzer scheduledJobAnalyzer;
    private readonly ApplicationLifetimeAnalyzer lifetimeAnalyzer;
    private readonly ManifestParser manifestParser;
    private readonly ConfidenceCalculator confidenceCalculator;

    /// <summary>
    /// Initializes a new instance of the <see cref="LifecycleDetector"/> class.
    /// </summary>
    public LifecycleDetector()
    {
        this.entryPointAnalyzer = new EntryPointAnalyzer();
        this.azureFunctionsAnalyzer = new AzureFunctionsAnalyzer();
        this.middlewareChainBuilder = new MiddlewareChainBuilder();
        this.aspNetCoreAnalyzer = new AspNetCoreStartupAnalyzer();
        this.scheduledJobAnalyzer = new ScheduledJobAnalyzer();
        this.lifetimeAnalyzer = new ApplicationLifetimeAnalyzer();
        this.manifestParser = new ManifestParser();
        this.confidenceCalculator = new ConfidenceCalculator();
    }

    /// <summary>
    /// Detects all lifecycle relationships in a directory.
    /// </summary>
    /// <param name="rootPath">Root directory to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All detected lifecycle relationships.</returns>
    public async Task<LifecycleAnalysisResult> AnalyzeAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var result = new LifecycleAnalysisResult();

        var csFiles = Directory.GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        foreach (var csFile in csFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await this.AnalyzeCSharpFileAsync(csFile, result, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new AnalysisError
                {
                    FilePath = csFile,
                    Message = ex.Message,
                    ErrorType = "ParseError"
                });
            }
        }

        // Build middleware chain if any registrations found
        if (result.MiddlewareRegistrations.Count > 0)
        {
            result.MiddlewareChain = this.middlewareChainBuilder.BuildChain(result.MiddlewareRegistrations);
        }

        return result;
    }

    /// <summary>
    /// Analyzes a single C# file.
    /// </summary>
    public async Task<LifecycleAnalysisResult> AnalyzeFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var result = new LifecycleAnalysisResult();
        await this.AnalyzeCSharpFileAsync(filePath, result, cancellationToken);

        if (result.MiddlewareRegistrations.Count > 0)
        {
            result.MiddlewareChain = this.middlewareChainBuilder.BuildChain(result.MiddlewareRegistrations);
        }

        return result;
    }

    private async Task AnalyzeCSharpFileAsync(
        string filePath,
        LifecycleAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var syntaxTree = CSharpSyntaxTree.ParseText(content, path: filePath, cancellationToken: cancellationToken);

        // Analyze entry points
        var entryPoints = this.entryPointAnalyzer.Analyze(syntaxTree, filePath);
        result.EntryPoints.AddRange(entryPoints);
        foreach (var ep in entryPoints)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(ep));
        }

        // Analyze Azure Functions
        var functions = this.azureFunctionsAnalyzer.Analyze(syntaxTree, filePath);
        result.AzureFunctions.AddRange(functions);
        foreach (var func in functions)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(func));
        }

        // Analyze middleware registrations
        var middlewareRegs = this.middlewareChainBuilder.Analyze(syntaxTree, filePath);
        result.MiddlewareRegistrations.AddRange(middlewareRegs);
        foreach (var mw in middlewareRegs)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(mw));
        }

        // Analyze ASP.NET Core startup patterns
        var startupPatterns = this.aspNetCoreAnalyzer.Analyze(syntaxTree, filePath);
        result.AspNetCoreStartups.AddRange(startupPatterns);
        foreach (var startup in startupPatterns)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(startup));
        }

        // Analyze scheduled jobs
        var scheduledJobs = this.scheduledJobAnalyzer.Analyze(syntaxTree, filePath);
        result.ScheduledJobs.AddRange(scheduledJobs);
        foreach (var job in scheduledJobs)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(job));
        }

        // Analyze application lifetime events
        var lifetimeEvents = this.lifetimeAnalyzer.Analyze(syntaxTree, filePath);
        result.ApplicationLifetimeEvents.AddRange(lifetimeEvents);
        foreach (var lifetime in lifetimeEvents)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(lifetime));
        }

        result.FilesAnalyzed++;
    }

    private DetectedRelationship ToDetectedRelationship(EntryPointInfo ep)
    {
        var relType = ep.EntryPointType switch
        {
            EntryPointType.Middleware => "Middleware",
            EntryPointType.ScheduledJob => "ScheduledTask",
            EntryPointType.StartupHook => "StartupHook",
            EntryPointType.ShutdownHook => "ShutdownHook",
            _ => "EntryPoint"
        };

        var properties = new Dictionary<string, object>
        {
            ["entryPointType"] = ep.EntryPointType.ToString()
        };

        if (ep.ClassName != null)
        {
            properties["className"] = ep.ClassName;
        }

        if (ep.MethodName != null)
        {
            properties["methodName"] = ep.MethodName;
        }

        if (ep.BaseType != null)
        {
            properties["baseType"] = ep.BaseType;
        }

        if (ep.IsAsync)
        {
            properties["isAsync"] = true;
        }

        if (ep.LifecycleMethods.Count > 0)
        {
            properties["lifecycleMethods"] = ep.LifecycleMethods;
        }

        if (ep.MiddlewareType != null)
        {
            properties["middlewareType"] = ep.MiddlewareType;
        }

        if (ep.ScheduleType != null)
        {
            properties["scheduleType"] = ep.ScheduleType;
        }

        return new DetectedRelationship
        {
            Id = $"lifecycle-{Guid.NewGuid():N}",
            Category = "lifecycle",
            RelationshipType = relType,
            Source = new RelationshipNode
            {
                NodeType = ep.EntryPointType.ToString(),
                FullyQualifiedName = ep.ClassName ?? "Program",
                Name = ep.ClassName?.Split('.').Last() ?? "Program",
                FilePath = ep.FilePath,
                LineNumber = ep.LineNumber
            },
            Target = null,
            Confidence = 0.90f,
            EvidenceChain = new List<Evidence>
            {
                new Evidence
                {
                    Source = "ast",
                    Description = $"Detected {ep.EntryPointType} entry point",
                    Weight = 0.90f,
                    FilePath = ep.FilePath,
                    LineNumber = ep.LineNumber
                }
            },
            DetectionStrategy = "ast",
            Properties = properties
        };
    }

    private DetectedRelationship ToDetectedRelationship(AzureFunctionInfo func)
    {
        var properties = new Dictionary<string, object>
        {
            ["entryPointType"] = "AzureFunction",
            ["functionName"] = func.FunctionName,
            ["methodName"] = func.MethodName,
            ["className"] = func.ClassName,
            ["returnType"] = func.ReturnType,
            ["isAsync"] = func.IsAsync,
            ["isIsolatedWorker"] = func.IsIsolatedWorker
        };

        if (func.Trigger != null)
        {
            properties["triggerType"] = func.Trigger.TriggerType;
            properties["triggerParameter"] = func.Trigger.ParameterName;
        }

        if (func.Bindings.Count > 0)
        {
            properties["bindings"] = func.Bindings.Select(b => new
            {
                type = b.BindingType,
                direction = b.Direction.ToString()
            }).ToList();
        }

        return new DetectedRelationship
        {
            Id = $"func-{Guid.NewGuid():N}",
            Category = "lifecycle",
            RelationshipType = "EntryPoint",
            Source = new RelationshipNode
            {
                NodeType = "AzureFunction",
                FullyQualifiedName = $"{func.ClassName}.{func.MethodName}",
                Name = func.FunctionName,
                FilePath = func.FilePath,
                LineNumber = func.LineNumber
            },
            Target = null,
            Confidence = 0.95f,
            EvidenceChain = new List<Evidence>
            {
                new Evidence
                {
                    Source = "ast",
                    Description = $"Azure Function '{func.FunctionName}' with {func.Trigger?.TriggerType ?? "unknown"} trigger",
                    Weight = 0.95f,
                    FilePath = func.FilePath,
                    LineNumber = func.LineNumber
                }
            },
            DetectionStrategy = "ast",
            Properties = properties
        };
    }

    private DetectedRelationship ToDetectedRelationship(MiddlewareRegistration mw)
    {
        var properties = new Dictionary<string, object>
        {
            ["methodName"] = mw.MethodName,
            ["order"] = mw.Order,
            ["middlewareType"] = mw.MiddlewareType.ToString(),
            ["isTerminal"] = mw.IsTerminal,
            ["isBranching"] = mw.IsBranching
        };

        if (mw.MiddlewareName != null)
        {
            properties["middlewareName"] = mw.MiddlewareName;
        }

        if (mw.Arguments.Count > 0)
        {
            properties["arguments"] = mw.Arguments;
        }

        return new DetectedRelationship
        {
            Id = $"mw-{Guid.NewGuid():N}",
            Category = "lifecycle",
            RelationshipType = "Middleware",
            Source = new RelationshipNode
            {
                NodeType = "Middleware",
                FullyQualifiedName = mw.MiddlewareName ?? mw.MethodName,
                Name = mw.MiddlewareName ?? mw.MethodName,
                FilePath = mw.FilePath,
                LineNumber = mw.LineNumber
            },
            Target = null,
            Confidence = 0.85f,
            EvidenceChain = new List<Evidence>
            {
                new Evidence
                {
                    Source = "ast",
                    Description = $"Middleware registration: {mw.MethodName}",
                    Weight = 0.85f,
                    FilePath = mw.FilePath,
                    LineNumber = mw.LineNumber
                }
            },
            DetectionStrategy = "ast",
            Properties = properties
        };
    }

    private DetectedRelationship ToDetectedRelationship(AspNetCoreStartupInfo startup)
    {
        var properties = new Dictionary<string, object>
        {
            ["startupType"] = startup.StartupType.ToString(),
            ["isMinimalApi"] = startup.IsMinimalApi
        };

        if (startup.BuilderMethod != null)
        {
            properties["builderMethod"] = startup.BuilderMethod;
        }

        if (startup.ClassName != null)
        {
            properties["className"] = startup.ClassName;
        }

        if (startup.MiddlewareMethods.Count > 0)
        {
            properties["middlewareMethods"] = startup.MiddlewareMethods;
        }

        if (startup.ConfigurationMethods.Count > 0)
        {
            properties["configurationMethods"] = startup.ConfigurationMethods;
        }

        return new DetectedRelationship
        {
            Id = $"startup-{Guid.NewGuid():N}",
            Category = "lifecycle",
            RelationshipType = "EntryPoint",
            Source = new RelationshipNode
            {
                NodeType = startup.StartupType.ToString(),
                FullyQualifiedName = startup.ClassName ?? "Program",
                Name = startup.ClassName?.Split('.').Last() ?? "Program",
                FilePath = startup.FilePath,
                LineNumber = startup.LineNumber
            },
            Target = null,
            Confidence = 0.90f,
            EvidenceChain = new List<Evidence>
            {
                this.confidenceCalculator.CreateAstEvidence(
                    $"ASP.NET Core {startup.StartupType} startup pattern",
                    startup.FilePath,
                    startup.LineNumber)
            },
            DetectionStrategy = "ast",
            Properties = properties
        };
    }

    private DetectedRelationship ToDetectedRelationship(ScheduledJobInfo job)
    {
        var properties = new Dictionary<string, object>
        {
            ["jobType"] = job.JobType.ToString()
        };

        if (job.ClassName != null)
        {
            properties["className"] = job.ClassName;
        }

        if (job.JobId != null)
        {
            properties["jobId"] = job.JobId;
        }

        if (job.CronExpression != null)
        {
            properties["cronExpression"] = job.CronExpression;
        }

        if (job.Interval != null)
        {
            properties["interval"] = job.Interval;
        }

        if (job.CallbackMethod != null)
        {
            properties["callbackMethod"] = job.CallbackMethod;
        }

        if (job.JobMethod != null)
        {
            properties["jobMethod"] = job.JobMethod;
        }

        return new DetectedRelationship
        {
            Id = $"job-{Guid.NewGuid():N}",
            Category = "lifecycle",
            RelationshipType = "ScheduledTask",
            Source = new RelationshipNode
            {
                NodeType = job.JobType.ToString(),
                FullyQualifiedName = job.ClassName ?? "Unknown",
                Name = job.ClassName?.Split('.').Last() ?? "Unknown",
                FilePath = job.FilePath,
                LineNumber = job.LineNumber
            },
            Target = null,
            Confidence = job.JobType == ScheduledJobType.QuartzJob ? 0.90f : 0.85f,
            EvidenceChain = new List<Evidence>
            {
                this.confidenceCalculator.CreateAstEvidence(
                    $"Scheduled job: {job.JobType}",
                    job.FilePath,
                    job.LineNumber)
            },
            DetectionStrategy = "ast",
            Properties = properties
        };
    }

    private DetectedRelationship ToDetectedRelationship(ApplicationLifetimeInfo lifetime)
    {
        var properties = new Dictionary<string, object>
        {
            ["eventType"] = lifetime.EventType.ToString()
        };

        if (lifetime.ClassName != null)
        {
            properties["className"] = lifetime.ClassName;
        }

        if (lifetime.EventName != null)
        {
            properties["eventName"] = lifetime.EventName;
        }

        if (lifetime.MethodName != null)
        {
            properties["methodName"] = lifetime.MethodName;
        }

        if (lifetime.CallbackMethod != null)
        {
            properties["callbackMethod"] = lifetime.CallbackMethod;
        }

        properties["isRegistration"] = lifetime.IsRegistration;

        var relType = lifetime.EventType switch
        {
            ApplicationLifetimeEventType.Started => "StartupHook",
            ApplicationLifetimeEventType.Stopping or ApplicationLifetimeEventType.Stopped => "ShutdownHook",
            _ => "LifecycleHook"
        };

        return new DetectedRelationship
        {
            Id = $"lifetime-{Guid.NewGuid():N}",
            Category = "lifecycle",
            RelationshipType = relType,
            Source = new RelationshipNode
            {
                NodeType = lifetime.EventType.ToString(),
                FullyQualifiedName = lifetime.ClassName ?? "Unknown",
                Name = lifetime.ClassName?.Split('.').Last() ?? "Unknown",
                FilePath = lifetime.FilePath,
                LineNumber = lifetime.LineNumber
            },
            Target = null,
            Confidence = 0.85f,
            EvidenceChain = new List<Evidence>
            {
                this.confidenceCalculator.CreateAstEvidence(
                    $"Application lifetime: {lifetime.EventType}",
                    lifetime.FilePath,
                    lifetime.LineNumber)
            },
            DetectionStrategy = "ast",
            Properties = properties
        };
    }
}

/// <summary>
/// Result of lifecycle analysis.
/// </summary>
public class LifecycleAnalysisResult
{
    /// <summary>Gets the detected relationships.</summary>
    public List<DetectedRelationship> DetectedRelationships { get; } = new();

    /// <summary>Gets the entry points.</summary>
    public List<EntryPointInfo> EntryPoints { get; } = new();

    /// <summary>Gets the Azure Functions.</summary>
    public List<AzureFunctionInfo> AzureFunctions { get; } = new();

    /// <summary>Gets the middleware registrations.</summary>
    public List<MiddlewareRegistration> MiddlewareRegistrations { get; } = new();

    /// <summary>Gets the ASP.NET Core startup patterns.</summary>
    public List<AspNetCoreStartupInfo> AspNetCoreStartups { get; } = new();

    /// <summary>Gets the scheduled jobs.</summary>
    public List<ScheduledJobInfo> ScheduledJobs { get; } = new();

    /// <summary>Gets the application lifetime events.</summary>
    public List<ApplicationLifetimeInfo> ApplicationLifetimeEvents { get; } = new();

    /// <summary>Gets or sets the middleware chain.</summary>
    public MiddlewareChain? MiddlewareChain { get; set; }

    /// <summary>Gets the analysis errors.</summary>
    public List<AnalysisError> Errors { get; } = new();

    /// <summary>Gets or sets the number of files analyzed.</summary>
    public int FilesAnalyzed { get; set; }
}
