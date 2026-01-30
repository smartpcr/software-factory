// -----------------------------------------------------------------------
// <copyright file="LifecycleDetector.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Detectors;

using System.Collections.Concurrent;
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
    private static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

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
    /// Discovers entry points from a repository root by first identifying executable projects,
    /// then analyzing each for entry points and lifecycle hooks.
    /// </summary>
    /// <param name="repositoryRoot">The root directory of the repository.</param>
    /// <param name="options">Discovery options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Repository discovery result with all found entry points organized by project.</returns>
    public async Task<RepositoryDiscoveryResult> DiscoverFromRepositoryAsync(
        string repositoryRoot,
        RepositoryDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RepositoryDiscoveryOptions();
        var result = new RepositoryDiscoveryResult { RepositoryRoot = repositoryRoot };

        // Step 1: Find all project files
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };
        var projectFiles = Directory.GetFiles(repositoryRoot, "*.csproj", enumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        result.TotalProjectsScanned = projectFiles.Count;

        // Step 2: Parse each project to identify executable projects
        var executableProjects = new ConcurrentBag<DiscoveredProject>();

        await Parallel.ForEachAsync(projectFiles, cancellationToken, async (projectPath, ct) =>
        {
            try
            {
                var sdkInfo = await this.manifestParser.ParseProjectSdkAsync(projectPath);
                if (sdkInfo == null)
                {
                    return;
                }

                // Skip test projects unless explicitly included
                var projectName = Path.GetFileNameWithoutExtension(projectPath);
                var isTestProject = projectName.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                                   projectName.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
                                   projectName.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
                                   projectName.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase);

                if (isTestProject && !options.IncludeTestProjects)
                {
                    return;
                }

                // Check if this is an executable project or has hosting indicators
                var isEntryPointCandidate = sdkInfo.IsExecutable ||
                                           sdkInfo.IsWebProject ||
                                           sdkInfo.IsWorkerProject;

                // For library projects, check if they contain hosting patterns
                // (IHostedService, BackgroundService, ServiceBase, StatelessService, etc.)
                var isLibraryWithHostingPatterns = false;
                if (!isEntryPointCandidate && options.IncludeLibrariesWithHostingPatterns)
                {
                    isLibraryWithHostingPatterns = HasHostingPatterns(projectPath, options);
                }

                if (isEntryPointCandidate || isLibraryWithHostingPatterns || options.IncludeAllProjects)
                {
                    executableProjects.Add(new DiscoveredProject
                    {
                        ProjectPath = projectPath,
                        ProjectName = projectName,
                        SdkInfo = sdkInfo,
                        IsTestProject = isTestProject,
                        IsLibraryWithHostingPatterns = isLibraryWithHostingPatterns
                    });
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new AnalysisError
                {
                    FilePath = projectPath,
                    Message = ex.Message,
                    ErrorType = "ProjectParseError"
                });
            }
        });

        // Step 3: Analyze each executable project for entry points
        foreach (var project in executableProjects.OrderBy(p => p.ProjectPath))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var projectDir = Path.GetDirectoryName(project.ProjectPath)!;
            var projectResult = new ProjectEntryPointResult
            {
                Project = project,
                RelativePath = Path.GetRelativePath(repositoryRoot, project.ProjectPath)
            };

            try
            {
                // Analyze the project directory for entry points
                var analysisResult = await this.AnalyzeAsync(projectDir, cancellationToken);

                projectResult.EntryPoints.AddRange(analysisResult.EntryPoints);
                projectResult.AzureFunctions.AddRange(analysisResult.AzureFunctions);
                projectResult.AspNetCoreStartups.AddRange(analysisResult.AspNetCoreStartups);
                projectResult.ScheduledJobs.AddRange(analysisResult.ScheduledJobs);
                projectResult.ApplicationLifetimeEvents.AddRange(analysisResult.ApplicationLifetimeEvents);
                projectResult.FilesAnalyzed = analysisResult.FilesAnalyzed;

                // Also check for manifest-based entry points
                var manifests = await this.manifestParser.GetEntryPointManifestsAsync(project.ProjectPath);
                foreach (var manifest in manifests)
                {
                    projectResult.Manifests.Add(manifest);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new AnalysisError
                {
                    FilePath = project.ProjectPath,
                    Message = ex.Message,
                    ErrorType = "AnalysisError"
                });
            }

            result.Projects.Add(projectResult);
        }

        return result;
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

        var csFiles = Directory.GetFiles(rootPath, "*.cs", SafeEnumerationOptions)
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

    /// <summary>
    /// Checks if a project directory contains classes that implement lifecycle contracts.
    /// Uses both quick pattern matching and AST analysis for high confidence detection.
    /// </summary>
    /// <param name="projectPath">Path to the .csproj file.</param>
    /// <param name="options">Discovery options with dependency repository paths.</param>
    /// <returns>True if lifecycle contracts are found.</returns>
    private static bool HasHostingPatterns(string projectPath, RepositoryDiscoveryOptions options)
    {
        var projectDir = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
        {
            return false;
        }

        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };

            var csFiles = Directory.GetFiles(projectDir, "*.cs", enumerationOptions)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

            // Use the EntryPointAnalyzer for AST-based detection
            var analyzer = new Analyzers.EntryPointAnalyzer();

            foreach (var csFile in csFiles)
            {
                try
                {
                    var content = File.ReadAllText(csFile);
                    var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content, path: csFile);

                    // Analyze for entry points
                    var entryPoints = analyzer.Analyze(syntaxTree, csFile);

                    // If any entry points found with hosting patterns, return true
                    if (entryPoints.Any(ep =>
                        ep.EntryPointType == Analyzers.EntryPointType.HostedService ||
                        ep.EntryPointType == Analyzers.EntryPointType.BackgroundService ||
                        ep.EntryPointType == Analyzers.EntryPointType.WindowsService ||
                        ep.EntryPointType == Analyzers.EntryPointType.ServiceFabric))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Skip files that can't be analyzed
                }
            }
        }
        catch
        {
            // Skip directories that can't be enumerated
        }

        return false;
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

/// <summary>
/// Options for repository discovery.
/// </summary>
public class RepositoryDiscoveryOptions
{
    /// <summary>Gets or sets whether to include test projects in discovery.</summary>
    public bool IncludeTestProjects { get; set; } = false;

    /// <summary>Gets or sets whether to include all projects (not just executables).</summary>
    public bool IncludeAllProjects { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to include library projects that contain hosting patterns
    /// (e.g., IHostedService, BackgroundService, ServiceBase, StatelessService).
    /// These libraries can be plugged into Windows Service or Service Fabric hosts.
    /// Default is true.
    /// </summary>
    public bool IncludeLibrariesWithHostingPatterns { get; set; } = true;

    /// <summary>Gets or sets the maximum depth to scan for projects.</summary>
    public int MaxDepth { get; set; } = int.MaxValue;

    /// <summary>
    /// Gets or sets paths to dependency repositories that contain base types/interfaces.
    /// When a class implements an interface from a dependency repository, the detector will
    /// analyze the dependency repo to understand the interface contract and lifecycle methods.
    /// The resolver follows interface inheritance chains to determine if a class is an entry point.
    /// </summary>
    public IReadOnlyList<string> DependencyRepositoryPaths { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Result of repository-level entry point discovery.
/// </summary>
public class RepositoryDiscoveryResult
{
    /// <summary>Gets or sets the repository root path.</summary>
    public required string RepositoryRoot { get; set; }

    /// <summary>Gets or sets the total number of projects scanned.</summary>
    public int TotalProjectsScanned { get; set; }

    /// <summary>Gets the discovered projects with their entry points.</summary>
    public List<ProjectEntryPointResult> Projects { get; } = new();

    /// <summary>Gets all entry points across all projects.</summary>
    public IEnumerable<EntryPointInfo> AllEntryPoints =>
        this.Projects.SelectMany(p => p.EntryPoints);

    /// <summary>Gets all Azure Functions across all projects.</summary>
    public IEnumerable<AzureFunctionInfo> AllAzureFunctions =>
        this.Projects.SelectMany(p => p.AzureFunctions);

    /// <summary>Gets the analysis errors.</summary>
    public List<AnalysisError> Errors { get; } = new();

    /// <summary>Gets the total number of files analyzed.</summary>
    public int TotalFilesAnalyzed => this.Projects.Sum(p => p.FilesAnalyzed);
}

/// <summary>
/// Entry point discovery result for a single project.
/// </summary>
public class ProjectEntryPointResult
{
    /// <summary>Gets or sets the discovered project info.</summary>
    public required DiscoveredProject Project { get; set; }

    /// <summary>Gets or sets the relative path from repository root.</summary>
    public required string RelativePath { get; set; }

    /// <summary>Gets the entry points found in this project.</summary>
    public List<EntryPointInfo> EntryPoints { get; } = new();

    /// <summary>Gets the Azure Functions found in this project.</summary>
    public List<AzureFunctionInfo> AzureFunctions { get; } = new();

    /// <summary>Gets the ASP.NET Core startup patterns found.</summary>
    public List<AspNetCoreStartupInfo> AspNetCoreStartups { get; } = new();

    /// <summary>Gets the scheduled jobs found.</summary>
    public List<ScheduledJobInfo> ScheduledJobs { get; } = new();

    /// <summary>Gets the application lifetime events found.</summary>
    public List<ApplicationLifetimeInfo> ApplicationLifetimeEvents { get; } = new();

    /// <summary>Gets the manifests associated with this project.</summary>
    public List<ManifestInfo> Manifests { get; } = new();

    /// <summary>Gets or sets the number of files analyzed.</summary>
    public int FilesAnalyzed { get; set; }

    /// <summary>Gets whether this project has any entry points.</summary>
    public bool HasEntryPoints => this.EntryPoints.Count > 0 ||
                                  this.AzureFunctions.Count > 0 ||
                                  this.AspNetCoreStartups.Count > 0;
}

/// <summary>
/// Information about a discovered project.
/// </summary>
public class DiscoveredProject
{
    /// <summary>Gets or sets the full path to the project file.</summary>
    public required string ProjectPath { get; set; }

    /// <summary>Gets or sets the project name.</summary>
    public required string ProjectName { get; set; }

    /// <summary>Gets or sets the SDK information.</summary>
    public required ProjectSdkInfo SdkInfo { get; set; }

    /// <summary>Gets or sets whether this is a test project.</summary>
    public bool IsTestProject { get; set; }

    /// <summary>
    /// Gets or sets whether this is a library project that contains hosting patterns
    /// (e.g., IHostedService, BackgroundService, ServiceBase, StatelessService).
    /// </summary>
    public bool IsLibraryWithHostingPatterns { get; set; }
}
