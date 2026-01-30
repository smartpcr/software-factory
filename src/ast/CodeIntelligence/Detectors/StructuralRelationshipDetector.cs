// -----------------------------------------------------------------------
// <copyright file="StructuralRelationshipDetector.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Detectors;

using CodeIntelligence.Analyzers;
using CodeIntelligence.Models;
using CodeIntelligence.Parsers;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Orchestrates structural relationship detection across all file types.
/// </summary>
public sealed class StructuralRelationshipDetector
{
    private static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private readonly InheritanceAnalyzer inheritanceAnalyzer;
    private readonly DependencyInjectionAnalyzer diAnalyzer;
    private readonly MsBuildProjectParser projectParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuralRelationshipDetector"/> class.
    /// </summary>
    public StructuralRelationshipDetector()
    {
        this.inheritanceAnalyzer = new InheritanceAnalyzer();
        this.diAnalyzer = new DependencyInjectionAnalyzer();
        this.projectParser = new MsBuildProjectParser();
    }

    /// <summary>
    /// Detects all structural relationships in a directory.
    /// </summary>
    /// <param name="rootPath">Root directory to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All detected structural relationships.</returns>
    public async Task<StructuralAnalysisResult> AnalyzeAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var result = new StructuralAnalysisResult();

        // Find all source files and project files
        var csFiles = Directory.GetFiles(rootPath, "*.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        var projectFiles = Directory.GetFiles(rootPath, "*.csproj", SafeEnumerationOptions)
            .ToList();

        // Analyze C# source files
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

        // Analyze project files
        foreach (var projectFile in projectFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await this.AnalyzeProjectFileAsync(projectFile, result, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new AnalysisError
                {
                    FilePath = projectFile,
                    Message = ex.Message,
                    ErrorType = "ProjectParseError"
                });
            }
        }

        // Build type hierarchy
        result.TypeHierarchy = this.inheritanceAnalyzer.BuildHierarchy();

        return result;
    }

    /// <summary>
    /// Analyzes a single C# file.
    /// </summary>
    public async Task<StructuralAnalysisResult> AnalyzeFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var result = new StructuralAnalysisResult();
        await this.AnalyzeCSharpFileAsync(filePath, result, cancellationToken);
        result.TypeHierarchy = this.inheritanceAnalyzer.BuildHierarchy();
        return result;
    }

    private async Task AnalyzeCSharpFileAsync(
        string filePath,
        StructuralAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var syntaxTree = CSharpSyntaxTree.ParseText(content, path: filePath, cancellationToken: cancellationToken);

        // Analyze inheritance
        var inheritanceRelationships = this.inheritanceAnalyzer.Analyze(syntaxTree, filePath);
        result.InheritanceRelationships.AddRange(inheritanceRelationships);

        // Convert to detected relationships
        foreach (var rel in inheritanceRelationships)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(rel));
        }

        // Analyze DI
        var diRelationships = this.diAnalyzer.Analyze(syntaxTree, filePath);
        result.DependencyInjectionRelationships.AddRange(diRelationships);

        // Convert to detected relationships
        foreach (var rel in diRelationships)
        {
            result.DetectedRelationships.Add(this.ToDetectedRelationship(rel));
        }

        result.FilesAnalyzed++;
    }

    private async Task AnalyzeProjectFileAsync(
        string filePath,
        StructuralAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var projectInfo = await this.projectParser.ParseAsync(filePath, cancellationToken);
        result.ProjectInfos.Add(projectInfo);

        // Convert project references to relationships
        foreach (var projRef in projectInfo.ProjectReferences)
        {
            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"projref-{Guid.NewGuid():N}",
                Category = "structural",
                RelationshipType = "ProjectReference",
                Source = new RelationshipNode
                {
                    NodeType = "Project",
                    FullyQualifiedName = projectInfo.ProjectName,
                    Name = projectInfo.ProjectName,
                    FilePath = filePath,
                    LineNumber = 1
                },
                Target = new RelationshipNode
                {
                    NodeType = "Project",
                    FullyQualifiedName = projRef.ProjectName,
                    Name = projRef.ProjectName,
                    FilePath = projRef.AbsolutePath,
                    LineNumber = 1
                },
                Confidence = 0.95f,
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "config",
                        Description = $"ProjectReference: {projRef.Include}",
                        Weight = 0.95f,
                        FilePath = filePath
                    }
                },
                DetectionStrategy = "config",
                Properties = new Dictionary<string, object>
                {
                    ["referencePath"] = projRef.Include,
                    ["absolutePath"] = projRef.AbsolutePath,
                    ["isConditional"] = projRef.IsConditional
                }
            });
        }

        // Convert package references to relationships
        foreach (var pkgRef in projectInfo.PackageReferences)
        {
            result.DetectedRelationships.Add(new DetectedRelationship
            {
                Id = $"pkgref-{Guid.NewGuid():N}",
                Category = "structural",
                RelationshipType = "PackageReference",
                Source = new RelationshipNode
                {
                    NodeType = "Project",
                    FullyQualifiedName = projectInfo.ProjectName,
                    Name = projectInfo.ProjectName,
                    FilePath = filePath,
                    LineNumber = 1
                },
                Target = new RelationshipNode
                {
                    NodeType = "Package",
                    FullyQualifiedName = pkgRef.PackageName,
                    Name = pkgRef.PackageName,
                    FilePath = filePath,
                    LineNumber = 1
                },
                Confidence = 0.95f,
                EvidenceChain = new List<Evidence>
                {
                    new Evidence
                    {
                        Source = "config",
                        Description = $"PackageReference: {pkgRef.PackageName} v{pkgRef.Version}",
                        Weight = 0.95f,
                        FilePath = filePath
                    }
                },
                DetectionStrategy = "config",
                Properties = new Dictionary<string, object>
                {
                    ["packageName"] = pkgRef.PackageName,
                    ["version"] = pkgRef.Version ?? "centrally managed",
                    ["isConditional"] = pkgRef.IsConditional,
                    ["isDevelopmentDependency"] = pkgRef.IsDevelopmentDependency
                }
            });
        }

        result.ProjectsAnalyzed++;
    }

    private DetectedRelationship ToDetectedRelationship(InheritanceRelationship rel)
    {
        var relType = rel.RelationType switch
        {
            InheritanceRelationType.Inherits => "Inheritance",
            InheritanceRelationType.Implements => "InterfaceImplementation",
            InheritanceRelationType.Extends => "InterfaceInheritance",
            _ => "Unknown"
        };

        var sourceNodeType = rel.SourceKind.ToString();
        var targetNodeType = rel.TargetKind.ToString();

        return new DetectedRelationship
        {
            Id = $"inh-{Guid.NewGuid():N}",
            Category = "structural",
            RelationshipType = relType,
            Source = new RelationshipNode
            {
                NodeType = sourceNodeType,
                FullyQualifiedName = rel.SourceType,
                Name = rel.SourceType.Contains('.') ? rel.SourceType[(rel.SourceType.LastIndexOf('.') + 1)..] : rel.SourceType,
                FilePath = rel.FilePath,
                LineNumber = rel.LineNumber
            },
            Target = new RelationshipNode
            {
                NodeType = targetNodeType,
                FullyQualifiedName = rel.TargetType,
                Name = rel.TargetType.Contains('.') ? rel.TargetType[(rel.TargetType.LastIndexOf('.') + 1)..] : rel.TargetType,
                FilePath = rel.FilePath,
                LineNumber = rel.LineNumber
            },
            Confidence = 0.95f,
            EvidenceChain = new List<Evidence>
            {
                new Evidence
                {
                    Source = "ast",
                    Description = $"{rel.SourceKind} {rel.SourceType} {relType.ToLower()} {rel.TargetType}",
                    Weight = 0.95f,
                    FilePath = rel.FilePath,
                    LineNumber = rel.LineNumber
                }
            },
            DetectionStrategy = "ast",
            Properties = new Dictionary<string, object>
            {
                ["sourceKind"] = rel.SourceKind.ToString(),
                ["targetKind"] = rel.TargetKind.ToString(),
                ["isGeneric"] = rel.IsGeneric,
                ["genericArguments"] = rel.GenericArguments
            }
        };
    }

    private DetectedRelationship ToDetectedRelationship(DependencyInjectionRelationship rel)
    {
        var relType = rel.InjectionMethod switch
        {
            InjectionMethod.Registration => "ServiceRegistration",
            _ => "DependencyInjection"
        };

        var properties = new Dictionary<string, object>
        {
            ["injectionMethod"] = rel.InjectionMethod.ToString(),
            ["dependencyName"] = rel.DependencyName,
            ["isOptional"] = rel.IsOptional,
            ["isInterface"] = rel.IsInterface
        };

        if (rel.Lifetime.HasValue)
        {
            properties["lifetime"] = rel.Lifetime.Value.ToString();
        }

        if (rel.ServiceType != null)
        {
            properties["serviceType"] = rel.ServiceType;
        }

        if (rel.ImplementationType != null)
        {
            properties["implementationType"] = rel.ImplementationType;
        }

        if (rel.IsKeyed)
        {
            properties["isKeyed"] = true;
            if (rel.KeyedServiceKey != null)
            {
                properties["serviceKey"] = rel.KeyedServiceKey;
            }
        }

        return new DetectedRelationship
        {
            Id = $"di-{Guid.NewGuid():N}",
            Category = "structural",
            RelationshipType = relType,
            Source = new RelationshipNode
            {
                NodeType = "Class",
                FullyQualifiedName = rel.ConsumerType,
                Name = rel.ConsumerType.Contains('.') ? rel.ConsumerType[(rel.ConsumerType.LastIndexOf('.') + 1)..] : rel.ConsumerType,
                FilePath = rel.FilePath,
                LineNumber = rel.LineNumber
            },
            Target = new RelationshipNode
            {
                NodeType = rel.IsInterface ? "Interface" : "Class",
                FullyQualifiedName = rel.DependencyType,
                Name = rel.DependencyType.Contains('.') ? rel.DependencyType[(rel.DependencyType.LastIndexOf('.') + 1)..] : rel.DependencyType,
                FilePath = rel.FilePath,
                LineNumber = rel.LineNumber
            },
            Confidence = rel.InjectionMethod == InjectionMethod.Registration ? 0.95f : 0.90f,
            EvidenceChain = new List<Evidence>
            {
                new Evidence
                {
                    Source = "ast",
                    Description = $"{rel.ConsumerType} depends on {rel.DependencyType} via {rel.InjectionMethod}",
                    Weight = 0.90f,
                    FilePath = rel.FilePath,
                    LineNumber = rel.LineNumber
                }
            },
            DetectionStrategy = "ast",
            Properties = properties
        };
    }
}

/// <summary>
/// Result of structural analysis.
/// </summary>
public class StructuralAnalysisResult
{
    /// <summary>Gets the detected relationships.</summary>
    public List<DetectedRelationship> DetectedRelationships { get; } = new();

    /// <summary>Gets the inheritance relationships.</summary>
    public List<InheritanceRelationship> InheritanceRelationships { get; } = new();

    /// <summary>Gets the DI relationships.</summary>
    public List<DependencyInjectionRelationship> DependencyInjectionRelationships { get; } = new();

    /// <summary>Gets the project infos.</summary>
    public List<ProjectInfo> ProjectInfos { get; } = new();

    /// <summary>Gets or sets the type hierarchy.</summary>
    public TypeHierarchy? TypeHierarchy { get; set; }

    /// <summary>Gets the analysis errors.</summary>
    public List<AnalysisError> Errors { get; } = new();

    /// <summary>Gets or sets the number of files analyzed.</summary>
    public int FilesAnalyzed { get; set; }

    /// <summary>Gets or sets the number of projects analyzed.</summary>
    public int ProjectsAnalyzed { get; set; }
}

/// <summary>
/// Represents an analysis error.
/// </summary>
public record AnalysisError
{
    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the error message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the error type.</summary>
    public required string ErrorType { get; init; }

    /// <summary>Gets the line number if applicable.</summary>
    public int? LineNumber { get; init; }
}
