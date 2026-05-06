// -----------------------------------------------------------------------
// <copyright file="HypergraphModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core;

/// <summary>
/// Types of hyperedges extracted from code.
/// </summary>
public enum HyperedgeType
{
    // C# specific
    ClassDefinition,
    InterfaceDefinition,
    StructDefinition,
    RecordDefinition,
    MethodDefinition,
    ConstructorDefinition,
    PropertyDefinition,
    MethodInvocation,
    ConstructorInvocation,
    UsingDirective,
    AttributeDecoration,
    LambdaExpression,
    LinqQuery,
    AsyncAwait,
    DependencyInjection,
    EventHandler,
    GenericTypeConstraint,

    // C# Architectural Patterns
    ServiceRegistration,      // DI container registrations (AddSingleton, AddScoped, etc.)
    ApiEndpoint,              // Controller routes and HTTP methods
    EntryPoint,               // Main program / startup configuration
    HttpClientUsage,          // External API calls via HttpClient
    DatabaseAccess,           // DbContext, SQL connections
    FileSystemAccess,         // File/Directory operations
    ConfigurationAccess,      // IConfiguration usage
    LoggingCall,              // ILogger usage patterns
    MiddlewarePipeline,       // ASP.NET middleware registration

    // PowerShell specific
    FunctionDefinition,
    CmdletCall,
    Pipeline,
    ModuleImport,
    ModuleFunctionCall,
    RemoteExecution,
    DotSource,
    ParamBlock,
    DscConfiguration,
    DscResource,

    // Class/Interface relationships
    Implements,               // Class implements interface
    Extends,                  // Class extends base class

    // Common
    Assignment,
    ControlFlow,
    ExceptionHandling,
    DataFlow,

    // Cross-language
    CrossLanguageTypeReference,
    CrossLanguageMethodCall,

    // Memory layer (all memory-only edges share this type;
    // the specific semantic relation lives in Metadata["SemanticRelation"])
    MemoryRelation
}

/// <summary>
/// Represents a node in the code hypergraph.
/// </summary>
public class HypergraphNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string NodeType { get; init; }
    public required string Language { get; init; }
    public required string FilePath { get; init; }
    public string Scope { get; init; } = string.Empty;
    public int LineStart { get; init; }
    public int LineEnd { get; init; }
    public List<string> Modifiers { get; init; } = [];
    public Dictionary<string, object?> Metadata { get; init; } = [];

    public override int GetHashCode() => this.Id.GetHashCode();
    public override bool Equals(object? obj) => obj is HypergraphNode node && this.Id == node.Id;

    /// <summary>
    /// Generate text suitable for vector embedding.
    /// </summary>
    public string GetEmbeddingText()
    {
        var parts = new List<string>
        {
            $"{this.NodeType}: {this.Name}",
            $"Language: {this.Language}",
            $"File: {this.FilePath}:{this.LineStart}"
        };

        if (!string.IsNullOrEmpty(this.Scope))
        {
            parts.Add($"Scope: {this.Scope}");
        }

        if (this.Modifiers.Count > 0)
        {
            parts.Add($"Modifiers: {string.Join(" ", this.Modifiers)}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Generate enhanced text for vector embedding with LSP enrichment data.
    /// This includes type signatures, documentation, and relationship information
    /// for higher quality semantic search.
    /// </summary>
    public string GetEnhancedEmbeddingText()
    {
        var parts = new List<string>
        {
            // 1. Symbol identification
            $"{this.NodeType}: {this.Name}"
        };

        // 2. Documentation (from LSP hover) - most important for search
        if (this.Metadata.TryGetValue("Documentation", out var doc) && doc is string docStr && !string.IsNullOrEmpty(docStr))
        {
            parts.Add(docStr);
        }

        // 3. Type signature (from LSP hover) - critical for type-aware search
        if (this.Metadata.TryGetValue("Signature", out var sig) && sig is string sigStr && !string.IsNullOrEmpty(sigStr))
        {
            parts.Add($"Signature: {sigStr}");
        }

        // 4. Return type
        if (this.Metadata.TryGetValue("ReturnType", out var ret) && ret is string retStr && !string.IsNullOrEmpty(retStr))
        {
            parts.Add($"Returns: {retStr}");
        }

        // 5. Parameters
        if (this.Metadata.TryGetValue("Parameters", out var param) && param is string paramStr && !string.IsNullOrEmpty(paramStr))
        {
            parts.Add($"Parameters: {paramStr}");
        }

        // 6. Relationships (for architectural understanding)
        if (this.Metadata.TryGetValue("Callers", out var callers) && callers is string callersStr && !string.IsNullOrEmpty(callersStr))
        {
            parts.Add($"Called by: {callersStr}");
        }

        if (this.Metadata.TryGetValue("Callees", out var callees) && callees is string calleesStr && !string.IsNullOrEmpty(calleesStr))
        {
            parts.Add($"Calls: {calleesStr}");
        }

        // 7. Scope and modifiers
        if (!string.IsNullOrEmpty(this.Scope))
        {
            parts.Add($"Scope: {this.Scope}");
        }

        if (this.Modifiers.Count > 0)
        {
            parts.Add($"Modifiers: {string.Join(" ", this.Modifiers)}");
        }

        // 8. Source code (if available in metadata)
        if (this.Metadata.TryGetValue("SourceCode", out var code) && code is string codeStr && !string.IsNullOrEmpty(codeStr))
        {
            // Truncate if too long
            var truncated = codeStr.Length > 2000 ? codeStr[..2000] : codeStr;
            parts.Add($"Code:\n{truncated}");
        }

        // 9. File location (last, as context)
        parts.Add($"Location: {this.FilePath}:{this.LineStart}");

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Checks if this node has been enriched with LSP data.
    /// </summary>
    public bool IsLspEnriched => this.Metadata.TryGetValue("LspEnriched", out var val) && val is true;
}

/// <summary>
/// Represents a hyperedge connecting multiple nodes.
/// Unlike traditional edges (pairwise), hyperedges connect ANY number of nodes,
/// preserving the full context of code relationships.
/// </summary>
public class Hyperedge
{
    public required string Id { get; init; }
    public required HyperedgeType EdgeType { get; init; }
    public required List<string> NodeIds { get; init; }
    public required Dictionary<string, string> Roles { get; init; }
    public List<string> Languages { get; init; } = [];
    public double Weight { get; init; } = 1.0;
    public Dictionary<string, object?> Metadata { get; init; } = [];

    public int Cardinality => this.NodeIds.Count;
    public bool IsCrossLanguage => this.Languages.Distinct().Count() > 1;

    /// <summary>
    /// Generate text suitable for vector embedding.
    /// </summary>
    public string GetEmbeddingText()
    {
        var parts = new List<string>
        {
            $"Edge: {this.EdgeType}",
            $"Nodes: {string.Join(", ", this.NodeIds)}",
            $"Roles: {string.Join(", ", this.Roles.Select(r => $"{r.Value}={r.Key}"))}"
        };

        if (this.IsCrossLanguage)
        {
            parts.Add($"Cross-language: {string.Join(", ", this.Languages)}");
        }

        return string.Join("\n", parts);
    }
}

/// <summary>
/// Container for hypergraph data from a repository.
/// </summary>
public class HypergraphData
{
    public Dictionary<string, HypergraphNode> Nodes { get; init; } = [];
    public List<Hyperedge> Hyperedges { get; init; } = [];
    public HypergraphMetadata Metadata { get; init; } = new();
}

public class HypergraphMetadata
{
    public string? RepoPath { get; set; }
    public int NumFiles { get; set; }
    public int NumCSharpNodes { get; set; }
    public int NumPowerShellNodes { get; set; }
    public int NumCrossLanguageEdges { get; set; }
    public List<string> Files { get; set; } = [];
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Repository-level metadata.
    /// </summary>
    public RepositoryMetadata? Repository { get; set; }

    /// <summary>
    /// List of projects in the repository.
    /// </summary>
    public List<ProjectMetadata> Projects { get; set; } = [];
}

/// <summary>
/// Repository-level metadata extracted from git and file structure.
/// </summary>
public class RepositoryMetadata
{
    /// <summary>
    /// Name of the repository (derived from folder name or .sln file).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Root path of the repository.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Languages detected in the repository.
    /// </summary>
    public List<string> Languages { get; init; } = [];

    /// <summary>
    /// Solution files (.sln) in the repository.
    /// </summary>
    public List<string> SolutionFiles { get; init; } = [];

    /// <summary>
    /// Git remote URL if available.
    /// </summary>
    public string? GitRemoteUrl { get; set; }

    /// <summary>
    /// Current git branch if available.
    /// </summary>
    public string? GitBranch { get; set; }

    /// <summary>
    /// Current git commit SHA if available.
    /// </summary>
    public string? GitCommitSha { get; set; }

    /// <summary>
    /// Whether the repository has a README file.
    /// </summary>
    public bool HasReadme { get; set; }

    /// <summary>
    /// Whether the repository has a LICENSE file.
    /// </summary>
    public bool HasLicense { get; set; }

    /// <summary>
    /// Total number of source files.
    /// </summary>
    public int TotalSourceFiles { get; set; }

    /// <summary>
    /// Total lines of code (approximate).
    /// </summary>
    public int TotalLinesOfCode { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];
}

/// <summary>
/// Project-level metadata extracted from .csproj, .psm1, or module manifest files.
/// </summary>
public class ProjectMetadata
{
    /// <summary>
    /// Name of the project.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Relative path to the project file.
    /// </summary>
    public required string ProjectPath { get; init; }

    /// <summary>
    /// Type of project (CSharp, PowerShellModule, PowerShellScript).
    /// </summary>
    public required ProjectType ProjectType { get; init; }

    /// <summary>
    /// Primary language of the project.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Target framework(s) for .NET projects.
    /// </summary>
    public List<string> TargetFrameworks { get; init; } = [];

    /// <summary>
    /// Output type (Exe, Library, etc.).
    /// </summary>
    public string? OutputType { get; set; }

    /// <summary>
    /// NuGet package references for .NET projects.
    /// </summary>
    public List<PackageReference> PackageReferences { get; init; } = [];

    /// <summary>
    /// Project references for .NET projects.
    /// </summary>
    public List<string> ProjectReferences { get; init; } = [];

    /// <summary>
    /// Required PowerShell modules for PowerShell projects.
    /// </summary>
    public List<ModuleReference> RequiredModules { get; init; } = [];

    /// <summary>
    /// Module version for PowerShell module manifests.
    /// </summary>
    public string? ModuleVersion { get; set; }

    /// <summary>
    /// Author information.
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// Description of the project.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Source files in the project.
    /// </summary>
    public List<string> SourceFiles { get; init; } = [];

    /// <summary>
    /// Number of nodes extracted from this project.
    /// </summary>
    public int NodeCount { get; set; }

    /// <summary>
    /// Number of hyperedges extracted from this project.
    /// </summary>
    public int HyperedgeCount { get; set; }

    /// <summary>
    /// Entry point information (Main method, script entry, etc.).
    /// </summary>
    public EntryPointInfo? EntryPoint { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];
}

/// <summary>
/// Type of project.
/// </summary>
public enum ProjectType
{
    CSharpLibrary,
    CSharpExecutable,
    CSharpTest,
    CSharpWebApp,
    PowerShellModule,
    PowerShellScript,
    Unknown
}

/// <summary>
/// NuGet package reference information.
/// </summary>
public class PackageReference
{
    public required string PackageId { get; init; }
    public string? Version { get; init; }
    public bool IsPrivateAssets { get; init; }
}

/// <summary>
/// PowerShell module reference information.
/// </summary>
public class ModuleReference
{
    public required string ModuleName { get; init; }
    public string? ModuleVersion { get; init; }
    public string? RequiredVersion { get; init; }
    public string? MaximumVersion { get; init; }
}

/// <summary>
/// Entry point information for a project.
/// </summary>
public class EntryPointInfo
{
    /// <summary>
    /// Type of entry point.
    /// </summary>
    public required string EntryType { get; init; }

    /// <summary>
    /// File containing the entry point.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Class containing the entry point (for C#).
    /// </summary>
    public string? ClassName { get; set; }

    /// <summary>
    /// Method name (Main, etc.).
    /// </summary>
    public string? MethodName { get; set; }

    /// <summary>
    /// Whether the entry point is async.
    /// </summary>
    public bool IsAsync { get; set; }

    /// <summary>
    /// Whether it uses top-level statements (C# 9+).
    /// </summary>
    public bool UsesTopLevelStatements { get; set; }
}