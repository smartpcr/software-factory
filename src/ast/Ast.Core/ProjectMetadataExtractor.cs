// -----------------------------------------------------------------------
// <copyright file="ProjectMetadataExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core;

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Extracts project and repository level metadata from solution files, project files, and manifests.
/// </summary>
public class ProjectMetadataExtractor
{
    private readonly string repoPath;
    private readonly AstConfiguration config;

    public ProjectMetadataExtractor(string repoPath, AstConfiguration? config = null)
    {
        this.repoPath = Path.GetFullPath(repoPath);
        this.config = config ?? new AstConfiguration();
    }

    /// <summary>
    /// Extract repository-level metadata.
    /// </summary>
    public RepositoryMetadata ExtractRepositoryMetadata()
    {
        var repoName = Path.GetFileName(this.repoPath);

        // Check for solution files to get a better name
        var slnFiles = Directory.GetFiles(this.repoPath, "*.sln", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetRelativePath(this.repoPath, f))
            .ToList();

        if (slnFiles.Count == 1)
        {
            repoName = Path.GetFileNameWithoutExtension(slnFiles[0]);
        }

        // Detect languages
        var languages = new List<string>();
        if (Directory.GetFiles(this.repoPath, "*.cs", SearchOption.AllDirectories)
            .Any(f => !this.IsExcluded(f)))
            languages.Add("csharp");
        if (Directory.GetFiles(this.repoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(this.repoPath, "*.psm1", SearchOption.AllDirectories))
            .Any(f => !this.IsExcluded(f)))
            languages.Add("powershell");

        var metadata = new RepositoryMetadata
        {
            Name = repoName,
            RootPath = this.repoPath,
            Languages = languages,
            SolutionFiles = slnFiles,
            HasReadme = File.Exists(Path.Combine(this.repoPath, "README.md")) ||
                        File.Exists(Path.Combine(this.repoPath, "readme.md")),
            HasLicense = File.Exists(Path.Combine(this.repoPath, "LICENSE")) ||
                         File.Exists(Path.Combine(this.repoPath, "LICENSE.txt")) ||
                         File.Exists(Path.Combine(this.repoPath, "LICENSE.md"))
        };

        // Extract git information
        this.ExtractGitInfo(metadata);

        // Count source files
        metadata.TotalSourceFiles = this.CountSourceFiles();

        return metadata;
    }

    /// <summary>
    /// Extract all project metadata from the repository.
    /// </summary>
    public List<ProjectMetadata> ExtractAllProjects()
    {
        var projects = new List<ProjectMetadata>();

        // Find C# projects
        foreach (var csprojFile in Directory.GetFiles(this.repoPath, "*.csproj", SearchOption.AllDirectories))
        {
            if (this.IsExcluded(csprojFile)) continue;

            try
            {
                var project = this.ExtractCSharpProject(csprojFile);
                projects.Add(project);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error extracting project metadata from {csprojFile}: {ex.Message}");
            }
        }

        // Find PowerShell module manifests
        foreach (var psd1File in Directory.GetFiles(this.repoPath, "*.psd1", SearchOption.AllDirectories))
        {
            if (this.IsExcluded(psd1File)) continue;

            try
            {
                var project = this.ExtractPowerShellModule(psd1File);
                projects.Add(project);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error extracting module metadata from {psd1File}: {ex.Message}");
            }
        }

        // Find standalone PowerShell scripts that aren't part of a module
        foreach (var ps1File in Directory.GetFiles(this.repoPath, "*.ps1", SearchOption.AllDirectories))
        {
            if (this.IsExcluded(ps1File)) continue;

            // Check if there's a module manifest in the same directory
            var dir = Path.GetDirectoryName(ps1File)!;
            if (Directory.GetFiles(dir, "*.psd1").Length > 0) continue;

            // Check if already covered by a parent module
            if (projects.Any(p => ps1File.StartsWith(Path.GetDirectoryName(p.ProjectPath)!, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var project = this.ExtractPowerShellScript(ps1File);
                projects.Add(project);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error extracting script metadata from {ps1File}: {ex.Message}");
            }
        }

        return projects;
    }

    /// <summary>
    /// Extract metadata from a C# project file.
    /// </summary>
    public ProjectMetadata ExtractCSharpProject(string csprojPath)
    {
        var relativePath = Path.GetRelativePath(this.repoPath, csprojPath);
        var projectName = Path.GetFileNameWithoutExtension(csprojPath);
        var projectDir = Path.GetDirectoryName(csprojPath)!;

        var doc = XDocument.Load(csprojPath);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        // Extract target framework(s)
        var targetFrameworks = new List<string>();
        var tfElement = doc.Descendants(ns + "TargetFramework").FirstOrDefault();
        var tfsElement = doc.Descendants(ns + "TargetFrameworks").FirstOrDefault();
        if (tfElement != null)
            targetFrameworks.Add(tfElement.Value);
        else if (tfsElement != null)
            targetFrameworks.AddRange(tfsElement.Value.Split(';', StringSplitOptions.RemoveEmptyEntries));

        // Extract output type
        var outputType = doc.Descendants(ns + "OutputType").FirstOrDefault()?.Value;

        // Extract package references
        var packageRefs = doc.Descendants(ns + "PackageReference")
            .Select(pr => new PackageReference
            {
                PackageId = pr.Attribute("Include")?.Value ?? "",
                Version = pr.Attribute("Version")?.Value ?? pr.Element(ns + "Version")?.Value,
                IsPrivateAssets = pr.Element(ns + "PrivateAssets")?.Value == "all"
            })
            .Where(pr => !string.IsNullOrEmpty(pr.PackageId))
            .ToList();

        // Extract project references
        var projectRefs = doc.Descendants(ns + "ProjectReference")
            .Select(pr => pr.Attribute("Include")?.Value ?? "")
            .Where(pr => !string.IsNullOrEmpty(pr))
            .ToList();

        // Determine project type
        var isTestProject = doc.Descendants(ns + "IsTestProject").FirstOrDefault()?.Value == "true" ||
                           packageRefs.Any(p => p.PackageId.Contains("MSTest") ||
                                               p.PackageId.Contains("xUnit") ||
                                               p.PackageId.Contains("NUnit"));
        var isWebProject = packageRefs.Any(p => p.PackageId.Contains("Microsoft.AspNetCore")) ||
                          doc.Descendants(ns + "Sdk").Any(s => s.Value?.Contains("Web") == true);

        var projectType = isTestProject ? ProjectType.CSharpTest :
                         isWebProject ? ProjectType.CSharpWebApp :
                         outputType?.ToLower() == "exe" ? ProjectType.CSharpExecutable :
                         ProjectType.CSharpLibrary;

        // Find source files
        var sourceFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !this.IsExcluded(f))
            .Select(f => Path.GetRelativePath(this.repoPath, f))
            .ToList();

        // Extract description and author from csproj
        var description = doc.Descendants(ns + "Description").FirstOrDefault()?.Value;
        var author = doc.Descendants(ns + "Authors").FirstOrDefault()?.Value ??
                    doc.Descendants(ns + "Company").FirstOrDefault()?.Value;

        return new ProjectMetadata
        {
            Name = projectName,
            ProjectPath = relativePath,
            ProjectType = projectType,
            Language = "csharp",
            TargetFrameworks = targetFrameworks,
            OutputType = outputType,
            PackageReferences = packageRefs,
            ProjectReferences = projectRefs,
            Description = description,
            Author = author,
            SourceFiles = sourceFiles
        };
    }

    /// <summary>
    /// Extract metadata from a PowerShell module manifest.
    /// </summary>
    public ProjectMetadata ExtractPowerShellModule(string psd1Path)
    {
        var relativePath = Path.GetRelativePath(this.repoPath, psd1Path);
        var moduleName = Path.GetFileNameWithoutExtension(psd1Path);
        var moduleDir = Path.GetDirectoryName(psd1Path)!;

        // Parse the PSD1 manifest
        var content = File.ReadAllText(psd1Path);

        // Extract common properties using regex
        var moduleVersion = ExtractPsd1Value(content, "ModuleVersion");
        var author = ExtractPsd1Value(content, "Author");
        var description = ExtractPsd1Value(content, "Description");

        // Extract required modules
        var requiredModules = ExtractRequiredModules(content);

        // Find source files
        var sourceFiles = Directory.GetFiles(moduleDir, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(moduleDir, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !this.IsExcluded(f))
            .Select(f => Path.GetRelativePath(this.repoPath, f))
            .ToList();

        return new ProjectMetadata
        {
            Name = moduleName,
            ProjectPath = relativePath,
            ProjectType = ProjectType.PowerShellModule,
            Language = "powershell",
            ModuleVersion = moduleVersion,
            Author = author,
            Description = description,
            RequiredModules = requiredModules,
            SourceFiles = sourceFiles
        };
    }

    /// <summary>
    /// Extract metadata from a standalone PowerShell script.
    /// </summary>
    public ProjectMetadata ExtractPowerShellScript(string ps1Path)
    {
        var relativePath = Path.GetRelativePath(this.repoPath, ps1Path);
        var scriptName = Path.GetFileNameWithoutExtension(ps1Path);

        return new ProjectMetadata
        {
            Name = scriptName,
            ProjectPath = relativePath,
            ProjectType = ProjectType.PowerShellScript,
            Language = "powershell",
            SourceFiles = [relativePath]
        };
    }

    private void ExtractGitInfo(RepositoryMetadata metadata)
    {
        var gitDir = Path.Combine(this.repoPath, ".git");
        if (!Directory.Exists(gitDir)) return;

        try
        {
            // Try to get remote URL
            var configPath = Path.Combine(gitDir, "config");
            if (File.Exists(configPath))
            {
                var configContent = File.ReadAllText(configPath);
                var remoteMatch = Regex.Match(configContent, @"url\s*=\s*(.+)");
                if (remoteMatch.Success)
                {
                    metadata.GitRemoteUrl = remoteMatch.Groups[1].Value.Trim();
                }
            }

            // Try to get current branch
            var headPath = Path.Combine(gitDir, "HEAD");
            if (File.Exists(headPath))
            {
                var headContent = File.ReadAllText(headPath).Trim();
                if (headContent.StartsWith("ref: refs/heads/"))
                {
                    metadata.GitBranch = headContent["ref: refs/heads/".Length..];
                }
            }

            // Try to get commit SHA (simple approach - read from HEAD or ref)
            if (!string.IsNullOrEmpty(metadata.GitBranch))
            {
                var refPath = Path.Combine(gitDir, "refs", "heads", metadata.GitBranch);
                if (File.Exists(refPath))
                {
                    metadata.GitCommitSha = File.ReadAllText(refPath).Trim();
                }
            }
        }
        catch
        {
            // Git info is optional, ignore errors
        }
    }

    private int CountSourceFiles()
    {
        var count = 0;

        foreach (var pattern in this.config.CSharpFilePatterns)
        {
            count += Directory.GetFiles(this.repoPath, pattern, SearchOption.AllDirectories)
                .Count(f => !this.IsExcluded(f));
        }

        foreach (var pattern in this.config.PowerShellFilePatterns)
        {
            count += Directory.GetFiles(this.repoPath, pattern, SearchOption.AllDirectories)
                .Count(f => !this.IsExcluded(f));
        }

        return count;
    }

    private bool IsExcluded(string path)
    {
        var relativePath = Path.GetRelativePath(this.repoPath, path);
        return this.config.ExcludedDirectories.Any(
            ex => relativePath.Contains(Path.DirectorySeparatorChar + ex + Path.DirectorySeparatorChar) ||
                  relativePath.StartsWith(ex + Path.DirectorySeparatorChar) ||
                  relativePath.Contains("/" + ex + "/") ||
                  relativePath.StartsWith(ex + "/"));
    }

    private static string? ExtractPsd1Value(string content, string key)
    {
        var pattern = $@"{key}\s*=\s*['""]([^'""]+)['""]";
        var match = Regex.Match(content, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<ModuleReference> ExtractRequiredModules(string content)
    {
        var modules = new List<ModuleReference>();

        // Pattern for simple module names: RequiredModules = @('Module1', 'Module2')
        var simplePattern = @"RequiredModules\s*=\s*@\(([^)]+)\)";
        var simpleMatch = Regex.Match(content, simplePattern);
        if (simpleMatch.Success)
        {
            var modulesString = simpleMatch.Groups[1].Value;

            // Extract simple module names (quoted strings)
            var nameMatches = Regex.Matches(modulesString, @"['""]([^'""]+)['""]");
            foreach (Match m in nameMatches)
            {
                modules.Add(new ModuleReference { ModuleName = m.Groups[1].Value });
            }

            // Extract hashtable-style module references
            var hashMatches = Regex.Matches(modulesString, @"@\{[^}]+ModuleName\s*=\s*['""]([^'""]+)['""][^}]*\}");
            foreach (Match m in hashMatches)
            {
                var moduleName = m.Groups[1].Value;
                var fullHash = m.Value;

                var versionMatch = Regex.Match(fullHash, @"ModuleVersion\s*=\s*['""]([^'""]+)['""]");
                var reqVersionMatch = Regex.Match(fullHash, @"RequiredVersion\s*=\s*['""]([^'""]+)['""]");

                modules.Add(new ModuleReference
                {
                    ModuleName = moduleName,
                    ModuleVersion = versionMatch.Success ? versionMatch.Groups[1].Value : null,
                    RequiredVersion = reqVersionMatch.Success ? reqVersionMatch.Groups[1].Value : null
                });
            }
        }

        return modules;
    }
}
