// -----------------------------------------------------------------------
// <copyright file="MsBuildProjectParser.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Parsers;

using System.Xml.Linq;
using System.Xml.XPath;
using CodeIntelligence.Models;

/// <summary>
/// Parses MSBuild project files (.csproj, .vbproj, .fsproj) for project and package references.
/// </summary>
public sealed class MsBuildProjectParser
{
    /// <summary>
    /// Parses a project file and extracts all references.
    /// </summary>
    /// <param name="projectPath">Path to the project file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed project information.</returns>
    public async Task<ProjectInfo> ParseAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException($"Project file not found: {projectPath}");
        }

        var content = await File.ReadAllTextAsync(projectPath, cancellationToken);
        return this.Parse(projectPath, content);
    }

    /// <summary>
    /// Parses project content and extracts all references.
    /// </summary>
    /// <param name="projectPath">Path to the project file (for reference resolution).</param>
    /// <param name="content">XML content of the project file.</param>
    /// <returns>Parsed project information.</returns>
    public ProjectInfo Parse(string projectPath, string content)
    {
        var doc = XDocument.Parse(content);
        var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        var projectInfo = new ProjectInfo
        {
            ProjectPath = projectPath,
            ProjectName = projectName,
            ProjectDirectory = projectDir,
            Sdk = this.GetSdk(doc),
            TargetFramework = this.GetTargetFramework(doc),
            TargetFrameworks = this.GetTargetFrameworks(doc),
            OutputType = this.GetOutputType(doc),
            RootNamespace = this.GetRootNamespace(doc, projectName),
            AssemblyName = this.GetAssemblyName(doc, projectName),
            ProjectReferences = this.GetProjectReferences(doc, projectDir).ToList(),
            PackageReferences = this.GetPackageReferences(doc).ToList(),
            FrameworkReferences = this.GetFrameworkReferences(doc).ToList(),
            Properties = this.GetProperties(doc)
        };

        return projectInfo;
    }

    private string? GetSdk(XDocument doc)
    {
        return doc.Root?.Attribute("Sdk")?.Value;
    }

    private string? GetTargetFramework(XDocument doc)
    {
        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TargetFramework")
            ?.Value;
    }

    private IReadOnlyList<string> GetTargetFrameworks(XDocument doc)
    {
        var value = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")
            ?.Value;

        if (string.IsNullOrEmpty(value))
        {
            var single = this.GetTargetFramework(doc);
            return single != null ? new[] { single } : Array.Empty<string>();
        }

        return value.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    private string? GetOutputType(XDocument doc)
    {
        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "OutputType")
            ?.Value;
    }

    private string GetRootNamespace(XDocument doc, string defaultValue)
    {
        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")
            ?.Value ?? defaultValue;
    }

    private string GetAssemblyName(XDocument doc, string defaultValue)
    {
        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")
            ?.Value ?? defaultValue;
    }

    private IEnumerable<ProjectReferenceInfo> GetProjectReferences(XDocument doc, string projectDir)
    {
        var references = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference");

        foreach (var refElement in references)
        {
            var include = refElement.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
            {
                continue;
            }

            var relativePath = include.Replace('\\', Path.DirectorySeparatorChar);
            var absolutePath = Path.GetFullPath(Path.Combine(projectDir, relativePath));
            var condition = refElement.Attribute("Condition")?.Value;

            yield return new ProjectReferenceInfo
            {
                Include = include,
                AbsolutePath = absolutePath,
                ProjectName = Path.GetFileNameWithoutExtension(include),
                Condition = condition,
                IsConditional = !string.IsNullOrEmpty(condition)
            };
        }
    }

    private IEnumerable<PackageReferenceInfo> GetPackageReferences(XDocument doc)
    {
        var references = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference");

        foreach (var refElement in references)
        {
            var include = refElement.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
            {
                continue;
            }

            var version = refElement.Attribute("Version")?.Value
                ?? refElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

            var condition = refElement.Attribute("Condition")?.Value;
            var privateAssets = refElement.Attribute("PrivateAssets")?.Value
                ?? refElement.Elements().FirstOrDefault(e => e.Name.LocalName == "PrivateAssets")?.Value;
            var includeAssets = refElement.Attribute("IncludeAssets")?.Value
                ?? refElement.Elements().FirstOrDefault(e => e.Name.LocalName == "IncludeAssets")?.Value;

            yield return new PackageReferenceInfo
            {
                PackageName = include,
                Version = version,
                Condition = condition,
                IsConditional = !string.IsNullOrEmpty(condition),
                PrivateAssets = privateAssets,
                IncludeAssets = includeAssets,
                IsDevelopmentDependency = privateAssets?.Equals("all", StringComparison.OrdinalIgnoreCase) == true
            };
        }
    }

    private IEnumerable<FrameworkReferenceInfo> GetFrameworkReferences(XDocument doc)
    {
        var references = doc.Descendants()
            .Where(e => e.Name.LocalName == "FrameworkReference");

        foreach (var refElement in references)
        {
            var include = refElement.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
            {
                continue;
            }

            yield return new FrameworkReferenceInfo
            {
                FrameworkName = include
            };
        }
    }

    private Dictionary<string, string> GetProperties(XDocument doc)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var propertyGroups = doc.Descendants()
            .Where(e => e.Name.LocalName == "PropertyGroup");

        foreach (var propGroup in propertyGroups)
        {
            foreach (var prop in propGroup.Elements())
            {
                var name = prop.Name.LocalName;
                var value = prop.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    properties[name] = value;
                }
            }
        }

        return properties;
    }
}

/// <summary>
/// Information about a parsed project file.
/// </summary>
public record ProjectInfo
{
    /// <summary>Gets the full path to the project file.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Gets the project name (without extension).</summary>
    public required string ProjectName { get; init; }

    /// <summary>Gets the project directory.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>Gets the SDK (e.g., Microsoft.NET.Sdk).</summary>
    public string? Sdk { get; init; }

    /// <summary>Gets the target framework.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Gets multiple target frameworks.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = Array.Empty<string>();

    /// <summary>Gets the output type (Exe, Library, etc.).</summary>
    public string? OutputType { get; init; }

    /// <summary>Gets the root namespace.</summary>
    public string RootNamespace { get; init; } = string.Empty;

    /// <summary>Gets the assembly name.</summary>
    public string AssemblyName { get; init; } = string.Empty;

    /// <summary>Gets the project references.</summary>
    public IReadOnlyList<ProjectReferenceInfo> ProjectReferences { get; init; } = Array.Empty<ProjectReferenceInfo>();

    /// <summary>Gets the package references.</summary>
    public IReadOnlyList<PackageReferenceInfo> PackageReferences { get; init; } = Array.Empty<PackageReferenceInfo>();

    /// <summary>Gets the framework references.</summary>
    public IReadOnlyList<FrameworkReferenceInfo> FrameworkReferences { get; init; } = Array.Empty<FrameworkReferenceInfo>();

    /// <summary>Gets additional MSBuild properties.</summary>
    public Dictionary<string, string> Properties { get; init; } = new();

    /// <summary>Gets whether this is an executable project.</summary>
    public bool IsExecutable => OutputType?.Equals("Exe", StringComparison.OrdinalIgnoreCase) == true
        || OutputType?.Equals("WinExe", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Gets whether this is a test project.</summary>
    public bool IsTestProject => Properties.ContainsKey("IsTestProject")
        && Properties["IsTestProject"].Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Information about a project reference.
/// </summary>
public record ProjectReferenceInfo
{
    /// <summary>Gets the Include path as specified in the project file.</summary>
    public required string Include { get; init; }

    /// <summary>Gets the absolute path to the referenced project.</summary>
    public required string AbsolutePath { get; init; }

    /// <summary>Gets the referenced project name.</summary>
    public required string ProjectName { get; init; }

    /// <summary>Gets the condition expression if any.</summary>
    public string? Condition { get; init; }

    /// <summary>Gets whether this is a conditional reference.</summary>
    public bool IsConditional { get; init; }
}

/// <summary>
/// Information about a package reference.
/// </summary>
public record PackageReferenceInfo
{
    /// <summary>Gets the package name.</summary>
    public required string PackageName { get; init; }

    /// <summary>Gets the version (may be null for central package management).</summary>
    public string? Version { get; init; }

    /// <summary>Gets the condition expression if any.</summary>
    public string? Condition { get; init; }

    /// <summary>Gets whether this is a conditional reference.</summary>
    public bool IsConditional { get; init; }

    /// <summary>Gets the PrivateAssets value.</summary>
    public string? PrivateAssets { get; init; }

    /// <summary>Gets the IncludeAssets value.</summary>
    public string? IncludeAssets { get; init; }

    /// <summary>Gets whether this is a development-only dependency.</summary>
    public bool IsDevelopmentDependency { get; init; }
}

/// <summary>
/// Information about a framework reference.
/// </summary>
public record FrameworkReferenceInfo
{
    /// <summary>Gets the framework name (e.g., Microsoft.AspNetCore.App).</summary>
    public required string FrameworkName { get; init; }
}
